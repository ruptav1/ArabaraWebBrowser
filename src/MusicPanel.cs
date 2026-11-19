using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Arabara
{
    // The start page's music terminal: the account's YouTube Music playlists and their
    // tracks, an ASCII record that spins while music plays, and the transport controls
    // (previous, back 10 s, play/pause, forward 10 s, next, repeat off/all/one, volume)
    // plus THEME, which makes the current playlist the start page's background music.
    internal sealed class MusicPanel : Control
    {
        public const int LogicalWidth = 880, LogicalHeight = 486;
        private const int RowHeight = 20, ListTop = 62, ListBottom = 334;
        private const string DiscRamp = " .,:;-=+*#%@";
        private readonly MusicEngine engine;
        private readonly Font bodyFont = UiFonts.Body(10F), smallFont = UiFonts.Body(9F), tinyFont = UiFonts.Body(7.5F), discFont = UiFonts.Body(6F);
        private readonly Timer frame = new Timer { Interval = 66 };
        private readonly List<KeyValuePair<RectangleF, Action>> hits = new List<KeyValuePair<RectangleF, Action>>();
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private List<MusicPlaylist> playlists;
        private MusicPlaylist openPlaylist;
        private List<MusicTrack> tracks;
        private int scroll, hot = -1;
        private string notice;
        private bool busy, draggingVolume;
        private double angle, lastTick;
        private RectangleF progressBar, volumeBar;
        private SizeF discCell;

        public string ThemePlaylistId { get; set; }
        public event Action CloseRequested, SignInRequested, ThemeToggled;

        public MusicPanel(MusicEngine engine)
        {
            this.engine = engine;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = UiPalette.Canvas;
            AccessibleName = "Music";
            AccessibleDescription = "YouTube Music player: playlists, tracks and playback controls.";
            Visible = false;
            frame.Tick += delegate { Tick(); };
            engine.Changed += delegate { if (Visible && !IsDisposed) Invalidate(); };
        }

        // Opens the panel: starts YouTube Music if needed and lists the library.
        public async Task OpenAsync()
        {
            bool wasOpen = IsOpen;
            Visible = true;
            BringToFront();
            Focus();
            if (!wasOpen) StartTransition(true);
            frame.Start();
            if (playlists == null && !busy) await LoadPlaylistsAsync();
        }

        // Open and not on its way out.
        public bool IsOpen { get { return Visible && !(Transitioning && !opening); } }

        // Decays away like the start page's HIDE UI; without animation it simply disappears.
        public void CloseView(bool animate = true)
        {
            if (!Visible) return;
            if (animate && IsHandleCreated) { StartTransition(false); return; }
            FinishClose();
        }

        private void FinishClose()
        {
            transition.Reset();
            frozen = false;
            SetRegion(null);
            Visible = false;
            frame.Stop();
        }

        // ---- Decay transition: cells appear (or vanish) from the centre outwards, with
        // red and white glitch characters at the front, like HIDE UI / SHOW UI. While it
        // runs the control's region holds only the cells in view, so the start page shows
        // through the rest.
        private const int TransitionMs = 650;
        private const float Front = 0.1F;
        private const string Glitch = "!/\\|<>[]{}#%&$@01?*+=~^;";
        private readonly System.Diagnostics.Stopwatch transition = new System.Diagnostics.Stopwatch();
        private readonly Random noise = new Random();
        private readonly Font glitchFont = UiFonts.Body(7.5F);
        private bool opening, frozen;
        private double transitionOffset;
        private float[] threshold;
        private Size thresholdSize;
        private int cellW, cellH, cellCols, cellRows;

        private void StartTransition(bool open)
        {
            // Reversing half-way carries on from the current point.
            float p = Progress;
            frozen = false;
            opening = open;
            transitionOffset = p > 0 && p < 1 ? (open ? p : 1 - p) * TransitionMs : 0;
            transition.Restart();
            frame.Interval = 33;
            frame.Start();
            UpdateRegion();
            Invalidate();
        }

        private bool Transitioning { get { return transition.IsRunning || frozen; } }

        // 0 = nothing shown, 1 = fully shown.
        private float Progress
        {
            get
            {
                if (!Transitioning) return Visible ? 1F : 0F;
                float t = (float)Math.Min(1, (transition.Elapsed.TotalMilliseconds + transitionOffset) / TransitionMs);
                return opening ? t : 1 - t;
            }
        }

        private void EnsureThresholds()
        {
            float scale = DeviceDpi / 96F;
            int w = Math.Max(4, (int)(8 * scale)), h = Math.Max(6, (int)(14 * scale));
            if (threshold != null && thresholdSize == ClientSize && w == cellW) return;
            cellW = w; cellH = h;
            thresholdSize = ClientSize;
            cellCols = ClientSize.Width / cellW + 1; cellRows = ClientSize.Height / cellH + 1;
            threshold = new float[cellCols * cellRows];
            float midX = cellCols / 2F, midY = cellRows / 2F;
            float reach = (float)Math.Sqrt(midX * midX * cellW * cellW + midY * midY * cellH * cellH);
            for (int r = 0; r < cellRows; r++)
                for (int c = 0; c < cellCols; c++)
                {
                    float dx = (c - midX) * cellW, dy = (r - midY) * cellH;
                    threshold[r * cellCols + c] = Math.Min(1F, (float)Math.Sqrt(dx * dx + dy * dy) / reach * 0.82F + (float)noise.NextDouble() * 0.18F);
                }
        }

        // Cells below `solid` show the panel; from there up to `edge`, glitch characters.
        private void Limits(out float solid, out float edge)
        {
            edge = Progress * (1 + Front);
            solid = edge - Front;
        }

        private void SetRegion(Region region)
        {
            var old = Region;
            Region = region;
            if (old != null) old.Dispose();
        }

        private void UpdateRegion()
        {
            if (!Transitioning) { SetRegion(null); return; }
            EnsureThresholds();
            float solid, edge;
            Limits(out solid, out edge);
            using (var path = new GraphicsPath())
            {
                for (int r = 0; r < cellRows; r++)
                {
                    int start = -1;
                    for (int c = 0; c <= cellCols; c++)
                    {
                        bool shown = c < cellCols && threshold[r * cellCols + c] < edge;
                        if (shown && start < 0) start = c;
                        else if (!shown && start >= 0) { path.AddRectangle(new Rectangle(start * cellW, r * cellH, (c - start) * cellW, cellH)); start = -1; }
                    }
                }
                SetRegion(new Region(path));
            }
        }

        // --music-test: freezes the opening transition at progress p (0..1).
        internal void FreezeTransitionForTest(float p)
        {
            opening = true;
            transitionOffset = p * TransitionMs;
            transition.Reset();
            frozen = true;
            UpdateRegion();
        }

        private void DrawGlitch(Graphics g)
        {
            if (!Transitioning || threshold == null) return;
            float solid, edge;
            Limits(out solid, out edge);
            var saved = g.Save();
            g.ResetTransform();
            using (var red = new SolidBrush(Color.FromArgb(255, 52, 80)))
            using (var white = new SolidBrush(Color.White))
            using (var back = new SolidBrush(UiPalette.Canvas))
                for (int r = 0; r < cellRows; r++)
                    for (int c = 0; c < cellCols; c++)
                    {
                        float v = threshold[r * cellCols + c];
                        if (v < solid || v >= edge) continue;
                        g.FillRectangle(back, c * cellW, r * cellH, cellW, cellH);
                        g.DrawString(Glitch[noise.Next(Glitch.Length)].ToString(), glitchFont, noise.Next(2) == 0 ? red : white, c * cellW, r * cellH, StringFormat.GenericTypographic);
                    }
            g.Restore(saved);
        }

        private async Task LoadPlaylistsAsync()
        {
            busy = true; notice = "LOADING LIBRARY"; Invalidate();
            try
            {
                var list = await engine.LoadPlaylistsAsync();
                if (engine.Status == MusicEngine.State.Ready) playlists = list;
                notice = engine.Status == MusicEngine.State.SignedOut ? null : list.Count == 0 && engine.Status == MusicEngine.State.Ready ? "NO SAVED PLAYLISTS" : null;
            }
            catch (Exception ex) { notice = "LIBRARY FAILED: " + ex.Message; }
            busy = false;
            Invalidate();
        }

        private async Task OpenPlaylistAsync(MusicPlaylist playlist)
        {
            busy = true; notice = "LOADING " + playlist.Title.ToUpperInvariant(); Invalidate();
            try
            {
                var list = await engine.LoadTracksAsync(playlist.Id);
                openPlaylist = playlist;
                tracks = list;
                scroll = 0;
                notice = list.Count == 0 ? "PLAYLIST IS EMPTY" : null;
            }
            catch (Exception ex) { notice = "PLAYLIST FAILED: " + ex.Message; }
            busy = false;
            Invalidate();
        }

        private async Task PlayAsync(int index)
        {
            engine.ThemeMode = false;
            try { await engine.PlayAsync(openPlaylist, tracks, index); }
            catch (Exception ex) { notice = "PLAYBACK FAILED: " + ex.Message; }
            Invalidate();
        }

        private void Tick()
        {
            if (Transitioning)
            {
                float p = Progress;
                if (!opening && p <= 0) { FinishClose(); return; }
                if (opening && p >= 1) { transition.Reset(); SetRegion(null); }
                else UpdateRegion();
            }
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.25, now - lastTick);
            lastTick = now;
            // 33 1/3 rpm while playing; a slower frame rate while still, and once a second while
            // another window is in front (a game) or Arabara is minimized.
            if (engine.Playing && !engine.Advertising) angle += dt * 2 * Math.PI * 0.5556;
            var host = FindForm();
            bool seen = host != null && Form.ActiveForm == host && host.WindowState != FormWindowState.Minimized;
            frame.Interval = Transitioning ? 33 : !seen ? 1000 : engine.Playing ? 66 : 400;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var point = Logical(e.Location);
            if (draggingVolume) { SetVolumeAt(point.X); return; }
            int index = hits.FindIndex(h => h.Key.Contains(point));
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            if (index != hot) { hot = index; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot >= 0) { hot = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            var point = Logical(e.Location);
            if (volumeBar.Contains(point)) { draggingVolume = true; SetVolumeAt(point.X); return; }
            if (progressBar.Contains(point) && engine.Duration > 0 && !engine.Advertising)
            { var _ = engine.SeekAsync((point.X - progressBar.X) / progressBar.Width * engine.Duration); return; }
            int index = hits.FindIndex(h => h.Key.Contains(point));
            if (index >= 0) hits[index].Value();
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); draggingVolume = false; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int count = tracks != null && openPlaylist != null ? tracks.Count : playlists != null ? playlists.Count : 0;
            scroll = Math.Max(0, Math.Min(Math.Max(0, count - VisibleRows), scroll - Math.Sign(e.Delta) * 3));
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Space || keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Raise(CloseRequested); e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { var _ = engine.TogglePauseAsync(); engine.ThemeMode = false; e.Handled = true; }
            else if (e.KeyCode == Keys.Left) { var _ = engine.SeekAsync(engine.Position - 10); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { var _ = engine.SeekAsync(engine.Position + 10); e.Handled = true; }
        }

        private void SetVolumeAt(float x)
        {
            int volume = (int)Math.Round((x - volumeBar.X) / volumeBar.Width * 100);
            var _ = engine.SetVolumeAsync(volume);
        }

        private static void Raise(Action action) { if (action != null) action(); }

        private float DpiScale { get { return DeviceDpi / 96F; } }
        private PointF Logical(Point p) { return new PointF(p.X / DpiScale, p.Y / DpiScale); }
        private static int VisibleRows { get { return (ListBottom - ListTop) / RowHeight; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(UiPalette.Canvas);
            g.ScaleTransform(DpiScale, DpiScale);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            hits.Clear();
            using (var pen = new Pen(UiPalette.Border))
            {
                g.DrawRectangle(pen, 0, 8, LogicalWidth - 1, LogicalHeight - 9);
                g.DrawRectangle(pen, 3, 11, LogicalWidth - 7, LogicalHeight - 15);
                g.DrawLine(pen, 344, 30, 344, 334);
                g.DrawLine(pen, 16, 352, LogicalWidth - 16, 352);
            }
            Notch(g, " MUSIC  //  YOUTUBE MUSIC ", 17, 0);
            Button(g, "[ X ]", LogicalWidth - 60, 16, 44, delegate { Raise(CloseRequested); });
            DrawDisc(g, 172, 166, 124);
            DrawNowPlaying(g);
            DrawList(g);
            DrawTransport(g);
            DrawGlitch(g);
        }

        // The record: grooves that catch a fixed light, dust that turns with it, and a
        // crimson label with a bar that shows the rotation.
        private void DrawDisc(Graphics g, float cx, float cy, float radius)
        {
            if (discCell.IsEmpty)
                using (var measure = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces })
                {
                    // The advance of one character, measured over a long run as it is drawn.
                    var run = g.MeasureString(new string('M', 40), discFont, PointF.Empty, measure);
                    discCell = new SizeF(run.Width / 40, discFont.GetHeight(g));
                }
            float cw = Math.Max(2, discCell.Width), ch = Math.Max(4, discCell.Height);
            int cols = (int)(radius * 2 / cw) + 2, rows = (int)(radius * 2 / ch) + 2;
            float x0 = cx - cols * cw / 2, y0 = cy - rows * ch / 2;
            var tones = new[] { UiPalette.Border, UiPalette.Muted, UiPalette.Ink, Color.FromArgb(150, 16, 34), UiPalette.Accent, Color.FromArgb(255, 170, 180) };
            var layers = new char[tones.Length][];
            for (int t = 0; t < tones.Length; t++) layers[t] = new char[cols];
            using (var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces })
                for (int r = 0; r < rows; r++)
                {
                    foreach (var layer in layers) for (int c = 0; c < cols; c++) layer[c] = ' ';
                    float py = y0 + (r + 0.5F) * ch;
                    for (int c = 0; c < cols; c++)
                    {
                        float px = x0 + (c + 0.5F) * cw;
                        double dx = (px - cx) / radius, dy = (py - cy) / radius, d = Math.Sqrt(dx * dx + dy * dy);
                        if (d > 1) continue;
                        double theta = Math.Atan2(dy, dx), spun = theta - angle;
                        char glyph; int tone;
                        if (d < 0.045) continue; // spindle hole
                        if (d < 0.075) { glyph = 'o'; tone = 1; }
                        else if (d < 0.34)
                        {
                            // Crimson label with a bright bar that turns with the record.
                            double bar = Math.Abs(Math.IEEERemainder(spun, Math.PI));
                            if (d > 0.11 && d < 0.29 && bar < 0.32) { glyph = '#'; tone = 5; }
                            else if (d > 0.31) { glyph = '='; tone = 4; }
                            else { glyph = '%'; tone = 3; }
                        }
                        else if (d > 0.97) { glyph = 'O'; tone = 1; }
                        else
                        {
                            // Fine grooves, two gaps between tracks, a fixed sheen and dust that turns.
                            int groove = (int)(d * 52);
                            if (Math.Abs(d - 0.56) < 0.012 || Math.Abs(d - 0.77) < 0.012) continue;
                            double sheen = Math.Pow(Math.Abs(Math.Cos(theta + Math.PI / 4)), 10);
                            int ring = (int)(d * 60), sector = (int)Math.Floor((spun / (2 * Math.PI) + 10) * 70);
                            bool dust = ((ring * 7919 + sector * 104729) % 97) == 0;
                            if (dust) { glyph = '*'; tone = 2; }
                            else if (sheen > 0.8) { glyph = groove % 2 == 0 ? '=' : '-'; tone = 2; }
                            else if (sheen > 0.35) { glyph = groove % 2 == 0 ? '-' : ':'; tone = 1; }
                            else { glyph = groove % 2 == 0 ? ':' : '.'; tone = 0; }
                        }
                        layers[tone][c] = glyph;
                    }
                    for (int t = 0; t < tones.Length; t++)
                    {
                        string line = new string(layers[t]);
                        if (line.Trim().Length == 0) continue;
                        using (var brush = new SolidBrush(tones[t])) g.DrawString(line, discFont, brush, x0, y0 + r * ch, format);
                    }
                }
            // Tone arm.
            using (var arm = new Pen(UiPalette.Muted, 2))
            {
                g.DrawLine(arm, cx + radius + 4, cy - radius + 6, cx + radius - 6, cy + 10);
                g.DrawLine(arm, cx + radius - 6, cy + 10, cx + radius * 0.62F, cy + radius * 0.42F);
            }
            using (var hub = new SolidBrush(UiPalette.Border)) g.FillEllipse(hub, cx + radius - 2, cy - radius, 12, 12);
        }

        private void DrawNowPlaying(Graphics g)
        {
            var track = engine.Queue.Current;
            string title = track == null ? "NO TRACK" : track.Title, artist = track == null ? "pick a playlist and a track" : track.Artist;
            Write(g, title, bodyFont, track == null ? UiPalette.Muted : UiPalette.Accent, 20, 300, 316, 22);
            if (engine.Advertising) artist = "ADVERTISEMENT  //  " + artist;
            Write(g, artist, smallFont, UiPalette.Muted, 20, 324, 316, 18);
        }

        private void DrawList(Graphics g)
        {
            int left = 358, width = LogicalWidth - left - 18;
            bool inPlaylist = openPlaylist != null && tracks != null;
            if (inPlaylist)
            {
                Button(g, "[ < LIBRARY ]", left, 30, 108, delegate { openPlaylist = null; tracks = null; scroll = 0; notice = null; Invalidate(); });
                Write(g, openPlaylist.Title.ToUpperInvariant() + "  //  " + tracks.Count + " TRACKS", smallFont, UiPalette.Ink, left + 116, 30, width - 116, 22);
            }
            else Write(g, "LIBRARY  //  " + (playlists == null ? "--" : playlists.Count + " PLAYLISTS"), smallFont, UiPalette.Ink, left, 30, width, 22);

            if (engine.Status == MusicEngine.State.SignedOut && playlists == null && tracks == null)
            {
                Write(g, "NOT SIGNED IN TO YOUTUBE MUSIC.", smallFont, UiPalette.Accent, left, ListTop + 10, width, 20);
                Write(g, "Sign in once in a normal tab; Arabara uses that session.", smallFont, UiPalette.Muted, left, ListTop + 32, width, 20);
                Button(g, "[ OPEN MUSIC.YOUTUBE.COM TO SIGN IN ]", left, ListTop + 62, 300, delegate { Raise(SignInRequested); });
                Button(g, "[ RETRY ]", left + 308, ListTop + 62, 80, delegate { playlists = null; var _ = RetryAsync(); });
                return;
            }
            if (busy || notice != null || engine.Status == MusicEngine.State.Starting || engine.Status == MusicEngine.State.Failed)
            {
                string message = engine.Status == MusicEngine.State.Failed ? engine.Message : notice ?? engine.Message;
                Write(g, (busy || engine.Status == MusicEngine.State.Starting ? "[" + "|/-\\"[(int)(clock.ElapsedMilliseconds / 120 % 4)] + "] " : "") + message,
                    smallFont, engine.Status == MusicEngine.State.Failed ? UiPalette.Accent : UiPalette.Muted, left, ListTop + 10, width, 20);
                if (engine.Status == MusicEngine.State.Failed) Button(g, "[ RETRY ]", left, ListTop + 40, 80, delegate { var _ = RetryAsync(); });
                if (busy || engine.Status != MusicEngine.State.Ready) return;
            }
            int count = inPlaylist ? tracks.Count : playlists == null ? 0 : playlists.Count;
            scroll = Math.Max(0, Math.Min(Math.Max(0, count - VisibleRows), scroll));
            int top = ListTop + (notice != null ? 26 : 0);
            for (int i = scroll; i < count && top + RowHeight <= ListBottom; i++, top += RowHeight)
            {
                var bounds = new RectangleF(left - 4, top, width - 10, RowHeight);
                int index = i;
                bool rowHot = hits.Count == hot;
                if (inPlaylist)
                {
                    var track = tracks[i];
                    bool current = engine.Playlist != null && engine.Playlist.Id == openPlaylist.Id && engine.Queue.Index == i;
                    hits.Add(new KeyValuePair<RectangleF, Action>(bounds, delegate { var _ = PlayAsync(index); }));
                    if (rowHot || current) using (var fill = new SolidBrush(current ? Color.FromArgb(34, 12, 16) : UiPalette.Active)) g.FillRectangle(fill, bounds);
                    Write(g, current ? (engine.Playing ? ">>" : "||") : (i + 1).ToString("000"), tinyFont, current ? UiPalette.Accent : UiPalette.Muted, left, top, 30, RowHeight);
                    Write(g, track.Title, smallFont, current || rowHot ? UiPalette.Accent : UiPalette.Ink, left + 34, top, width * 0.52F, RowHeight);
                    Write(g, track.Artist, tinyFont, UiPalette.Muted, left + 40 + width * 0.52F, top, width * 0.3F, RowHeight);
                    Write(g, track.Duration, tinyFont, UiPalette.Muted, left + width - 62, top, 44, RowHeight, StringAlignment.Far);
                }
                else
                {
                    var playlist = playlists[i];
                    hits.Add(new KeyValuePair<RectangleF, Action>(bounds, delegate { var _ = OpenPlaylistAsync(playlist); }));
                    if (rowHot) using (var fill = new SolidBrush(UiPalette.Active)) g.FillRectangle(fill, bounds);
                    bool theme = playlist.Id == ThemePlaylistId;
                    Write(g, "> " + playlist.Title, smallFont, rowHot ? UiPalette.Accent : UiPalette.Ink, left, top, width * 0.6F, RowHeight);
                    Write(g, (theme ? "[THEME]  " : "") + playlist.Subtitle, tinyFont, theme ? UiPalette.Accent : UiPalette.Muted, left + width * 0.6F, top, width * 0.4F - 18, RowHeight, StringAlignment.Far);
                }
            }
            // ASCII scroll bar.
            if (count > VisibleRows)
            {
                int track = (ListBottom - ListTop) / 12;
                int thumb = Math.Max(1, track * VisibleRows / count), at = (track - thumb) * scroll / Math.Max(1, count - VisibleRows);
                for (int k = 0; k < track; k++)
                    Write(g, k >= at && k < at + thumb ? "#" : "|", tinyFont, k >= at && k < at + thumb ? UiPalette.Accent : UiPalette.Border, LogicalWidth - 22, ListTop + k * 12, 10, 12);
            }
        }

        // --music-test: shows a playlist without going through the library.
        internal void ShowForTest(List<MusicPlaylist> library, MusicPlaylist playlist, List<MusicTrack> list)
        {
            playlists = library; openPlaylist = playlist; tracks = list; notice = null;
        }

        private async Task RetryAsync()
        {
            if (engine.Status == MusicEngine.State.Failed) engine.Stop();
            notice = null;
            await LoadPlaylistsAsync();
        }

        private void DrawTransport(Graphics g)
        {
            // Progress: elapsed, a striped bar (click to seek) and the length.
            double length = engine.Duration, position = Math.Min(engine.Position, length);
            Write(g, Clock(position), smallFont, UiPalette.Ink, 20, 364, 60, 20);
            Write(g, Clock(length), smallFont, UiPalette.Muted, LogicalWidth - 80, 364, 60, 20, StringAlignment.Far);
            progressBar = new RectangleF(88, 368, LogicalWidth - 176, 12);
            using (var pen = new Pen(UiPalette.Border)) g.DrawRectangle(pen, progressBar.X, progressBar.Y, progressBar.Width, progressBar.Height);
            if (length > 0)
            {
                float done = (float)(position / length) * (progressBar.Width - 4);
                using (var fill = new SolidBrush(engine.Advertising ? UiPalette.Muted : UiPalette.Accent))
                    for (float x = 0; x < done; x += 4) g.FillRectangle(fill, progressBar.X + 2 + x, progressBar.Y + 2, Math.Min(2, done - x), progressBar.Height - 4);
            }

            float y = 398, x0 = 20;
            Button(g, "[ |< ]", x0, y, 52, delegate { engine.ThemeMode = false; var _ = engine.PreviousAsync(); });
            Button(g, "[ <<10 ]", x0 += 58, y, 66, delegate { var _ = engine.SeekAsync(engine.Position - 10); });
            Button(g, engine.Playing ? "[ PAUSE ]" : "[ PLAY ]", x0 += 72, y, 80, delegate { engine.ThemeMode = false; var _ = engine.TogglePauseAsync(); }, engine.Playing);
            Button(g, "[ 10>> ]", x0 += 86, y, 66, delegate { var _ = engine.SeekAsync(engine.Position + 10); });
            Button(g, "[ >| ]", x0 += 72, y, 52, delegate { engine.ThemeMode = false; var _ = engine.NextAsync(); });
            string repeat = engine.Queue.Repeat == MusicRepeat.One ? "ONE" : engine.Queue.Repeat == MusicRepeat.All ? "ALL" : "OFF";
            Button(g, "[ REPEAT " + repeat + " ]", x0 += 64, y, 116, delegate
            {
                var next = engine.Queue.Repeat == MusicRepeat.Off ? MusicRepeat.All : engine.Queue.Repeat == MusicRepeat.All ? MusicRepeat.One : MusicRepeat.Off;
                var _ = engine.SetRepeatAsync(next);
            }, engine.Queue.Repeat != MusicRepeat.Off);

            // Volume: a draggable bar.
            Write(g, "VOL", smallFont, UiPalette.Muted, x0 += 128, y, 34, 24);
            volumeBar = new RectangleF(x0 + 36, y + 7, 100, 10);
            using (var pen = new Pen(UiPalette.Border)) g.DrawRectangle(pen, volumeBar.X, volumeBar.Y, volumeBar.Width, volumeBar.Height);
            using (var fill = new SolidBrush(UiPalette.Accent))
                for (float x = 0; x < engine.Volume - 1; x += 4) g.FillRectangle(fill, volumeBar.X + 1 + x, volumeBar.Y + 2, 2, volumeBar.Height - 4);
            Write(g, engine.Volume.ToString("000"), smallFont, UiPalette.Ink, volumeBar.Right + 6, y, 34, 24);

            bool canTheme = engine.Playlist != null;
            bool isTheme = canTheme && engine.Playlist.Id == ThemePlaylistId;
            if (canTheme || ThemePlaylistId != null)
                Button(g, isTheme || (!canTheme && ThemePlaylistId != null) ? "[ THEME - ]" : "[ THEME + ]", LogicalWidth - 196, y, 96, delegate { Raise(ThemeToggled); }, isTheme);
            Button(g, "[ EJECT ]", LogicalWidth - 94, y, 76, delegate { engine.Stop(); playlists = null; openPlaylist = null; tracks = null; Invalidate(); });

            string line = engine.Status == MusicEngine.State.Off ? "ENGINE OFF  //  OPENS ON DEMAND" :
                "ENGINE " + engine.Message + (engine.ThemeMode ? "  //  PLAYING AS START PAGE THEME" : "") +
                (ThemePlaylistId != null ? "  //  THEME SET" : "") + "  //  SPACE PLAY/PAUSE  //  ARROWS SEEK  //  ESC CLOSE";
            Write(g, line, tinyFont, UiPalette.Muted, 20, 440, LogicalWidth - 40, 16);
            Write(g, "Playback runs in a hidden YouTube Music page signed in with your browser session.", tinyFont, Color.FromArgb(76, 69, 62), 20, 458, LogicalWidth - 40, 16);
        }

        private static string Clock(double seconds)
        {
            if (seconds <= 0 || double.IsNaN(seconds)) return "--:--";
            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
        }

        private void Button(Graphics g, string label, float x, float y, float width, Action action, bool lit = false)
        {
            var bounds = new RectangleF(x, y, width, 24);
            bool isHot = hits.Count == hot;
            hits.Add(new KeyValuePair<RectangleF, Action>(bounds, action));
            using (var fill = new SolidBrush(isHot ? UiPalette.Active : UiPalette.Canvas)) g.FillRectangle(fill, bounds);
            using (var pen = new Pen(isHot || lit ? UiPalette.Accent : UiPalette.Border)) g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            Write(g, label, smallFont, isHot || lit ? UiPalette.Accent : UiPalette.Ink, x, y, width, 24, StringAlignment.Center);
        }

        private void Notch(Graphics g, string text, int x, int y)
        {
            float width = g.MeasureString(text, smallFont).Width + 4;
            using (var background = new SolidBrush(UiPalette.Canvas)) g.FillRectangle(background, x, y, width, 20);
            Write(g, text, smallFont, UiPalette.Muted, x + 1, y, width - 2, 20);
        }

        private static void Write(Graphics g, string text, Font font, Color color, float x, float y, float width, float height,
            StringAlignment align = StringAlignment.Near)
        {
            using (var format = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            using (var brush = new SolidBrush(color)) g.DrawString(text ?? "", font, brush, new RectangleF(x, y, Math.Max(1, width), height), format);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                frame.Dispose();
                bodyFont.Dispose(); smallFont.Dispose(); tinyFont.Dispose(); discFont.Dispose(); glitchFont.Dispose();
                SetRegion(null);
            }
            base.Dispose(disposing);
        }
    }
}
