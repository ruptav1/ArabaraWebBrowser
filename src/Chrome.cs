using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Arabara
{
    internal static class UiPalette
    {
        public static readonly Color Canvas = Color.FromArgb(5, 5, 5);
        public static readonly Color Surface = Color.FromArgb(12, 11, 11);
        public static readonly Color Active = Color.FromArgb(46, 16, 21);
        public static readonly Color Hover = Color.FromArgb(28, 20, 19);
        public static readonly Color Border = Color.FromArgb(82, 64, 59);
        public static readonly Color Ink = Color.FromArgb(229, 218, 189);
        public static readonly Color Muted = Color.FromArgb(172, 159, 135);
        public static readonly Color Accent = Color.FromArgb(255, 70, 92);

    }

    internal sealed class HttpsAddressBox : TextBox
    {
        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (Text == AddressPolicy.HttpsPrefix) Select(TextLength, 0);
        }

        protected override void WndProc(ref Message message)
        {
            const int Paste = 0x0302;
            if (message.Msg == Paste && Text == AddressPolicy.HttpsPrefix)
            {
                // A complete pasted URL replaces the starter prefix, including
                // when Paste comes from the native context menu or Shift+Insert.
                try
                {
                    string pasted = Clipboard.GetText().TrimStart();
                    if (pasted.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        pasted.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || LocalPath.TryParseInput(pasted) != null) SelectAll();
                }
                catch (System.Runtime.InteropServices.ExternalException) { }
            }
            base.WndProc(ref message);
        }
    }

    internal sealed class ChromeButton : Button
    {
        private bool hovered;
        public ChromeButton() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(hovered && Enabled ? UiPalette.Hover : BackColor);
            using (var pen = new Pen(hovered && Enabled ? UiPalette.Accent : UiPalette.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var shadow = new Pen(UiPalette.Canvas))
                e.Graphics.DrawLine(shadow, 1, Height - 2, Width - 2, Height - 2);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            flags |= TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            int inset = Width < 42 ? 2 : 8;
            var bounds = new Rectangle(inset, 0, Math.Max(0, Width - inset * 2), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : Color.FromArgb(96, 100, 112), flags);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(UiPalette.Accent)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
    }

    // A borderless switch in the status line, as text: its name in muted ink and its value,
    // red while its saving mode is on; a dot separates it from the switch on its left. The
    // name brightens on hover. Its width follows its text.
    internal sealed class SwitchLabel : Button
    {
        private bool hovered, on;
        private string caption = "", setting = "";
        public bool Separated = true;
        public bool On { get { return on; } set { if (on != value) { on = value; Invalidate(); } } }
        public string Value { get { return setting; } set { if (setting != (value ?? "")) { setting = value ?? ""; Fit(); } } }
        public string Caption { get { return caption; } set { caption = value ?? ""; Text = caption; Fit(); } }

        public SwitchLabel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
        }

        private const TextFormatFlags Flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        private string Lead { get { return Separated ? "·  " : ""; } }

        private void Fit()
        {
            string all = Lead + caption + (setting.Length > 0 ? " " + setting : "");
            Width = TextRenderer.MeasureText(all, Font, Size.Empty, Flags).Width + (int)Math.Round(14 * DeviceDpi / 96F);
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int x = (int)Math.Round(7 * DeviceDpi / 96F);
            var parts = new[]
            {
                new KeyValuePair<string, Color>(Lead, Color.FromArgb(122, 108, 92)),
                new KeyValuePair<string, Color>(caption + (setting.Length > 0 ? " " : ""), hovered ? UiPalette.Ink : UiPalette.Muted),
                new KeyValuePair<string, Color>(setting, on ? UiPalette.Accent : hovered ? UiPalette.Ink : UiPalette.Muted)
            };
            foreach (var part in parts)
            {
                if (part.Key.Length == 0) continue;
                TextRenderer.DrawText(g, part.Key, Font, new Rectangle(x, 0, Width, Height), part.Value, Flags);
                x += TextRenderer.MeasureText(g, part.Key, Font, Size.Empty, Flags).Width;
            }
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(UiPalette.Accent)) g.DrawRectangle(pen, 0, 2, Width - 1, Height - 5);
        }
    }

    internal enum CaptionKind { Minimize, Maximize, Close }

    // Window buttons drawn in the tab strip, which serves as the title bar.
    internal sealed class CaptionButton : Control
    {
        private bool hovered, pressed;
        public CaptionKind Kind { get; private set; }

        public CaptionButton(CaptionKind kind)
        {
            Kind = kind;
            Dock = DockStyle.Right;
            Width = 46;
            TabStop = false;
            BackColor = UiPalette.Canvas;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = kind == CaptionKind.Minimize ? "Minimize" : kind == CaptionKind.Maximize ? "Maximize" : "Close";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            bool close = Kind == CaptionKind.Close;
            Color back = hovered ? (close ? Color.FromArgb(pressed ? 160 : 196, 24, 46) : (pressed ? UiPalette.Active : UiPalette.Hover)) : BackColor;
            g.Clear(back);
            var form = FindForm();
            bool active = form == null || Form.ActiveForm == form;
            Color ink = hovered && close ? Color.White : hovered ? UiPalette.Accent : active ? UiPalette.Ink : UiPalette.Muted;
            float scale = DeviceDpi / 96F;
            int box = (int)Math.Round(10 * scale), cx = Width / 2, cy = Height / 2;
            int left = cx - box / 2, top = cy - box / 2;
            using (var pen = new Pen(ink, Math.Max(1F, (float)Math.Round(scale))))
            {
                if (Kind == CaptionKind.Minimize) g.DrawLine(pen, left, cy, left + box, cy);
                else if (Kind == CaptionKind.Maximize)
                {
                    if (form != null && form.WindowState == FormWindowState.Maximized)
                    {
                        int step = Math.Max(2, box / 5), inner = box - step;
                        g.DrawRectangle(pen, left, top + step, inner, inner);
                        g.DrawLine(pen, left + step, top, left + box, top);
                        g.DrawLine(pen, left + box, top, left + box, top + inner);
                    }
                    else g.DrawRectangle(pen, left, top, box, box);
                }
                else
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawLine(pen, left, top, left + box, top + box);
                    g.DrawLine(pen, left + box, top, left, top + box);
                }
            }
        }
    }

    // The striped Arabara mark shown in place of the wordmark at the top-left corner.
    internal sealed class MarkLabel : Control
    {
        public MarkLabel()
        {
            AccessibleName = "Arabara";
            AccessibleRole = AccessibleRole.Graphic;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            float scale = DeviceDpi / 96F;
            float width = ClientSize.Width / scale, height = ClientSize.Height / scale;
            float markHeight = Math.Min(28F, height - 4), markWidth = markHeight * 184F / 76F;
            HomeView.DrawStripedMark(e.Graphics, scale, 2, (height - markHeight) / 2, Math.Min(markWidth, width - 4), markHeight);
        }
    }

    // Snapshot of browser state shown on the start page, read on each paint.
    internal sealed class HomeInfo
    {
        public IList<HistoryEntry> Recent;
        public int HistoryCount, Tabs;
        public bool EngineOnline, LowMemory, LiveVisuals;
        public string Session;
    }

    // Native start page. A spinning ASCII mark behind the panels, spinners, a
    // blinking cursor and a one-time typewriter run at about 12 frames per second,
    // only while the page is visible in the active, non-minimized window.
    internal sealed class HomeView : Control
    {
        private const string Spinner = "|/-\\";
        private const int TypeStep = 70, TypeChar = 12, TypeLines = 20;
        private static readonly Color Grid = Color.FromArgb(21, 19, 18);
        private static readonly Color Faint = Color.FromArgb(76, 69, 62);
        private static readonly Color Ok = Color.FromArgb(132, 214, 150);
        private readonly Font titleFont = UiFonts.Wordmark(34F);
        private readonly Font bodyFont = UiFonts.Body(10F);
        private readonly Font smallFont = UiFonts.Body(9F);
        private readonly Font tinyFont = UiFonts.Body(7.5F);
        private readonly List<KeyValuePair<RectangleF, string>> links = new List<KeyValuePair<RectangleF, string>>();
        private int hovered = -1;
        private readonly Timer animation = new Timer { Interval = 83 };
        // While the animation is stopped (another window in front, or minimized), the frame
        // buffers and the scene's data are let go after a minute; a later paint rebuilds them.
        private readonly Timer idleRelease = new Timer { Interval = 60000 };
        private readonly Stopwatch spin = new Stopwatch(), typing = new Stopwatch();
        private SpinningMark mark;
        private Form host;
        private Bitmap overlay;
        private int[] overlayIndex, overlayColor;
        private int overlayCount;
        private Size overlaySize;
        private bool staticDirty = true, ticking;
        private readonly List<Rectangle> panes = new List<Rectangle>();
        private Rectangle leftColumn, rightColumn;
        // "View only": the interface decays away (and back) so only the animations remain.
        private const int VeilMs = 900;
        private readonly Stopwatch veilClock = new Stopwatch();
        private bool uiHidden, veiling, chipHot, musicHot;
        internal float? VeilOverride; // --home-test only
        public bool UiHidden { get { return uiHidden; } }

        public Func<HomeInfo> Info { get; set; }
        // Whether the music terminal is open over the page (the background then takes no input).
        public Func<bool> MusicOpen { get; set; }

        // The background takes the mouse only in view-only mode, with the music terminal closed.
        private bool BackgroundInteractive { get { return uiHidden && !veiling && mark != null && (MusicOpen == null || !MusicOpen()); } }
        public event Action<string> OpenRequested;
        public event Action MusicRequested;

        public HomeView()
        {
            Dock = DockStyle.Fill;
            BackColor = UiPalette.Canvas;
            AccessibleName = "Arabara terminal";
            AccessibleDescription = "Start page: the striped Arabara mark and title, a boot log, recent pages, system status and shortcuts.";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            animation.Tick += delegate { ticking = true; Invalidate(); ticking = false; };
            idleRelease.Tick += delegate { idleRelease.Stop(); if (!animation.Enabled) ReleaseFrames(); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            host = FindForm();
            if (host != null) { host.Activated += HostChanged; host.Deactivate += HostChanged; host.Resize += HostChanged; }
            UpdateAnimation();
        }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateAnimation(); }
        protected override void OnParentVisibleChanged(EventArgs e) { base.OnParentVisibleChanged(e); UpdateAnimation(); }
        private void HostChanged(object sender, EventArgs e) { UpdateAnimation(); }

        private void UpdateAnimation()
        {
            bool run = IsHandleCreated && !IsDisposed && Visible && host != null &&
                host.WindowState != FormWindowState.Minimized && Form.ActiveForm == host;
            if (run && !animation.Enabled) { animation.Start(); spin.Start(); typing.Start(); idleRelease.Stop(); }
            else if (!run && animation.Enabled) { animation.Stop(); spin.Stop(); typing.Stop(); idleRelease.Stop(); idleRelease.Start(); }
            // A hidden page keeps no frame buffers.
            if (!run && !Visible) ReleaseFrames();
        }

        // One animation frame painted synchronously (used by --home-test).
        internal void PaintFrameNow() { ticking = true; Invalidate(); ticking = false; Update(); }

        private void ReleaseFrames()
        {
            if (mark != null) mark.Release();
            if (overlay != null) overlay.Dispose();
            overlay = null;
            overlayIndex = overlayColor = null;
            overlayCount = 0;
            staticDirty = true;
        }

        // Typewriter text: line `order` appears after the previous ones. Shown in full
        // while the animation is paused, so a static page never hides text.
        private string Typed(string text, int order)
        {
            if (!animation.Enabled) return text;
            long shown = (typing.ElapsedMilliseconds - order * TypeStep) / TypeChar;
            return shown >= text.Length ? text : shown <= 0 ? "" : text.Substring(0, (int)shown);
        }

        private char Spin { get { return Spinner[(int)(spin.ElapsedMilliseconds / 120 % Spinner.Length)]; } }
        private bool CursorOn { get { return spin.ElapsedMilliseconds / 500 % 2 == 0; } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || chipHot || musicHot || !BackgroundInteractive) return;
            // RINGS: press anywhere and move to run the text faster.
            if (mark.OnRings) mark.BeginRingSpin(e.Location);
            else if (mark.HitMark(e.Location)) mark.BeginDrag(e.Location);
            else return;
            Capture = true;
            Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (mark != null && mark.RingSpinning) { mark.EndRingSpin(); Capture = false; Cursor = Cursors.Default; return; }
            if (mark == null || !mark.Dragging) return;
            mark.EndDrag();
            Capture = false;
            Cursor = BackgroundInteractive && mark.HitMark(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && mark != null && mark.Dragging) mark.EndDrag();
            if (!Capture && mark != null && mark.RingSpinning) mark.EndRingSpin();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mark != null && mark.Dragging) { mark.Drag(e.Location); Invalidate(); return; }
            if (mark != null && mark.RingSpinning) { mark.RingSpin(e.Location); return; }
            // SOUND WAVE: the cursor pushes the water.
            if (mark != null)
            {
                if (BackgroundInteractive && mark.OnWater(e.Location)) mark.TouchWater(e.Location);
                else mark.ReleaseWater();
            }
            float scale = DeviceDpi / 96F;
            var point = new PointF(e.X / scale, e.Y / scale);
            bool overChip = ChipBounds(scale).Contains(point), overMusic = MusicChipBounds(scale).Contains(point);
            if (overChip != chipHot || overMusic != musicHot) { chipHot = overChip; musicHot = overMusic; Invalidate(); }
            int index = uiHidden || veiling ? -1 : links.FindIndex(link => link.Key.Contains(point));
            bool overMark = !overChip && !overMusic && BackgroundInteractive && mark.HitMark(e.Location);
            Cursor = index >= 0 || overChip || overMusic || overMark ? Cursors.Hand : Cursors.Default;
            if (index != hovered) { hovered = index; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (mark != null) mark.ReleaseWater();
            if (hovered >= 0 || chipHot || musicHot) { hovered = -1; chipHot = musicHot = false; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && chipHot) { ToggleUi(); return; }
            if (e.Button == MouseButtons.Left && musicHot) { var music = MusicRequested; if (music != null) music(); return; }
            var handler = OpenRequested;
            if (e.Button == MouseButtons.Left && hovered >= 0 && hovered < links.Count && handler != null) handler(links[hovered].Value);
        }

        // Each frame: the spinning mark, then a cached layer holding all static text
        // and frames, then the few animated characters. The cached layer is rebuilt
        // only after a non-animation repaint (resize, hover, state change) or while
        // the typewriter is still running.
        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            base.OnInvalidated(e);
            if (!ticking) staticDirty = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float scale = DeviceDpi / 96F;
            var info = (Info != null ? Info() : null) ?? new HomeInfo { Recent = new HistoryEntry[0], Session = "0x0000" };
            var size = ClientSize;
            if (size.Width < 1 || size.Height < 1) return;
            bool typingActive = animation.Enabled && typing.ElapsedMilliseconds < TypeLines * TypeStep + 60 * TypeChar;
            if (staticDirty || typingActive || overlayIndex == null || overlaySize != size) BuildOverlay(size, scale, info, typingActive);
            if (mark == null) mark = new SpinningMark();
            // The mark spans the whole page and may pass beyond its edges while turning.
            float veil = Veil;
            // Faster frames only while a transition (view-only or scene fade) runs.
            int interval = mark.Interacting ? 16 : veiling || SpinningMark.SceneFading ? 33 : SpinningMark.Scene == SpinningMark.VideoScene || SpinningMark.Scene == SpinningMark.PictureScene ? 50 : SpinningMark.Scene == SpinningMark.SoundScene || SpinningMark.Scene == SpinningMark.RingScene ? 33 : 83;
            if (animation.Interval != interval) animation.Interval = interval;
            mark.Render(e.Graphics, size, new Rectangle(Point.Empty, size),
                spin.Elapsed.TotalSeconds, scale, UiPalette.Canvas, panes, overlayIndex, overlayColor, overlayCount, leftColumn, rightColumn, veil);
            var g = e.Graphics;
            g.ScaleTransform(scale, scale);
            if (veil < 0.35F) Draw(g, scale, info, false);
            DrawChip(g, scale);
            // A still page painted again keeps its buffers only for the next minute.
            if (!animation.Enabled) { idleRelease.Stop(); idleRelease.Start(); }
        }

        // 0 = interface shown, 1 = hidden; eased while a transition runs.
        private float Veil
        {
            get
            {
                if (VeilOverride.HasValue) return VeilOverride.Value;
                if (!veiling) return uiHidden ? 1F : 0F;
                float p = Math.Min(1F, veilClock.ElapsedMilliseconds / (float)VeilMs);
                if (p >= 1F) { veiling = false; veilClock.Reset(); animation.Interval = 83; }
                return uiHidden ? p : 1F - p;
            }
        }

        public void ToggleUi()
        {
            uiHidden = !uiHidden;
            hovered = -1;
            if (animation.Enabled)
            {
                // Faster frames only for the transition.
                veiling = true;
                veilClock.Restart();
                animation.Interval = 33;
            }
            Invalidate();
        }

        private RectangleF ChipBounds(float scale)
        { return new RectangleF(ClientSize.Width / scale / 2F - 116, 7, 112, 18); }

        private RectangleF MusicChipBounds(float scale)
        { return new RectangleF(ClientSize.Width / scale / 2F + 4, 7, 112, 18); }

        private void DrawChip(Graphics g, float scale)
        {
            var bounds = ChipBounds(scale);
            using (var fill = new SolidBrush(chipHot ? UiPalette.Active : UiPalette.Canvas)) g.FillRectangle(fill, bounds);
            using (var pen = new Pen(chipHot ? UiPalette.Accent : UiPalette.Border)) g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            DrawLine(g, uiHidden ? "[ SHOW UI ]" : "[ HIDE UI ]", tinyFont, chipHot ? UiPalette.Accent : UiPalette.Muted,
                (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, StringAlignment.Center);
            var music = MusicChipBounds(scale);
            using (var fill = new SolidBrush(musicHot ? UiPalette.Active : UiPalette.Canvas)) g.FillRectangle(fill, music);
            using (var pen = new Pen(musicHot ? UiPalette.Accent : UiPalette.Border)) g.DrawRectangle(pen, music.X, music.Y, music.Width, music.Height);
            DrawLine(g, "[ MUSIC ]", tinyFont, musicHot ? UiPalette.Accent : UiPalette.Muted,
                (int)music.X, (int)music.Y, (int)music.Width, (int)music.Height, StringAlignment.Center);
        }

        private void BuildOverlay(Size size, float scale, HomeInfo info, bool keepBitmap)
        {
            staticDirty = false;
            overlaySize = size;
            if (overlay == null || overlay.Size != size)
            {
                if (overlay != null) overlay.Dispose();
                overlay = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
            }
            panes.Clear();
            links.Clear();
            using (var g = Graphics.FromImage(overlay))
            {
                g.Clear(UiPalette.Canvas);
                g.ScaleTransform(scale, scale);
                Draw(g, scale, info, true);
            }
            // Keep only the pixels that differ from the canvas; the rest shows the mark.
            int canvas = UiPalette.Canvas.ToArgb() & 0xFFFFFF;
            var row = new int[size.Width];
            if (overlayIndex == null) { overlayIndex = new int[size.Width * size.Height / 16 + 1]; overlayColor = new int[overlayIndex.Length]; }
            overlayCount = 0;
            var data = overlay.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            for (int y = 0; y < size.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, size.Width);
                for (int x = 0; x < size.Width; x++)
                {
                    int p = row[x] & 0xFFFFFF;
                    if (p == canvas) continue;
                    if (overlayCount == overlayIndex.Length)
                    {
                        Array.Resize(ref overlayIndex, overlayIndex.Length * 2);
                        Array.Resize(ref overlayColor, overlayColor.Length * 2);
                    }
                    overlayIndex[overlayCount] = y * size.Width + x;
                    overlayColor[overlayCount++] = unchecked((int)0xFF000000) | p;
                }
            }
            overlay.UnlockBits(data);
            // Between rebuilds only the sparse pixel list is kept.
            if (!keepBitmap) { overlay.Dispose(); overlay = null; }
        }

        // Lays out and draws the page. With staticPass, everything except the
        // animated characters (which are drawn per frame with staticPass false).
        private void Draw(Graphics g, float scale, HomeInfo info, bool staticPass)
        {
            int width = (int)(ClientSize.Width / scale), height = (int)(ClientSize.Height / scale);
            int contentWidth = Math.Max(1, Math.Min(744, width - 40));
            bool panels = contentWidth >= 600 && height >= 520;
            int blockHeight = 282 + (panels ? 162 : 0);
            int left = (width - contentWidth) / 2, top = Math.Max(panels ? 40 : 14, (height - blockHeight) / 2);
            bool compact = contentWidth < 500;
            int markWidth = compact ? 112 : 184;
            int titleLeft = left + 24 + markWidth + 18;
            int half = (contentWidth - 40) / 2;
            bool rails = top >= 34 && height - top - blockHeight >= 30;
            int panelTop = top + 296, gap = 12, panelWidth = (contentWidth - gap * 2) / 3;
            int recentLeft = left, systemLeft = left + panelWidth + gap, keysLeft = left + (panelWidth + gap) * 2;

            if (!staticPass)
            {
                DrawLine(g, "URL > https://" + (CursorOn ? "_" : ""), bodyFont, UiPalette.Ink, left + 20, top + 169, contentWidth - 40, 24);
                if (!info.EngineOnline)
                {
                    DrawLine(g, "[ " + Spin + "  ]", smallFont, UiPalette.Accent, left + 20 + half, top + 226, 56, 20);
                    if (panels) DrawLine(g, "STANDBY " + Spin, smallFont, UiPalette.Ink, systemLeft + 12, panelTop + 22, panelWidth - 24, 20, StringAlignment.Far);
                }
                if (rails) DrawLine(g, "SYS > AWAITING INPUT  [" + Spin + "]", tinyFont, UiPalette.Muted, 28, height - 26, 200, 14);
                return;
            }

            // Side columns print live code from the background animation.
            leftColumn = rightColumn = Rectangle.Empty;
            if (rails && left >= 150)
            {
                leftColumn = Scaled(16, 40, left - 32, height - 80, scale);
                rightColumn = Scaled(left + contentWidth + 16, 40, width - left - contentWidth - 32, height - 80, scale);
                panes.Add(leftColumn);
                panes.Add(rightColumn);
            }
            // The mark is dimmed further under these panes so their text stays clear.
            panes.Add(Scaled(left, top + 8, contentWidth, 274, scale));
            if (panels)
                for (int p = 0; p < 3; p++)
                    panes.Add(Scaled(left + p * (panelWidth + gap), panelTop + 8, p < 2 ? panelWidth : contentWidth - 2 * (panelWidth + gap), 138, scale));

            DrawBackdrop(g, width, height, left, rails, info);
            using (var frame = new Pen(UiPalette.Border))
            {
                g.DrawRectangle(frame, left, top + 8, contentWidth, 274);
                g.DrawRectangle(frame, left + 3, top + 11, Math.Max(1, contentWidth - 6), 268);
                g.DrawLine(frame, left + 16, top + 125, left + contentWidth - 16, top + 125);
            }
            Notch(g, " MAIN TERMINAL ", left + 17, top, 154);
            DrawLine(g, "[" + info.Session + "]", tinyFont, Faint, left + contentWidth - 120, top + 18, 104, 14, StringAlignment.Far);
            DrawStripedMark(g, scale, left + 24, top + 30, markWidth, 76);
            DrawWordmark(g, "ARABARA", titleFont, UiPalette.Ink, titleLeft, top + 34, left + contentWidth - titleLeft - 18, 46);
            DrawLine(g, "PERSONAL WEB TERMINAL  //  HTTPS ONLY  //  NO SEARCH", smallFont, UiPalette.Muted, titleLeft + 2, top + 86, left + contentWidth - titleLeft - 22, 20);
            DrawLine(g, "[ READY ]", bodyFont, UiPalette.Accent, left + 20, top + 139, 100, 23);
            DrawLine(g, "Enter a domain, HTTPS link or local path (D:\\...). No keyword search.", smallFont, UiPalette.Ink, left + 112, top + 139, contentWidth - 132, 23);
            BootLine(g, true, Typed("PROFILE MOUNTED", 0), left + 20, top + 204, half);
            BootLine(g, true, Typed("HISTORY  " + info.HistoryCount + " RECORDS", 1), left + 20 + half, top + 204, half);
            BootLine(g, true, Typed("FONTS  WHITE RABBIT / BAHNSCHRIFT", 2), left + 20, top + 226, half);
            BootLine(g, info.EngineOnline, Typed(info.EngineOnline ? "ENGINE  WEBVIEW2 ONLINE" : "ENGINE  STANDBY  //  AWAITING FIRST LINK", 3), left + 20 + half, top + 226, half);
            BootLine(g, true, Typed("TELEMETRY  NONE  //  SEARCH PROVIDER  NONE", 4), left + 20, top + 248, contentWidth - 40);

            if (!panels) return;
            DrawPanel(g, " RECENT ", recentLeft, panelTop, panelWidth);
            DrawPanel(g, " SYSTEM ", systemLeft, panelTop, panelWidth);
            DrawPanel(g, " KEYMAP ", keysLeft, panelTop, contentWidth - (keysLeft - left));

            int row = panelTop + 22, order = 5;
            if (info.Recent.Count == 0) DrawLine(g, Typed("-- NO RECORDS --", order++), smallFont, Faint, recentLeft + 14, row, panelWidth - 28, 20);
            foreach (var entry in info.Recent.Take(5))
            {
                var bounds = new RectangleF(recentLeft + 6, row, panelWidth - 12, 20);
                bool hot = links.Count == hovered;
                links.Add(new KeyValuePair<RectangleF, string>(bounds, entry.Url));
                if (hot) using (var fill = new SolidBrush(UiPalette.Active)) g.FillRectangle(fill, bounds);
                DrawLine(g, Age(entry.VisitedUtc), tinyFont, hot ? UiPalette.Ink : Faint, recentLeft + panelWidth - 58, row, 46, 20, StringAlignment.Far);
                DrawLine(g, Typed("> " + Host(entry.Url), order++), smallFont, hot ? UiPalette.Accent : UiPalette.Ink, recentLeft + 12, row, panelWidth - 72, 20);
                row += 22;
            }

            row = panelTop + 22;
            foreach (var pair in new[]
            {
                new[] { "ENGINE", info.EngineOnline ? "ONLINE" : null },
                new[] { "LOW RAM", info.LowMemory ? "ON" : "OFF" },
                new[] { "VISUALS", info.LiveVisuals ? "LIVE" : "AUTO" },
                new[] { "TABS", info.Tabs.ToString("00") },
                new[] { "PROTOCOL", "HTTPS/TLS" }
            })
            {
                string label = Typed(pair[0], order++);
                DrawLine(g, label, smallFont, UiPalette.Muted, systemLeft + 12, row, panelWidth - 24, 20);
                if (pair[1] != null && label.Length == pair[0].Length) DrawLine(g, pair[1], smallFont, UiPalette.Ink, systemLeft + 12, row, panelWidth - 24, 20, StringAlignment.Far);
                row += 22;
            }

            row = panelTop + 22;
            foreach (var pair in new[]
            {
                new[] { "CTRL+L", "ADDRESS" }, new[] { "CTRL+T", "NEW TAB" }, new[] { "CTRL+W", "CLOSE TAB" },
                new[] { "CTRL+H", "HISTORY" }, new[] { "F5", "RELOAD" }
            })
            {
                string key = Typed("[" + pair[0] + "]", order++);
                DrawLine(g, key, smallFont, UiPalette.Accent, keysLeft + 12, row, 90, 20);
                if (key.Length == pair[0].Length + 2) DrawLine(g, pair[1], smallFont, UiPalette.Muted, keysLeft + 102, row, panelWidth - 114, 20);
                row += 22;
            }
        }

        private static Rectangle Scaled(int x, int y, int width, int height, float scale)
        { return new Rectangle((int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale)); }

        // Dot grid, corner brackets, address rulers and the header/footer rails.
        private void DrawBackdrop(Graphics g, int width, int height, int contentLeft, bool rails, HomeInfo info)
        {
            var dots = new List<RectangleF>();
            for (int y = 18; y < height - 10; y += 18)
                for (int x = 18; x < width - 10; x += 18) dots.Add(new RectangleF(x, y, 1, 1));
            if (dots.Count > 0) using (var brush = new SolidBrush(Grid)) g.FillRectangles(brush, dots.ToArray());
            using (var pen = new Pen(UiPalette.Border))
            {
                const int inset = 8, arm = 14;
                foreach (var corner in new[] { new Point(inset, inset), new Point(width - inset, inset), new Point(inset, height - inset), new Point(width - inset, height - inset) })
                {
                    int dx = corner.X < width / 2 ? arm : -arm, dy = corner.Y < height / 2 ? arm : -arm;
                    g.DrawLine(pen, corner.X, corner.Y, corner.X + dx, corner.Y);
                    g.DrawLine(pen, corner.X, corner.Y, corner.X, corner.Y + dy);
                }
            }
            if (!rails) return;
            Rail(g, "ARABARA://HOME", "NODE " + info.Session + "  //  BUILD 0.7.1", 14, width, UiPalette.Accent, true);
            // The footer's left text carries a spinner and is drawn per frame.
            Rail(g, "SYS > AWAITING INPUT  [|]", "41 52 41 42 41 52 41 // 0D 0A", height - 26, width, UiPalette.Muted, false);
            if (contentLeft < 86 || contentLeft >= 150) return; // wide pages show code columns instead
            using (var tick = new Pen(Faint))
                for (int y = 48, n = 0; y < height - 40; y += 36, n++)
                {
                    string label = "0x" + (n * 0x40).ToString("X4");
                    g.DrawLine(tick, 14, y, 20, y);
                    g.DrawLine(tick, width - 20, y, width - 14, y);
                    DrawLine(g, label, tinyFont, Faint, 24, y - 7, 60, 14);
                    DrawLine(g, label, tinyFont, Faint, width - 84, y - 7, 60, 14, StringAlignment.Far);
                }
        }

        private void Rail(Graphics g, string leftText, string rightText, int y, int width, Color leftColor, bool drawLeft)
        {
            float leftWidth = g.MeasureString(leftText, tinyFont).Width, rightWidth = g.MeasureString(rightText, tinyFont).Width;
            if (drawLeft) DrawLine(g, leftText, tinyFont, leftColor, 28, y, (int)leftWidth + 4, 14);
            DrawLine(g, rightText, tinyFont, Faint, width - 28 - (int)rightWidth - 4, y, (int)rightWidth + 4, 14, StringAlignment.Far);
            float from = 28 + leftWidth + 10, to = width - 28 - rightWidth - 12;
            if (to - from < 20) return;
            using (var pen = new Pen(Grid) { DashPattern = new[] { 2F, 3F } }) g.DrawLine(pen, from, y + 7, to, y + 7);
        }

        private void Notch(Graphics g, string text, int x, int y, int width)
        {
            using (var background = new SolidBrush(UiPalette.Canvas)) g.FillRectangle(background, x, y, width, 20);
            DrawLine(g, text, smallFont, UiPalette.Muted, x + 1, y, width - 2, 20);
        }

        private void DrawPanel(Graphics g, string title, int x, int y, int width)
        {
            using (var pen = new Pen(UiPalette.Border)) g.DrawRectangle(pen, x, y + 8, width, 138);
            Notch(g, title, x + 10, y, (int)g.MeasureString(title, smallFont).Width + 4);
        }

        // A pending line's spinner token is drawn per frame instead.
        private void BootLine(Graphics g, bool ok, string text, int x, int y, int width)
        {
            if (ok) DrawLine(g, "[ OK ]", smallFont, Ok, x, y, 56, 20);
            DrawLine(g, text, smallFont, UiPalette.Muted, x + 56, y, width - 60, 20);
        }

        private static string Host(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return url;
            string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host.Substring(4) : uri.Host;
            return uri.AbsolutePath.Length > 1 ? host + uri.AbsolutePath : host;
        }

        private static string Age(DateTime visitedUtc)
        {
            var age = DateTime.UtcNow - visitedUtc;
            if (age.TotalMinutes < 1) return "NOW";
            if (age.TotalHours < 1) return (int)age.TotalMinutes + "M";
            if (age.TotalDays < 1) return (int)age.TotalHours + "H";
            return (int)age.TotalDays + "D";
        }

        // The original Arabara mark (triangle, central triangular opening and
        // horizontal slots), flipped so its apex points up, stretched horizontally
        // and filled with thin, evenly spaced stripes. Drawn in device pixels so the stripes stay crisp.
        // Coordinates are traced from assets/arabara.png (1111 x 880 artwork box).
        private static readonly float[][] MarkHoles =
        {
            new float[] { 109, 104, 1001, 104, 967, 157, 141, 157 },
            new float[] { -400, 292, 410, 292, 443, 340, -400, 340 },
            new float[] { 698, 292, 1500, 292, 1500, 340, 665, 340 },
            new float[] { 397, 194, 712, 194, 552, 439 },
            new float[] { 345, 477, 761, 477, 728, 529, 378, 529 }
        };

        internal static void DrawStripedMark(Graphics g, float scale, float x, float y, float logicalWidth, float logicalHeight, int logicalPeriod = 3)
        {
            DrawStripedMark(g, scale, x, y, logicalWidth, logicalHeight, UiPalette.Accent, Color.FromArgb(150, 16, 34), logicalPeriod);
        }

        // The same mark with its stripes shaded from top to bottom (the address box's ad
        // blocker switch greys it).
        internal static void DrawStripedMark(Graphics g, float scale, float x, float y, float logicalWidth, float logicalHeight, Color top, Color bottom, int logicalPeriod = 3)
        {
            float w = logicalWidth * scale, h = logicalHeight * scale, ox = x * scale, oy = y * scale;
            Func<float, float, PointF> P = (u, v) => new PointF(ox + u / 1111F * w, oy + (880F - v) / 880F * h);
            var saved = g.Save();
            g.ResetTransform();
            using (var outer = new GraphicsPath())
            using (var shape = new Region())
            {
                outer.AddPolygon(new[] { P(0, 0), P(1111, 0), P(549, 880) });
                shape.MakeEmpty();
                shape.Union(outer);
                foreach (var hole in MarkHoles)
                    using (var cut = new GraphicsPath())
                    {
                        var points = new PointF[hole.Length / 2];
                        for (int i = 0; i < points.Length; i++) points[i] = P(hole[i * 2], hole[i * 2 + 1]);
                        cut.AddPolygon(points);
                        shape.Exclude(cut);
                    }
                int period = Math.Max(2, (int)Math.Round(logicalPeriod * scale)), gap = Math.Max(1, (int)Math.Round(scale));
                int y0 = (int)Math.Floor(oy), y1 = (int)Math.Ceiling(oy + h);
                var bounds = new RectangleF(ox - 1, y0, w + 2, y1 - y0 + 1);
                g.Clip = shape;
                g.SmoothingMode = SmoothingMode.None;
                using (var ink = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical))
                    for (int row = y0; row < y1; row += period)
                        g.FillRectangle(ink, bounds.X, row, bounds.Width, period - gap);
                g.ResetClip();
            }
            g.Restore(saved);
        }

        // Uppercase wordmark stretched horizontally with open letter spacing, echoing
        // the wide striped mark. It shrinks to fit narrow windows.
        internal static float MeasureWordmark(Graphics g, string text, Font font, float maxWidth)
        {
            float natural = NaturalWordmarkWidth(g, text, font);
            return natural * Math.Min(1.55F, Math.Max(0.1F, maxWidth / natural));
        }

        private static float NaturalWordmarkWidth(Graphics g, string text, Font font)
        {
            float tracking = font.Size * 0.12F, natural = 0;
            foreach (char c in text) natural += g.MeasureString(c.ToString(), font, PointF.Empty, StringFormat.GenericTypographic).Width + tracking;
            return natural - tracking;
        }

        // The wordmark is cut by the same horizontal stripes as the mark: its glyph
        // outlines clip stripes drawn in device pixels, with the mark's period and gap.
        internal static void DrawWordmark(Graphics g, string text, Font font, Color color, float x, float y, float maxWidth, float maxHeight)
        {
            var format = StringFormat.GenericTypographic;
            float tracking = font.Size * 0.12F, natural = NaturalWordmarkWidth(g, text, font);
            float glyphHeight = g.MeasureString(text, font, PointF.Empty, format).Height;
            float stretch = Math.Min(1.55F, Math.Max(0.1F, maxWidth / natural));
            float fit = Math.Min(1F, maxHeight / glyphHeight);
            float emSize = font.SizeInPoints * g.DpiY / 72F;
            float unit = Math.Abs(g.Transform.Elements[3]); // device pixels per logical pixel
            using (var path = new GraphicsPath())
            using (var place = new Matrix())
            {
                float cursor = 0;
                foreach (char c in text)
                {
                    path.AddString(c.ToString(), font.FontFamily, (int)font.Style, emSize, new PointF(cursor, 0), format);
                    cursor += g.MeasureString(c.ToString(), font, PointF.Empty, format).Width + tracking;
                }
                place.Multiply(g.Transform);
                place.Translate(x, y + (maxHeight - glyphHeight * fit) / 2);
                place.Scale(stretch * fit, fit);
                path.Transform(place);
                var bounds = path.GetBounds();
                if (bounds.Height < 1) return;
                var saved = g.Save();
                g.ResetTransform();
                g.SetClip(path);
                g.SmoothingMode = SmoothingMode.None;
                int period = Math.Max(2, (int)Math.Round(3 * unit)), gap = Math.Max(1, (int)Math.Round(unit));
                int top = (int)Math.Floor(bounds.Top), bottom = (int)Math.Ceiling(bounds.Bottom);
                var area = new RectangleF(bounds.X - 1, top, bounds.Width + 2, bottom - top + 1);
                var low = Color.FromArgb(color.R * 3 / 4, color.G * 3 / 4, color.B * 3 / 4);
                using (var ink = new LinearGradientBrush(area, color, low, LinearGradientMode.Vertical))
                    for (int row = top; row < bottom; row += period)
                        g.FillRectangle(ink, area.X, row, area.Width, period - gap);
                g.Restore(saved);
            }
        }

        private static void DrawLine(Graphics graphics, string text, Font font, Color color, int x, int y, int width, int height,
            StringAlignment align = StringAlignment.Near)
        {
            using (var format = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            using (var brush = new SolidBrush(color)) graphics.DrawString(text, font, brush, new RectangleF(x, y, Math.Max(1, width), height), format);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (host != null) { host.Activated -= HostChanged; host.Deactivate -= HostChanged; host.Resize -= HostChanged; }
                animation.Dispose();
                idleRelease.Dispose();
                if (mark != null) mark.Dispose();
                if (overlay != null) overlay.Dispose();
                titleFont.Dispose(); bodyFont.Dispose(); smallFont.Dispose(); tinyFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
