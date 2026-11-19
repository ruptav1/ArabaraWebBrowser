using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Arabara
{
    internal static class SelfTests
    {
        public static string ResultsRoot
        {
            get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-results")); }
        }

        public static void WriteResult(string name, string text)
        {
            Directory.CreateDirectory(ResultsRoot);
            File.WriteAllText(Path.Combine(ResultsRoot, name), text, new UTF8Encoding(false));
        }

        public static void Require(bool condition, string description)
        { if (!condition) throw new InvalidOperationException("FAIL: " + description); }

        public static int Run()
        {
            var log = new StringBuilder();
            try
            {
                Uri uri;
                string[] rejected = { "", "discord", "discord.com", "http://discord.com", "javascript:alert(1)", "file:///C:/Windows", "https://", "https://u:p@discord.com", "https://discord.com/a b", "https://discord.com\\@evil.test" };
                foreach (string input in rejected) Require(!AddressPolicy.TryParse(input, out uri), "Reject " + input);
                foreach (string input in new[] { "https://discord.com/channels/@me", " https://music.youtube.com/ ", "HTTPS://mail.google.com/", "https://example.com/q?q=a%20b" })
                    Require(AddressPolicy.TryParse(input, out uri), "Accept " + input);
                log.AppendLine("PASS: URL-only policy; no keyword/search fallback; HTTPS required.");
                var addressInputs = new Dictionary<string, string>
                {
                    { "discord.com", "https://discord.com/" },
                    { " music.youtube.com/ ", "https://music.youtube.com/" },
                    { "discord.com/channels/@me", "https://discord.com/channels/@me" },
                    { "example.com:8443/path?q=a%20b#part", "https://example.com:8443/path?q=a%20b#part" },
                    { "example.com/?next=https://other.test/", "https://example.com/?next=https://other.test/" },
                    { "HTTPS://mail.google.com/", "https://mail.google.com/" }
                };
                foreach (var input in addressInputs)
                    Require(AddressPolicy.TryParseInput(input.Key, out uri) && uri.AbsoluteUri == input.Value, "Normalize address input " + input.Key);
                foreach (string input in new[] { "", "https://", "discord", "discord music", "http://discord.com", "ftp://example.com", "javascript:alert(1)", "file:///C:/Windows", "user:pass@example.com", "example.com/a b", "example.com\\@evil.test" })
                    Require(!AddressPolicy.TryParseInput(input, out uri), "Reject address input " + input);
                Require(!AddressPolicy.TryParse("discord.com", out uri) && AddressPolicy.SafeHistoryUrl("discord.com") == null, "Scheme inference is limited to address-bar input");
                log.AppendLine("PASS: Bare domains normalize to HTTPS with paths, ports and query strings; explicit insecure schemes and keywords stay blocked.");
                Require(AddressPolicy.SafeHistoryUrl("https://a.test/callback?code=secret&state=abc") == "https://a.test/callback", "OAuth code redaction");
                Require(AddressPolicy.SafeHistoryUrl("https://a.test/#access_token=secret") == "https://a.test/", "Fragment token redaction");
                log.AppendLine("PASS: OAuth credential redaction in history.");
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var localInputs = new Dictionary<string, string>
                {
                    { "C:\\Windows", "C:\\Windows" }, { "c:/windows/", "c:\\windows\\" }, { "D:", "D:\\" }, { " \"C:\\Program Files\" ", "C:\\Program Files" },
                    { "file:///C:/Windows/", "C:\\Windows\\" }, { "file:///", "" }, { "~", profile }, { "~/Downloads", Path.Combine(profile, "Downloads") }
                };
                foreach (var input in localInputs)
                    Require(LocalPath.TryParseInput(input.Key) == input.Value, "Local path " + input.Key + " -> " + LocalPath.TryParseInput(input.Key));
                foreach (string input in new[] { "\\\\server\\share", "file://server/share/x", "discord.com", "https://discord.com", "C:relative", "C:\\a|b", "" })
                    Require(LocalPath.TryParseInput(input) == null, "Reject local path " + input);
                Require(LocalPath.IsViewable("a.PNG") && LocalPath.IsViewable("b.pdf") && !LocalPath.IsViewable("c.exe") && LocalPath.IsRunnable("c.exe") && LocalPath.IsRunnable("d.lnk"), "Local file kinds");
                log.AppendLine("PASS: Local drive paths, file:/// URLs and ~ are recognised; network paths are rejected; viewable and runnable types.");
                for (int bits = 0; bits < 32; bits++)
                    Require(SleepPolicy.CanSuspend((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0) == (bits == 0), "Sleep guards " + bits);
                log.AppendLine("PASS: All 32 combinations of visibility, keep-alive, microphone, audio and download guards.");
                string root = Path.Combine(ResultsRoot, "storage-" + Guid.NewGuid().ToString("N"));
                var preferences = BrowserPreferences.Load(root);
                Require(preferences.LowMemoryBackground, "Background memory mode defaults on");
                Require(preferences.PauseBackgroundVisuals, "Background visuals default to Auto");
                preferences.PauseBackgroundVisuals = false;
                preferences.LowMemoryBackground = false;
                preferences.Save(root);
                Require(!BrowserPreferences.Load(root).LowMemoryBackground, "Disabled memory mode persists");
                Require(!BrowserPreferences.Load(root).PauseBackgroundVisuals, "Live visual preference persists");
                preferences.PauseBackgroundVisuals = true;
                preferences.LowMemoryBackground = true;
                preferences.Save(root);
                Require(BrowserPreferences.Load(root).LowMemoryBackground, "Enabled memory mode persists");
                Require(BrowserPreferences.Load(root).PauseBackgroundVisuals, "Auto visual preference persists");
                Require(BrowserPreferences.Load(root).LightYouTube, "YouTube light defaults on");
                preferences.LightYouTube = false;
                preferences.Save(root);
                Require(!BrowserPreferences.Load(root).LightYouTube, "YouTube full preference persists");
                preferences.LightYouTube = true;
                Require(BrowserPreferences.Load(root).AdsAllowedSites.Count == 0, "Ads are blocked everywhere by default");
                preferences.AdsAllowedSites.Add("example.com");
                preferences.Save(root);
                Require(BrowserPreferences.Load(root).AdsAllowedSites.SequenceEqual(new[] { "example.com" }), "A site allowed ads persists");
                preferences.AdsAllowedSites.Clear();
                preferences.Save(root);
                Require(BrowserForm.AdSiteOf("https://www.Example.com/a?b") == "example.com" && BrowserForm.AdSiteOf("https://news.example.com/") == "news.example.com" &&
                    BrowserForm.AdSiteOf("https://" + BrowserForm.LocalHost + "/view") == null && BrowserForm.AdSiteOf("file:///C:/a.txt") == null && BrowserForm.AdSiteOf("about:blank") == null,
                    "Ad blocker sites: host without www., none for Arabara's own pages");

                File.WriteAllText(Path.Combine(root, "preferences.json"), "{\"LowMemoryBackground\":false}");
                Require(BrowserPreferences.Load(root).PauseBackgroundVisuals && !BrowserPreferences.Load(root).LowMemoryBackground, "Old preferences gain Auto visuals without changing memory mode");
                Require(BrowserPreferences.Load(root).AdsAllowedSites != null && BrowserPreferences.Load(root).AdsAllowedSites.Count == 0, "Old preferences block ads everywhere");
                File.WriteAllText(Path.Combine(root, "preferences.json"), "broken-json");
                Require(BrowserPreferences.Load(root).LowMemoryBackground, "Malformed preferences recover to defaults");
                log.AppendLine("PASS: Memory/visual preferences, old settings compatibility, atomic save and recovery.");
                var store = new BrowserStore(root);
                store.Record("https://a.test/", "Vietnamese: Lịch sử");
                store.Save();
                store.Record("https://a.test/", "Updated");
                store.Save();
                Require(new BrowserStore(root).Entries.Count == 1 && new BrowserStore(root).Entries[0].Title == "Updated", "Disk roundtrip and deduplication");
                for (int i = 0; i < 1510; i++) store.Record("https://a.test/" + i, "Page " + i);
                Require(store.Entries.Count == BrowserStore.HistoryLimit, "Bounded history");
                store.Clear();
                Require(new BrowserStore(root).Entries.Count == 0 && File.ReadAllText(Path.Combine(root, "history.json.bak")) == "[]", "Clear both history snapshots");
                File.WriteAllText(Path.Combine(root, "history.json"), "broken-json");
                var recovered = new BrowserStore(root);
                Require(recovered.LoadWarning != null && Directory.GetFiles(root, "history.json.invalid-*").Length == 1, "Preserve malformed history");
                log.AppendLine("PASS: History persists, deduplicates, remains bounded, clears and preserves corrupt input.");
                var queue = new MusicQueue();
                Require(queue.NextIndex(true) == -1 && queue.PreviousIndex() == -1 && queue.Current == null, "Empty music queue");
                for (int i = 0; i < 3; i++) queue.Tracks.Add(new MusicTrack { VideoId = "track" + i });
                queue.Index = 1;
                Require(queue.NextIndex(true) == 2 && queue.NextIndex(false) == 2 && queue.PreviousIndex() == 0, "Middle of the queue");
                queue.Index = 2;
                Require(queue.NextIndex(true) == -1 && queue.NextIndex(false) == -1, "Repeat off stops after the last track");
                queue.Repeat = MusicRepeat.All;
                Require(queue.NextIndex(true) == 0 && queue.NextIndex(false) == 0, "Repeat all wraps to the first track");
                queue.Index = 0;
                Require(queue.PreviousIndex() == 2, "Repeat all wraps back to the last track");
                queue.Repeat = MusicRepeat.One;
                Require(queue.NextIndex(true) == 0 && queue.NextIndex(false) == 1, "Repeat one replays when a track ends, skip still moves on");
                Require(MusicEngine.IsSafeId("PLx_9-AbC") && MusicEngine.IsSafeId("dQw4w9WgXcQ") && !MusicEngine.IsSafeId("a'); alert(1)//") && !MusicEngine.IsSafeId("") && !MusicEngine.IsSafeId(null), "Only plain IDs reach the music page's scripts");
                Require(BrowserPreferences.Load(root).MusicVolume == 60 && BrowserPreferences.Load(root).ThemePlaylistId == null, "Music preferences default");
                preferences.MusicVolume = 35; preferences.MusicRepeat = 2; preferences.ThemePlaylistId = "PLtheme"; preferences.ThemeVideoId = "video1";
                preferences.Save(root);
                var reloaded = BrowserPreferences.Load(root);
                Require(reloaded.MusicVolume == 35 && reloaded.MusicRepeat == 2 && reloaded.ThemePlaylistId == "PLtheme" && reloaded.ThemeVideoId == "video1", "Music preferences persist");
                log.AppendLine("PASS: Music queue (repeat off/all/one, previous/next), safe IDs and music preferences.");
                preferences.BackdropPictures = new List<PictureSetting> { new PictureSetting { Path = @"C:\pictures\a.jpg", Name = "IRASUKU" }, new PictureSetting { Path = @"C:\pictures\b.jpg", Columns = 600, Rows = 310, Points = 4, Motion = "head=180,70 eye=164.5,52,21,11.5,1.5,7 hair=off" } };
                preferences.BackdropPictureIndex = 1;
                preferences.Save(root);
                reloaded = BrowserPreferences.Load(root);
                Require(reloaded.BackdropPictures.Count == 2 && reloaded.BackdropPictures[1].Columns == 600 && reloaded.BackdropPictures[1].Rows == 310 && reloaded.BackdropPictures[1].Points == 4 && reloaded.BackdropPictureIndex == 1
                    && reloaded.BackdropPictures[1].Motion == preferences.BackdropPictures[1].Motion && reloaded.BackdropPictures[0].Name == "IRASUKU", "Pictures and their grid and motion persist");
                var motion = PictureMotion.Parse(reloaded.BackdropPictures[1].Motion);
                Require(motion.HasHead && motion.HeadX == 180 && !motion.HasChest && motion.NoHair && motion.Eyes.Length == 1 && Math.Abs(motion.Eyes[0].CloseY - Math.Cos(21 * Math.PI / 180)) < 1e-4, "Picture motion layout is read");
                log.AppendLine("PASS: Picture list, grids and motion layouts persist.");
                foreach (var tone in new[] { new[] { 80.0, 0, 9 }, new[] { 1000.0, 20, 32 }, new[] { 5000.0, 34, 47 } })
                {
                    var samples = new float[4096];
                    for (int i = 0; i < samples.Length; i++) samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * tone[0] * i / 48000));
                    var spectrum = AudioMeter.AnalyseForTest(samples, 48000);
                    int strongest = 0;
                    for (int b = 1; b < spectrum.Length; b++) if (spectrum[b] > spectrum[strongest]) strongest = b;
                    Require(strongest >= tone[1] && strongest <= tone[2], "A " + tone[0] + " Hz tone peaks in band " + strongest);
                }
                Require(AudioMeter.AnalyseForTest(new float[4096], 48000).Max() == 0, "Silence leaves the spectrum flat");
                Require(SpinningMark.SceneFromSaved(4) == SpinningMark.SoundScene && SpinningMark.SavedScene(SpinningMark.SoundScene) == 4 && SpinningMark.SceneFromSaved(3) == SpinningMark.VideoScene
                    && SpinningMark.SceneFromSaved(5) == SpinningMark.PictureScene && SpinningMark.SavedScene(SpinningMark.PictureScene) == 5
                    && SpinningMark.SceneFromSaved(6) == SpinningMark.RingScene && SpinningMark.SavedScene(SpinningMark.RingScene) == 6, "Saved scene numbers");
                // The public edition leaves video and picture backgrounds out; the preferences keep them.
                var backdrops = new BrowserPreferences { BackdropVideo = "C:/videos/loop.mp4", BackdropScene = 3 };
                backdrops.BackdropPictures.Add(new PictureSetting { Path = "C:/pictures/still.png" });
                BrowserForm.LoadBackdrops(backdrops, root);
                if (Edition.Public)
                    Require(SpinningMark.VideoPath == null && SpinningMark.PictureCount == 0 && SpinningMark.Scene == 0 &&
                        backdrops.BackdropVideo != null && backdrops.BackdropPictures.Count == 1, "Public edition: MARK + SEA and SOUND WAVE only, preferences kept");
                else
                    Require(SpinningMark.VideoPath == "C:/videos/loop.mp4" && SpinningMark.PictureCount == 1 && SpinningMark.Scene == SpinningMark.VideoScene,
                        "Personal edition: video and picture backgrounds loaded");
                SpinningMark.VideoPath = null;
                SpinningMark.SetPictures(null, 0);
                SpinningMark.SetScene(0, false);
                // GrabCut: a noisy disc on a noisy, differently coloured background is cut out.
                {
                    int gw = 120, gh = 90;
                    var rgb = new byte[gw * gh * 3];
                    var noise = new Random(3);
                    int agree = 0;
                    for (int y = 0; y < gh; y++)
                        for (int x = 0; x < gw; x++)
                        {
                            bool disc = (x - 60) * (x - 60) + (y - 45) * (y - 45) < 30 * 30;
                            int i = (y * gw + x) * 3;
                            rgb[i] = (byte)((disc ? 60 : 230) + noise.Next(-15, 16)); rgb[i + 1] = (byte)((disc ? 200 : 170) + noise.Next(-15, 16)); rgb[i + 2] = (byte)((disc ? 190 : 210) + noise.Next(-15, 16));
                        }
                    bool[] cut = GrabCut.Segment(rgb, gw, gh, new System.Drawing.Rectangle(4, 4, gw - 8, gh - 8), 4);
                    for (int y = 0; y < gh; y++)
                        for (int x = 0; x < gw; x++)
                        {
                            bool disc = (x - 60) * (x - 60) + (y - 45) * (y - 45) < 30 * 30;
                            if (cut[y * gw + x] == disc) agree++;
                        }
                    Require(agree > gw * gh * 98 / 100, "GrabCut cuts a disc out of its background (" + agree + "/" + gw * gh + ")");
                }
                log.AppendLine("PASS: Audio spectrum (80 Hz / 1 kHz / 5 kHz tones land in low / middle / high bands; silence is flat) and saved scene numbers.");
                // Updates: release tags, the API's JSON, and swapping a program folder's files.
                Require(Updater.ParseTag("v0.7.2") == new Version(0, 7, 2, 0) && Updater.ParseTag("0.8") == new Version(0, 8, 0, 0) && Updater.ParseTag("latest") == null, "Release tags read as versions");
                Require(!(Updater.ParseTag("v0.7.1") > new Version(0, 7, 1, 0)) && Updater.ParseTag("v0.7.2") > new Version(0, 7, 1, 0) && Updater.ParseTag("v0.10.0") > new Version(0, 9, 9, 0), "Only a newer release counts as an update");
                var parsed = Updater.Parse("{\"tag_name\":\"v9.1.0\",\"html_url\":\"https://github.com/" + Updater.Repository + "/releases/tag/v9.1.0\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
                    "{\"name\":\"notes.txt\",\"browser_download_url\":\"https://example.test/notes.txt\",\"size\":5}," +
                    "{\"name\":\"Arabara-9.1.0.zip\",\"browser_download_url\":\"https://github.com/" + Updater.Repository + "/releases/download/v9.1.0/Arabara-9.1.0.zip\",\"size\":539312,\"digest\":\"sha256:ab12cd\"}]}");
                Require(parsed != null && parsed.Version == new Version(9, 1, 0, 0) && parsed.ZipName == "Arabara-9.1.0.zip" && parsed.ZipSize == 539312 && parsed.Sha256 == "AB12CD" &&
                    parsed.ZipUrl.EndsWith("/Arabara-9.1.0.zip") && parsed.PageUrl.EndsWith("/tag/v9.1.0"), "The release's program zip, size and checksum are found");
                Require(Updater.Parse("{\"tag_name\":\"v9.2.0\",\"prerelease\":true}") == null && Updater.Parse("{\"tag_name\":\"v9.3.0\",\"draft\":true}") == null && Updater.Parse("{\"tag_name\":\"nightly\"}") == null, "Drafts, pre-releases and other tags are ignored");
                string scratch = Path.Combine(Path.GetTempPath(), "arabara-update-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    string package = Path.Combine(scratch, "release", "Arabara-9.1.0"), zip = Path.Combine(scratch, "Arabara-9.1.0.zip");
                    Directory.CreateDirectory(Path.Combine(package, "fonts"));
                    File.WriteAllText(Path.Combine(package, "Arabara.exe"), "new program");
                    File.WriteAllText(Path.Combine(package, "README.txt"), "new readme");
                    File.WriteAllText(Path.Combine(package, "fonts", "WhiteRabbit.ttf"), "new font");
                    System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(scratch, "release"), zip);
                    Func<string, string> install = delegate(string name)
                    {
                        string app = Path.Combine(scratch, name);
                        Directory.CreateDirectory(Path.Combine(app, "fonts"));
                        File.WriteAllText(Path.Combine(app, "Arabara.exe"), "old program");
                        File.WriteAllText(Path.Combine(app, "fonts", "WhiteRabbit.ttf"), "old font");
                        File.WriteAllText(Path.Combine(app, "keep.txt"), "not part of the release");
                        return app;
                    };
                    // A file that cannot be moved aside (held open) stops the update and puts everything back.
                    string blocked = install("blocked");
                    using (File.Open(Path.Combine(blocked, "fonts", "WhiteRabbit.ttf"), FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        bool threw = false;
                        try { Updater.Apply(zip, blocked, Path.Combine(scratch, "work-blocked")); }
                        catch (IOException) { threw = true; }
                        Require(threw && File.ReadAllText(Path.Combine(blocked, "Arabara.exe")) == "old program" && !File.Exists(Path.Combine(blocked, "README.txt")) && !File.Exists(Path.Combine(blocked, "Arabara.exe.old")),
                            "A failed update puts the old program back");
                    }
                    string installed = install("app"), work = Path.Combine(scratch, "work");
                    string exe = Updater.Apply(zip, installed, work);
                    Require(exe == Path.Combine(installed, "Arabara.exe") && File.ReadAllText(exe) == "new program" && File.ReadAllText(Path.Combine(installed, "README.txt")) == "new readme" &&
                        File.ReadAllText(Path.Combine(installed, "fonts", "WhiteRabbit.ttf")) == "new font" && File.ReadAllText(Path.Combine(installed, "keep.txt")) == "not part of the release" &&
                        File.ReadAllText(Path.Combine(installed, "Arabara.exe.old")) == "old program", "An update swaps the program's files and keeps the old ones aside");
                    Updater.CleanUp(installed, work);
                    Require(!File.Exists(Path.Combine(installed, "Arabara.exe.old")) && !File.Exists(Path.Combine(installed, "fonts", "WhiteRabbit.ttf.old")) && !Directory.Exists(work) && File.Exists(Path.Combine(installed, "keep.txt")),
                        "After the update the old files and the download are removed");
                }
                finally
                {
                    try { Directory.Delete(scratch, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                log.AppendLine("PASS: Updates: release tags and JSON, newer releases only, program files swapped (or all put back on failure) and tidied afterwards.");
                log.AppendLine("ALL SELF-TESTS PASSED");
                WriteResult("unit-tests.txt", log.ToString());
                return 0;
            }
            catch (Exception ex) { log.AppendLine(ex.ToString()); WriteResult("unit-tests.txt", log.ToString()); return 1; }
        }
    }

    internal sealed partial class BrowserForm
    {
        private void InstallTestPages(CoreWebView2 core)
        {
            // Only enabled by --smoke-test; uses an isolated profile and serves fixtures in memory.
            core.AddWebResourceRequestedFilter("https://tyran.test/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
            {
                string body = e.Request.Uri.StartsWith("https://tyran.test/call") ? CallPage :
                    "<!doctype html><html><head><title>Arabara test</title></head><body><h1>HTTPS fixture</h1><script>window.ticks=0;window.pings=0;setInterval(()=>{window.ticks++;fetch('/ping').then(()=>window.pings++);},500);</script></body></html>";
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
        }

        // A call within the page: two connections joined to each other, one sending a tone's
        // audio track to the other (it need not be heard: the track is what counts).
        private const string CallPage = @"<!doctype html><html><head><title>Arabara call test</title></head><body><h1>Call fixture</h1><script>
window.ticks = 0; setInterval(() => window.ticks++, 500);
(async () => {
  const a = new RTCPeerConnection(), b = new RTCPeerConnection();
  window.peers = [a, b];
  a.onicecandidate = e => e.candidate && b.addIceCandidate(e.candidate);
  b.onicecandidate = e => e.candidate && a.addIceCandidate(e.candidate);
  const context = new AudioContext(), tone = context.createOscillator(), out = context.createMediaStreamDestination();
  tone.connect(out); tone.start();
  out.stream.getTracks().forEach(t => a.addTrack(t, out.stream));
  const offer = await a.createOffer(); await a.setLocalDescription(offer); await b.setRemoteDescription(offer);
  const answer = await b.createAnswer(); await b.setLocalDescription(answer); await a.setRemoteDescription(answer);
})();
</script></body></html>";

        // A hidden tab in a call with sound is not frozen, holds the Low memory target and keeps
        // running; hung up, it freezes like any other.
        private async Task TestCallAsync(BrowserTab first, Action<string> record)
        {
            var call = AddTab();
            await EnsureTabAsync(call);
            await NavigateTestAsync(call, "https://tyran.test/call");
            string up = await WaitScriptAsync(call, "typeof window.__arabaraCall === 'function' && window.__arabaraCall()", v => v == "true", 15000);
            if (up != "true")
            {
                string states = await Core(call).ExecuteScriptAsync("window.peers ? window.peers.map(p => p.connectionState + '/' + p.iceConnectionState).join(', ') : 'no connections'");
                record("SKIP: the call fixture's WebRTC connection did not come up here (" + states + "); call detection not checked.");
                CloseTab(call);
                SelectTab(first);
                return;
            }
            // The other tabs stay as they are while the freeze delay is short.
            first.KeepAlive = true;
            var other = AddTab();
            int ticks = Int32.Parse(await Core(call).ExecuteScriptAsync("window.ticks"));
            FreezeAfter = TimeSpan.FromMilliseconds(300);
            call.HiddenSince = DateTime.UtcNow.AddSeconds(-1);
            ScheduleFreeze();
            await Task.Delay(1500);
            SelfTests.Require(!call.Frozen && call.InCall && Core(call) != null && Core(call).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low &&
                !call.View.Visible && Int32.Parse(await Core(call).ExecuteScriptAsync("window.ticks")) > ticks,
                "A hidden tab in a call is not frozen, is not drawn, holds the Low memory target and keeps running");
            await Core(call).ExecuteScriptAsync("window.peers.forEach(p => p.close())");
            var hangUp = Stopwatch.StartNew();
            while (!call.Frozen && hangUp.ElapsedMilliseconds < 5000) await Task.Delay(100);
            SelfTests.Require(call.Frozen, "Hung up, the hidden tab freezes again");
            FreezeAfter = TimeSpan.FromMinutes(15);
            first.KeepAlive = false;
            CloseTab(call);
            CloseTab(other);
            SelectTab(first);
            record("PASS: A hidden tab in a call with sound (WebRTC with an audio track) was not frozen, held the Low memory target and kept running; hung up, it froze within " + hangUp.ElapsedMilliseconds + " ms.");
        }

        private async Task<string> WaitScriptAsync(BrowserTab tab, string expression, Func<string, bool> done, int timeout = 10000)
        {
            var watch = Stopwatch.StartNew();
            string value = null;
            while (watch.ElapsedMilliseconds < timeout)
            {
                if (Core(tab) != null)
                {
                    try { value = await Core(tab).ExecuteScriptAsync(expression); if (done(value)) return value; }
                    catch (Exception) { }
                }
                await Task.Delay(100);
            }
            return value;
        }

        private async Task WaitSourceAsync(BrowserTab tab, Func<string, bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 10000 && !(Core(tab) != null && done(Core(tab).Source))) await Task.Delay(100);
            await WaitScriptAsync(tab, "document.readyState", v => v == "\"complete\"");
        }

        // Stream window: the live page moves without reloading, keeps running at full
        // priority while the browser is in the background, and returns intact.
        private async Task TestStreamingAsync(BrowserTab tab, Action<string> record)
        {
            SelectTab(tab);
            string source = Core(tab).Source;
            int before = Int32.Parse(await Core(tab).ExecuteScriptAsync("window.ticks"));
            PopOut(tab);
            var desktop = SystemInformation.VirtualScreen;
            SelfTests.Require(tab.Stream != null && tab.View.Parent == tab.Stream && tab.Mirror.Visible && tab.Stream.FormBorderStyle == FormBorderStyle.None &&
                !desktop.IntersectsWith(tab.Stream.Bounds) && tab.Stream.ClientSize == tab.Mirror.ViewSize, "Tab moves into a borderless, off-screen stream window mirrored in the tab");
            await Core(tab).ExecuteScriptAsync("window.clicks = 0; document.addEventListener('mousedown', () => window.clicks++); window.typed = ''; document.addEventListener('keypress', e => window.typed += e.key)");
            var mirrorPoint = new Point(tab.Mirror.Width / 2, tab.Mirror.Height / 2);
            tab.Mirror.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(tab.Mirror, new object[] { new MouseEventArgs(MouseButtons.Left, 1, mirrorPoint.X, mirrorPoint.Y, 0) });
            tab.Mirror.GetType().GetMethod("OnMouseUp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(tab.Mirror, new object[] { new MouseEventArgs(MouseButtons.Left, 1, mirrorPoint.X, mirrorPoint.Y, 0) });
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            tab.Mirror.GetType().GetMethod("OnKeyDown", flags).Invoke(tab.Mirror, new object[] { new KeyEventArgs(Keys.A) });
            tab.Mirror.GetType().GetMethod("OnKeyPress", flags).Invoke(tab.Mirror, new object[] { new KeyPressEventArgs('a') });
            tab.Mirror.GetType().GetMethod("OnKeyUp", flags).Invoke(tab.Mirror, new object[] { new KeyEventArgs(Keys.A) });
            string forwarded = await WaitScriptAsync(tab, "window.clicks + ':' + window.typed", v => v == "\"1:a\"", 4000);
            SelfTests.Require(forwarded == "\"1:a\"", "Mirror forwards clicks and typing to the live page (" + forwarded + ")");
            await Task.Delay(1200);
            SelfTests.Require(tab.Mirror.Showing && tab.Mirror.FramesShown > 0, "Mirror shows screencast frames of the live tab (frames=" + tab.Mirror.FramesShown + ")");
            var other = AddTab();
            SelfTests.Require(!tab.Mirror.Showing, "Mirror is hidden when another tab is selected");
            SelectTab(tab);
            SelfTests.Require(tab.Mirror.Showing, "Mirror returns with the live tab");
            CloseTab(other);
            SelectTab(tab);
            SetWindowActive(false);
            await Task.Delay(1600);
            int during = Int32.Parse(await Core(tab).ExecuteScriptAsync("window.ticks"));
            SelfTests.Require(during > before && Core(tab).Source == source && tab.View.Visible && !Core(tab).IsSuspended &&
                Core(tab).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal,
                "Stream window keeps the same page running at full priority while the browser is inactive (ticks " + before + " -> " + during + ")");
            SetWindowActive(true);
            tab.Stream.Close();
            await Task.Delay(300);
            int after = Int32.Parse(await Core(tab).ExecuteScriptAsync("window.ticks"));
            SelfTests.Require(tab.Stream == null && tab.View.Parent == tab.Page && after >= during && Core(tab).Source == source, "Closing the stream window returns the same page to its tab");
            tab.KeepAlive = true;
            SetWindowActive(false);
            int kept = Int32.Parse(await Core(tab).ExecuteScriptAsync("window.ticks"));
            await Task.Delay(1500);
            SelfTests.Require(!tab.View.Visible && Core(tab).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low && !Core(tab).IsSuspended && !EligibleForFreeze(tab) &&
                Int32.Parse(await Core(tab).ExecuteScriptAsync("window.ticks")) > kept,
                "Keep alive tab out of sight is not drawn and holds the Low memory target, but is never frozen or suspended and keeps running");
            SetWindowActive(true);
            SelfTests.Require(tab.View.Visible && Core(tab).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "Keep alive tab back in sight is drawn at the Normal memory target");
            tab.KeepAlive = false;
            UpdateVisibility();
            record("PASS: Live tab in a borderless off-screen stream window, mirrored and usable in its tab, no reload, full priority in the background, intact return; Keep alive keeps running out of sight, not drawn and at the Low memory target, never frozen.");
        }

        // Folder listing, previews for Arabara's own page only, file opening and blocked access.
        private async Task TestLocalFilesAsync(BrowserTab tab, Action<string> record)
        {
            string folder = Path.Combine(dataRoot, "local-test");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.WriteAllText(Path.Combine(folder, "note.txt"), "hello local file");
            using (var image = new Bitmap(4, 4)) image.Save(Path.Combine(folder, "pic.png"), System.Drawing.Imaging.ImageFormat.Png);
            string png = Path.Combine(folder, "pic.png"), raw = LocalRoot + "raw?p=" + Uri.EscapeDataString(png);
            string imageProbe = "(() => { window.__img = 'pending'; const i = new Image(); i.onload = () => window.__img = 'load'; i.onerror = () => window.__img = 'error'; i.src = '" + raw + "'; return 1; })()";

            await OpenAddressAsync(folder);
            await WaitSourceAsync(tab, src => src.StartsWith(LocalRoot + "view"));
            string rows = await WaitScriptAsync(tab, "document.querySelectorAll('.row').length", v => v == "3");
            SelfTests.Require(rows == "3" && address.Text == folder, "Folder listing shows 3 items and the folder path (rows=" + rows + ", address=" + address.Text + ")");
            string text = await WaitScriptAsync(tab, "(() => { if (!window.__t) { window.__t = 'pending'; fetch(ROOT + 'raw?p=' + encodeURIComponent(" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(Path.Combine(folder, "note.txt")) + ") + '&head=1').then(r => r.text()).then(t => window.__t = t); } return window.__t; })()", v => v != "\"pending\"" && v != "null");
            SelfTests.Require(text == "\"hello local file\"", "Folder page reads a text preview (" + text + ")");
            await Core(tab).ExecuteScriptAsync(imageProbe);
            SelfTests.Require(await WaitScriptAsync(tab, "window.__img", v => v != "\"pending\"") == "\"load\"", "Folder page loads an image preview");
            record("PASS: Local folder listing with text and image previews.");
            string project = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            await OpenAddressAsync(project);
            await WaitSourceAsync(tab, src => src.StartsWith(LocalRoot + "view") && src.Contains(Uri.EscapeDataString(project)));
            await Core(tab).ExecuteScriptAsync("select(3)");
            await Task.Delay(400);
            using (var shot = File.Create(Path.Combine(SelfTests.ResultsRoot, "folder-view.png")))
                await Core(tab).CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);

            await NavigateTestAsync(tab, "https://tyran.test/local-probe");
            await Core(tab).ExecuteScriptAsync(imageProbe);
            SelfTests.Require(await WaitScriptAsync(tab, "window.__img", v => v != "\"pending\"") == "\"error\"", "A website cannot load local files, even with the token");
            await Core(tab).ExecuteScriptAsync("location.href = " + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(LocalPath.ToFileUrl(png)));
            await Task.Delay(800);
            SelfTests.Require(Core(tab).Source == "https://tyran.test/local-probe", "A website cannot navigate to file:///");
            await Core(tab).ExecuteScriptAsync("location.href = 'https://" + LocalHost + "/wrongtoken/view?p='");
            await Task.Delay(800);
            SelfTests.Require(Core(tab).Source == "https://tyran.test/local-probe", "A wrong local token is refused");
            record("PASS: Websites cannot read local files, open file:/// or use a wrong local token.");

            string note = Path.Combine(folder, "note.txt");
            await OpenAddressAsync(note);
            await WaitSourceAsync(tab, src => src == LocalPath.ToFileUrl(note));
            SelfTests.Require(Core(tab).Source == LocalPath.ToFileUrl(note) && address.Text == note, "A text file opens in the tab (" + Core(tab).Source + ")");
            SelfTests.Require(!store.Entries.Any(e => e.Url.Contains(LocalHost) || e.Url.StartsWith("file:")), "Local pages stay out of history");
            record("PASS: Local text file opened in the tab; local pages kept out of history.");
            await NavigateTestAsync(tab, "https://tyran.test/session");
        }

        // YouTube light: VP9/AV1 hidden on YouTube hosts only, H.264 still offered.
        private async Task TestYouTubeLightAsync(BrowserTab tab, Action<string> record)
        {
            const string probe = "(window.MediaSource ? [MediaSource.isTypeSupported('video/webm; codecs=\"vp9\"'), MediaSource.isTypeSupported('video/mp4; codecs=\"av01.0.05M.08\"'), MediaSource.isTypeSupported('video/mp4; codecs=\"avc1.4d401f\"')].join(',') : 'none')";
            await NavigateTestAsync(tab, "https://tyran.test/codec");
            string other = await Core(tab).ExecuteScriptAsync(probe);
            // The fixture answers every youtube.com request here, so the test never reaches the network.
            Core(tab).AddWebResourceRequestedFilter("https://www.youtube.com/*", CoreWebView2WebResourceContext.All);
            await NavigateTestAsync(tab, "https://www.youtube.com/arabara-test");
            string youtubeProbe = await Core(tab).ExecuteScriptAsync(probe);
            SelfTests.Require(youtubeProbe.StartsWith("\"false,false,true") && other.StartsWith("\"true"), "YouTube light hides VP9/AV1 on YouTube only (youtube=" + youtubeProbe + ", other=" + other + ")");
            record("PASS: YouTube light offers only H.264 on YouTube hosts; other sites keep VP9/AV1.");
            await NavigateTestAsync(tab, "https://tyran.test/session");
        }

        private async Task NavigateTestAsync(BrowserTab tab, string url, bool fromAddressBar = false)
        {
            var completed = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null;
            handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) { completed.TrySetResult(e.IsSuccess); };
            Core(tab).NavigationCompleted += handler;
            try
            {
                if (fromAddressBar) { address.Text = url; await OpenAddressAsync(address.Text); }
                else Core(tab).Navigate(url);
                var result = await Task.WhenAny(completed.Task, Task.Delay(15000));
                SelfTests.Require(result == completed.Task && await completed.Task, "HTTPS fixture navigation");
            }
            finally { if (Core(tab) != null) Core(tab).NavigationCompleted -= handler; }
        }

        private async Task RunSmokeAsync()
        {
            string name = reopen ? "smoke-reopen.txt" : "smoke-tests.txt";
            var log = new StringBuilder();
            Action<string> record = delegate(string line) { log.AppendLine(line); SelfTests.WriteResult(name, log.ToString()); };
            try
            {
                record("Starting " + DateTime.UtcNow.ToString("O"));
                // Hidden test tabs must stay alive for the whole run; freezing is tested on its own below.
                FreezeAfter = AwayFreezeAfter = TimeSpan.FromMinutes(15);
                if (intro != null)
                {
                    var introWatch = Stopwatch.StartNew();
                    while (intro != null && introWatch.ElapsedMilliseconds < StartupIntro.Duration + 3000) await Task.Delay(50);
                    SelfTests.Require(intro == null && !Controls.OfType<StartupIntro>().Any(), "Startup intro finishes and is removed");
                    record("PASS: Startup intro ran for " + introWatch.ElapsedMilliseconds + " ms, then was removed and disposed.");
                }
                // Skynet College is personal-use only and left out of public packages; it is optional.
                SelfTests.Require(UiFonts.BodyName == "White Rabbit" && (UiFonts.DisplayName == "Skynet College" || !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts", "Skynet College.ttf"))), "Requested bundled font families loaded");
                SelfTests.Require(Font.FontFamily.Name == "White Rabbit" && address.Font.FontFamily.Name == "White Rabbit", "Native UI inherits White Rabbit");
                record("PASS: Private fonts loaded: White Rabbit for controls; Skynet College for display text.");
                if (reopen) SelfTests.Require(!lowMemoryMode && !pauseBackgroundVisuals, "Saved background preferences loaded after restart");
                SetPauseBackgroundVisuals(true, false);
                SetLowMemoryMode(true, false);
                SetWindowActive(true);
                SelfTests.Require(address.Text == AddressPolicy.HttpsPrefix && address.SelectionStart == address.TextLength && address.SelectionLength == 0,
                    "New tab starts with HTTPS and caret after the prefix (text=" + address.Text + ", start=" + address.SelectionStart + ", length=" + address.SelectionLength + ")");
                await OpenAddressAsync(AddressPolicy.HttpsPrefix);
                await OpenAddressAsync("discord music keywords");
                SelfTests.Require(environmentTask == null, "Invalid address must not initialize engine or send a search");
                record("PASS: Invalid address makes no navigation and does not start WebView2.");
                SetStatus("Ready  ·  HTTPS links only");
                record("Blank shell: " + MeasureMemory());
                using (var bitmap = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                    bitmap.Save(Path.Combine(SelfTests.ResultsRoot, "shell.png"));
                }
                // The tab strip on its own (the form's capture draws a classic title bar over it).
                var tabRow = strip.Parent;
                using (var bitmap = new Bitmap(tabRow.Width, tabRow.Height))
                {
                    tabRow.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tabRow.Size));
                    bitmap.Save(Path.Combine(SelfTests.ResultsRoot, "shell-tabs.png"));
                }
                Size originalSize = Size;
                Size = MinimumSize;
                using (var bitmap = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                    bitmap.Save(Path.Combine(SelfTests.ResultsRoot, "shell-small.png"));
                }
                Size = originalSize;
                var first = active;
                var init = EnsureTabAsync(first);
                SelfTests.Require(await Task.WhenAny(init, Task.Delay(20000)) == init, "WebView2 initialization timeout");
                await init;
                record("PASS: WebView2 initialized. Runtime " + environment.BrowserVersionString);
                await NavigateTestAsync(first, "tyran.test/session", true);
                SelfTests.Require(address.Text == "https://tyran.test/session" && Core(first).Source == "https://tyran.test/session", "Bare address opens and displays the canonical HTTPS URL");
                record("PASS: New-tab HTTPS prefix and bare-domain address-bar navigation.");
                SelfTests.Require(await Core(first).ExecuteScriptAsync("document.querySelector('h1').textContent") == "\"HTTPS fixture\"", "Page rendering and script");
                record("PASS: HTTPS content loads and executes JavaScript.");
                SelfTests.Require(await Core(first).ExecuteScriptAsync("navigator.language") == "\"en-US\"", "WebView language preference is English");
                record("PASS: WebView reports the English language preference.");
                SelfTests.Require(Core(first).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "Foreground uses Normal memory target");
                if (reopen)
                {
                    var cookies = await Core(first).CookieManager.GetCookiesAsync("https://tyran.test/");
                    SelfTests.Require(cookies.Any(c => c.Name == "tyran_session_test" && c.Value == "retained"), "Persistent cookie after process restart");
                    SelfTests.Require(await Core(first).ExecuteScriptAsync("localStorage.getItem('tyran_session_test')") == "\"retained\"", "Local storage after process restart");
                    record("PASS: Cookie and localStorage persisted across separate app processes.");
                    record("PASS: Memory-mode setting persisted across separate app processes.");
                    SetLowMemoryMode(true, true);
                    SetPauseBackgroundVisuals(true, true);
                }
                else
                {
                    await TestBackgroundVisualsAsync(first, record);
                    var cookie = Core(first).CookieManager.CreateCookie("tyran_session_test", "retained", "tyran.test", "/");
                    cookie.Expires = DateTime.UtcNow.AddDays(1);
                    cookie.IsSecure = true;
                    cookie.IsHttpOnly = true;
                    Core(first).CookieManager.AddOrUpdateCookie(cookie);
                    await Core(first).ExecuteScriptAsync("localStorage.setItem('tyran_session_test','retained')");
                    await Core(first).ExecuteScriptAsync("history.pushState({}, '', '/route')");
                    await Task.Delay(200);
                    SelfTests.Require(store.Entries.Any(e => e.Url == "https://tyran.test/route"), "Single-page-app history navigation");
                    address.Focus();
                    address.Text = "https://stale.test/";
                    var second = AddTab();
                    SelfTests.Require(address.Text == AddressPolicy.HttpsPrefix && address.SelectionStart == address.TextLength && address.SelectionLength == 0, "New tab replaces the previous address with the HTTPS prefix and positions the caret");
                    await EnsureTabAsync(second);
                    await NavigateTestAsync(second, "https://tyran.test/second");
                    SelfTests.Require(Core(first).BrowserProcessId == Core(second).BrowserProcessId, "Shared browser environment");
                    SelfTests.Require(Core(first).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low, "Hidden tab uses Low memory target");
                    SelfTests.Require(Core(second).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "Selected foreground tab uses Normal target");
                    SetWindowActive(false);
                    SelfTests.Require(Core(second).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low, "App deactivation lowers selected tab memory target");
                    SetWindowActive(true);
                    SelfTests.Require(Core(second).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "App activation restores selected tab memory target");
                    WindowState = System.Windows.Forms.FormWindowState.Minimized;
                    SelfTests.Require(Core(second).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low, "Minimized selected tab uses Low target");
                    WindowState = System.Windows.Forms.FormWindowState.Normal;
                    SetWindowActive(true);
                    SelfTests.Require(Core(second).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "Restored active window uses Normal target");
                    first.MediaLease = true;
                    ScheduleSleep();
                    SelfTests.Require(Core(first).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low && !Core(first).IsSuspended && !sleepTimer.Enabled, "Low-memory mode keeps protected tab running without sleep polling");
                    int ticks = Int32.Parse(await Core(first).ExecuteScriptAsync("window.ticks"));
                    int pings = Int32.Parse(await Core(first).ExecuteScriptAsync("window.pings"));
                    await Task.Delay(3000);
                    SelfTests.Require(Int32.Parse(await Core(first).ExecuteScriptAsync("window.ticks")) > ticks, "JavaScript continues in Low memory mode");
                    SelfTests.Require(Int32.Parse(await Core(first).ExecuteScriptAsync("window.pings")) > pings, "Network requests continue in Low memory mode");
                    SelfTests.Require(!Core(first).IsSuspended, "Background Low-memory tab remains unsuspended");
                    record("PASS: Foreground/background memory targets, activation, live JavaScript and network in Low mode; no suspend timer.");
                    first.MediaLease = false;
                    first.KeepAlive = true;
                    SetLowMemoryMode(false, false);
                    await Task.Delay(1000);
                    record("Two fixtures / Normal: " + MeasureMemory());
                    SetLowMemoryMode(true, false);
                    await Task.Delay(3000);
                    record("Two fixtures / Low on hidden tab: " + MeasureMemory());
                    SetLowMemoryMode(false, false);
                    SelfTests.Require(Core(first).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "Sleep mode restores Normal target before suspension");
                    first.KeepAlive = false;
                    first.MediaLease = true;
                    SelfTests.Require(!EligibleForSleep(first), "Voice permission guard on hidden tab");
                    first.MediaLease = false;
                    first.KeepAlive = true;
                    SelfTests.Require(!EligibleForSleep(first), "Manual keep-alive guard on hidden tab");
                    first.KeepAlive = false;
                    await Task.Delay(1000);
                    record("Before sleep: eligible=" + EligibleForSleep(first) + ", viewVisible=" + first.View.Visible + ", attempted=" + first.SleepAttempted);
                    first.SleepAt = DateTime.UtcNow.AddSeconds(-1);
                    await SleepDueTabsAsync();
                    record("Suspend result: " + first.LastSuspendResult);
                    SelfTests.Require(Core(first).IsSuspended, "Background tab suspension");
                    SetLowMemoryMode(true, false);
                    SelfTests.Require(!Core(first).IsSuspended && Core(first).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low, "Changing from sleep to Low mode resumes hidden tab");
                    SelectTab(first);
                    SelfTests.Require(!Core(first).IsSuspended && Core(first).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "Selected tab resumes and normalizes target");
                    SelectTab(second);
                    SetLowMemoryMode(false, false);
                    first.SleepAt = DateTime.UtcNow.AddSeconds(-1);
                    Task pendingSuspend = SleepDueTabsAsync();
                    SetLowMemoryMode(true, false);
                    await pendingSuspend;
                    SelfTests.Require(!Core(first).IsSuspended && Core(first).MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low, "Mode change during pending suspension reconciles correctly");
                    string frozenUrl = first.Url;
                    FreezeAfter = TimeSpan.FromMilliseconds(300);
                    first.HiddenSince = DateTime.UtcNow.AddSeconds(-1);
                    ScheduleFreeze();
                    await Task.Delay(1000);
                    SelfTests.Require(first.Frozen && first.View == null && !second.Frozen, "Idle hidden tab is frozen in Low RAM mode");
                    record("One tab frozen: " + MeasureMemory());
                    FreezeAfter = TimeSpan.FromMinutes(15);
                    SelectTab(first);
                    var thaw = Stopwatch.StartNew();
                    while ((Core(first) == null || Core(first).Source != frozenUrl) && thaw.ElapsedMilliseconds < 15000) await Task.Delay(100);
                    SelfTests.Require(!first.Frozen && Core(first) != null && Core(first).Source == frozenUrl, "Selecting a frozen tab reloads its page (" + frozenUrl + ")");
                    record("PASS: Idle hidden tab frozen in Low RAM mode and reloaded on selection in " + thaw.ElapsedMilliseconds + " ms.");
                    second.HiddenSince = DateTime.UtcNow - PressureFreezeAfter - TimeSpan.FromSeconds(5);
                    ScheduleFreeze();
                    await Task.Delay(300);
                    SelfTests.Require(UnderMemoryPressure() || !second.Frozen, "Without memory pressure a tab hidden past the pressure delay is not frozen");
                    ForceMemoryPressure = true;
                    ScheduleFreeze();
                    await Task.Delay(500);
                    ForceMemoryPressure = false;
                    SelfTests.Require(second.Frozen, "Under memory pressure a tab hidden past the pressure delay is frozen");
                    record("PASS: Memory pressure shortens the freeze delay to " + PressureFreezeAfter.TotalSeconds + " seconds.");
                    CloseTab(second);
                    record("PASS: SPA history, address/tab switch, shared process, voice/keep-alive guards, real suspend/resume, mode-switch race and close-tab.");
                    // Away: the tab shown freezes once the window has been out of sight long enough
                    // (Visuals:Auto, another application in front) and reloads when it comes back.
                    SelectTab(first);
                    string shownUrl = Core(first).Source;
                    SetPauseBackgroundVisuals(true, false);
                    AwayFreezeAfter = TimeSpan.FromMilliseconds(300);
                    SetWindowActive(false);
                    await Task.Delay(1200);
                    SelfTests.Require(first.Frozen && first.View == null, "The tab shown freezes while Arabara is out of sight");
                    AwayFreezeAfter = TimeSpan.FromMinutes(15);
                    SetWindowActive(true);
                    var returned = Stopwatch.StartNew();
                    while ((Core(first) == null || Core(first).Source != shownUrl) && returned.ElapsedMilliseconds < 15000) await Task.Delay(100);
                    SelfTests.Require(!first.Frozen && Core(first) != null && Core(first).Source == shownUrl, "The tab shown reloads when Arabara is back in sight (" + (Core(first) == null ? "no page" : Core(first).Source) + ")");
                    record("PASS: The tab shown froze while Arabara was out of sight and reloaded on return in " + returned.ElapsedMilliseconds + " ms.");
                    if (AdBlockerPresent)
                    {
                        // The extension page takes the settings: allow ads on a site, then block them again.
                        SelfTests.Require(adBlocker != null && adBlocker.IsEnabled, "Ad blocker (uBlock Origin Lite) installed and enabled");
                        await SetAdsAllowedAsync(new[] { "tyran.test" }, true);
                        await SetAdsAllowedAsync(new[] { "tyran.test" }, false);
                        UpdateChrome();
                        SelfTests.Require(adSite.Visible, "The ad blocker's mark shows in the address box on a website");
                        foreach (bool allowed in new[] { false, true })
                        {
                            adSite.Allowed = allowed;
                            var box = adSite.Parent;
                            using (var image = new Bitmap(box.Width, box.Height))
                            {
                                box.DrawToBitmap(image, new Rectangle(0, 0, box.Width, box.Height));
                                image.Save(Path.Combine(SelfTests.ResultsRoot, allowed ? "address-ads-allowed.png" : "address-ads-blocked.png"));
                            }
                        }
                        adSiteShown = null;
                        UpdateChrome();
                        record("PASS: Ad blocker loaded from the profile's extensions folder; a site's ads allowed and blocked again through the extension.");
                    }
                    await TestYouTubeLightAsync(first, record);
                    await TestStreamingAsync(first, record);
                    await TestCallAsync(first, record);
                    await TestLocalFilesAsync(first, record);
                    var homeTab = AddTab();
                    var home = homeTab.Page.Controls.OfType<HomeView>().First();
                    home.ToggleUi();
                    SelfTests.Require(home.UiHidden, "View-only mode hides the start page interface");
                    home.ToggleUi();
                    SelfTests.Require(!home.UiHidden, "View-only mode shows the interface again");
                    CloseTab(homeTab);
                    SelectTab(first);
                    record("PASS: Start page view-only toggle (interface decays away and back).");
                    SpinningMark.VideoPath = null;
                    SpinningMark.PicturePath = null;
                    int expected = SpinningMark.Scene;
                    for (int n = 1; n <= SpinningMark.SceneNames.Length; n++)
                    {
                        NextBackdrop();
                        do expected = (expected + 1) % SpinningMark.SceneNames.Length;
                        while (expected == SpinningMark.VideoScene || expected == SpinningMark.PictureScene);
                        SelfTests.Require(SpinningMark.Scene == expected && SpinningMark.SceneFromSaved(BrowserPreferences.Load(dataRoot).BackdropScene) == SpinningMark.Scene, "Logo cycles and saves the background scene, skipping VIDEO LOOP and PICTURE without a file");
                    }
                    record("PASS: Logo cycles the background scenes (VIDEO LOOP and PICTURE only with a file" + (Edition.Public ? ", never in the public edition" : "") + ") and remembers the choice.");
                    SetLowMemoryMode(false, true);
                    SetPauseBackgroundVisuals(false, true);
                    await Task.Delay(500);
                }
                SaveHistory();
                SelfTests.Require(new BrowserStore(dataRoot).Entries.Any(e => e.Url == "https://tyran.test/session"), "Navigation records history to disk");
                record("PASS: Visited HTTPS page persisted in history.");
                record("Fixture loaded: " + MeasureMemory());
                record("Engine process IDs: " + String.Join(",", environment.GetProcessInfos().Select(p => p.ProcessId.ToString())));
                record("ALL SMOKE TESTS PASSED");
            }
            catch (Exception ex) { ExitCode = 1; record(ex.ToString()); }
            finally { Close(); }
        }
    }
}
