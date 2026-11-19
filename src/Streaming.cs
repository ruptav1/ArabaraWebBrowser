using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Arabara
{
    // The window a screen-share picker (Discord web or the Discord app) captures for a
    // live tab. It holds the tab's own WebView2, so nothing reloads. It is borderless
    // (no title bar in the stream) and parked outside every monitor (the mouse can
    // never be over it, so no cursor in the stream, and it never covers anything).
    // The page stays usable inside its tab through a LiveMirror.
    internal sealed class StreamWindow : Form
    {
        public StreamWindow(string title, Icon icon)
        {
            Text = "ARABARA STREAM · " + title;
            Icon = icon;
            BackColor = UiPalette.Canvas;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Park(new Size(1280, 720));
        }

        // Clicking its taskbar button shows the live tab instead.
        public event EventHandler Summoned;

        protected override bool ShowWithoutActivation { get { return true; } }

        public void SetTitle(string title) { Text = "ARABARA STREAM · " + title; }

        // Outside the virtual desktop, sized like the mirror that shows it.
        public void Park(Size size)
        {
            var desktop = SystemInformation.VirtualScreen;
            Bounds = new Rectangle(desktop.Right + 64, desktop.Top, Math.Max(320, size.Width), Math.Max(200, size.Height));
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            var handler = Summoned;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    // Shows a live tab inside the browser from DevTools screencast frames (JPEG,
    // sent by the engine only when the page changes), decoded off the UI thread, with
    // mouse, wheel and keyboard input forwarded to the page over the DevTools protocol.
    // The screencast runs only while the mirror is on screen. (A DWM thumbnail of the
    // stream window was tried first; combined with tab switching it stalled the capture.)
    internal sealed class LiveMirror : Control
    {
        private readonly Font font = UiFonts.Body(8.5F);
        private readonly Func<CoreWebView2> core;
        private StreamWindow source;
        private CoreWebView2DevToolsProtocolEventReceiver frames;
        private Bitmap frame;
        private bool casting, decoding, moving, movePending;
        private Size castSize;
        private Point lastMove;
        private int buttons;

        public LiveMirror(Func<CoreWebView2> core)
        {
            this.core = core;
            Dock = DockStyle.Fill;
            BackColor = UiPalette.Canvas;
            TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        private int Strip { get { return (int)Math.Round(24 * DeviceDpi / 96F); } }
        private Rectangle View { get { return new Rectangle(0, Strip, ClientSize.Width, Math.Max(1, ClientSize.Height - Strip)); } }
        public Size ViewSize { get { return View.Size; } }

        // Whether the mirror is on screen and receiving frames.
        public bool Showing { get; private set; }
        public int FramesShown { get; private set; }

        // On screen only while this control and every container up to the form are visible.
        private bool Shown(Form form)
        {
            if (form.WindowState == FormWindowState.Minimized) return false;
            for (Control c = this; c != null && c != form; c = c.Parent) if (!c.Visible) return false;
            return true;
        }

        public void Attach(StreamWindow window)
        {
            Detach();
            source = window;
            var target = core();
            if (target != null)
            {
                frames = target.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
                frames.DevToolsProtocolEventReceived += OnFrame;
            }
            Sync();
            Invalidate();
        }

        public void Detach()
        {
            StopCast();
            if (frames != null) { frames.DevToolsProtocolEventReceived -= OnFrame; frames = null; }
            if (frame != null) { frame.Dispose(); frame = null; }
            Showing = false;
            source = null;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Sync(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); Sync(); }
        protected override void OnParentVisibleChanged(EventArgs e) { base.OnParentVisibleChanged(e); Sync(); }

        // Starts or stops the screencast with visibility and keeps the stream window the same size as the view.
        public void Sync()
        {
            if (source == null || source.IsDisposed || !IsHandleCreated) return;
            var form = FindForm();
            if (form == null) return;
            var view = View;
            if (source.ClientSize != view.Size) source.Park(view.Size);
            bool shown = Shown(form);
            Showing = shown;
            if (!shown) { StopCast(); return; }
            if (casting && castSize == view.Size) return;
            StopCast();
            var target = core();
            if (target == null) return;
            casting = true;
            castSize = view.Size;
            target.CallDevToolsProtocolMethodAsync("Page.startScreencast", "{\"format\":\"jpeg\",\"quality\":80,\"maxWidth\":" + view.Width +
                ",\"maxHeight\":" + view.Height + ",\"everyNthFrame\":1}").ContinueWith(t => { var ignored = t.Exception; });
        }

        private void StopCast()
        {
            if (!casting) return;
            casting = false;
            var target = core();
            if (target != null) target.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}").ContinueWith(t => { var ignored = t.Exception; });
        }

        // Every frame is acknowledged at once; frames arriving while one is decoding are skipped.
        private void OnFrame(object sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            string json = e.ParameterObjectAsJson;
            string session = Field(json, "\"sessionId\":", ',', '}');
            var target = core();
            if (target != null && session != null)
                target.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", "{\"sessionId\":" + session + "}").ContinueWith(t => { var ignored = t.Exception; });
            if (!casting || decoding) return;
            string data = Field(json, "\"data\":\"", '"');
            if (data == null) return;
            decoding = true;
            Task.Run(() =>
            {
                Bitmap decoded = null;
                try
                {
                    using (var stream = new System.IO.MemoryStream(Convert.FromBase64String(data)))
                    using (var image = Image.FromStream(stream))
                        decoded = new Bitmap(image);
                }
                catch (Exception) { }
                return decoded;
            }).ContinueWith(t =>
            {
                if (IsDisposed || !IsHandleCreated) { if (t.Result != null) t.Result.Dispose(); return; }
                BeginInvoke(new Action(delegate
                {
                    decoding = false;
                    if (t.Result == null) return;
                    if (!casting) { t.Result.Dispose(); return; }
                    if (frame != null) frame.Dispose();
                    frame = t.Result;
                    FramesShown++;
                    Invalidate(View);
                }));
            });
        }

        private static string Field(string json, string key, params char[] end)
        {
            int start = json.IndexOf(key, StringComparison.Ordinal);
            if (start < 0) return null;
            start += key.Length;
            int stop = json.IndexOfAny(end, start);
            return stop < 0 ? null : json.Substring(start, stop - start);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(UiPalette.Canvas);
            if (frame != null)
            {
                var view = View;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                if (frame.Size == view.Size) g.DrawImageUnscaledAndClipped(frame, view);
                else g.DrawImage(frame, view);
            }
            var strip = new Rectangle(0, 0, ClientSize.Width, Strip);
            using (var fill = new SolidBrush(UiPalette.Active)) g.FillRectangle(fill, strip);
            string title = source != null ? source.Text : "ARABARA STREAM";
            TextRenderer.DrawText(g, "● LIVE   " + title + "   //   viewers see this page without cursor or title bar   //   press STREAM to stop",
                font, new Rectangle(10, 0, strip.Width - 20, strip.Height), UiPalette.Accent,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        // ---- input forwarding (CSS pixels relative to the page) ----

        private void Send(string method, string json)
        {
            var target = core();
            if (target == null) return;
            target.CallDevToolsProtocolMethodAsync(method, json).ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private string Css(Point p)
        {
            float scale = DeviceDpi / 96F;
            return "\"x\":" + Math.Round((p.X) / scale, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"y\":" + Math.Round((p.Y - Strip) / scale, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Button(MouseButtons button)
        { return button == MouseButtons.Right ? "right" : button == MouseButtons.Middle ? "middle" : button == MouseButtons.Left ? "left" : "none"; }

        private static int Mask(MouseButtons button)
        { return button == MouseButtons.Left ? 1 : button == MouseButtons.Right ? 2 : button == MouseButtons.Middle ? 4 : 0; }

        private static int Modifiers()
        {
            var keys = Control.ModifierKeys;
            return ((keys & Keys.Alt) != 0 ? 1 : 0) | ((keys & Keys.Control) != 0 ? 2 : 0) | ((keys & Keys.Shift) != 0 ? 8 : 0);
        }

        private bool InView(Point p) { return p.Y >= Strip; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (!InView(e.Location)) return;
            buttons |= Mask(e.Button);
            Send("Input.dispatchMouseEvent", "{\"type\":\"mousePressed\"," + Css(e.Location) + ",\"button\":\"" + Button(e.Button) + "\",\"buttons\":" + buttons +
                ",\"clickCount\":" + Math.Max(1, e.Clicks) + ",\"modifiers\":" + Modifiers() + "}");
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            buttons &= ~Mask(e.Button);
            Send("Input.dispatchMouseEvent", "{\"type\":\"mouseReleased\"," + Css(e.Location) + ",\"button\":\"" + Button(e.Button) + "\",\"buttons\":" + buttons +
                ",\"clickCount\":1,\"modifiers\":" + Modifiers() + "}");
        }

        // Mouse moves are coalesced: at most one call in flight, the latest position wins.
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            lastMove = e.Location;
            if (moving) { movePending = true; return; }
            SendMove();
        }

        private void SendMove()
        {
            var target = core();
            if (target == null || !InView(lastMove)) return;
            moving = true;
            movePending = false;
            target.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseMoved\"," + Css(lastMove) + ",\"buttons\":" + buttons + ",\"modifiers\":" + Modifiers() + "}")
                .ContinueWith(t =>
                {
                    var ignored = t.Exception;
                    BeginInvoke(new Action(delegate { moving = false; if (movePending && !IsDisposed) SendMove(); }));
                });
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!InView(e.Location)) return;
            Send("Input.dispatchMouseEvent", "{\"type\":\"mouseWheel\"," + Css(e.Location) + ",\"deltaX\":0,\"deltaY\":" + (-e.Delta) + ",\"modifiers\":" + Modifiers() + "}");
        }

        protected override bool IsInputKey(Keys keyData) { return true; }

        // Printable keys wait for KeyPress and are sent once, as keyDown with their text
        // (keydown + keypress + input in the page). Other keys and shortcuts are sent
        // as rawKeyDown with their DOM key name.
        private int pendingKey;

        private static bool Printable(Keys key)
        {
            if ((Control.ModifierKeys & (Keys.Control | Keys.Alt)) != 0) return false;
            return (key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) || (key >= Keys.NumPad0 && key <= Keys.Divide) ||
                key == Keys.Space || (key >= Keys.OemSemicolon && key <= Keys.Oem102) || key == Keys.Return;
        }

        private static string KeyName(Keys key)
        {
            switch (key)
            {
                case Keys.Back: return "Backspace";
                case Keys.Tab: return "Tab";
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Escape";
                case Keys.Space: return " ";
                case Keys.Left: return "ArrowLeft";
                case Keys.Right: return "ArrowRight";
                case Keys.Up: return "ArrowUp";
                case Keys.Down: return "ArrowDown";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PageUp";
                case Keys.PageDown: return "PageDown";
                case Keys.Delete: return "Delete";
                case Keys.Insert: return "Insert";
                case Keys.ShiftKey: return "Shift";
                case Keys.ControlKey: return "Control";
                case Keys.Menu: return "Alt";
                default:
                    if (key >= Keys.F1 && key <= Keys.F24) return "F" + (key - Keys.F1 + 1);
                    if (key >= Keys.A && key <= Keys.Z) return ((char)('a' + (key - Keys.A))).ToString();
                    if (key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + (key - Keys.D0))).ToString();
                    return "Unidentified";
            }
        }

        private static string Json(string text) { return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(text); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            e.Handled = true;
            int code = (int)e.KeyCode;
            if (Printable(e.KeyCode)) { pendingKey = code; return; }
            Send("Input.dispatchKeyEvent", "{\"type\":\"rawKeyDown\",\"windowsVirtualKeyCode\":" + code + ",\"nativeVirtualKeyCode\":" + code +
                ",\"key\":" + Json(KeyName(e.KeyCode)) + ",\"modifiers\":" + Modifiers() + "}");
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            e.Handled = true;
            if (pendingKey == 0) return;
            string text = e.KeyChar == '\r' ? "\r" : e.KeyChar.ToString();
            string key = e.KeyChar == '\r' ? "Enter" : text;
            Send("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"text\":" + Json(text) + ",\"unmodifiedText\":" + Json(text) + ",\"key\":" + Json(key) +
                ",\"windowsVirtualKeyCode\":" + pendingKey + ",\"nativeVirtualKeyCode\":" + pendingKey + ",\"modifiers\":" + Modifiers() + "}");
            pendingKey = 0;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            e.Handled = true;
            int code = (int)e.KeyCode;
            Send("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"windowsVirtualKeyCode\":" + code + ",\"nativeVirtualKeyCode\":" + code +
                ",\"key\":" + Json(KeyName(e.KeyCode)) + ",\"modifiers\":" + Modifiers() + "}");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Detach(); font.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Asked when a page starts screen sharing: share one tab, or the usual screen/window picker.
    internal sealed class ShareChooser : Form
    {
        private readonly ListBox list = new ListBox();
        public int SelectedTab { get; private set; }

        public ShareChooser(IList<string> titles)
        {
            SelectedTab = -1;
            Text = "Share";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = UiPalette.Canvas;
            ForeColor = UiPalette.Ink;
            Font = UiFonts.Body(10F);
            ClientSize = new Size(520, 330);
            Padding = new Padding(16);
            var heading = new Label { Dock = DockStyle.Top, Height = 54, ForeColor = UiPalette.Ink,
                Text = titles.Count > 0 ? "> SHARE ONE TAB\nPick a tab: it opens in its own window. Then choose that window\n(ARABARA STREAM · ...) in the next picker." :
                    "> SHARE ONE TAB\nOpen the page you want to share in another tab first,\nor share a screen or window." };
            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = UiPalette.Surface;
            list.ForeColor = UiPalette.Ink;
            list.IntegralHeight = false;
            list.ItemHeight = 22;
            list.Items.AddRange(titles.Cast<object>().ToArray());
            if (titles.Count > 0) list.SelectedIndex = 0;
            list.DoubleClick += delegate { Choose(); };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            buttons.Controls.Add(Button("CANCEL", 90, delegate { DialogResult = DialogResult.Cancel; }));
            buttons.Controls.Add(Button("SCREEN / WINDOW", 150, delegate { DialogResult = DialogResult.Ignore; }));
            var share = Button("SHARE TAB", 110, delegate { Choose(); });
            share.Enabled = titles.Count > 0;
            buttons.Controls.Add(share);
            Controls.Add(list);
            Controls.Add(buttons);
            Controls.Add(heading);
            AcceptButton = share;
        }

        private void Choose()
        {
            if (list.SelectedIndex < 0) return;
            SelectedTab = list.SelectedIndex;
            DialogResult = DialogResult.OK;
        }

        private static Button Button(string text, int width, EventHandler click)
        {
            var button = new ChromeButton { Text = text, Width = width, Height = 32, BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Margin = new Padding(6, 0, 0, 0) };
            button.Click += click;
            return button;
        }
    }

    internal sealed partial class BrowserForm
    {
        // Tabs that must never be throttled: an active screen share, or a stream window. (Keep
        // alive, a call and microphone access only keep a tab from being frozen or suspended:
        // out of sight it is not drawn and holds the Low memory target, but it keeps running.)
        private static bool Protected(BrowserTab tab) { return tab.Capturing || tab.Stream != null; }

        private void ScreenCaptureStarting(BrowserTab tab, CoreWebView2ScreenCaptureStartingEventArgs args)
        {
            var deferral = args.GetDeferral();
            BeginInvoke(new Action(delegate
            {
                try
                {
                    if (closing || tab.Closed) { args.Cancel = true; return; }
                    var result = DialogResult.None;
                    var others = tabs.Where(t => t != tab && !t.Closed && Core(t) != null).ToList();
                    using (var chooser = new ShareChooser(others.Select(t => (t.Stream != null ? "[LIVE] " : "") + (String.IsNullOrWhiteSpace(t.Title) ? t.Url : t.Title)).ToList()))
                    {
                        result = chooser.ShowDialog(this);
                        if (result == DialogResult.Cancel) { args.Cancel = true; return; }
                        if (result == DialogResult.OK)
                        {
                            var live = others[chooser.SelectedTab];
                            PopOut(live);
                            live.SharedBy = tab;
                        }
                    }
                    // The sharing page keeps full priority until it is closed.
                    tab.Capturing = true;
                    if (Core(tab) != null && Core(tab).IsSuspended) Core(tab).Resume();
                    // WebView2 draws its share picker inside this tab: keep the browser in front.
                    Activate();
                    SetStatus(result == DialogResult.OK ? "In the picker, choose the window \"ARABARA STREAM · ...\" and press Share." :
                        "Screen sharing: this tab stays fully active until it is closed.");
                    UpdateVisibility();
                    UpdateChrome();
                }
                finally { deferral.Complete(); }
            }));
        }

        // --frame-test: a page streamed live, for an on-screen check of the mirror.
        internal async Task FrameTestLiveAsync(string folder)
        {
            await OpenAddressAsync(folder);
            for (int i = 0; i < 50 && (Core(active) == null || !IsLocalPage(Core(active).Source)); i++) await Task.Delay(100);
            await Task.Delay(800);
            PopOut(active);
            await Task.Delay(1200);
        }

        private void ToggleStream()
        {
            if (active == null) return;
            if (active.Stream != null) { active.Stream.Close(); return; }
            if (Core(active) == null) { SetStatus("Open a page first, then press STREAM to share just that page."); return; }
            PopOut(active);
        }

        // Moves the tab's WebView2 into its parked stream window without reloading the
        // page; the tab shows it through a LiveMirror and stays usable.
        private void PopOut(BrowserTab tab)
        {
            if (tab.Stream != null || tab.View == null) return;
            var window = new StreamWindow(String.IsNullOrWhiteSpace(tab.Title) ? "Arabara" : tab.Title, Icon);
            if (smoke) window.ShowInTaskbar = false;
            tab.Stream = window;
            if (Core(tab).IsSuspended) Core(tab).Resume();
            ApplyMemoryTarget(tab, CoreWebView2MemoryUsageTargetLevel.Normal);
            if (tab.Mirror == null)
            {
                tab.Mirror = new LiveMirror(delegate { return Core(tab); });
                tab.Page.Controls.Add(tab.Mirror);
            }
            tab.Mirror.Visible = true;
            tab.Mirror.BringToFront();
            window.Park(tab.Mirror.ViewSize);
            tab.Page.Controls.Remove(tab.View);
            window.Controls.Add(tab.View);
            tab.View.Dock = DockStyle.Fill;
            tab.View.Visible = true;
            window.FormClosed += delegate { DockBack(tab); };
            window.Summoned += delegate { if (!tab.Closed) { Activate(); SelectTab(tab); } };
            window.Show();
            // The page thinks it has focus, so keyboard input from the mirror works.
            Core(tab).CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
            tab.Mirror.Attach(window);
            tab.Select.Text = "[LIVE] " + (String.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title);
            SetStatus("Live: share the window \"" + window.Text + "\". Viewers see only the page, without cursor or title bar.");
            UpdateVisibility();
            UpdateChrome();
        }

        // Records every getDisplayMedia stream a page creates, so Arabara can end the
        // share when the live tab it shows is stopped or closed.
        internal const string TrackShares = @"(() => {
  const media = navigator.mediaDevices;
  if (!media || !media.getDisplayMedia || media.__arabara) return;
  const original = media.getDisplayMedia.bind(media);
  window.__arabaraShares = [];
  media.getDisplayMedia = function (constraints) { return original(constraints).then(s => { window.__arabaraShares.push(s); return s; }); };
  media.__arabara = true;
})();";

        // Records every RTCPeerConnection a page creates (weakly), so Arabara can ask, before it
        // freezes or suspends the tab, whether the page is in a call with sound: a connection
        // that is up (or briefly dropped) carrying a live audio track either way, such as a
        // Discord voice channel, even while everyone is silent. Subclasses still work.
        internal const string TrackCalls = @"(() => {
  const Native = window.RTCPeerConnection;
  if (!Native || Native.__arabara) return;
  const peers = [];
  function Watched(...args) {
    if (!new.target) throw new TypeError(""Failed to construct 'RTCPeerConnection': Please use the 'new' operator."");
    const pc = Reflect.construct(Native, args, new.target);
    peers.push(new WeakRef(pc));
    return pc;
  }
  Watched.prototype = Native.prototype;
  Object.setPrototypeOf(Watched, Native);
  Object.defineProperty(Watched, '__arabara', { value: true });
  window.RTCPeerConnection = Watched;
  if (window.webkitRTCPeerConnection) window.webkitRTCPeerConnection = Watched;
  const audio = t => !!t && t.kind === 'audio' && t.readyState === 'live';
  Object.defineProperty(window, '__arabaraCall', { value: () => {
    let live = false;
    for (let i = peers.length - 1; i >= 0; i--) {
      const pc = peers[i].deref();
      if (!pc || pc.signalingState === 'closed' || pc.connectionState === 'closed') { peers.splice(i, 1); continue; }
      const up = ['connected', 'disconnected'].includes(pc.connectionState) || ['connected', 'completed', 'disconnected'].includes(pc.iceConnectionState);
      if (up && (pc.getReceivers().some(r => audio(r.track)) || pc.getSenders().some(s => audio(s.track)))) live = true;
    }
    return live;
  } });
})();";

        internal const string EndSharesScript = EndShares;
        private const string EndShares = "(window.__arabaraShares || []).splice(0).forEach(s => s.getTracks().forEach(t => { t.stop(); t.dispatchEvent(new Event('ended')); }))";

        // Stops the share in the tab that is streaming this live tab (Discord sees the track end).
        private void EndShare(BrowserTab live)
        {
            var sharer = live.SharedBy;
            live.SharedBy = null;
            if (sharer == null || sharer.Closed || Core(sharer) == null) return;
            Core(sharer).ExecuteScriptAsync(EndShares).ContinueWith(t => { var ignored = t.Exception; });
            sharer.Capturing = false;
            SetStatus("Stopped sharing the live tab.");
        }

        private void DockBack(BrowserTab tab)
        {
            EndShare(tab);
            var window = tab.Stream;
            tab.Stream = null;
            if (tab.Mirror != null) { tab.Mirror.Detach(); tab.Mirror.Visible = false; }
            if (!tab.Closed && tab.View != null && !tab.View.IsDisposed && window != null)
            {
                window.Controls.Remove(tab.View);
                tab.Page.Controls.Add(tab.View);
                tab.View.Dock = DockStyle.Fill;
                tab.View.BringToFront();
                Core(tab).CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":false}");
                tab.Select.Text = String.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title;
            }
            if (window != null) BeginInvoke(new Action(window.Dispose));
            if (closing) return;
            UpdateVisibility();
            UpdateChrome();
        }
    }
}
