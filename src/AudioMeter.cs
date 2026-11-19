using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Arabara
{
    // What the speakers are playing, analysed for the SOUND WAVE background: a WASAPI
    // loopback capture of the default output device (so it follows Arabara's music player,
    // and any other sound) on a background thread, mixed to mono and fed straight into a
    // MusicAnalyzer, which analyses about 94 times a second (spectrum, kick/snare/hat
    // onsets, tempo, part of the song). Nothing is recorded or stored: samples only pass
    // through. Capture runs only while the scene asks for it.
    // With OwnAudio the capture hears only what Arabara itself plays (its WebView2 processes,
    // so the music terminal; not a game or another app): Windows' process loopback
    // (ActivateAudioInterfaceAsync with the process loopback parameters, Windows 10 build
    // 20348 / Windows 11 and later), written after Microsoft's ApplicationLoopback sample and
    // NAudio's process capture as references; where it is missing, the speakers are heard.
    internal static class AudioMeter
    {
        public const int BandCount = MusicAnalyzer.BandCount;
        private static readonly object gate = new object();
        private static MusicAnalyzer analyzer;
        private static int sampleRate = 48000;
        private static long lastRequest;
        private static Thread worker;
        private static readonly float[] bands = new float[BandCount];
        private static float level;
        // --home-test: fixed bands instead of the speakers.
        internal static float[] TestBands;
        private static float testSum = -1;
        private static MusicEvents testEvents;
        // --audio-test --frames: an analyzer fed from a file instead of the speakers, and the
        // events it found (the test also logs them, so it hands them over here).
        internal static MusicAnalyzer TestAnalyzer;
        internal static MusicEvents TestPending;
        // --own-audio-test: the frames heard with sound in them, and the loudest sample.
        internal static long TestFramesHeard;
        internal static float TestPeak;

        public static string Error { get; private set; }
        // Hear only Arabara's own sound (see above); false once Windows has refused it.
        // OwnAudioProcess is the process whose tree is heard: WebView2's browser process,
        // which starts the renderers and the audio service (0: this process).
        public static volatile bool OwnAudio;
        public static volatile int OwnAudioProcess;
        public static bool OwnAudioAvailable { get { return ownAudioWorks; } }
        private static volatile bool ownAudioWorks = true;
        // Whether the analyzer's beat clock holds the tempo.
        public static bool Locked { get; private set; }
        // The latest capture problem (the loop retries), and the device's sample rate.
        public static string LastError { get; private set; }
        public static int SampleRate { get { lock (gate) return sampleRate; } }
        public static bool Capturing { get; private set; }
        // Overall loudness 0..1 and the bass 0..1, from the latest bands.
        public static float Level { get { return level; } }
        public static float Bass { get; private set; }
        // The part of the song: about 1 normally, up to 3 at a drop, below 1 in a bridge.
        public static float Section { get; private set; }
        public static double Bpm { get; private set; }

        // Updates and returns the bands (0..1), low to high: the highest each reached since
        // the last call, so nothing between two frames is missed.
        public static float[] Read()
        {
            if (TestBands != null)
            {
                Array.Copy(TestBands, bands, BandCount);
                float sum = 0; foreach (float b in bands) sum += b;
                level = sum / BandCount; Bass = (bands[0] + bands[1] + bands[2] + bands[3]) / 4;
                // A jump in the test bands counts as a kick and a snare at a drop.
                Section = sum > 0 ? 1.8F : 0;
                if (testSum >= 0 && sum - testSum > 0.1F * BandCount) testEvents = new MusicEvents { Kicks = 1, Kick = 1.2F, Snares = 1, Snare = 1F };
                testSum = sum;
                return bands;
            }
            if (TestAnalyzer != null)
            {
                TestAnalyzer.TakeBands(bands);
                level = TestAnalyzer.Loudness; Section = TestAnalyzer.Section; Bpm = TestAnalyzer.Bpm;
                float low = 0;
                for (int b = 0; b < 6; b++) low += bands[b];
                Bass = low / 6;
                return bands;
            }
            Listen();
            lock (gate)
            {
                if (analyzer == null) Array.Clear(bands, 0, BandCount);
                else
                {
                    analyzer.TakeBands(bands);
                    level = analyzer.Loudness;
                    Section = analyzer.Section;
                    Bpm = analyzer.TempoConfidence > 0.25 ? analyzer.Bpm : 0;
                    Locked = analyzer.Locked;
                }
            }
            float bass = 0;
            for (int b = 0; b < 6; b++) bass += bands[b];
            Bass = bass / 6;
            return bands;
        }

        // Keeps the capture running (it stops a second after the last request).
        private static void Listen()
        {
            Interlocked.Exchange(ref lastRequest, Environment.TickCount);
            if (worker == null && Error == null)
            {
                worker = new Thread(Capture) { IsBackground = true, Name = "Arabara audio meter", Priority = ThreadPriority.BelowNormal };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
        }

        // For the window's music bars: the bands as they are now and the number of kicks
        // and snares heard so far, without taking anything from the SOUND WAVE scene's reads.
        public static int Peek(float[] into, out int snares)
        {
            snares = 0;
            if (TestBands != null) { Array.Copy(TestBands, into, BandCount); return 0; }
            if (TestAnalyzer != null) { TestAnalyzer.PeekBands(into); snares = TestAnalyzer.SnareCount; return TestAnalyzer.KickCount; }
            Listen();
            lock (gate)
            {
                if (analyzer == null) { Array.Clear(into, 0, BandCount); return 0; }
                analyzer.PeekBands(into);
                snares = analyzer.SnareCount;
                return analyzer.KickCount;
            }
        }

        // Kicks, snares, hats and clock beats since the last call.
        public static MusicEvents TakeEvents()
        {
            MusicEvents events;
            if (TestBands != null) { events = testEvents; testEvents = new MusicEvents(); return events; }
            if (TestAnalyzer != null) { events = TestPending; TestPending = new MusicEvents(); return events; }
            lock (gate)
            {
                if (analyzer == null) return new MusicEvents();
                analyzer.TakeEvents(out events);
            }
            return events;
        }

        // Self-test: the bands for a block of samples, as if the speakers had played it.
        internal static float[] AnalyseForTest(float[] samples, int rate)
        {
            var test = new MusicAnalyzer(rate);
            test.Push(samples, 0, samples.Length);
            var result = new float[BandCount];
            test.TakeBands(result);
            return result;
        }

        private static bool Wanted { get { return Environment.TickCount - Interlocked.Read(ref lastRequest) < 1000; } }

        // Capture loop: (re)opens the default output device in loopback mode while wanted,
        // and recovers after device changes (for example plugging in headphones).
        private static void Capture()
        {
            while (true)
            {
                if (!Wanted) { Thread.Sleep(300); continue; }
                try { CaptureOnce(); }
                catch (Exception ex) { LastError = ex.Message; Debug.WriteLine("Audio meter: " + ex.Message); Thread.Sleep(1000); }
            }
        }

        private static void CaptureOnce()
        {
            bool own = OwnAudio && ownAudioWorks;
            int target = OwnAudioProcess;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null; IAudioClient client = null; IAudioCaptureClient capture = null;
            IntPtr format = IntPtr.Zero;
            AutoResetEvent ready = null;
            try
            {
                if (own)
                {
                    try { client = ActivateProcessLoopback(); }
                    catch (Exception ex) { ownAudioWorks = false; LastError = "own audio unavailable: " + ex.Message; return; }
                    // Process loopback has no mix format of its own: it converts to this one
                    // (16-bit stereo PCM at 44.1 kHz, as in Microsoft's sample).
                    format = Marshal.AllocCoTaskMem(18);
                    Marshal.WriteInt16(format, 0, 1);          // PCM
                    Marshal.WriteInt16(format, 2, 2);          // stereo
                    Marshal.WriteInt32(format, 4, 44100);
                    Marshal.WriteInt32(format, 8, 44100 * 4);  // bytes a second
                    Marshal.WriteInt16(format, 12, 4);         // block align
                    Marshal.WriteInt16(format, 14, 16);        // bits
                    Marshal.WriteInt16(format, 16, 0);
                }
                else
                {
                    enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
                    Check(enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out device));
                    var clientId = typeof(IAudioClient).GUID;
                    object activated;
                    Check(device.Activate(ref clientId, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out activated));
                    client = (IAudioClient)activated;
                    Check(client.GetMixFormat(out format));
                }
                int tag = Marshal.ReadInt16(format, 0), channels = Marshal.ReadInt16(format, 2), bits = Marshal.ReadInt16(format, 14);
                int rate = Marshal.ReadInt32(format, 4), blockAlign = Marshal.ReadInt16(format, 12);
                bool isFloat = tag == 3 || (tag == unchecked((short)0xFFFE) && Marshal.ReadInt32(format, 24) == 3);
                if (!isFloat && bits != 16) throw new NotSupportedException("audio format " + tag + "/" + bits);
                if (own)
                {
                    // Loopback, event driven, converted to the format above.
                    Check(client.Initialize(0 /* shared */, 0x00020000 | 0x00040000 | unchecked((int)0x80000000) | 0x08000000, 2000000 /* 200 ms */, 0, format, IntPtr.Zero));
                    ready = new AutoResetEvent(false);
                    Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
                }
                else Check(client.Initialize(0 /* shared */, 0x00020000 /* loopback */, 2000000 /* 200 ms */, 0, format, IntPtr.Zero));
                var captureId = typeof(IAudioCaptureClient).GUID;
                object service;
                Check(client.GetService(ref captureId, out service));
                capture = (IAudioCaptureClient)service;
                lock (gate) { sampleRate = rate; analyzer = new MusicAnalyzer(rate); }
                Check(client.Start());
                Capturing = true;
                LastError = null;
                var floats = new float[0];
                var shorts = new short[0];
                var mono = new float[0];
                // Until it is no longer wanted, or the sound to hear changes.
                while (Wanted && own == (OwnAudio && ownAudioWorks) && (!own || target == OwnAudioProcess))
                {
                    if (ready != null) ready.WaitOne(100); else Thread.Sleep(10);
                    uint packet;
                    Check(capture.GetNextPacketSize(out packet));
                    while (packet > 0)
                    {
                        IntPtr data; uint frames, flags; ulong position, qpc;
                        Check(capture.GetBuffer(out data, out frames, out flags, out position, out qpc));
                        int count = (int)frames;
                        bool silent = (flags & 2) != 0;
                        int values = count * blockAlign / Math.Max(1, bits / 8);
                        if (!silent)
                        {
                            if (isFloat) { if (floats.Length < values) floats = new float[values]; Marshal.Copy(data, floats, 0, values); }
                            else { if (shorts.Length < values) shorts = new short[values]; Marshal.Copy(data, shorts, 0, values); }
                        }
                        int stride = blockAlign / Math.Max(1, bits / 8);
                        if (!silent) TestFramesHeard += count;
                        if (mono.Length < count) mono = new float[count];
                        for (int f = 0; f < count; f++)
                        {
                            float sum = 0;
                            if (!silent)
                                for (int c = 0; c < channels; c++)
                                    sum += isFloat ? floats[f * stride + c] : shorts[f * stride + c] / 32768F;
                            mono[f] = sum / Math.Max(1, channels);
                            if (mono[f] > TestPeak) TestPeak = mono[f];
                        }
                        lock (gate) analyzer.Push(mono, 0, count);
                        Check(capture.ReleaseBuffer(frames));
                        Check(capture.GetNextPacketSize(out packet));
                    }
                }
                client.Stop();
                Capturing = false;
                // Forget the music once capture stops.
                lock (gate) analyzer = null;
            }
            finally
            {
                Capturing = false;
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                if (capture != null) Marshal.ReleaseComObject(capture);
                if (client != null) Marshal.ReleaseComObject(client);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
                if (ready != null) ready.Dispose();
            }
        }

        // An audio client that hears this process and every process it started (WebView2's
        // browser, renderers and audio service): ActivateAudioInterfaceAsync on the process
        // loopback device, waited for on this (MTA) thread.
        private static IAudioClient ActivateProcessLoopback()
        {
            IntPtr parameters = Marshal.AllocHGlobal(12), variant = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.WriteInt32(parameters, 0, 1);                                      // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                Marshal.WriteInt32(parameters, 4, OwnAudioProcess != 0 ? OwnAudioProcess : Process.GetCurrentProcess().Id); // TargetProcessId
                Marshal.WriteInt32(parameters, 8, 0);                                      // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(variant, i, 0);
                Marshal.WriteInt16(variant, 0, 65);                                        // VT_BLOB
                Marshal.WriteInt32(variant, 8, 12);                                        // cbSize
                Marshal.WriteIntPtr(variant, 16, parameters);                              // pBlobData
                var done = new ActivationDone();
                IActivateAudioInterfaceAsyncOperation operation;
                Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, variant, done, out operation));
                try
                {
                    if (!done.Finished.WaitOne(5000)) throw new TimeoutException("process loopback did not activate");
                    int result;
                    object activated;
                    Check(operation.GetActivateResult(out result, out activated));
                    Check(result);
                    return (IAudioClient)activated;
                }
                finally { Marshal.ReleaseComObject(operation); }
            }
            finally { Marshal.FreeHGlobal(variant); Marshal.FreeHGlobal(parameters); }
        }

        // Called by Windows (on any thread) when the activation is done; agile, as required.
        private sealed class ActivationDone : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Finished = new ManualResetEvent(false);
            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) { Finished.Set(); return 0; }
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject { }

        private static void Check(int hr) { if (hr < 0) throw new COMException("WASAPI call failed", hr); }

        // ---- Core Audio interop (only the members used; the rest are vtable placeholders) ----

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints();
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
