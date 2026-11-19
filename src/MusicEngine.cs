using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Arabara
{
    internal sealed class MusicPlaylist
    {
        public string Id, Title, Subtitle;
    }

    internal sealed class MusicTrack
    {
        public string VideoId, Title, Artist, Duration;
    }

    internal enum MusicRepeat { Off, All, One }

    // The play order: which track is current and which plays after it, for the
    // three repeat modes. Pure logic, so it is unit tested.
    internal sealed class MusicQueue
    {
        public readonly List<MusicTrack> Tracks = new List<MusicTrack>();
        public int Index = -1;
        public MusicRepeat Repeat;

        public MusicTrack Current { get { return Index >= 0 && Index < Tracks.Count ? Tracks[Index] : null; } }

        // The track after the current one: when it ends by itself (automatic) repeat-one
        // replays it; at the end of the list repeat-all wraps, otherwise playback stops (-1).
        public int NextIndex(bool automatic)
        {
            if (Tracks.Count == 0) return -1;
            if (automatic && Repeat == MusicRepeat.One && Index >= 0) return Index;
            int next = Index + 1;
            if (next < Tracks.Count) return next;
            return Repeat == MusicRepeat.All ? 0 : -1;
        }

        public int PreviousIndex()
        {
            if (Tracks.Count == 0) return -1;
            if (Index > 0) return Index - 1;
            return Repeat == MusicRepeat.All ? Tracks.Count - 1 : 0;
        }
    }

    // YouTube Music, played by a hidden music.youtube.com page in the shared browser
    // profile, so it uses the account already signed in there. Arabara never sees the
    // password or tokens: the page's own script calls YouTube Music's internal API
    // (InnerTube "browse", as documented by the open-source ytmusicapi project) for the
    // library playlists and their tracks, and drives the page's player for playback.
    // YouTube Music plays the playlist with its own queue; Arabara follows it.
    internal sealed class MusicEngine : IDisposable
    {
        public enum State { Off, Starting, Ready, SignedOut, Failed }

        private const string Home = "https://music.youtube.com/";
        private static readonly Regex SafeId = new Regex("^[A-Za-z0-9_-]{2,64}$");
        private readonly Control host;
        private readonly Func<Task<CoreWebView2Environment>> environment;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        private readonly Timer poll = new Timer { Interval = 500 };
        private WebView2 view;
        private Task starting;
        private bool polling, disposed;
        private bool switching;
        private bool requested; // a track change asked for by the listener (next, previous, a pick)
        private bool stopPending;
        internal System.Text.StringBuilder Trace; // --music-test only // repeat off reached the end; stop once the first track is up
        private string loadedList; // the playlist whose queue the page holds
        // Resting: after RestAfter with nothing playing (and not while InUse), the hidden page
        // is released and only the playlist, the track and the position are kept; playing
        // again opens the page again and continues from there. Null: never rests.
        public Func<TimeSpan> RestAfter;
        public Func<bool> InUse;
        public bool Resting { get; private set; }
        // Tracks started by PollAsync after standing paused at their start (for the soak test).
        internal int StartsResumed;
        internal bool AutoStartOff; // --soak-test --no-autostart: shows what happens without it
        private DateTime idleSince = DateTime.MinValue;
        // When the page last played (MinValue after the listener paused), and since when a new
        // track has stood paused at its start (see PollAsync).
        private DateTime lastPlayed = DateTime.MinValue, stuckSince = DateTime.MinValue;
        // Renewal: at a track change, once the page has lived RenewAfter, it is opened again at
        // the new track, which starts afresh. Measured with --soak-test (2026-10-08) the page
        // held flat for three hours (403, 390, 403, 408 MB per half hour) and a renewal after
        // one hour gave back about 15 MB, so this is only a safety net for nights of music.
        // (Its element count is no measure: the queue it shows grows with the position in the
        // playlist, so a page opened later in the playlist is larger, not leaner.)
        private DateTime pageLoadedAt = DateTime.MinValue;
        public static TimeSpan RenewAfter = TimeSpan.FromHours(6);
        internal int Renewals; // for the soak test
        private double resumeAt; // where to continue once the track plays again (0: as it starts)
        private bool muted;

        public readonly MusicQueue Queue = new MusicQueue();
        public MusicPlaylist Playlist { get; private set; }
        public State Status { get; private set; }
        public string Message { get; private set; }
        public bool Playing { get; private set; }
        public bool Advertising { get; private set; }
        public double Position { get; private set; }
        public double Duration { get; private set; }
        public int Volume { get; private set; }
        // Started by the start page's theme music: paused when no start page is shown.
        public bool ThemeMode;
        public bool Running { get { return view != null; } }
        public event Action Changed;

        public MusicEngine(Control host, Func<Task<CoreWebView2Environment>> environment, int volume)
        {
            this.host = host;
            this.environment = environment;
            Volume = Math.Max(0, Math.Min(100, volume));
            poll.Tick += async delegate { await PollAsync(); RestIfIdle(); };
        }

        public static bool IsSafeId(string id) { return id != null && SafeId.IsMatch(id); }

        public Task StartAsync()
        {
            if (starting == null || Status == State.Failed) starting = StartCoreAsync();
            return starting;
        }

        private async Task StartCoreAsync()
        {
            Resting = false;
            idleSince = DateTime.MinValue;
            SetStatus(State.Starting, "CONNECTING TO YOUTUBE MUSIC");
            try
            {
                var env = await environment();
                if (disposed) return;
                if (view == null)
                {
                    // Chromium defers media loading in a page that is not visible, so the page is a
                    // 1 x 1 pixel view kept behind everything else.
                    view = new WebView2 { Visible = true, Size = new System.Drawing.Size(1, 1), Location = System.Drawing.Point.Empty, TabStop = false };
                    host.Controls.Add(view);
                    view.SendToBack();
                    var handle = view.Handle;
                    await view.EnsureCoreWebView2Async(env);
                    if (disposed) return;
                    // RINGS hears Arabara's own sound through WebView2's browser process (AudioMeter).
                    AudioMeter.OwnAudioProcess = (int)view.CoreWebView2.BrowserProcessId;
                    var core = view.CoreWebView2;
                    core.IsMuted = muted;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.AreHostObjectsAllowed = false;
                    core.Settings.IsWebMessageEnabled = false;
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    // Nobody sees this page, so its dialogs are answered here: a "leave site?"
                    // prompt (YouTube Music raises one while music plays) would otherwise block
                    // the page when Arabara opens another playlist.
                    core.Settings.AreDefaultScriptDialogsEnabled = false;
                    core.ScriptDialogOpening += delegate(object sender, CoreWebView2ScriptDialogOpeningEventArgs e) { e.Accept(); };
                    core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
                    core.LaunchingExternalUriScheme += delegate(object sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e) { e.Cancel = true; };
                    // Stay on YouTube Music (sign-in pages open in a normal tab instead).
                    core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
                    {
                        Uri uri;
                        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "music.youtube.com") e.Cancel = true;
                    };
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(Helper);
                }
                var loaded = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs> done = null;
                done = delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) { view.CoreWebView2.NavigationCompleted -= done; loaded.TrySetResult(e.IsSuccess); };
                view.CoreWebView2.NavigationCompleted += done;
                view.CoreWebView2.Navigate(Home);
                pageLoadedAt = DateTime.UtcNow;
                if (!await loaded.Task) { SetStatus(State.Failed, "YOUTUBE MUSIC DID NOT LOAD"); return; }
                // The page's configuration (API key, sign-in state) appears shortly after load.
                for (int i = 0; i < 60 && !disposed; i++)
                {
                    var status = await ReadStatusAsync();
                    if (status != null && True(status, "ready"))
                    {
                        bool signedIn = True(status, "signedIn");
                        SetStatus(signedIn ? State.Ready : State.SignedOut, signedIn ? "ONLINE" : "NOT SIGNED IN");
                        poll.Start();
                        return;
                    }
                    await Task.Delay(250);
                }
                SetStatus(State.Failed, "YOUTUBE MUSIC DID NOT RESPOND");
            }
            catch (Exception ex) { SetStatus(State.Failed, "ERROR: " + ex.Message); }
        }

        public async Task<List<MusicPlaylist>> LoadPlaylistsAsync()
        {
            await StartAsync();
            var list = new List<MusicPlaylist>();
            if (Status != State.Ready) return list;
            var items = json.Deserialize<List<Dictionary<string, object>>>(await EvaluateAsync("__arabara.playlists()", false) ?? "[]");
            foreach (var item in items)
            {
                string id = Text(item, "id");
                if (IsSafeId(id)) list.Add(new MusicPlaylist { Id = id, Title = Text(item, "title"), Subtitle = Text(item, "subtitle") });
            }
            return list;
        }

        public async Task<List<MusicTrack>> LoadTracksAsync(string playlistId, bool allowSignedOut = false)
        {
            await StartAsync();
            var list = new List<MusicTrack>();
            if (!(Status == State.Ready || (allowSignedOut && Status == State.SignedOut)) || !IsSafeId(playlistId)) return list;
            var items = json.Deserialize<List<Dictionary<string, object>>>(await EvaluateAsync("__arabara.tracks('" + playlistId + "')", false) ?? "[]");
            foreach (var item in items)
            {
                string id = Text(item, "id");
                if (IsSafeId(id)) list.Add(new MusicTrack { VideoId = id, Title = Text(item, "title"), Artist = Text(item, "artist"), Duration = Text(item, "duration") });
            }
            return list;
        }

        // Starts a playlist at a track. YouTube Music itself plays the playlist (its own
        // queue, repeat mode and track versions, exactly as on the website); Arabara only
        // points it at a track and follows what it plays.
        public async Task PlayAsync(MusicPlaylist playlist, IList<MusicTrack> tracks, int index)
        {
            Playlist = playlist;
            Queue.Tracks.Clear();
            Queue.Tracks.AddRange(tracks);
            Queue.Index = Math.Max(0, Math.Min(tracks.Count - 1, index));
            resumeAt = 0;
            await PlayCurrentAsync();
        }

        private async Task PlayCurrentAsync()
        {
            var track = Queue.Current;
            if (track == null || Playlist == null) return;
            switching = true;
            requested = true;
            stopPending = false;
            Position = Duration = 0;
            Playing = true;
            Raise();
            try
            {
                // After a rest the page is opened again first.
                if (view == null) await StartAsync();
                if (view == null || (Status != State.Ready && Status != State.SignedOut)) { Playing = false; Raise(); return; }
                // Within the playlist already loaded: jump in YouTube Music's queue. Otherwise
                // (or if the track is not in the loaded part of the queue) open the playlist,
                // which loads a fresh queue.
                bool started = loadedList == Playlist.Id && await JumpAsync(track.VideoId, 16);
                // YouTube Music queues a playlist opened at a later track as that track followed
                // by the playlist from its start, so the next track would be the first one. Opened
                // at its first track the queue is in order: the track is then picked in it (the
                // page muted meanwhile, so the first track is not heard).
                var first = Queue.Tracks[0];
                if (!started && Queue.Index > 0 && first.VideoId != track.VideoId)
                {
                    loadedList = Playlist.Id;
                    view.CoreWebView2.IsMuted = true;
                    try
                    {
                        await NavigateAsync(WatchUrl(first.VideoId));
                        if (await WaitForTrackAsync(first.VideoId, 40)) started = await JumpAsync(track.VideoId, 40);
                    }
                    finally { if (view != null && view.CoreWebView2 != null) view.CoreWebView2.IsMuted = muted; }
                }
                if (!started)
                {
                    loadedList = Playlist.Id;
                    await NavigateAsync(WatchUrl(track.VideoId));
                    await WaitForTrackAsync(track.VideoId, 80);
                }
                await ApplySettingsAsync();
            }
            finally { switching = false; }
            Raise();
        }

        private string WatchUrl(string videoId) { return "https://music.youtube.com/watch?v=" + videoId + "&list=" + Playlist.Id; }

        // Picks a track in the queue the page holds, and waits (in quarter seconds) until it plays.
        private async Task<bool> JumpAsync(string videoId, int quarters)
        {
            string jumped;
            try { jumped = await EvaluateAsync("__arabara.jump('" + videoId + "')", true); }
            catch (TimeoutException) { jumped = null; }
            return jumped == "ok" && await WaitForTrackAsync(videoId, quarters);
        }

        // Waits (in quarter seconds) until the page plays the track, or an ad before it.
        private async Task<bool> WaitForTrackAsync(string videoId, int quarters)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(quarters * 250);
            for (int i = 0; DateTime.UtcNow < deadline && !disposed; i++)
            {
                Dictionary<string, object> status;
                // A page busy loading may not answer for a moment; keep asking.
                try { status = await ReadStatusAsync(); }
                catch (TimeoutException) { continue; }
                catch (InvalidOperationException) { status = null; }
                if (status != null && Text(status, "id") == videoId && (Number(status, "time") > 0 || True(status, "ad")))
                {
                    // A freshly opened page may hold the track paused (autoplay rules): start it
                    // with a command that counts as a user gesture.
                    if (True(status, "paused")) await EvaluateAsync("__arabara.resume()", true);
                    return true;
                }
                if (status != null && Text(status, "id") == videoId && True(status, "paused") && i % 4 == 3) await EvaluateAsync("__arabara.resume()", true);
                await Task.Delay(250);
            }
            return false;
        }

        private async Task NavigateAsync(string url)
        {
            var loaded = new TaskCompletionSource<bool>();
            EventHandler<CoreWebView2NavigationCompletedEventArgs> done = null;
            done = delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) { view.CoreWebView2.NavigationCompleted -= done; loaded.TrySetResult(e.IsSuccess); };
            view.CoreWebView2.NavigationCompleted += done;
            view.CoreWebView2.Navigate(url);
            pageLoadedAt = DateTime.UtcNow;
            await Task.WhenAny(loaded.Task, Task.Delay(20000));
        }

        private static string RepeatName(MusicRepeat repeat) { return repeat == MusicRepeat.One ? "ONE" : repeat == MusicRepeat.All ? "ALL" : "NONE"; }

        // YouTube Music's own repeat mode and volume, set to Arabara's.
        private async Task ApplySettingsAsync()
        {
            if (view == null) return;
            await EvaluateAsync("__arabara.settings('" + RepeatName(Queue.Repeat) + "', " + Volume + ")", true);
        }

        public async Task TogglePauseAsync()
        {
            if (Queue.Current == null) return;
            if (view == null) { if (Resting) await PlayCurrentAsync(); return; }
            stopPending = false;
            if (Playing) lastPlayed = DateTime.MinValue;
            await EvaluateAsync(Playing ? "__arabara.pause()" : "__arabara.resume()", true);
            Playing = !Playing;
            Raise();
        }

        public async Task PauseAsync()
        {
            if (view == null || !Playing) return;
            lastPlayed = DateTime.MinValue;
            await EvaluateAsync("__arabara.pause()", true);
            Playing = false;
            Raise();
        }

        public async Task ResumeAsync()
        {
            if (Playing || Queue.Current == null) return;
            if (view == null) { if (Resting) await PlayCurrentAsync(); return; }
            await EvaluateAsync("__arabara.resume()", true);
            Playing = true;
            Raise();
        }

        public async Task SeekAsync(double seconds)
        {
            if (Queue.Current == null) return;
            seconds = Math.Max(0, Duration > 0 ? Math.Min(Duration - 0.5, seconds) : seconds);
            // Resting: the track continues from there when it plays again.
            if (view == null) { if (Resting) { resumeAt = seconds; Position = seconds; Raise(); } return; }
            resumeAt = 0;
            await EvaluateAsync("__arabara.seek(" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")", true);
            Position = seconds;
            Raise();
        }

        // Next and previous are YouTube Music's own buttons (previous restarts a track that
        // has played a few seconds); Arabara follows the track that starts.
        public async Task NextAsync()
        {
            if (Queue.Current == null) return;
            int next = Queue.NextIndex(false);
            if (next < 0) return;
            resumeAt = 0;
            if (view == null) { if (Resting) { Queue.Index = next; await PlayCurrentAsync(); } return; }
            requested = true;
            stopPending = false;
            if (await EvaluateAsync("__arabara.skip(1)", true) != "ok") { Queue.Index = next; await PlayCurrentAsync(); }
        }

        public async Task PreviousAsync()
        {
            if (Queue.Current == null) return;
            resumeAt = 0;
            if (view == null)
            {
                if (!Resting) return;
                int before = Queue.PreviousIndex();
                if (before >= 0) Queue.Index = before;
                await PlayCurrentAsync();
                return;
            }
            requested = true;
            stopPending = false;
            if (await EvaluateAsync("__arabara.skip(-1)", true) != "ok")
            {
                int previous = Queue.PreviousIndex();
                if (previous < 0) return;
                Queue.Index = previous;
                await PlayCurrentAsync();
            }
        }

        public async Task SetVolumeAsync(int volume)
        {
            Volume = Math.Max(0, Math.Min(100, volume));
            if (view != null) await EvaluateAsync("__arabara.volume(" + Volume + ")", false);
            Raise();
        }

        public async Task SetRepeatAsync(MusicRepeat repeat)
        {
            Queue.Repeat = repeat;
            await ApplySettingsAsync();
            Raise();
        }

        // Where a track the page plays sits in the playlist: searched from the current one on.
        private int IndexNear(string id)
        {
            for (int k = 0; k < Queue.Tracks.Count; k++)
            {
                int i = (Math.Max(0, Queue.Index) + k) % Queue.Tracks.Count;
                if (Queue.Tracks[i].VideoId == id) return i;
            }
            return -1;
        }

        private async Task PollAsync()
        {
            if (polling || view == null || disposed) return;
            polling = true;
            try
            {
                var status = await ReadStatusAsync();
                if (status == null) return;
                if (Status == State.SignedOut && True(status, "signedIn")) SetStatus(State.Ready, "ONLINE");
                if (Queue.Current == null || switching) return;
                Advertising = True(status, "ad");
                string id = Text(status, "id");
                bool paused = True(status, "paused");
                // During an ad the page already reports the track that follows it.
                if (id != null && id != Queue.Current.VideoId)
                {
                    int index = IndexNear(id);
                    resumeAt = 0; // another track: nothing to continue
                    if (Trace != null) Trace.AppendLine(DateTime.Now.ToString("HH:mm:ss.f") + " page " + id + " -> index " + index + " (queue " + Queue.Index + ", requested " + requested + ", ad " + Advertising + ", paused " + paused + ")");
                    // Without repeat all, a playlist that wraps around by itself (YouTube Music
                    // loops some queues) stops at the first track instead.
                    if (index == 0 && !requested && Queue.Repeat != MusicRepeat.All && Queue.Index == Queue.Tracks.Count - 1 && Queue.Tracks.Count > 1)
                    {
                        Queue.Index = 0;
                        stopPending = true;
                    }
                    else if (index >= 0)
                    {
                        Queue.Index = index;
                        // A track that began by itself on a page that has lived RenewAfter: the
                        // page is renewed (see Renewals).
                        if (!requested && !Advertising && DateTime.UtcNow - pageLoadedAt > RenewAfter)
                        {
                            if (Trace != null) Trace.AppendLine(DateTime.Now.ToString("HH:mm:ss.f") + " renew the page at track " + index);
                            Renewals++;
                            requested = false;
                            loadedList = null;
                            await PlayCurrentAsync();
                            return;
                        }
                    }
                    else if (!paused && Text(status, "page") == "/watch")
                    {
                        // Past the playlist: YouTube Music's automix after the last track.
                        // Repeat all starts the playlist again; otherwise playback stops there.
                        if (Queue.Repeat == MusicRepeat.All) { Queue.Index = 0; await PlayCurrentAsync(); return; }
                        await EvaluateAsync("__arabara.pause()", true);
                        paused = true;
                    }
                    requested = false;
                }
                else if (!Advertising && id == Queue.Current.VideoId && Number(status, "time") > 1) requested = false;
                // Stopped at the end: an ad cannot be paused, so the stop waits until the track
                // itself is up, and it holds (YouTube Music may start the cued first track by
                // itself a little later) until the listener presses something.
                if (stopPending && !Advertising && id == Queue.Current.VideoId)
                {
                    // Pause only: seeking a track YouTube Music has just cued would start it.
                    if (!paused) await EvaluateAsync("__arabara.pause()", true);
                    paused = true;
                }
                // Back from a rest: once the track itself (not an ad before it) plays, it
                // continues where it was.
                if (resumeAt > 0 && !Advertising && !paused && id == Queue.Current.VideoId)
                {
                    double at = resumeAt;
                    resumeAt = 0;
                    if (Number(status, "time") < at - 3)
                    {
                        await EvaluateAsync("__arabara.seek(" + at.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")", true);
                        status["time"] = at;
                    }
                }
                // Background music that stops by itself goes on: YouTube Music's "still there?"
                // prompt is answered, and a new track left paused at its start is started after
                // 3 seconds (Chromium pauses a muted video it cannot see, and at volume 0 the
                // player mutes it). A pause from the listener (Arabara's controls, the media keys
                // mid-track, or the end of a playlist without repeat) is left alone.
                if (True(status, "stillThere")) await EvaluateAsync("__arabara.stillHere()", true);
                if (!paused) { lastPlayed = DateTime.UtcNow; stuckSince = DateTime.MinValue; }
                else if (!AutoStartOff && !stopPending && !Advertising && id == Queue.Current.VideoId && Number(status, "time") < 1 &&
                    DateTime.UtcNow - lastPlayed < TimeSpan.FromSeconds(30))
                {
                    if (stuckSince == DateTime.MinValue) stuckSince = DateTime.UtcNow;
                    else if (DateTime.UtcNow - stuckSince > TimeSpan.FromSeconds(3))
                    {
                        if (Trace != null) Trace.AppendLine(DateTime.Now.ToString("HH:mm:ss.f") + " track " + id + " stood paused at its start: started");
                        await EvaluateAsync("__arabara.resume()", true);
                        stuckSince = DateTime.MinValue;
                        StartsResumed++;
                    }
                }
                else stuckSince = DateTime.MinValue;
                // YouTube Music may reset its repeat mode with a new queue.
                string repeat = Text(status, "repeat");
                if (repeat != null && repeat != "none" && repeat != RepeatName(Queue.Repeat)) await ApplySettingsAsync();
                Position = Number(status, "time");
                Duration = Number(status, "duration");
                Playing = !paused;
                Raise();
            }
            catch (Exception) { }
            finally { polling = false; }
        }

        private async Task<Dictionary<string, object>> ReadStatusAsync()
        {
            string text = await EvaluateAsync("__arabara ? __arabara.status() : null", false);
            return String.IsNullOrEmpty(text) ? null : json.Deserialize<Dictionary<string, object>>(text);
        }

        // Runs a page expression through the DevTools protocol, so promises are awaited and
        // player commands count as a user gesture (media autoplay rules).
        internal async Task<string> EvaluateAsync(string expression, bool gesture)
        {
            if (view == null || view.CoreWebView2 == null) return null;
            var request = new Dictionary<string, object>
            {
                { "expression", expression }, { "awaitPromise", true }, { "returnByValue", true }, { "userGesture", gesture }
            };
            // A page that is navigating may never answer; nothing waits on it for long.
            var call = view.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", json.Serialize(request));
            if (await Task.WhenAny(call, Task.Delay(gesture ? 8000 : 15000)) != call) throw new TimeoutException("YouTube Music did not answer");
            string reply = await call;
            var parsed = json.Deserialize<Dictionary<string, object>>(reply);
            object exception;
            if (parsed.TryGetValue("exceptionDetails", out exception))
            {
                var details = exception as Dictionary<string, object>;
                object error;
                string message = details != null && details.TryGetValue("exception", out error) && error is Dictionary<string, object> ? Text((Dictionary<string, object>)error, "description") : "script error";
                throw new InvalidOperationException(message);
            }
            object result;
            if (!parsed.TryGetValue("result", out result)) return null;
            var value = result as Dictionary<string, object>;
            object inner;
            return value != null && value.TryGetValue("value", out inner) && inner != null ? inner.ToString() : null;
        }

        // --music-test: mutes the page and checks the API path without an account, using the
        // public playlists on the YouTube Music home page.
        internal async Task<string> DiagnoseAsync()
        {
            await StartAsync();
            if (view == null) return "no page: " + Message;
            view.CoreWebView2.IsMuted = true;
            return await EvaluateAsync(@"(async () => {
  const data = await __arabara.api({ browseId: 'FEmusic_home' });
  const ids = [];
  JSON.stringify(data, (k, v) => { if (k === 'browseId' && typeof v === 'string' && v.indexOf('VL') === 0 && ids.indexOf(v) < 0) ids.push(v); return v; });
  return JSON.stringify(ids.slice(0, 5).map(x => x.substring(2)));
})()", false);
        }

        internal void Mute() { muted = true; if (view != null && view.CoreWebView2 != null) view.CoreWebView2.IsMuted = true; }

        // --soak-test engine --low: the hidden page at WebView2's Low memory target.
        internal void SetLowMemoryForTest() { if (view != null && view.CoreWebView2 != null) view.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; }

        // Called after each poll: rests once nothing has played for RestAfter. A poll or a
        // track change in progress only postpones the check; playing or use restarts the wait.
        private void RestIfIdle()
        {
            if (view == null || disposed || polling || switching || Status == State.Starting) return;
            if (Playing || RestAfter == null || (InUse != null && InUse())) { idleSince = DateTime.MinValue; return; }
            if (idleSince == DateTime.MinValue) { idleSince = DateTime.UtcNow; return; }
            if (DateTime.UtcNow - idleSince >= RestAfter()) Rest();
        }

        // Releases the hidden page while nothing plays (it holds about as much memory as a
        // YouTube Music tab), keeping the playlist, the track and where it was.
        private void Rest()
        {
            poll.Stop();
            if (Queue.Current != null && Position > 0) resumeAt = Position;
            host.Controls.Remove(view);
            view.Dispose();
            view = null;
            starting = null;
            loadedList = null;
            Advertising = false;
            idleSince = DateTime.MinValue;
            Resting = true;
            SetStatus(Status == State.Failed ? State.Off : Status, "RESTING  //  PAGE RELEASED UNTIL PLAY");
        }

        // Releases the hidden page (and its memory) entirely.
        public void Stop()
        {
            poll.Stop();
            if (view != null) { host.Controls.Remove(view); view.Dispose(); view = null; }
            Resting = false; resumeAt = 0; idleSince = DateTime.MinValue; lastPlayed = stuckSince = DateTime.MinValue;
            starting = null;
            Queue.Tracks.Clear();
            Queue.Index = -1;
            Playlist = null;
            Playing = false; Position = Duration = 0; loadedList = null; ThemeMode = false;
            SetStatus(State.Off, "OFF");
        }

        private void SetStatus(State state, string message)
        {
            Status = state;
            Message = message;
            Raise();
        }

        private void Raise() { var handler = Changed; if (handler != null && !disposed) handler(); }

        private static string Text(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value != null ? value.ToString() : null;
        }

        private static bool True(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static double Number(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return 0;
            try { return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch (FormatException) { return 0; }
            catch (InvalidCastException) { return 0; }
        }

        public void Dispose()
        {
            disposed = true;
            poll.Dispose();
            if (view != null) view.Dispose();
            view = null;
        }

        // Installed in the hidden page before its own scripts run. Everything here runs
        // inside music.youtube.com with that page's own session.
        private const string Helper = @"(() => {
  if (window.__arabara) return;
  const A = window.__arabara = {};
  const player = () => document.getElementById('movie_player');
  const video = () => document.querySelector('video');
  const cfg = k => (window.ytcfg && ytcfg.get) ? ytcfg.get(k) : undefined;
  const walk = (o, name, out) => {
    if (!o || typeof o !== 'object') return out;
    if (Array.isArray(o)) { for (const x of o) walk(x, name, out); return out; }
    for (const k in o) { if (k === name) out.push(o[k]); else walk(o[k], name, out); }
    return out;
  };
  const text = t => t ? (t.simpleText || (t.runs || []).map(r => r.text).join('')) : '';
  A.api = async (body, query) => {
    const key = cfg('INNERTUBE_API_KEY');
    const headers = { 'Content-Type': 'application/json', 'X-Origin': location.origin,
      'X-Goog-AuthUser': String(cfg('SESSION_INDEX') || 0) };
    const visitor = cfg('VISITOR_DATA'); if (visitor) headers['X-Goog-Visitor-Id'] = visitor;
    const m = document.cookie.match(/(?:^|;\s*)SAPISID=([^;]+)/) || document.cookie.match(/(?:^|;\s*)__Secure-3PAPISID=([^;]+)/);
    if (m) {
      const ts = Math.floor(Date.now() / 1000);
      const digest = await crypto.subtle.digest('SHA-1', new TextEncoder().encode(ts + ' ' + m[1] + ' ' + location.origin));
      headers.Authorization = 'SAPISIDHASH ' + ts + '_' + Array.from(new Uint8Array(digest)).map(b => b.toString(16).padStart(2, '0')).join('');
    }
    const r = await fetch('/youtubei/v1/browse?prettyPrint=false' + (key ? '&key=' + key : '') + (query || ''),
      { method: 'POST', credentials: 'include', headers, body: JSON.stringify(Object.assign({ context: cfg('INNERTUBE_CONTEXT') }, body)) });
    if (!r.ok) throw new Error('YouTube Music answered ' + r.status);
    return r.json();
  };
  A.token = data => {
    const a = walk(data, 'nextContinuationData', [])[0]; if (a && a.continuation) return a.continuation;
    const b = walk(data, 'continuationCommand', [])[0]; return b && b.token ? b.token : null;
  };
  A.more = async token => {
    const data = await A.api({ continuation: token });
    if (walk(data, 'musicResponsiveListItemRenderer', []).length || walk(data, 'musicTwoRowItemRenderer', []).length) return data;
    return A.api({}, '&ctoken=' + encodeURIComponent(token) + '&continuation=' + encodeURIComponent(token) + '&type=next');
  };
  A.playlists = async () => {
    let data = await A.api({ browseId: 'FEmusic_liked_playlists' });
    const out = [], seen = {};
    for (let page = 0; page < 10 && data; page++) {
      for (const item of walk(data, 'musicTwoRowItemRenderer', [])) {
        const ep = item.navigationEndpoint && item.navigationEndpoint.browseEndpoint;
        if (!ep || !ep.browseId || ep.browseId.indexOf('VL') !== 0) continue;
        const id = ep.browseId.substring(2);
        if (seen[id]) continue; seen[id] = 1;
        out.push({ id, title: text(item.title), subtitle: text(item.subtitle) });
      }
      const token = A.token(data); if (!token) break;
      data = await A.more(token);
    }
    return JSON.stringify(out);
  };
  A.tracks = async pid => {
    let data = await A.api({ browseId: 'VL' + pid });
    const out = [], seen = {};
    for (let page = 0; page < 50 && data; page++) {
      for (const item of walk(data, 'musicResponsiveListItemRenderer', [])) {
        let id = item.playlistItemData && item.playlistItemData.videoId;
        if (!id) { const w = walk(item, 'watchEndpoint', [])[0]; id = w && w.videoId; }
        if (!id || seen[id]) continue; seen[id] = 1;
        const flex = (item.flexColumns || []).map(c => text(c.musicResponsiveListItemFlexColumnRenderer && c.musicResponsiveListItemFlexColumnRenderer.text));
        const fixed = (item.fixedColumns || []).map(c => text(c.musicResponsiveListItemFixedColumnRenderer && c.musicResponsiveListItemFixedColumnRenderer.text));
        out.push({ id, title: flex[0] || '', artist: flex[1] || '', duration: fixed[0] || '' });
      }
      const token = A.token(data); if (!token) break;
      data = await A.more(token);
    }
    return JSON.stringify(out);
  };
  const bar = () => document.querySelector('ytmusic-player-bar');
  // Jumps within YouTube Music's queue (as the open-source pear-desktop player does).
  A.jump = id => {
    if (location.pathname !== '/watch') return 'no';
    const q = document.querySelector('#queue');
    if (!q || !q.queue || !q.queue.getItems || !q.dispatch) return 'no';
    const items = q.queue.getItems();
    for (let i = 0; i < items.length; i++) {
      if (walk(items[i], 'videoId', [])[0] === id) { q.dispatch({ type: 'SET_INDEX', payload: i }); return 'ok'; }
    }
    return 'no';
  };
  A.skip = step => {
    const b = document.querySelector(step > 0 ? '.next-button.ytmusic-player-bar' : '.previous-button.ytmusic-player-bar');
    if (!b) return 'no';
    b.click();
    return 'ok';
  };
  A.repeatMode = () => { const b = bar(); try { return b && b.getState ? b.getState().queue.repeatMode : 'none'; } catch (e) { return 'none'; } };
  A.volume = v => {
    const b = bar(); if (b && b.updateVolume) b.updateVolume(v);
    const p = player(); if (p && p.setVolume) { p.setVolume(v); if (v > 0 && p.unMute) p.unMute(); }
    return 'ok';
  };
  A.settings = (mode, v) => {
    const b = bar();
    if (b && b.onRepeatButtonClick) for (let i = 0; i < 3 && A.repeatMode() !== mode; i++) b.onRepeatButtonClick();
    A.volume(v);
    return A.repeatMode();
  };
  A.pause = () => { const p = player(); if (p && p.pauseVideo) p.pauseVideo(); else if (video()) video().pause(); return 'ok'; };
  A.resume = () => { const p = player(); if (p && p.playVideo) p.playVideo(); else if (video()) video().play(); return 'ok'; };
  // YouTube Music's 'still there?' prompt after long listening: answered, and playback goes on.
  const stillThere = () => { const d = document.querySelector('ytmusic-you-there-renderer'); return d && d.offsetParent !== null ? d : null; };
  A.stillHere = () => { const d = stillThere(); const b = d && d.querySelector('button, tp-yt-paper-button, yt-button-renderer'); if (b) b.click(); return A.resume(); };
  A.seek = s => { const p = player(); if (p && p.seekTo) p.seekTo(s, true); else if (video()) video().currentTime = s; return 'ok'; };
  A.status = () => {
    const p = player(), v = video();
    let id = null;
    try { id = p && p.getVideoData ? p.getVideoData().video_id || null : null; } catch (e) { }
    return JSON.stringify({
      ready: !!cfg('INNERTUBE_API_KEY'), signedIn: !!cfg('LOGGED_IN'), id,
      time: v ? v.currentTime || 0 : 0, duration: v && isFinite(v.duration) ? v.duration : 0,
      paused: v ? v.paused : true, repeat: A.repeatMode(), page: location.pathname,
      ad: !!(p && p.classList && p.classList.contains('ad-showing')), stillThere: !!stillThere()
    });
  };
})();";
    }
}
