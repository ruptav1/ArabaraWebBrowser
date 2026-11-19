using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Arabara
{
    // --music-test: the YouTube Music engine on a throwaway, signed-out profile with the
    // page muted: loads YouTube Music, finds public playlists, reads one playlist's tracks,
    // plays the first track and checks that time advances, skips, seeks and pauses, that a
    // paused engine rests (releases its page) and continues where it was, in playlist order.
    internal static class MusicTest
    {
        public static int Run()
        {
            var log = new StringBuilder();
            int code = 1;
            using (var form = new Form { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-3000, -3000) })
            {
                string profile = Path.Combine(Path.GetTempPath(), "arabara-music-test");
                Task<CoreWebView2Environment> env = null;
                var engine = new MusicEngine(form, delegate { return env ?? (env = CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions { Language = "en-US" })); }, 0);
                form.Shown += async delegate
                {
                    try
                    {
                        string ids = await engine.DiagnoseAsync();
                        Note(log, "Status: " + engine.Status + " / " + engine.Message);
                        Note(log, "Public playlists on home: " + ids);
                        engine.Mute();
                        var list = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<string[]>(ids ?? "[]").ToList();
                        // The home page varies; a public chart playlist seen earlier is the fallback.
                        list.Add("RDCLAK5uy_lBNUteBRencHzKelu5iDHwLF6mYqjL-JU");
                        foreach (string id in list)
                        {
                            // Tracks need the Ready state; the signed-out state is fine for public lists.
                            var tracks = await TracksAsync(engine, id);
                            Note(log, "Playlist " + id + ": " + tracks.Count + " tracks" + (tracks.Count > 0 ? " (first: " + tracks[0].Title + " / " + tracks[0].Artist + " / " + tracks[0].Duration + ")" : ""));
                            if (tracks.Count < 2) continue;
                            await engine.PlayAsync(new MusicPlaylist { Id = id, Title = "test" }, tracks, 0);
                            await Task.Delay(6000);
                            Func<string> lengths = delegate { var t = engine.Queue.Current; return "listed " + (t == null ? "-" : t.Duration) + " / player " + engine.Duration.ToString("F1") + " s" + (engine.Advertising ? " (ad)" : ""); };
                            Note(log, "After 6 s: playing=" + engine.Playing + " ad=" + engine.Advertising + " t=" + engine.Position.ToString("F1") + "/" + engine.Duration.ToString("F1"));
                            using (var panel = new MusicPanel(engine) { Size = new System.Drawing.Size(MusicPanel.LogicalWidth, MusicPanel.LogicalHeight), ThemePlaylistId = id })
                            {
                                form.Controls.Add(panel);
                                var playlist = new MusicPlaylist { Id = id, Title = "Public test playlist", Subtitle = "Playlist" };
                                panel.ShowForTest(new System.Collections.Generic.List<MusicPlaylist> { playlist }, playlist, tracks);
                                await Task.Delay(1500);
                                using (var image = new System.Drawing.Bitmap(panel.Width, panel.Height))
                                {
                                    panel.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, panel.Width, panel.Height));
                                    image.Save(Path.Combine(SelfTests.ResultsRoot, "music-panel.png"));
                                }
                                panel.FreezeTransitionForTest(0.55F);
                                using (var image = new System.Drawing.Bitmap(panel.Width, panel.Height))
                                {
                                    panel.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, panel.Width, panel.Height));
                                    // DrawToBitmap ignores the region: show what lies outside it as the page behind.
                                    using (var g = System.Drawing.Graphics.FromImage(image))
                                    using (var hidden = new System.Drawing.Region(new System.Drawing.Rectangle(0, 0, panel.Width, panel.Height)))
                                    using (var behind = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(40, 20, 24)))
                                    {
                                        hidden.Exclude(panel.Region);
                                        g.FillRegion(behind, hidden);
                                    }
                                    image.Save(Path.Combine(SelfTests.ResultsRoot, "music-panel-decay.png"));
                                }
                                form.Controls.Remove(panel);
                            }
                            await engine.SeekAsync(30);
                            await Task.Delay(1500);
                            Note(log, "After seek 30: t=" + engine.Position.ToString("F1"));
                            await engine.NextAsync();
                            await Task.Delay(5000);
                            Note(log, "After next: index=" + engine.Queue.Index + " t=" + engine.Position.ToString("F1") + " playing=" + engine.Playing + " " + lengths());
                            var watch = System.Diagnostics.Stopwatch.StartNew();
                            await engine.PlayAsync(new MusicPlaylist { Id = id, Title = "test" }, tracks, 4);
                            Note(log, "Pick track 5 in the same playlist: index=" + engine.Queue.Index + " in " + watch.ElapsedMilliseconds + " ms, page " + await engine.EvaluateAsync("__arabara.status()", false));
                            await engine.TogglePauseAsync();
                            await Task.Delay(1500);
                            double paused = engine.Position;
                            await Task.Delay(1500);
                            Note(log, "Paused: playing=" + engine.Playing + " t stays " + paused.ToString("F1") + " -> " + engine.Position.ToString("F1"));
                            await engine.TogglePauseAsync();
                            for (int i = 0; i < 90 && (engine.Advertising || engine.Duration < 30); i++) await Task.Delay(1000);
                            Note(log, "Without ad: index " + engine.Queue.Index + " t=" + engine.Position.ToString("F1") + ", " + lengths());
                            // Rest: paused past the rest delay, the page is released; play opens it
                            // again and continues where it was (after an ad, if one comes first).
                            await engine.SeekAsync(Math.Min(60, engine.Duration / 2));
                            await Task.Delay(1500);
                            await engine.TogglePauseAsync();
                            await Task.Delay(1000);
                            double restAt = engine.Position;
                            engine.RestAfter = () => TimeSpan.FromSeconds(2);
                            for (int i = 0; i < 20 && !engine.Resting; i++) await Task.Delay(500);
                            engine.RestAfter = null;
                            bool rested = engine.Resting && !engine.Running;
                            Note(log, "Rest after 2 s paused at t=" + restAt.ToString("F1") + ": resting=" + engine.Resting + ", page " + (engine.Running ? "kept" : "released") + " (" + engine.Message + ")");
                            var wake = System.Diagnostics.Stopwatch.StartNew();
                            await engine.TogglePauseAsync();
                            for (int i = 0; i < 90 && (engine.Advertising || !engine.Playing || engine.Position <= 0); i++) await Task.Delay(1000);
                            await Task.Delay(3000);
                            bool woke = engine.Running && engine.Playing && engine.Position >= restAt - 1 && engine.Position < restAt + 20;
                            Note(log, "Play after the rest: playing=" + engine.Playing + " index " + engine.Queue.Index + " t=" + engine.Position.ToString("F1") + " (was " + restAt.ToString("F1") + "), " + wake.ElapsedMilliseconds + " ms");
                            int before = engine.Queue.Index;
                            await engine.SeekAsync(engine.Duration - 3);
                            await Task.Delay(8000);
                            Note(log, "Auto-advance at the end: index " + before + " -> " + engine.Queue.Index + ", " + lengths() + ", page " + await engine.EvaluateAsync("__arabara.status()", false));
                            // The page reopened at a later track must still queue the playlist in order.
                            bool advanced = engine.Queue.Index == before + 1;
                            await engine.SetRepeatAsync(MusicRepeat.One);
                            for (int round = 1; round <= 3; round++)
                            {
                                for (int i = 0; i < 90 && (engine.Advertising || engine.Duration < 30); i++) await Task.Delay(1000);
                                before = engine.Queue.Index;
                                await engine.SeekAsync(engine.Duration - 3);
                                await Task.Delay(8000);
                                Note(log, "Repeat one, round " + round + ": index " + before + " -> " + engine.Queue.Index + ", t=" + engine.Position.ToString("F1") + " playing=" + engine.Playing + " " + lengths());
                            }
                            Note(log, "Setting repeat all and opening the last track");
                            await engine.SetRepeatAsync(MusicRepeat.All);
                            await engine.PlayAsync(new MusicPlaylist { Id = id, Title = "test" }, tracks, tracks.Count - 1);
                            for (int i = 0; i < 90 && (engine.Advertising || engine.Duration < 30); i++) await Task.Delay(1000);
                            await engine.SeekAsync(engine.Duration - 3);
                            await Task.Delay(8000);
                            Note(log, "Repeat all at the last track: index " + (tracks.Count - 1) + " -> " + engine.Queue.Index + " playing=" + engine.Playing);
                            for (int i = 0; i < 90 && (engine.Advertising || engine.Duration < 30); i++) await Task.Delay(1000);
                            await engine.SeekAsync(engine.Duration - 3);
                            await Task.Delay(8000);
                            Note(log, "Then the next end: index -> " + engine.Queue.Index + " playing=" + engine.Playing);
                            Note(log, "Setting repeat off and opening the last track");
                            engine.Trace = new StringBuilder();
                            await engine.SetRepeatAsync(MusicRepeat.Off);
                            await engine.PlayAsync(new MusicPlaylist { Id = id, Title = "test" }, tracks, tracks.Count - 1);
                            for (int i = 0; i < 90 && (engine.Advertising || engine.Duration < 30); i++) await Task.Delay(1000);
                            await engine.SeekAsync(engine.Duration - 3);
                            await Task.Delay(10000);
                            for (int i = 0; i < 60 && engine.Advertising; i++) await Task.Delay(1000);
                            await Task.Delay(3000);
                            Note(log, "Trace:" + Environment.NewLine + engine.Trace);
                            Note(log, "Repeat off at the last track: index " + engine.Queue.Index + " playing=" + engine.Playing + " page " + await engine.EvaluateAsync("__arabara.status()", false));
                            Note(log, "Rest and play again: " + (rested && woke ? "PASS" : "FAIL") + "; next track after reopening: " + (advanced ? "PASS" : "FAIL"));
                            code = !engine.Playing && engine.Queue.Index == 0 && rested && woke && advanced ? 0 : 1; // repeat off stops at the end
                            break;
                        }
                    }
                    catch (Exception ex) { Note(log, "ERROR " + ex); }
                    engine.Dispose();
                    form.Close();
                };
                Application.Run(form);
            }
            SelfTests.WriteResult("music-test.txt", log.ToString());
            return code;
        }

        // Written as it goes, so a stalled run still shows how far it got.
        private static void Note(StringBuilder log, string line)
        {
            log.AppendLine(DateTime.Now.ToString("HH:mm:ss ") + line);
            SelfTests.WriteResult("music-test.txt", log.ToString());
        }

        private static async Task<System.Collections.Generic.List<MusicTrack>> TracksAsync(MusicEngine engine, string id)
        {
            return await engine.LoadTracksAsync(id, true);
        }
    }
}
