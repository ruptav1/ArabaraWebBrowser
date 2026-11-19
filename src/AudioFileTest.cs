using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Arabara
{
    // --audio-test "<file>": decodes a music file's sound with Windows Media Foundation
    // (nothing is played) and runs the MusicAnalyzer over it, writing what it hears to
    // test-results: music-analysis.csv (every 50 ms: part of the song, loudness, tempo,
    // kick/snare/hat/beat events) and a per-second summary in music-analysis.txt.
    // With --frames 60,100.5 it also renders the SOUND WAVE scene from that analysis,
    // 30 frames a second for the second before each listed time, and saves the frame at
    // each time as wave-<seconds>.png.
    internal static class AudioFileTest
    {
        public static int Run(string path)
        {
            var csv = new StringBuilder("time,section,loudness,bpm,confidence,kicks,kick,snares,snare,hats,hat,beats,silent\n");
            var log = new StringBuilder();
            IMFSourceReader reader = null;
            try
            {
                Check(MFStartup(0x00020070, 1), "MFStartup");
                Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader), "Open file");
                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(FirstAudioStream, true);
                IMFMediaType type;
                Check(MFCreateMediaType(out type), "MFCreateMediaType");
                var major = MajorType; var audio = MediaTypeAudio; var subtype = Subtype; var pcmFloat = FormatFloat;
                type.SetGUID(ref major, ref audio);
                type.SetGUID(ref subtype, ref pcmFloat);
                Check(reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, type), "Float output");
                Marshal.ReleaseComObject(type);
                IMFMediaType current;
                Check(reader.GetCurrentMediaType(FirstAudioStream, out current), "Audio format");
                int channels, rate;
                var channelsKey = AudioChannels; var rateKey = AudioRate;
                current.GetUINT32(ref channelsKey, out channels);
                current.GetUINT32(ref rateKey, out rate);
                Marshal.ReleaseComObject(current);
                log.AppendLine("File: " + Path.GetFileName(path) + ", " + rate + " Hz, " + channels + " channels");
                var analyzer = new MusicAnalyzer(rate) { Trace = new StringBuilder("time,kind,strength,phase,locked,taken\n") };
                var floats = new float[0]; var mono = new float[0];
                var args = Environment.GetCommandLineArgs();
                int framesArg = Array.IndexOf(args, "--frames");
                var targets = new System.Collections.Generic.List<double>();
                if (framesArg > 0 && framesArg + 1 < args.Length)
                    foreach (string part in args[framesArg + 1].Split(','))
                        targets.Add(double.Parse(part, System.Globalization.CultureInfo.InvariantCulture));
                targets.Sort();
                SpinningMark mark = null; System.Drawing.Bitmap frame = null; System.Drawing.Graphics g = null;
                double nextFrame = 0;
                // --bars: the window's music bars followed through the song, 60 times a second.
                bool bars = Array.IndexOf(args, "--bars") > 0;
                // --rings: the RINGS scene's speed through the song, 60 times a second (music-rings.csv).
                bool ringsFollow = Array.IndexOf(args, "--rings") > 0;
                var ringDrive = new RingDrive();
                var ringLog = new StringBuilder("time,speed,beats,kicks,locked,bpm,change" + Environment.NewLine);
                var ringPending = new MusicEvents();
                double nextRing = 0;
                var barLog = new StringBuilder("time,meter,kick\n");
                double nextBar = 0;
                if (bars) AudioMeter.TestAnalyzer = analyzer;
                if (targets.Count > 0)
                {
                    UiFonts.Initialize();
                    mark = new SpinningMark();
                    frame = new System.Drawing.Bitmap(1280, 900, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                    g = System.Drawing.Graphics.FromImage(frame);
                    SpinningMark.SetScene(SpinningMark.SoundScene, false);
                    AudioMeter.TestAnalyzer = analyzer;
                }
                double nextRow = 0.05, nextSecond = 1;
                var second = new MusicEvents(); var row = new MusicEvents();
                float sectionMin = 9, sectionMax = 0;
                while (true)
                {
                    int actual, flags; long stamp; IMFSample sample;
                    Check(reader.ReadSample(FirstAudioStream, 0, out actual, out flags, out stamp, out sample), "ReadSample");
                    if ((flags & 0x2) != 0) { if (sample != null) Marshal.ReleaseComObject(sample); break; }
                    if (sample == null) continue;
                    IMFMediaBuffer buffer;
                    sample.ConvertToContiguousBuffer(out buffer);
                    IntPtr data; int max, length;
                    buffer.Lock(out data, out max, out length);
                    int values = length / 4, frames = values / Math.Max(1, channels);
                    if (floats.Length < values) floats = new float[values];
                    if (mono.Length < frames) mono = new float[frames];
                    Marshal.Copy(data, floats, 0, values);
                    buffer.Unlock();
                    Marshal.ReleaseComObject(buffer);
                    Marshal.ReleaseComObject(sample);
                    for (int f = 0; f < frames; f++)
                    {
                        float sum = 0;
                        for (int c = 0; c < channels; c++) sum += floats[f * channels + c];
                        mono[f] = sum / channels;
                    }
                    // Feed in small blocks so the 50 ms rows line up with the audio time.
                    for (int at = 0; at < frames; at += 480)
                    {
                        analyzer.Push(mono, at, Math.Min(480, frames - at));
                        MusicEvents e;
                        analyzer.TakeEvents(out e);
                        Add(ref row, e); Add(ref second, e);
                        if (mark != null) Add(ref AudioMeter.TestPending, e);
                        if (targets.Count > 0 && analyzer.Time >= targets[0] - 1 && analyzer.Time >= nextFrame)
                        {
                            nextFrame = analyzer.Time + 1 / 30.0;
                            mark.Render(g, new System.Drawing.Size(1280, 900), new System.Drawing.Rectangle(0, 0, 1280, 900), analyzer.Time, 1F, UiPalette.Canvas,
                                new System.Drawing.Rectangle[0], new int[0], new int[0], 0, System.Drawing.Rectangle.Empty, System.Drawing.Rectangle.Empty);
                            if (analyzer.Time >= targets[0])
                            {
                                frame.Save(Path.Combine(SelfTests.ResultsRoot, "wave-" + targets[0].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".png"));
                                log.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "frame at {0:F2}s: section {1:F2}", analyzer.Time, analyzer.Section));
                                targets.RemoveAt(0);
                            }
                        }
                        if (ringsFollow)
                        {
                            Add(ref ringPending, e);
                            if (analyzer.Time >= nextRing)
                            {
                                nextRing += 1 / 60.0;
                                bool change = ringDrive.Update(1 / 60.0, ringPending, analyzer.Locked, analyzer.Bpm);
                                ringLog.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F3},{1:F2},{2},{3},{4},{5:F1},{6}",
                                    analyzer.Time, 1 + ringDrive.Boost, ringPending.Beats, ringPending.Kicks, analyzer.Locked ? 1 : 0, analyzer.Bpm, change ? 1 : 0));
                                ringPending = new MusicEvents();
                            }
                        }
                        if (bars && analyzer.Time >= nextBar)
                        {
                            nextBar += 1 / 60.0;
                            MusicBar.TestNow = analyzer.Time;
                            MusicBar.Update(true);
                            barLog.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F3},{1:F3},{2}", analyzer.Time, MusicBar.Meter, e.Kicks));
                        }
                        if (analyzer.Time >= nextRow)
                        {
                            csv.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "{0:F2},{1:F2},{2:F2},{3:F1},{4:F2},{5},{6:F2},{7},{8:F2},{9},{10:F2},{11},{12}",
                                analyzer.Time, analyzer.Section, analyzer.Loudness, analyzer.Bpm, analyzer.TempoConfidence,
                                row.Kicks, row.Kick, row.Snares, row.Snare, row.Hats, row.Hat, row.Beats, analyzer.Silent ? 1 : 0));
                            row = new MusicEvents();
                            nextRow += 0.05;
                        }
                        if (analyzer.Time >= nextSecond)
                        {
                            sectionMin = Math.Min(sectionMin, analyzer.Section); sectionMax = Math.Max(sectionMax, analyzer.Section);
                            log.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "{0,4:F0}s  section {1:F2}  loud {2:F2}  bpm {3,5:F1} ({4:F2})  kick {5,2}  snare {6,2}  hat {7,2}  beat {8,2}  {9}",
                                nextSecond, analyzer.Section, analyzer.Loudness, analyzer.Bpm, analyzer.TempoConfidence,
                                second.Kicks, second.Snares, second.Hats, second.Beats, new string('#', (int)Math.Round(analyzer.Section * 10))));
                            second = new MusicEvents();
                            nextSecond += 1;
                        }
                    }
                }
                log.Insert(0, "Section range " + sectionMin.ToString("F2") + " .. " + sectionMax.ToString("F2") + Environment.NewLine);
                SelfTests.WriteResult("music-analysis.csv", csv.ToString());
                SelfTests.WriteResult("music-analysis.txt", log.ToString());
                SelfTests.WriteResult("music-onsets.csv", analyzer.Trace.ToString());
                if (bars) { SelfTests.WriteResult("music-bars.csv", barLog.ToString()); AudioMeter.TestAnalyzer = null; MusicBar.TestNow = -1; }
                if (ringsFollow) SelfTests.WriteResult("music-rings.csv", ringLog.ToString());
                if (mark != null) { g.Dispose(); frame.Dispose(); mark.Dispose(); AudioMeter.TestAnalyzer = null; UiFonts.Shutdown(); }
                return 0;
            }
            catch (Exception ex) { SelfTests.WriteResult("music-analysis.txt", log + "ERROR " + ex); return 1; }
            finally
            {
                if (reader != null) Marshal.ReleaseComObject(reader);
                MFShutdown();
            }
        }

        private static void Add(ref MusicEvents total, MusicEvents e)
        {
            total.Kicks += e.Kicks; total.Snares += e.Snares; total.Hats += e.Hats; total.Beats += e.Beats;
            total.Kick = Math.Max(total.Kick, e.Kick); total.Snare = Math.Max(total.Snare, e.Snare); total.Hat = Math.Max(total.Hat, e.Hat);
        }

        private static void Check(int hr, string step) { if (hr < 0) throw new InvalidOperationException(step + " failed (0x" + hr.ToString("X8") + ")"); }

        // ---- Media Foundation interop (only the members used; the rest are vtable placeholders) ----
        private const int FirstAudioStream = unchecked((int)0xFFFFFFFD), AllStreams = unchecked((int)0xFFFFFFFE);
        private static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MediaTypeAudio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        private static readonly Guid FormatFloat = new Guid("00000003-0000-0010-8000-00AA00389B71");
        private static readonly Guid AudioChannels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid AudioRate = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");

        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

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
            [PreserveSig] int SetCurrentPosition(ref Guid format, IntPtr position);
            [PreserveSig] int ReadSample(int stream, int control, out int actualStream, out int flags, out long timestamp, out IMFSample sample);
        }
    }
}
