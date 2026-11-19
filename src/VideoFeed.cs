using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Arabara
{
    // The still character cut out of a video: full-resolution luma, a mask (1 = character)
    // and the mask's bounding box, all in source pixels.
    internal sealed class VideoFigure
    {
        public readonly int Width, Height;
        public readonly byte[] Luma, Mask;
        public readonly System.Drawing.Rectangle Bounds;
        // The tone curve's gamma: above 1 spreads light tones (most artwork is light).
        public float Gamma = 1.15F;
        // Shown a little smaller than the page, with soft edges (a picture), rather than
        // covering it (a video).
        public bool Fit;
        // The plain brightness of the whole picture (when Luma is a tone map made for the
        // character), for drawing both layers under one light.
        public byte[] Plain;

        public VideoFigure(int width, int height, byte[] luma, byte[] mask, System.Drawing.Rectangle bounds)
        {
            Width = width; Height = height; Luma = luma; Mask = mask; Bounds = bounds;
        }
    }

    // Plays a local video file as a looping, muted luma (brightness) feed for the
    // VIDEO LOOP background. Windows Media Foundation decodes it on a background
    // thread, to NV12, and only the Y plane is read, box-scaled to at most 640 pixels
    // wide. Decoding runs only while frames are being requested, follows a playback
    // clock that pauses with it, and restarts from the beginning at the end of the
    // file. The video itself is never copied: only its path is kept.
    internal sealed class VideoFeed : IDisposable
    {
        public const int MaxWidth = 640;
        private readonly string path;
        private readonly object gate = new object();
        private readonly Stopwatch clock = new Stopwatch();
        private readonly ManualResetEvent wake = new ManualResetEvent(false);
        private Thread worker;
        private volatile bool stopping;
        private long lastRequest;
        private byte[] front, back;
        private byte[] copyLuma; // owned by the caller of TryGetFrame (one UI thread)
        private int frameWidth, frameHeight;
        public string Error { get; private set; }
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }

        public VideoFeed(string path) { this.path = path; }

        public string Path { get { return path; } }

        // The still character found before playback (see ExtractFigure), or null.
        public VideoFigure Figure { get; private set; }
        public bool FigureDone { get; private set; }

        // The latest frame (luma, row-major), or false before the first one is ready.
        public bool TryGetFrame(out byte[] luma, out int width, out int height)
        {
            Interlocked.Exchange(ref lastRequest, Environment.TickCount);
            if (worker == null && Error == null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "Arabara video feed", Priority = ThreadPriority.BelowNormal };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
            wake.Set();
            lock (gate)
            {
                width = frameWidth;
                height = frameHeight;
                if (front == null) { luma = null; return false; }
                // The caller gets its own copy, so decoding can reuse its buffers freely.
                if (copyLuma == null || copyLuma.Length != front.Length) copyLuma = new byte[front.Length];
                Buffer.BlockCopy(front, 0, copyLuma, 0, front.Length);
                luma = copyLuma;
            }
            return true;
        }

        private bool Wanted { get { return Environment.TickCount - Interlocked.Read(ref lastRequest) < 500; } }

        private void Run()
        {
            IMFSourceReader reader = null;
            try
            {
                Check(MFStartup(0x00020070, 1), "MFStartup");
                IMFAttributes attributes;
                Check(MFCreateAttributes(out attributes, 1), "MFCreateAttributes");
                var processing = EnableVideoProcessing;
                attributes.SetUINT32(ref processing, 1);
                Check(MFCreateSourceReaderFromURL(path, attributes, out reader), "Open video");
                Marshal.ReleaseComObject(attributes);
                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(FirstVideoStream, true);
                IMFMediaType type;
                Check(MFCreateMediaType(out type), "MFCreateMediaType");
                var major = MajorType; var video = MediaTypeVideo; var subtype = Subtype; var nv12 = FormatNV12;
                type.SetGUID(ref major, ref video);
                type.SetGUID(ref subtype, ref nv12);
                Check(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, type), "NV12 output");
                Marshal.ReleaseComObject(type);
                int width, height, stride;
                ReadFormat(reader, out width, out height, out stride);
                try { Figure = ExtractFigure(reader, ref width, ref height, ref stride); }
                catch (Exception ex) { Debug.WriteLine("Video figure: " + ex.Message); }
                FigureDone = true;
                var origin = new PropVariant { Type = 20, Value = 0 };
                var timeFormat = Guid.Empty;
                reader.SetCurrentPosition(ref timeFormat, ref origin);
                long loopStart = 0;
                clock.Start();
                while (!stopping)
                {
                    if (!Wanted)
                    {
                        clock.Stop();
                        wake.Reset();
                        wake.WaitOne(1000);
                        continue;
                    }
                    if (!clock.IsRunning) clock.Start();
                    int actual, flags;
                    long stamp;
                    IMFSample sample;
                    Check(reader.ReadSample(FirstVideoStream, 0, out actual, out flags, out stamp, out sample), "ReadSample");
                    if ((flags & 0x20) != 0) ReadFormat(reader, out width, out height, out stride);
                    if ((flags & 0x2) != 0)
                    {
                        // End of file: loop from the start.
                        if (sample != null) Marshal.ReleaseComObject(sample);
                        var start = new PropVariant { Type = 20, Value = 0 };
                        var none = Guid.Empty;
                        reader.SetCurrentPosition(ref none, ref start);
                        loopStart = clock.ElapsedTicks * 10000000L / Stopwatch.Frequency;
                        continue;
                    }
                    if (sample == null) continue;
                    try
                    {
                        // Wait until the frame is due (100 ns units), unless decoding has fallen behind.
                        long due = loopStart + stamp, now = clock.ElapsedTicks * 10000000L / Stopwatch.Frequency;
                        if (due - now > 10000) Thread.Sleep((int)Math.Min(200, (due - now) / 10000));
                        IMFMediaBuffer buffer;
                        if (sample.ConvertToContiguousBuffer(out buffer) != 0 || buffer == null) continue;
                        try
                        {
                            IntPtr data; int max, current;
                            if (buffer.Lock(out data, out max, out current) != 0) continue;
                            try { Downscale(data, current, width, height, stride); }
                            finally { buffer.Unlock(); }
                        }
                        finally { Marshal.ReleaseComObject(buffer); }
                    }
                    finally { Marshal.ReleaseComObject(sample); }
                }
            }
            catch (Exception ex) { Error = ex.Message; }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                MFShutdown();
            }
        }

        private void ReadFormat(IMFSourceReader reader, out int width, out int height, out int stride)
        {
            IMFMediaType current;
            Check(reader.GetCurrentMediaType(FirstVideoStream, out current), "Video format");
            var sizeKey = FrameSize; var strideKey = DefaultStride;
            long size;
            current.GetUINT64(ref sizeKey, out size);
            width = (int)(size >> 32);
            height = (int)(size & 0xFFFFFFFF);
            int defaultStride;
            stride = current.GetUINT32(ref strideKey, out defaultStride) == 0 && defaultStride > 0 ? defaultStride : width;
            Marshal.ReleaseComObject(current);
            SourceWidth = width;
            SourceHeight = height;
        }

        // Box-averages the Y plane down to at most MaxWidth pixels wide.
        private void Downscale(IntPtr data, int length, int width, int height, int stride)
        {
            if (width <= 0 || height <= 0 || length < stride * height) return;
            int factor = Math.Max(1, (width + MaxWidth - 1) / MaxWidth);
            int w = width / factor, h = height / factor;
            if (back == null || back.Length != w * h) back = new byte[w * h];
            var row = new byte[stride];
            var sums = new int[w];
            for (int y = 0; y < h; y++)
            {
                Array.Clear(sums, 0, w);
                for (int dy = 0; dy < factor; dy++)
                {
                    Marshal.Copy(data + (y * factor + dy) * stride, row, 0, stride);
                    for (int x = 0; x < w; x++)
                    {
                        int s = 0, x0 = x * factor;
                        for (int dx = 0; dx < factor; dx++) s += row[x0 + dx];
                        sums[x] += s;
                    }
                }
                int area = factor * factor;
                for (int x = 0; x < w; x++) back[y * w + x] = (byte)(sums[x] / area);
            }
            lock (gate)
            {
                var done = back;
                back = front;
                front = done;
                frameWidth = w;
                frameHeight = h;
            }
        }

        // The video is treated as two layers: a still character in front of a moving
        // background. About four seconds are decoded as fast as possible, sampled every
        // 100 ms, and per pixel the brightness variation over those samples is measured.
        // Still pixels are candidates for the character, except those coloured like the
        // background's dominant colour (the most common chroma among moving pixels), which
        // removes still background marks of that colour. A small opening drops thin
        // still rings and dashes, the largest piece crossing the centre is the character,
        // a closing (limited to candidates) restores thin details near it, and small
        // enclosed holes are filled. The last sample, at full resolution, is the still.
        private VideoFigure ExtractFigure(IMFSourceReader reader, ref int width, ref int height, ref int stride)
        {
            const long Step = 1000000, Span = 40000000; // 100 ms and 4 s, in 100 ns units
            int[] sum = null, squares = null;
            byte[] still = null, chroma = null;
            int samples = 0, w = 0, h = 0;
            long next = 0;
            while (!stopping)
            {
                int actual, flags; long stamp; IMFSample sample;
                Check(reader.ReadSample(FirstVideoStream, 0, out actual, out flags, out stamp, out sample), "ReadSample");
                if ((flags & 0x20) != 0) { ReadFormat(reader, out width, out height, out stride); sum = null; samples = 0; }
                if ((flags & 0x2) != 0) { if (sample != null) Marshal.ReleaseComObject(sample); break; }
                if (sample == null) continue;
                try
                {
                    if (stamp < next) continue;
                    next = stamp + Step;
                    IMFMediaBuffer buffer;
                    if (sample.ConvertToContiguousBuffer(out buffer) != 0 || buffer == null) continue;
                    try
                    {
                        IntPtr data; int max, current;
                        if (buffer.Lock(out data, out max, out current) != 0) continue;
                        try
                        {
                            if (current < stride * height * 3 / 2) continue;
                            w = width; h = height;
                            if (sum == null || sum.Length != w * h)
                            {
                                sum = new int[w * h]; squares = new int[w * h];
                                still = new byte[w * h]; chroma = new byte[(w / 2) * (h / 2) * 2];
                            }
                            int uvOffset = stride * (current / stride * 2 / 3);
                            var row = new byte[stride];
                            for (int y = 0; y < h; y++)
                            {
                                Marshal.Copy(data + y * stride, row, 0, stride);
                                int line = y * w;
                                for (int x = 0; x < w; x++)
                                {
                                    int v = row[x];
                                    sum[line + x] += v; squares[line + x] += v * v; still[line + x] = (byte)v;
                                }
                            }
                            for (int y = 0; y < h / 2; y++) Marshal.Copy(data + uvOffset + y * stride, chroma, y * (w / 2) * 2, (w / 2) * 2);
                            samples++;
                        }
                        finally { buffer.Unlock(); }
                    }
                    finally { Marshal.ReleaseComObject(buffer); }
                }
                finally { Marshal.ReleaseComObject(sample); }
                if (stamp >= Span) break;
            }
            if (samples < 8 || sum == null) return null;
            int n = w * h, half = w / 2;
            long k = samples;
            // Still: deviation under 4 levels; moving: 6 or more (variance times samples squared).
            var candidate = new bool[n];
            var moving = new bool[n];
            var colours = new int[32 * 32];
            for (int i = 0; i < n; i++)
            {
                long spread = squares[i] * k - (long)sum[i] * sum[i];
                if (spread >= 36 * k * k)
                {
                    moving[i] = true;
                    int c = ((i / w) / 2 * half + (i % w) / 2) * 2;
                    colours[(chroma[c] >> 3) * 32 + (chroma[c + 1] >> 3)]++;
                }
                else if (spread < 16 * k * k) candidate[i] = true;
            }
            sum = squares = null;
            int dominant = 0;
            for (int i = 1; i < colours.Length; i++) if (colours[i] > colours[dominant]) dominant = i;
            int u0 = (dominant / 32) * 8 + 4, v0 = (dominant % 32) * 8 + 4;
            for (int i = 0; i < n; i++)
            {
                if (!candidate[i]) continue;
                int c = ((i / w) / 2 * half + (i % w) / 2) * 2;
                if (Math.Abs(chroma[c] - u0) + Math.Abs(chroma[c + 1] - v0) < 24) candidate[i] = false;
            }
            var spare = new bool[n];
            var core = (bool[])candidate.Clone();
            Grow(core, spare, w, h, 2, false);
            Grow(core, spare, w, h, 2, true);
            var queue = new int[n];
            var label = new int[n];
            int best = 0, bestSize = 0, id = 0;
            for (int y = 0; y < h; y++)
                for (int x = w * 2 / 5; x < w * 3 / 5; x++)
                {
                    int start = y * w + x;
                    if (!core[start] || label[start] != 0) continue;
                    int size = Flood(core, true, label, ++id, start, w, h, queue);
                    if (size > bestSize) { bestSize = size; best = id; }
                }
            if (best == 0) return null;
            for (int i = 0; i < n; i++) core[i] = label[i] == best;
            var closed = (bool[])core.Clone();
            Grow(closed, spare, w, h, 10, true);
            Grow(closed, spare, w, h, 10, false);
            var mask = new byte[n];
            for (int i = 0; i < n; i++) if (core[i] || (closed[i] && candidate[i])) mask[i] = 1;
            // Fill enclosed background pockets smaller than 1% of the frame.
            var inside = new bool[n];
            for (int i = 0; i < n; i++) inside[i] = mask[i] == 0;
            Array.Clear(label, 0, n);
            id = 0;
            for (int start = 0; start < n; start++)
            {
                if (!inside[start] || label[start] != 0) continue;
                int size = Flood(inside, true, label, ++id, start, w, h, queue);
                bool border = false;
                for (int q = 0; q < size && !border; q++)
                {
                    int i = queue[q], x = i % w, y = i / w;
                    border = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                }
                if (!border && size < n / 100) for (int q = 0; q < size; q++) mask[queue[q]] = 1;
            }
            int area = 0, left = w, top = h, right = -1, bottom = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (mask[y * w + x] == 0) continue;
                    area++;
                    if (x < left) left = x; if (x > right) right = x;
                    if (y < top) top = y; if (y > bottom) bottom = y;
                }
            if (area < n / 100 || area > n * 6 / 10) return null;
            return new VideoFigure(w, h, still, mask, new System.Drawing.Rectangle(left, top, right - left + 1, bottom - top + 1));
        }

        // 4-connected flood fill of pixels where set[i] == value; the filled indices are
        // left at the start of queue. Returns how many were filled.
        internal static int Flood(bool[] set, bool value, int[] label, int id, int start, int w, int h, int[] queue)
        {
            int head = 0, tail = 0, n = w * h;
            label[start] = id; queue[tail++] = start;
            while (head < tail)
            {
                int i = queue[head++], x = i % w;
                if (x > 0 && set[i - 1] == value && label[i - 1] == 0) { label[i - 1] = id; queue[tail++] = i - 1; }
                if (x < w - 1 && set[i + 1] == value && label[i + 1] == 0) { label[i + 1] = id; queue[tail++] = i + 1; }
                if (i >= w && set[i - w] == value && label[i - w] == 0) { label[i - w] = id; queue[tail++] = i - w; }
                if (i < n - w && set[i + w] == value && label[i + w] == 0) { label[i + w] = id; queue[tail++] = i + w; }
            }
            return tail;
        }

        // Square dilation (grow) or erosion (shrink) of radius r, separable, in place.
        // Outside the frame counts as empty, so shapes are eroded from the border too.
        internal static void Grow(bool[] set, bool[] temp, int w, int h, int r, bool grow)
        {
            for (int y = 0; y < h; y++)
            {
                int line = y * w, run = -1000;
                for (int x = 0; x < w; x++) { if (set[line + x] == grow) run = x; temp[line + x] = x - run <= r; }
                run = 100000;
                for (int x = w - 1; x >= 0; x--) { if (set[line + x] == grow) run = x; if (run - x <= r) temp[line + x] = true; }
                if (!grow) for (int x = 0; x < w; x++) if (x < r || x >= w - r) temp[line + x] = true;
            }
            for (int x = 0; x < w; x++)
            {
                int run = -1000;
                for (int y = 0; y < h; y++) { if (temp[y * w + x]) run = y; set[y * w + x] = y - run <= r; }
                run = 100000;
                for (int y = h - 1; y >= 0; y--) { if (temp[y * w + x]) run = y; if (run - y <= r) set[y * w + x] = true; }
                if (!grow) for (int y = 0; y < h; y++) if (y < r || y >= h - r) set[y * w + x] = true;
            }
            // temp held "near a hit"; for erosion the hits were empty pixels, so flip back.
            if (!grow) for (int i = 0; i < set.Length; i++) set[i] = !set[i];
        }

        private static void Check(int hr, string step) { if (hr < 0) throw new InvalidOperationException(step + " failed (0x" + hr.ToString("X8") + ")"); }

        public void Dispose()
        {
            stopping = true;
            wake.Set();
            if (worker != null) worker.Join(2000);
            wake.Dispose();
            front = back = null;
        }

        // ---- Media Foundation interop (only the members used here; the rest are vtable placeholders) ----

        private const int FirstVideoStream = unchecked((int)0xFFFFFFFC), AllStreams = unchecked((int)0xFFFFFFFE);
        private static readonly Guid EnableVideoProcessing = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        private static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MediaTypeVideo = new Guid("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid FormatNV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        private static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant { public ushort Type, R1, R2, R3; public long Value; public long Pad; }

        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IMFAttributes attributes, int size);
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        private static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attributes, out IMFSourceReader reader);

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            [PreserveSig] int GetItem(); [PreserveSig] int GetItemType(); [PreserveSig] int CompareItem(); [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32(ref Guid key, out int value);
            [PreserveSig] int GetUINT64(ref Guid key, out long value);
            [PreserveSig] int GetDouble(); [PreserveSig] int GetGUID(); [PreserveSig] int GetStringLength(); [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString(); [PreserveSig] int GetBlobSize(); [PreserveSig] int GetBlob(); [PreserveSig] int GetAllocatedBlob();
            [PreserveSig] int GetUnknown(); [PreserveSig] int SetItem(); [PreserveSig] int DeleteItem(); [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, int value);
            [PreserveSig] int SetUINT64(); [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            [PreserveSig] int GetItem(); [PreserveSig] int GetItemType(); [PreserveSig] int CompareItem(); [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32(ref Guid key, out int value);
            [PreserveSig] int GetUINT64(ref Guid key, out long value);
            [PreserveSig] int GetDouble(); [PreserveSig] int GetGUID(); [PreserveSig] int GetStringLength(); [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString(); [PreserveSig] int GetBlobSize(); [PreserveSig] int GetBlob(); [PreserveSig] int GetAllocatedBlob();
            [PreserveSig] int GetUnknown(); [PreserveSig] int SetItem(); [PreserveSig] int DeleteItem(); [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, int value);
            [PreserveSig] int SetUINT64(); [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            [PreserveSig] int GetItem(); [PreserveSig] int GetItemType(); [PreserveSig] int CompareItem(); [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32(); [PreserveSig] int GetUINT64(); [PreserveSig] int GetDouble(); [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength(); [PreserveSig] int GetString(); [PreserveSig] int GetAllocatedString(); [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob(); [PreserveSig] int GetAllocatedBlob(); [PreserveSig] int GetUnknown(); [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem(); [PreserveSig] int DeleteAllItems(); [PreserveSig] int SetUINT32(); [PreserveSig] int SetUINT64();
            [PreserveSig] int SetDouble(); [PreserveSig] int SetGUID(); [PreserveSig] int SetString(); [PreserveSig] int SetBlob();
            [PreserveSig] int SetUnknown(); [PreserveSig] int LockStore(); [PreserveSig] int UnlockStore(); [PreserveSig] int GetCount();
            [PreserveSig] int GetItemByIndex(); [PreserveSig] int CopyAllItems();
            [PreserveSig] int GetSampleFlags(); [PreserveSig] int SetSampleFlags(); [PreserveSig] int GetSampleTime(); [PreserveSig] int SetSampleTime();
            [PreserveSig] int GetSampleDuration(); [PreserveSig] int SetSampleDuration(); [PreserveSig] int GetBufferCount(); [PreserveSig] int GetBufferByIndex();
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
        }

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection(int stream, out bool selected);
            [PreserveSig] int SetStreamSelection(int stream, bool selected);
            [PreserveSig] int GetNativeMediaType(int stream, int index, out IMFMediaType type);
            [PreserveSig] int GetCurrentMediaType(int stream, out IMFMediaType type);
            [PreserveSig] int SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
            [PreserveSig] int SetCurrentPosition(ref Guid format, ref PropVariant position);
            [PreserveSig] int ReadSample(int stream, int control, out int actualStream, out int flags, out long timestamp, out IMFSample sample);
        }
    }
}
