using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Arabara
{
    // Per-process memory readings. Private working set is the "Memory" column in
    // Task Manager; commit (private bytes) is reported alongside it.
    internal static class ProcessMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Counters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
                PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

        // Returns false when the process has exited or cannot be read.
        public static bool Read(int processId, out long privateWorkingSet, out long commit)
        {
            privateWorkingSet = commit = 0;
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    var counters = new Counters { cb = (uint)Marshal.SizeOf(typeof(Counters)) };
                    if (K32GetProcessMemoryInfo(process.Handle, ref counters, counters.cb))
                    {
                        privateWorkingSet = (long)counters.PrivateWorkingSetSize.ToUInt64();
                        commit = (long)counters.PrivateUsage.ToUInt64();
                    }
                    else { privateWorkingSet = process.WorkingSet64; commit = process.PrivateMemorySize64; }
                    return true;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
    }

    // --memory-bench <variant>: loads YouTube, YouTube Music and Discord in an
    // isolated throwaway profile with one engine flag set, then records memory.
    // --pages a,b,c loads other pages; "ubol" variants add the unpacked extension
    // folder given after --extension (uBlock Origin Lite) and check that it blocks.
    internal static class MemoryBench
    {
        private static string[] Pages =
        {
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "https://music.youtube.com/",
            "https://discord.com/login"
        };

        private static string extension;
        private static bool navigate, keepAlive;
        private static string[] rulesets;

        // Whether a page's requests to an ad server and a tracker (blocked outright by EasyList,
        // EasyPrivacy and Peter Lowe's list) get through, and whether YouTube shows an ad.
        private const string AdProbe = "Promise.all(['https://ib.adnxs.com/ut/v3', 'https://sb.scorecardresearch.com/b?c1=2'].map(u => fetch(u, { mode: 'no-cors', cache: 'no-store' }).then(() => 'loaded', e => 'blocked'))).then(r => r.join(', '))";
        private const string AdShowing = "!!document.querySelector('#movie_player.ad-showing, #movie_player.ad-interrupting')";

        public static int Run(string[] args)
        {
            int at = Array.IndexOf(args, "--memory-bench");
            string variant = at >= 0 && at + 1 < args.Length ? args[at + 1] : "baseline";
            int run = at + 2 < args.Length ? int.Parse(args[at + 2]) : 0;
            bool pageLevel = variant == "baseline" || variant == "strict" || variant == "h264" || variant == "strict-h264" || variant.Contains("ubol");
            string flags = pageLevel ? "" : EngineFlags.Variant(variant);
            // "ubol" variants load the unpacked extension folder given after --extension.
            int ext = Array.IndexOf(args, "--extension");
            extension = ext >= 0 && ext + 1 < args.Length ? args[ext + 1] : null;
            // --pages a,b,c: another set of pages (the first one visible).
            int list = Array.IndexOf(args, "--pages");
            if (list >= 0 && list + 1 < args.Length) Pages = args[list + 1].Split(',');
            navigate = args.Contains("--navigate");
            keepAlive = args.Contains("--keepalive");
            // --rulesets a,b: the extension's lists turned on by "ubolvn" variants (default: Arabara's).
            int sets = Array.IndexOf(args, "--rulesets");
            rulesets = sets >= 0 && sets + 1 < args.Length ? args[sets + 1].Split(',') : AdBlockerPage.ExtraRulesets;
            if (flags == null) { SelfTests.WriteResult("memory-bench-" + variant + ".txt", "Unknown variant"); return 2; }
            UiFonts.Initialize();
            int code = 1;
            try
            {
                using (var form = new Form { Text = "Arabara memory bench", ClientSize = new Size(1280, 720), ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) })
                {
                    form.Shown += async delegate
                    {
                        try { await MeasureAsync(form, variant, flags, run); code = 0; }
                        catch (Exception ex) { SelfTests.WriteResult("memory-bench-" + variant + ".txt", ex.ToString()); }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
            }
            finally { UiFonts.Shutdown(); }
            return code;
        }

        private static async Task MeasureAsync(Form form, string variant, string flags, int run)
        {
            string profile = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".bench-profile", variant));
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
            // Autoplay is allowed and window occlusion ignored only in this benchmark so
            // the invisible bench window plays the same video for every variant.
            string benchFlags = flags.Replace("--disable-features=", "--disable-features=CalculateNativeWinOcclusion,");
            if (benchFlags == flags) benchFlags += " --disable-features=CalculateNativeWinOcclusion";
            var options = new CoreWebView2EnvironmentOptions((benchFlags + " --autoplay-policy=no-user-gesture-required").Trim());
            options.Language = "en-US";
            bool withExtension = variant.Contains("ubol");
            options.AreBrowserExtensionsEnabled = withExtension;
            var environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
            if (navigate) { await MeasureBrowsingAsync(form, environment, variant, flags, run); return; }
            if (keepAlive) { await MeasureKeepAliveAsync(form, environment, variant, run); return; }
            var views = new List<WebView2>();
            string extensionNote = "(none)";
            foreach (string url in Pages)
            {
                var view = new WebView2 { Dock = DockStyle.Fill, Visible = views.Count == 0 };
                form.Controls.Add(view);
                await view.EnsureCoreWebView2Async(environment);
                if (withExtension && views.Count == 0)
                {
                    var added = await view.CoreWebView2.Profile.AddBrowserExtensionAsync(extension);
                    extensionNote = added.Name + " " + added.Id + " enabled=" + added.IsEnabled;
                    await Task.Delay(3000); // its service worker turns the rulesets on
                    if (variant.Contains("ubolvn"))
                        extensionNote += ", extra lists: " + await AdBlockerPage.RunAsync(form, environment, added.Id, AdBlockerPage.EnableRulesetsScript(rulesets)) +
                            " (" + String.Join(", ", rulesets) + ")";
                }
                view.CoreWebView2.IsMuted = true;
                if (variant.Contains("strict")) view.CoreWebView2.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
                if (variant.Contains("h264")) await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PageTweaks.H264Only);
                view.CoreWebView2.Navigate(url);
                views.Add(view);
            }
            for (int i = 1; i < views.Count; i++) views[i].CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            var log = new StringBuilder();
            log.AppendLine("Variant: " + variant);
            log.AppendLine("Flags: " + (flags.Length == 0 ? "(none)" : flags));
            log.AppendLine("Pages: " + string.Join(", ", Pages) + " (first visible and playing muted; others hidden at Low target)");
            log.AppendLine("Extension: " + extensionNote);
            await Task.Delay(12000);
            await views[0].CoreWebView2.ExecuteScriptAsync("(document.querySelector('video')||{play(){}}).play()");
            int adChecks = 0, adsSeen = 0;
            for (int s = 0; s < 18; s++)
            {
                await Task.Delay(1000);
                adChecks++;
                if (await views[0].CoreWebView2.ExecuteScriptAsync(AdShowing) == "true") adsSeen++;
            }
            log.AppendLine("YouTube ad on screen: " + adsSeen + " of " + adChecks + " checks (1 s apart)");
            var probe = new TaskCompletionSource<string>();
            views[0].CoreWebView2.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e) { probe.TrySetResult(e.TryGetWebMessageAsString()); };
            await views[0].CoreWebView2.ExecuteScriptAsync(AdProbe + ".then(r => chrome.webview.postMessage(r))");
            log.AppendLine("Ad server request: " + (await Task.WhenAny(probe.Task, Task.Delay(10000)) == probe.Task ? probe.Task.Result : "no answer"));
            bool playing = views[0].CoreWebView2.IsDocumentPlayingAudio;
            string time = await views[0].CoreWebView2.ExecuteScriptAsync("(document.querySelector('video')||{}).currentTime");
            log.AppendLine("Video playing: " + playing + " (currentTime " + time + ")");
            foreach (var view in views) log.AppendLine("Title: " + view.CoreWebView2.DocumentTitle);
            log.AppendLine("Video: " + await views[0].CoreWebView2.ExecuteScriptAsync(
                "(() => { const p = document.getElementById('movie_player'); const s = p && p.getStatsForNerds ? p.getStatsForNerds() : {}; const v = document.querySelector('video'); " +
                "const q = v && v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality() : {}; return (s.codecs || '?') + ' ' + (v ? v.videoWidth + 'x' + v.videoHeight : '') + ' dropped=' + (q.droppedVideoFrames || 0) + '/' + (q.totalVideoFrames || 0); })()"));
            await SampleAsync(environment, log, "All tabs open");
            // What Low RAM mode's tab freezing releases: dispose the two hidden tabs.
            for (int i = 1; i < views.Count; i++) views[i].Dispose();
            await Task.Delay(8000);
            await SampleAsync(environment, log, "Hidden tabs frozen");
            SelfTests.WriteResult("memory-bench-" + variant + (run > 0 ? "-" + run : "") + ".txt", log.ToString());
            views[0].Dispose();
        }

        // --navigate: one tab opens the pages one after another, as when browsing, then the
        // memory held is measured (pages kept for the back button stay in it), and the time a
        // step back takes.
        private static async Task MeasureBrowsingAsync(Form form, CoreWebView2Environment environment, string variant, string flags, int run)
        {
            var view = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(view);
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.IsMuted = true;
            var log = new StringBuilder();
            log.AppendLine("Variant: " + variant + " (browsing in one tab)");
            log.AppendLine("Flags: " + (flags.Length == 0 ? "(none)" : flags));
            log.AppendLine("Pages: " + string.Join(", ", Pages) + " (one after another, 6 s on each)");
            foreach (string url in Pages)
            {
                await LoadAsync(view.CoreWebView2, url);
                await Task.Delay(6000);
            }
            await SampleAsync(environment, log, "After " + Pages.Length + " pages");
            var watch = Stopwatch.StartNew();
            var back = LoadAsync(view.CoreWebView2, null);
            view.CoreWebView2.GoBack();
            bool loaded = await back;
            log.AppendLine("Back one page: " + watch.ElapsedMilliseconds + " ms" + (loaded ? "" : " (no completed navigation)"));
            SelfTests.WriteResult("memory-bench-" + variant + (run > 0 ? "-" + run : "") + ".txt", log.ToString());
            view.Dispose();
        }

        // --keepalive: what a kept-alive tab holds, on the first page: drawn at the Normal memory
        // target (Keep alive before, as the tab shown with another app in front), not drawn at
        // Normal (before, as a tab in the background), then not drawn at the Low target (Keep
        // alive since October 2026, out of sight); and whether its script still runs.
        private static async Task MeasureKeepAliveAsync(Form form, CoreWebView2Environment environment, string variant, int run)
        {
            var view = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(view);
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.IsMuted = true;
            var log = new StringBuilder();
            log.AppendLine("Variant: " + variant + " (one kept-alive tab)");
            log.AppendLine("Page: " + Pages[0]);
            await LoadAsync(view.CoreWebView2, Pages[0]);
            await view.CoreWebView2.ExecuteScriptAsync("window.__ticks = 0; setInterval(() => window.__ticks++, 1000)");
            await Task.Delay(15000);
            await SampleAsync(environment, log, "Drawn, Normal target");
            view.Visible = false;
            await Task.Delay(20000);
            await SampleAsync(environment, log, "Not drawn, Normal target");
            view.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            string before = await view.CoreWebView2.ExecuteScriptAsync("window.__ticks");
            await Task.Delay(20000);
            await SampleAsync(environment, log, "Not drawn, Low target");
            log.AppendLine("Script ticks at Low over the last 30 s: " + before + " -> " + await view.CoreWebView2.ExecuteScriptAsync("window.__ticks"));
            SelfTests.WriteResult("memory-bench-" + variant + "-keepalive" + (run > 0 ? "-" + run : "") + ".txt", log.ToString());
            view.Dispose();
        }

        // Navigates (or, without a URL, waits for the navigation started next) until it completes.
        private static async Task<bool> LoadAsync(CoreWebView2 core, string url)
        {
            var done = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) { done.TrySetResult(e.IsSuccess); };
            core.NavigationCompleted += handler;
            try
            {
                if (url != null) core.Navigate(url);
                return await Task.WhenAny(done.Task, Task.Delay(20000)) == done.Task;
            }
            finally { core.NavigationCompleted -= handler; }
        }

        private static double CpuSeconds(CoreWebView2Environment environment)
        {
            double total = 0;
            var ids = new List<int> { Process.GetCurrentProcess().Id };
            foreach (var info in environment.GetProcessInfos()) ids.Add(info.ProcessId);
            foreach (int id in ids)
            {
                try { using (var process = Process.GetProcessById(id)) total += process.TotalProcessorTime.TotalSeconds; }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return total;
        }

        private static async Task SampleAsync(CoreWebView2Environment environment, StringBuilder log, string label)
        {
            double cpuStart = CpuSeconds(environment);
            var cpuWatch = Stopwatch.StartNew();
            long totalPws = 0, totalCommit = 0;
            int samples = 3, processes = 0;
            var byKind = new Dictionary<string, long>();
            for (int s = 0; s < samples; s++)
            {
                if (s > 0) await Task.Delay(5000);
                var ids = new List<KeyValuePair<int, string>> { new KeyValuePair<int, string>(Process.GetCurrentProcess().Id, "Host") };
                foreach (var info in environment.GetProcessInfos()) ids.Add(new KeyValuePair<int, string>(info.ProcessId, info.Kind.ToString()));
                processes = ids.Count;
                foreach (var id in ids)
                {
                    long pws, commit;
                    if (!ProcessMemory.Read(id.Key, out pws, out commit)) continue;
                    totalPws += pws; totalCommit += commit;
                    long sum; byKind.TryGetValue(id.Value, out sum); byKind[id.Value] = sum + pws;
                }
            }
            log.AppendLine(label + " / processes: " + processes + string.Format("  //  CPU {0:N1}% of one core", (CpuSeconds(environment) - cpuStart) / cpuWatch.Elapsed.TotalSeconds * 100));
            log.AppendLine(string.Format("Private working set: {0:N1} MiB  ·  Commit: {1:N1} MiB  (average of {2} samples)",
                totalPws / samples / 1048576.0, totalCommit / samples / 1048576.0, samples));
            foreach (var kind in byKind.OrderByDescending(k => k.Value))
                log.AppendLine(string.Format("  {0,-22} {1,8:N1} MiB", kind.Key, kind.Value / samples / 1048576.0));
        }
    }
}
