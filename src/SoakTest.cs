using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Arabara
{
    // --soak-test <minutes> [engine|tab] [--extension <folder>]: plays YouTube Music for a long
    // time, muted, on a throwaway signed-out profile, and records the memory of every process
    // each minute in test-results\soak-<mode>.csv, to see what grows over hours of music.
    // engine: the music terminal's hidden page as Arabara runs it (a public playlist, repeat
    // all). tab: a YouTube Music page playing a public playlist as in an ordinary tab (its own
    // autoplay continues after the playlist). Commit (private bytes) is the steady measure;
    // the working set also moves when Windows trims it.
    internal static class SoakTest
    {
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);

        // A public chart playlist and its first track, seen on the YouTube Music home page.
        private const string Playlist = "RDCLAK5uy_lBNUteBRencHzKelu5iDHwLF6mYqjL-JU", FirstTrack = "mw3kSNIxjqo";

        // The page's JavaScript heap, element count and player state.
        private const string PageProbe = "(() => { const v = document.querySelector('video'); const p = document.getElementById('movie_player'); " +
            "let id = null; try { id = p && p.getVideoData ? p.getVideoData().video_id : null; } catch (e) { } " +
            "return JSON.stringify({ heap: performance.memory ? performance.memory.usedJSHeapSize : 0, nodes: document.getElementsByTagName('*').length, " +
            "t: v ? v.currentTime : -1, paused: v ? v.paused : true, ad: !!document.querySelector('#movie_player.ad-showing, #movie_player.ad-interrupting'), id: id }); })()";

        public static int Run(string[] args)
        {
            int at = Array.IndexOf(args, "--soak-test");
            int minutes = at + 1 < args.Length ? int.Parse(args[at + 1]) : 120;
            string mode = at + 2 < args.Length && !args[at + 2].StartsWith("--") ? args[at + 2] : "engine";
            int ext = Array.IndexOf(args, "--extension");
            string extension = ext >= 0 && ext + 1 < args.Length ? args[ext + 1] : null;
            int code = 1;
            UiFonts.Initialize();
            try
            {
                if (mode == "home") return SoakHome(minutes);
                using (var form = new Form { Text = "Arabara soak test", ClientSize = new System.Drawing.Size(1280, 720), ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000) })
                {
                    form.Shown += async delegate
                    {
                        try { code = await SoakAsync(form, minutes, mode, extension); }
                        catch (Exception ex) { SelfTests.WriteResult("soak-" + mode + "-error.txt", ex.ToString()); }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
            }
            finally { UiFonts.Shutdown(); }
            return code;
        }

        private static async Task<int> SoakAsync(Form form, int minutes, string mode, string extension)
        {
            // --low: the engine's page at WebView2's Low memory target (soak-engine-low.csv).
            bool low = Environment.GetCommandLineArgs().Contains("--low") && mode == "engine";
            // --fast: the engine skips to near the end of each track every 20 seconds, so many
            // track changes are seen in a short run (soak-engine-fast.csv).
            bool fast = Environment.GetCommandLineArgs().Contains("--fast") && mode == "engine";
            bool noAuto = Environment.GetCommandLineArgs().Contains("--no-autostart") && mode == "engine";
            var commandLine = Environment.GetCommandLineArgs();
            int tagAt = Array.IndexOf(commandLine, "--tag");
            string name = mode + (low ? "-low" : "") + (fast ? "-fast" : "") + (noAuto ? "-noauto" : "") +
                (tagAt > 0 && tagAt + 1 < commandLine.Length ? "-" + commandLine[tagAt + 1] : "");
            string profile = Path.Combine(Path.GetTempPath(), "arabara-soak-" + name);
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
            // As Arabara runs it; the tab is allowed to start playing without a click.
            string flags = "--disable-features=CalculateNativeWinOcclusion" + (mode == "tab" ? " --autoplay-policy=no-user-gesture-required" : "");
            var options = new CoreWebView2EnvironmentOptions(flags) { Language = "en-US", AreBrowserExtensionsEnabled = extension != null };
            var environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
            string extensionNote = "none";
            if (extension != null)
            {
                // Each run loads its own copy: Chromium writes the indexed rules into the folder.
                string copy = profile + "-extension";
                if (Directory.Exists(copy)) Directory.Delete(copy, true);
                foreach (string dir in Directory.GetDirectories(extension, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(copy + dir.Substring(extension.Length));
                Directory.CreateDirectory(copy);
                foreach (string source in Directory.GetFiles(extension, "*", SearchOption.AllDirectories))
                    if (source.IndexOf(Path.DirectorySeparatorChar + "_metadata" + Path.DirectorySeparatorChar, StringComparison.Ordinal) < 0)
                        File.Copy(source, copy + source.Substring(extension.Length));
                if (Directory.Exists(Path.Combine(copy, "_metadata"))) Directory.Delete(Path.Combine(copy, "_metadata"), true);
                extension = copy;
                using (var probe = new WebView2 { Visible = false })
                {
                    form.Controls.Add(probe);
                    await probe.EnsureCoreWebView2Async(environment);
                    var added = await probe.CoreWebView2.Profile.AddBrowserExtensionAsync(extension);
                    extensionNote = added.Name + " " + added.Id;
                    form.Controls.Remove(probe);
                }
            }

            MusicEngine engine = null;
            WebView2 tab = null;
            Func<string, Task<string>> evaluate;
            if (mode == "tab")
            {
                tab = new WebView2 { Dock = DockStyle.Fill };
                form.Controls.Add(tab);
                await tab.EnsureCoreWebView2Async(environment);
                tab.CoreWebView2.IsMuted = true;
                tab.CoreWebView2.Navigate("https://music.youtube.com/watch?v=" + FirstTrack + "&list=" + Playlist);
                evaluate = async delegate(string script) { return Unquote(await tab.CoreWebView2.ExecuteScriptAsync(script)); };
            }
            else
            {
                // --volume <0..100>: the player's own volume (the page stays muted in WebView2).
                int volumeAt = Array.IndexOf(commandLine, "--volume");
                int volume = volumeAt > 0 && volumeAt + 1 < commandLine.Length ? int.Parse(commandLine[volumeAt + 1]) : 0;
                engine = new MusicEngine(form, delegate { return Task.FromResult(environment); }, volume);
                engine.Mute();
                await engine.StartAsync();
                var tracks = await engine.LoadTracksAsync(Playlist, true);
                if (tracks.Count < 2) throw new InvalidOperationException("The test playlist has " + tracks.Count + " tracks (" + engine.Message + ")");
                await engine.SetRepeatAsync(MusicRepeat.All);
                await engine.PlayAsync(new MusicPlaylist { Id = Playlist, Title = "soak" }, tracks, 0);
                if (low) engine.SetLowMemoryForTest();
                // --renew-after <minutes>: the page's renewal age, to see what a fresh page gives back.
                int renewAt = Array.IndexOf(commandLine, "--renew-after");
                if (renewAt > 0 && renewAt + 1 < commandLine.Length) MusicEngine.RenewAfter = TimeSpan.FromMinutes(int.Parse(commandLine[renewAt + 1]));
                engine.AutoStartOff = noAuto;
                if (fast) { var skipping = SkipToEndsAsync(engine, minutes); }
                evaluate = delegate(string script) { return engine.EvaluateAsync(script, false); };
            }

            string file = Path.Combine(SelfTests.ResultsRoot, "soak-" + name + ".csv");
            var csv = new StringBuilder();
            csv.AppendLine("# soak " + mode + (low ? " (Low memory target)" : "") + ", " + minutes + " min, extension " + extensionNote + ", started " + DateTime.Now.ToString("s"));
            csv.AppendLine("minute,total_pws_mb,total_commit_mb,host_commit_mb,host_managed_mb,host_gdi,host_user,browser_commit_mb,gpu_commit_mb,renderer_commit_mb,renderer_max_commit_mb,renderers,utility_commit_mb,js_heap_mb,dom_nodes,playing,ad,track,position_s,event");
            File.WriteAllText(file, csv.ToString());
            var json = new JavaScriptSerializer();
            var clock = Stopwatch.StartNew();
            int stalled = 0;
            for (int minute = 0; minute <= minutes; minute++)
            {
                if (minute > 0) await Task.Delay(Math.Max(0, (int)(minute * 60000 - clock.ElapsedMilliseconds)));
                string note = "";
                Dictionary<string, object> page = null;
                try { page = json.Deserialize<Dictionary<string, object>>(await evaluate(PageProbe) ?? "{}"); }
                catch (Exception ex) { note = "probe failed: " + ex.GetType().Name; }
                bool playing = engine != null ? engine.Playing : page != null && !Flag(page, "paused");
                bool ad = engine != null ? engine.Advertising : page != null && Flag(page, "ad");
                // Music that stops is put right (and noted): a listener would press play.
                stalled = playing ? 0 : stalled + 1;
                if (stalled >= 2)
                {
                    note += (note.Length > 0 ? "; " : "") + "stopped, resumed";
                    if (engine != null) await engine.TogglePauseAsync();
                    else await tab.CoreWebView2.ExecuteScriptAsync("(document.querySelector('video') || { play() { } }).play()");
                    stalled = 0;
                }
                if (engine != null && engine.StartsResumed > 0) note += (note.Length > 0 ? "; " : "") + "tracks started after standing paused: " + engine.StartsResumed;
                if (engine != null && engine.Renewals > 0) note += (note.Length > 0 ? "; " : "") + "page renewals: " + engine.Renewals;
                csv.AppendLine(Sample(environment, minute, page, playing, ad,
                    engine != null ? engine.Queue.Index.ToString() : Text(page, "id"),
                    engine != null ? engine.Position : Number(page, "t"), note));
                File.WriteAllText(file, csv.ToString());
            }
            if (engine != null) engine.Dispose();
            if (tab != null) tab.Dispose();
            return 0;
        }

        // home: the start page's background drawn into a bitmap at about 20 frames a second for
        // a long time, a quarter of an hour on each scene in turn (the sound wave fed with test
        // bands that kick at 128 BPM; in a personal build also the video and the first picture
        // the preferences name, read only), and released every 5 minutes as when a page opens.
        // Each minute: Arabara's own memory, managed heap, handles and the mean frame time.
        private static int SoakHome(int minutes)
        {
            var preferences = BrowserPreferences.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arabara"));
            var scenes = new List<int> { 0, SpinningMark.SoundScene };
            if (!Edition.Public && !String.IsNullOrEmpty(preferences.BackdropVideo) && File.Exists(preferences.BackdropVideo))
            { SpinningMark.VideoPath = preferences.BackdropVideo; scenes.Add(SpinningMark.VideoScene); }
            var picture = preferences.BackdropPictures == null ? null : preferences.BackdropPictures.FirstOrDefault(p => p != null && File.Exists(p.Path));
            if (!Edition.Public && picture != null) { SpinningMark.SetPictures(new List<PictureSetting> { picture }, 0); scenes.Add(SpinningMark.PictureScene); }
            SpinningMark.CacheFolder = Path.Combine(Path.GetTempPath(), "arabara-soak-home-cache");
            AudioMeter.TestBands = new float[AudioMeter.BandCount];
            // --tag <name>: soak-home-<name>.csv, so two runs can go side by side.
            var commandLine = Environment.GetCommandLineArgs();
            int tagArg = Array.IndexOf(commandLine, "--tag");
            string file = Path.Combine(SelfTests.ResultsRoot, "soak-home" + (tagArg > 0 && tagArg + 1 < commandLine.Length ? "-" + commandLine[tagArg + 1] : "") + ".csv");
            var csv = new StringBuilder();
            csv.AppendLine("# soak home, " + minutes + " min, scenes " + string.Join(" ", scenes.Select(s => SpinningMark.SceneNames[s])) + ", started " + DateTime.Now.ToString("s"));
            csv.AppendLine("minute,scene,host_pws_mb,host_commit_mb,host_managed_mb,host_gdi,host_user,host_handles,frame_ms,frames");
            File.WriteAllText(file, csv.ToString());
            var size = new System.Drawing.Size(1280, 900);
            var clock = Stopwatch.StartNew();
            using (var mark = new SpinningMark())
            using (var frame = new System.Drawing.Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
            using (var g = System.Drawing.Graphics.FromImage(frame))
            {
                int scene = -1, frames = 0, released = 0;
                double frameMs = 0;
                for (int minute = 0; minute <= minutes; )
                {
                    double t = clock.Elapsed.TotalSeconds;
                    int wanted = scenes[(int)(t / 900) % scenes.Count];
                    if (wanted != scene) { scene = wanted; SpinningMark.SetScene(scene, false); }
                    // A kick on every beat at 128 BPM: the low bands jump and fall back.
                    double beat = (t * 128 / 60) % 1;
                    for (int b = 0; b < AudioMeter.BandCount; b++)
                        AudioMeter.TestBands[b] = (float)(b < 8 ? Math.Max(0.2, 1 - beat * 3) : 0.25 + 0.2 * Math.Sin(t * 2 + b));
                    var watch = Stopwatch.StartNew();
                    mark.Render(g, size, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size), t, 1F, UiPalette.Canvas, new System.Drawing.Rectangle[0], new int[0], new int[0], 0,
                        new System.Drawing.Rectangle(14, 40, 240, 820), new System.Drawing.Rectangle(1026, 40, 240, 820));
                    frameMs += watch.Elapsed.TotalMilliseconds;
                    frames++;
                    if ((int)(t / 300) > released) { released = (int)(t / 300); mark.Release(); }
                    if (t >= minute * 60)
                    {
                        long pws, commit;
                        using (var host = Process.GetCurrentProcess())
                        {
                            ProcessMemory.Read(host.Id, out pws, out commit);
                            Func<long, string> mb = v => (v / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                            csv.AppendLine(string.Join(",", new[] { minute.ToString(), SpinningMark.SceneNames[scene].Replace(",", " "), mb(pws), mb(commit), mb(GC.GetTotalMemory(false)),
                                GetGuiResources(host.Handle, 0).ToString(), GetGuiResources(host.Handle, 1).ToString(), host.HandleCount.ToString(),
                                (frames > 0 ? frameMs / frames : 0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture), frames.ToString() }));
                        }
                        File.WriteAllText(file, csv.ToString());
                        frames = 0; frameMs = 0;
                        minute++;
                    }
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(Math.Max(1, 50 - (int)watch.ElapsedMilliseconds));
                }
            }
            AudioMeter.TestBands = null;
            return 0;
        }

        private static async Task SkipToEndsAsync(MusicEngine engine, int minutes)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalMinutes < minutes)
            {
                await Task.Delay(20000);
                // Without a user gesture, so the next track starts as after a track played to its end.
                if (engine.Playing && !engine.Advertising && engine.Duration > 30 && engine.Position < engine.Duration - 10)
                    try { await engine.EvaluateAsync("__arabara.seek(" + ((int)engine.Duration - 6) + ")", false); }
                    catch (TimeoutException) { }
            }
        }

        // --own-audio-test: a page in Arabara's own WebView2 plays a very quiet tone (-66 dB)
        // while the capture hears only Arabara (AudioMeter.OwnAudio): it must hear the tone,
        // then silence once the tone stops. Writes test-results\own-audio-test.txt.
        public static int OwnAudioTest()
        {
            var log = new StringBuilder();
            int code = 1;
            UiFonts.Initialize();
            try
            {
                using (var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), ClientSize = new System.Drawing.Size(400, 300) })
                {
                    form.Shown += async delegate
                    {
                        try
                        {
                            string profile = Path.Combine(Path.GetTempPath(), "arabara-own-audio-test");
                            var environment = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                            var view = new WebView2 { Dock = DockStyle.Fill };
                            form.Controls.Add(view);
                            await view.EnsureCoreWebView2Async(environment);
                            // --own-audio-self: this process's tree instead of WebView2's browser process.
                            AudioMeter.OwnAudioProcess = Environment.GetCommandLineArgs().Contains("--own-audio-self") ? 0 : (int)view.CoreWebView2.BrowserProcessId;
                            log.AppendLine("Heard process: " + (AudioMeter.OwnAudioProcess == 0 ? "this one" : "WebView2 browser " + AudioMeter.OwnAudioProcess));
                            AudioMeter.OwnAudio = true;
                            Func<int, Task> listen = async delegate(int ms)
                            {
                                var watch = Stopwatch.StartNew();
                                while (watch.ElapsedMilliseconds < ms) { AudioMeter.Read(); await Task.Delay(100); }
                            };
                            await listen(1500); // the capture opens first
                            AudioMeter.TestFramesHeard = 0; AudioMeter.TestPeak = 0;
                            view.CoreWebView2.NavigateToString("<script>const c = new AudioContext(); const o = c.createOscillator(); const g = c.createGain(); g.gain.value = 0.0005; o.frequency.value = 440; o.connect(g).connect(c.destination); o.start(); window.c = c;</script>");
                            await listen(3000);
                            long heard = AudioMeter.TestFramesHeard; float peak = AudioMeter.TestPeak;
                            log.AppendLine("Page audio: " + await view.CoreWebView2.ExecuteScriptAsync("window.c ? window.c.state + ' at ' + window.c.currentTime.toFixed(1) + ' s, ' + window.c.sampleRate + ' Hz' : 'no context'") +
                                ", document playing audio: " + view.CoreWebView2.IsDocumentPlayingAudio);
                            // The same tone through the speakers' loopback, for comparison.
                            AudioMeter.OwnAudio = false;
                            await listen(1500);
                            AudioMeter.TestFramesHeard = 0; AudioMeter.TestPeak = 0;
                            await listen(1500);
                            log.AppendLine("Speakers' loopback meanwhile: " + AudioMeter.TestFramesHeard + " frames with sound, peak " + AudioMeter.TestPeak.ToString("F5", System.Globalization.CultureInfo.InvariantCulture));
                            AudioMeter.OwnAudio = true;
                            await listen(1500);
                            log.AppendLine("Own audio available: " + AudioMeter.OwnAudioAvailable + (AudioMeter.LastError != null ? " (" + AudioMeter.LastError + ")" : ""));
                            log.AppendLine("While the tone played: " + heard + " frames with sound, peak " + peak.ToString("F5", System.Globalization.CultureInfo.InvariantCulture));
                            await view.CoreWebView2.ExecuteScriptAsync("window.c && window.c.close()");
                            await listen(800);
                            AudioMeter.TestFramesHeard = 0; AudioMeter.TestPeak = 0;
                            await listen(2000);
                            log.AppendLine("After it stopped: " + AudioMeter.TestFramesHeard + " frames with sound, peak " + AudioMeter.TestPeak.ToString("F5", System.Globalization.CultureInfo.InvariantCulture));
                            bool ok = AudioMeter.OwnAudioAvailable && heard > 48000 && peak > 0.0002F && AudioMeter.TestPeak < peak / 4;
                            log.AppendLine(ok ? "PASS: Arabara's own sound is heard, and only while it plays." : "FAIL");
                            code = ok ? 0 : 1;
                            AudioMeter.OwnAudio = false;
                            view.Dispose();
                        }
                        catch (Exception ex) { log.AppendLine("ERROR " + ex); }
                        finally { SelfTests.WriteResult("own-audio-test.txt", log.ToString()); form.Close(); }
                    };
                    Application.Run(form);
                }
            }
            finally { UiFonts.Shutdown(); }
            return code;
        }

        private static string Sample(CoreWebView2Environment environment, int minute, Dictionary<string, object> page, bool playing, bool ad, string track, double position, string note)
        {
            long totalPws = 0, totalCommit = 0, browser = 0, gpu = 0, renderer = 0, rendererMax = 0, utility = 0, hostCommit = 0, pws, commit;
            int renderers = 0;
            using (var host = Process.GetCurrentProcess())
            {
                if (ProcessMemory.Read(host.Id, out pws, out commit)) { totalPws += pws; totalCommit += commit; hostCommit = commit; }
                foreach (var info in environment.GetProcessInfos())
                {
                    if (!ProcessMemory.Read(info.ProcessId, out pws, out commit)) continue;
                    totalPws += pws; totalCommit += commit;
                    switch (info.Kind)
                    {
                        case CoreWebView2ProcessKind.Browser: browser += commit; break;
                        case CoreWebView2ProcessKind.Gpu: gpu += commit; break;
                        case CoreWebView2ProcessKind.Renderer: renderer += commit; renderers++; rendererMax = Math.Max(rendererMax, commit); break;
                        default: utility += commit; break;
                    }
                }
                Func<long, string> mb = v => (v / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                return string.Join(",", new[]
                {
                    minute.ToString(), mb(totalPws), mb(totalCommit), mb(hostCommit), mb(GC.GetTotalMemory(false)),
                    GetGuiResources(host.Handle, 0).ToString(), GetGuiResources(host.Handle, 1).ToString(),
                    mb(browser), mb(gpu), mb(renderer), mb(rendererMax), renderers.ToString(), mb(utility),
                    mb((long)Number(page, "heap")), ((long)Number(page, "nodes")).ToString(),
                    playing ? "1" : "0", ad ? "1" : "0", track ?? "", position.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                    "\"" + note.Replace("\"", "'") + "\""
                });
            }
        }

        private static string Unquote(string json)
        {
            return json == null || json == "null" ? null : new JavaScriptSerializer().Deserialize<string>(json);
        }

        private static bool Flag(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static string Text(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value != null ? value.ToString() : null;
        }

        private static double Number(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return 0;
            try { return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch (FormatException) { return 0; }
            catch (InvalidCastException) { return 0; }
        }
    }
}
