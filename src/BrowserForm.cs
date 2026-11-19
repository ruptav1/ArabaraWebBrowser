using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Arabara
{
    internal sealed class BrowserTab
    {
        public Panel Page;
        public Panel Header;
        public Button Select;
        public WebView2 View;
        public Label PausedVisuals;
        public Task Initialize;
        public string Url = "";
        public string Title = "New tab";
        public bool Closed;
        public bool KeepAlive;
        public bool MediaLease;
        public bool SleepAttempted;
        public DateTime SleepAt;
        public int Downloads;
        public int NavigationVersion;
        public string LastSuspendResult;
        public bool SuspendPending;
        public bool MemoryTargetUnavailable;
        public bool Frozen;
        // In a call when last asked (see TrackCalls), and not to be frozen or suspended before this.
        public bool InCall;
        public DateTime CallUntil;
        public string AllowedFile;
        public StreamWindow Stream;
        public LiveMirror Mirror;
        public bool Capturing;
        public BrowserTab SharedBy;
        public string LightScript;
        public DateTime HiddenSince;
    }

    internal sealed partial class BrowserForm : Form
    {
        internal static readonly Color Canvas = UiPalette.Canvas;
        private readonly MusicBar topBar = new MusicBar { FromRight = true }, bottomBar = new MusicBar();
        private readonly Timer barTimer = new Timer { Interval = 16 };
        private static readonly Color Surface = UiPalette.Surface;
        private static readonly Color Ink = UiPalette.Ink;
        private static readonly Color Muted = UiPalette.Muted;
        private static readonly Color Accent = UiPalette.Accent;
        private readonly List<BrowserTab> tabs = new List<BrowserTab>();
        private readonly string dataRoot;
        private readonly BrowserStore store;
        private readonly BrowserPreferences preferences;
        private readonly bool smoke;
        private readonly bool reopen;
        private readonly Panel viewport = new Panel();
        private readonly FlowLayoutPanel strip = new FlowLayoutPanel();
        private readonly TextBox address = new HttpsAddressBox();
        private readonly Label status = new Label();
        private readonly Panel historyPanel = new Panel();
        private readonly ListBox historyList = new ListBox();
        private readonly ToolTip tips = new ToolTip();
        private readonly Timer sleepTimer = new Timer();
        private readonly Timer saveTimer = new Timer();
        private readonly Timer focusTimer = new Timer();
        private readonly Timer freezeTimer = new Timer();
        // Low RAM mode releases the whole page of a tab left idle in the background this long.
        internal static TimeSpan FreezeAfter = TimeSpan.FromMinutes(1);
        // The tab shown freezes too once Arabara has been out of sight this long: minimized, or
        // behind other windows while Visuals:Auto pauses the page. It reloads on return.
        internal static TimeSpan AwayFreezeAfter = TimeSpan.FromMinutes(5);
        private DateTime awaySince = DateTime.MinValue;
        private readonly string session = "0x" + new Random().Next(0x1000, 0xFFFF).ToString("X4");
        private StartupIntro intro;
        private CaptionButton minButton, maxButton, closeButton;
        private int lastCaptionClick;
        private Point lastCaptionPoint;
        private FadeCurtain curtain;
        private Button back, forward, keep, add, stream, update;
        // A newer release found on GitHub, shown by the UPDATE button (see Updater).
        private ReleaseInfo available;
        private bool updating;
        private readonly Timer updateTimer = new Timer();
        private SwitchLabel economy, visuals, youtube;
        private string keepTip, streamTip;
        private BrowserTab active;
        private Task<CoreWebView2Environment> environmentTask;
        private CoreWebView2Environment environment;
        private bool closing;
        private bool windowActive;
        private bool lowMemoryMode;
        private bool pauseBackgroundVisuals;
        public int ExitCode { get; private set; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        public BrowserForm(string root, bool smokeTest, bool smokeReopen = false)
        {
            dataRoot = root;
            smoke = smokeTest;
            reopen = smokeReopen;
            store = new BrowserStore(root);
            preferences = BrowserPreferences.Load(root);
            lowMemoryMode = preferences.LowMemoryBackground;
            pauseBackgroundVisuals = preferences.PauseBackgroundVisuals;
            youtubeLight = preferences.LightYouTube;
            if (preferences.AdsAllowedSites == null) preferences.AdsAllowedSites = new List<string>();
            LoadBackdrops(preferences, root);
            // RINGS moves with the music terminal's music only (not an ad).
            SpinningMark.MusicPlaying = () => music != null && music.Playing && !music.Advertising;
            Text = "Arabara";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = UiFonts.Body(10F);
            BackColor = Canvas;
            ForeColor = Ink;
            ClientSize = new Size(1120, 730);
            MinimumSize = new Size(800, 480);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            if (smoke) { ShowInTaskbar = false; Opacity = 0; }
            else WindowState = FormWindowState.Maximized;
            BuildChrome();
            var startArgs = Environment.GetCommandLineArgs();
            if (smoke ? startArgs.Contains("--intro") : !startArgs.Contains("--no-intro"))
            {
                intro = new StartupIntro();
                // Deferred so the intro is not disposed inside its own event handler.
                intro.Finished += delegate { BeginInvoke(new Action(EndIntro)); };
                Controls.Add(intro);
                intro.BringToFront();
            }
            sleepTimer.Tick += async delegate { await SleepDueTabsAsync(); };
            saveTimer.Interval = 1200;
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveHistory(); };
            // One-shot debounce. Do not hide a WebView during a transient focus change
            // to its own native menus, file chooser or permission dialog.
            focusTimer.Interval = 200;
            focusTimer.Tick += delegate { SetWindowActive(IsOwnForegroundWindow()); };
            freezeTimer.Tick += delegate { FreezeDueTabs(); };
            // Updates: a first look a minute after start, then every 12 hours.
            if (!smoke && preferences.CheckForUpdates)
            {
                updateTimer.Interval = 60000;
                updateTimer.Tick += async delegate { updateTimer.Interval = 12 * 3600 * 1000; await CheckForUpdateAsync(); };
                updateTimer.Start();
            }
            WatchMemoryPressure();
            Resize += delegate { UpdateVisibility(); UpdateCaption(); };
            Activated += delegate { UpdateCaption(); };
            Deactivate += delegate { UpdateCaption(); };
            UpdateCaption();
            AddTab();
            SetStatus(store.LoadWarning ?? "Ready  ·  HTTPS links only");
        }

        // The tab strip's and toolbar's buttons are this tall (the rows are 34 and 38 pixels).
        private const int BarButtonHeight = 26;

        private Button ButtonFor(string text, int width, Action action)
        {
            var button = new ChromeButton { Text = text, Width = width, Height = 32, FlatStyle = FlatStyle.Flat,
                BackColor = Surface, ForeColor = Ink, Margin = new Padding(2), TabStop = true,
                UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = UiPalette.Hover;
            button.Click += delegate { action(); };
            return button;
        }

        private Button BarButton(string text, int width, Action action)
        {
            var button = ButtonFor(text, width, action);
            button.Height = BarButtonHeight;
            return button;
        }

        private SwitchLabel SwitchFor(string name, string tip, Action action)
        {
            var button = new SwitchLabel { Dock = DockStyle.Right, BackColor = Canvas, Font = UiFonts.Body(9F), TabStop = true, AccessibleName = name };
            button.Caption = name;
            button.Click += delegate { action(); };
            tips.SetToolTip(button, tip);
            return button;
        }

        private void BuildChrome()
        {
            // Rows: tab strip, toolbar, music bar, page, music bar, status line and buttons.
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 2));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 2));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            strip.Dock = DockStyle.Fill;
            strip.WrapContents = false;
            strip.AutoScroll = true;
            strip.Padding = new Padding(8, 4, 8, 0);
            strip.Margin = Padding.Empty;
            var mark = new MarkLabel { Width = 66, Height = BarButtonHeight, Margin = new Padding(6, 0, 12, 0) };
            tips.SetToolTip(mark, "Arabara");
            strip.Controls.Add(mark);
            add = BarButton("+", 30, delegate { AddTab(); address.Focus(); });
            tips.SetToolTip(add, "New tab (Ctrl+T)");
            strip.Controls.Add(add);
            // The tab strip is also the title bar: its empty space moves the window and
            // the window buttons sit at its right end.
            var titleRow = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Canvas };
            closeButton = new CaptionButton(CaptionKind.Close);
            maxButton = new CaptionButton(CaptionKind.Maximize);
            minButton = new CaptionButton(CaptionKind.Minimize);
            closeButton.Click += delegate { Close(); };
            maxButton.Click += delegate { ToggleMaximized(); };
            minButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            tips.SetToolTip(closeButton, "Close");
            tips.SetToolTip(maxButton, "Maximize / Restore");
            tips.SetToolTip(minButton, "Minimize");
            titleRow.Controls.Add(strip);
            titleRow.Controls.Add(minButton);
            titleRow.Controls.Add(maxButton);
            titleRow.Controls.Add(closeButton);
            // The logo switches the start page's background animation.
            mark.Cursor = Cursors.Hand;
            tips.SetToolTip(mark, Edition.Public ? "Click: next background (MARK + SEA, SOUND WAVE, RINGS)."
                : "Click: next background (each picture in turn). Right-click: choose a video, or add a picture. Shift+right-click: remove the picture shown.");
            mark.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && Edition.Public) return;
                if (e.Button == MouseButtons.Right && (ModifierKeys & Keys.Shift) != 0) RemoveBackdropPicture();
                else if (e.Button == MouseButtons.Right) ChooseBackdropFile();
                else if (e.Button == MouseButtons.Left) NextBackdrop();
            };
            foreach (Control dragArea in new Control[] { strip, titleRow })
                dragArea.MouseDown += delegate(object sender, MouseEventArgs e) { CaptionMouseDown((Control)sender, e); };
            layout.Controls.Add(titleRow, 0, 0);

            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 9, RowCount = 1, Padding = new Padding(8, 4, 8, 4), Margin = Padding.Empty };
            foreach (float width in new[] { 36F, 36F, 36F }) toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (float width in new[] { 65F, 103F, 78F, 80F }) toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            back = BarButton("<", 32, delegate { if (Core(active) != null && Core(active).CanGoBack) Core(active).GoBack(); });
            forward = BarButton(">", 32, delegate { if (Core(active) != null && Core(active).CanGoForward) Core(active).GoForward(); });
            var refresh = BarButton("F5", 32, delegate { if (Core(active) != null) Core(active).Reload(); });
            tips.SetToolTip(back, "Back (Alt+←)");
            tips.SetToolTip(forward, "Forward (Alt+→)");
            tips.SetToolTip(refresh, "Reload (F5)");
            toolbar.Controls.Add(back, 0, 0);
            toolbar.Controls.Add(forward, 1, 0);
            toolbar.Controls.Add(refresh, 2, 0);
            var addressBox = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(8, 5, 8, 2), Margin = new Padding(5, 2, 5, 2) };
            var prompt = new Label { Text = ">", Dock = DockStyle.Left, Width = 24,
                ForeColor = Accent, TextAlign = ContentAlignment.TopLeft };
            addressBox.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(address.Focused ? Accent : UiPalette.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, addressBox.Width - 1, addressBox.Height - 1);
            };
            address.Enter += delegate { addressBox.Invalidate(); };
            address.Leave += delegate { addressBox.Invalidate(); };
            prompt.Click += delegate { address.Focus(); };
            address.BorderStyle = BorderStyle.None;
            address.Dock = DockStyle.Fill;
            address.BackColor = Surface;
            address.ForeColor = Ink;
            address.AutoCompleteMode = AutoCompleteMode.None;
            address.MaxLength = 8192;
            address.AccessibleName = "HTTPS address";
            address.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await OpenAddressAsync(address.Text); }
            };
            tips.SetToolTip(address, "Enter a domain such as discord.com or paste an HTTPS link. HTTPS is added automatically; keywords are never sent to a search engine.");
            // The ad blocker's switch for the site shown (see AdBlock), at the box's right end.
            adSite = new AdSiteButton { Dock = DockStyle.Right, Width = 40, BackColor = Surface, Visible = false };
            adSite.Click += delegate { ToggleAdsOnSite(); };
            addressBox.Controls.Add(address);
            addressBox.Controls.Add(prompt);
            addressBox.Controls.Add(adSite);
            toolbar.Controls.Add(addressBox, 3, 0);
            toolbar.Controls.Add(BarButton("OPEN", 61, async delegate { await OpenAddressAsync(address.Text); }), 4, 0);
            keep = BarButton("KEEP ALIVE", 99, ToggleKeepAlive);
            tips.SetToolTip(keep, KeepTip(null));
            toolbar.Controls.Add(keep, 5, 0);
            stream = BarButton("STREAM", 74, ToggleStream);
            tips.SetToolTip(stream, StreamTip(null));
            toolbar.Controls.Add(stream, 6, 0);
            var history = BarButton("HISTORY", 76, ToggleHistory);
            tips.SetToolTip(history, "History (Ctrl+H)");
            toolbar.Controls.Add(history, 7, 0);
            // Shown only when GitHub has a newer release.
            update = BarButton("UPDATE", 116, async delegate { await UpdateNowAsync(); });
            update.ForeColor = Accent;
            update.Visible = false;
            toolbar.Controls.Add(update, 8, 0);
            layout.Controls.Add(toolbar, 0, 1);
            layout.Controls.Add(topBar, 0, 2);

            var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            viewport.Dock = DockStyle.Fill;
            historyPanel.Dock = DockStyle.Right;
            historyPanel.Width = 330;
            historyPanel.BackColor = Surface;
            historyPanel.Padding = new Padding(12);
            historyPanel.Visible = false;
            historyList.Dock = DockStyle.Fill;
            historyList.BorderStyle = BorderStyle.None;
            historyList.BackColor = Surface;
            historyList.ForeColor = Ink;
            historyList.DrawMode = DrawMode.OwnerDrawFixed;
            historyList.ItemHeight = 56;
            historyList.IntegralHeight = false;
            historyList.DrawItem += DrawHistory;
            historyList.DoubleClick += async delegate
            {
                var entry = historyList.SelectedItem as HistoryEntry;
                if (entry != null) await OpenAddressAsync(entry.Url);
            };
            var historyHeading = new Label { Text = "> HISTORY", Dock = DockStyle.Top, Height = 34, ForeColor = Accent };
            var clear = ButtonFor("CLEAR HISTORY", 140, delegate
            {
                if (MessageBox.Show(this, "Clear Arabara's browsing history? Your sign-in sessions will be kept.", "History", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    try { store.Clear(); PopulateHistory(); } catch (Exception ex) { SetStatus("Unable to clear history: " + ex.Message); }
                }
            });
            clear.Dock = DockStyle.Bottom;
            historyPanel.Controls.Add(historyList);
            historyPanel.Controls.Add(historyHeading);
            historyPanel.Controls.Add(clear);
            body.Controls.Add(viewport);
            body.Controls.Add(historyPanel);
            layout.Controls.Add(body, 0, 3);
            var footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12, 0, 8, 0) };
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            status.ForeColor = Muted;
            status.Font = UiFonts.Body(9F);
            // The switches, borderless text in the status line (see SwitchLabel); a value turns
            // red while its saving mode is on.
            var resources = SwitchFor("MEMORY", "Memory used by Arabara and its pages", ShowMemory);
            economy = SwitchFor("LOW RAM", "On: reduce background memory while scripts and connections stay active. Off: idle tabs sleep after 30 seconds. Turn off if voice stutters.", delegate { SetLowMemoryMode(!lowMemoryMode, true); });
            youtube = SwitchFor("YT", "LIGHT: YouTube plays H.264 (up to 1080p), decoded by the GPU on most PCs; about half the CPU in Arabara's measurements. FULL: VP9/AV1 up to 4K. Applies when YouTube reloads.", delegate { SetYouTubeLight(!youtubeLight, true); });
            youtube.Separated = false;
            visuals = SwitchFor("VISUALS", "Auto: stop drawing the web page when you switch to another app. Scripts and media remain enabled. Choose Live if a website pauses playback in the background.", delegate { SetPauseBackgroundVisuals(!pauseBackgroundVisuals, true); });
            footer.Controls.Add(status);
            footer.Controls.Add(youtube);
            footer.Controls.Add(visuals);
            footer.Controls.Add(economy);
            footer.Controls.Add(resources);
            layout.Controls.Add(bottomBar, 0, 4);
            layout.Controls.Add(footer, 0, 5);
            Controls.Add(layout);
            // The music bars listen only while this window is active and not minimized: every
            // 16 ms while sound plays, every quarter second while it is quiet (to notice the
            // music starting), and not at all otherwise.
            barTimer.Tick += delegate
            {
                bool listening = WindowState != FormWindowState.Minimized && ActiveForm == this;
                if (MusicBar.Update(listening)) { topBar.Invalidate(); bottomBar.Invalidate(); }
                int interval = listening ? (MusicBar.Lively ? 16 : 250) : 500;
                if (barTimer.Interval != interval) barTimer.Interval = interval;
            };
            barTimer.Start();
        }

        // Removes every trace of the startup animation and returns its memory.
        private void EndIntro()
        {
            if (intro == null) return;
            var finished = intro;
            intro = null;
            Controls.Remove(finished);
            finished.Dispose();
            address.Focus();
            // The live interface, already animating behind the intro, fades in from black.
            if (!smoke && Visible && WindowState != FormWindowState.Minimized)
            {
                curtain = new FadeCurtain(this, 600);
                curtain.FormClosed += delegate { BeginInvoke(new Action(EndCurtain)); };
                curtain.Begin();
            }
            else ReleaseIntroMemory();
        }

        private void EndCurtain()
        {
            if (curtain != null) { curtain.Dispose(); curtain = null; }
            ReleaseIntroMemory();
        }

        private static void ReleaseIntroMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (intro != null) { intro.Skip(); e.Handled = e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            int caption = ColorTranslator.ToWin32(Canvas);
            int captionText = ColorTranslator.ToWin32(Ink);
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(Handle, 36, ref captionText, sizeof(int));
            address.Focus();
            if (intro != null) intro.Start();
            if (smoke) BeginInvoke(new Action(async delegate { await RunSmokeAsync(); }));
            else BeginInvoke(new Action(async delegate { await StartThemeMusicAsync(); }));
        }

        private BrowserTab AddTab()
        {
            var tab = new BrowserTab();
            tab.Page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas, Visible = false };
            var welcome = new HomeView { Info = HomeSnapshot, MusicOpen = () => musicPanel != null && musicPanel.IsOpen };
            welcome.OpenRequested += async delegate(string url) { if (active == tab) await OpenAddressAsync(url); };
            welcome.MusicRequested += delegate { if (active == tab) OpenMusic(); };
            tab.Page.Controls.Add(welcome);
            tab.PausedVisuals = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Canvas, ForeColor = Muted, Visible = false,
                Text = "[ RENDERING PAUSED ]\n\nReturn to Arabara to resume the page." };
            tab.Page.Controls.Add(tab.PausedVisuals);
            tab.Header = new Panel { Width = 160, Height = BarButtonHeight, Margin = new Padding(2, 0, 2, 0), BackColor = Surface, Padding = new Padding(0, 0, 0, 2) };
            tab.Header.Paint += delegate(object sender, PaintEventArgs e)
            { if (tab == active) using (var pen = new Pen(Accent, 2)) e.Graphics.DrawLine(pen, 0, tab.Header.Height - 1, tab.Header.Width, tab.Header.Height - 1); };
            tab.Select = ButtonFor("New tab", 132, delegate { SelectTab(tab); });
            tab.Select.Dock = DockStyle.Fill;
            tab.Select.TextAlign = ContentAlignment.MiddleLeft;
            tab.Select.AutoEllipsis = true;
            var close = ButtonFor("x", 26, delegate { CloseTab(tab); });
            close.Dock = DockStyle.Right;
            tips.SetToolTip(close, "Close tab (Ctrl+W)");
            tab.Header.Controls.Add(tab.Select);
            tab.Header.Controls.Add(close);
            viewport.Controls.Add(tab.Page);
            strip.Controls.Add(tab.Header);
            strip.Controls.SetChildIndex(add, strip.Controls.Count - 1);
            tabs.Add(tab);
            SelectTab(tab);
            return tab;
        }

        private void SelectTab(BrowserTab tab)
        {
            if (tab.Closed || closing) return;
            active = tab;
            if (tab.Frozen) Thaw(tab);
            address.Text = AddressTextFor(tab);
            address.SelectionStart = address.TextLength;
            address.SelectionLength = 0;
            UpdateVisibility();
            UpdateChrome();
        }

        private void CloseTab(BrowserTab tab)
        {
            if (tab.Closed) return;
            int index = tabs.IndexOf(tab);
            tab.Closed = true;
            tabs.Remove(tab);
            EndShare(tab);
            if (tab.Stream != null) { var window = tab.Stream; tab.Stream = null; window.Dispose(); }
            if (tab.Mirror != null) tab.Mirror.Detach();
            if (tab.View != null) tab.View.Dispose();
            tab.Page.Dispose();
            tab.Header.Dispose();
            if (tabs.Count == 0) { active = null; AddTab(); }
            else if (active == tab) SelectTab(tabs[Math.Min(index, tabs.Count - 1)]);
            ScheduleSleep();
        }

        private static CoreWebView2 Core(BrowserTab tab)
        {
            return tab == null || tab.Closed || tab.View == null || tab.View.IsDisposed ? null : tab.View.CoreWebView2;
        }

        private static string AddressTextFor(BrowserTab tab)
        {
            if (String.IsNullOrEmpty(tab.Url) || tab.Url == "about:blank") return AddressPolicy.HttpsPrefix;
            return LocalAddressText(tab.Url, null) ?? tab.Url;
        }

        private bool TabVisible(BrowserTab tab) { return tab == active && WindowState != FormWindowState.Minimized; }

        private bool ShouldRender(BrowserTab tab)
        { return TabVisible(tab) && (!pauseBackgroundVisuals || windowActive || Protected(tab)); }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // The invisible smoke window drives focus states explicitly so another
            // desktop application cannot race its foreground/background assertions.
            if (!smoke) SetWindowActive(true);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!closing && !smoke) { focusTimer.Stop(); focusTimer.Start(); }
        }

        private bool IsOwnForegroundWindow()
        {
            uint foregroundProcess;
            GetWindowThreadProcessId(GetForegroundWindow(), out foregroundProcess);
            using (var host = Process.GetCurrentProcess())
                if (foregroundProcess == host.Id) return true;
            if (environment == null) return false;
            try { return environment.GetProcessInfos().Any(info => info.ProcessId == foregroundProcess); }
            catch (COMException) { return true; }
            catch (InvalidOperationException) { return true; }
        }

        private void SetWindowActive(bool value)
        {
            focusTimer.Stop();
            windowActive = value;
            if (!closing) UpdateVisibility();
        }

        private void SetPauseBackgroundVisuals(bool enabled, bool persist)
        {
            pauseBackgroundVisuals = enabled;
            if (persist)
            {
                preferences.PauseBackgroundVisuals = enabled;
                try { preferences.Save(dataRoot); }
                catch (Exception ex) { SetStatus("Unable to save visual preference: " + ex.Message); }
            }
            UpdateVisibility();
            UpdateChrome();
        }

        private void UpdateVisibility()
        {
            if (closing) return;
            // Out of sight: the page shown is not drawn. Back in sight, a tab frozen meanwhile reloads.
            bool away = active != null && !active.Closed && !ShouldRender(active);
            if (away) { if (awaySince == DateTime.MinValue) awaySince = DateTime.UtcNow; }
            else
            {
                awaySince = DateTime.MinValue;
                if (active != null && active.Frozen) Thaw(active);
            }
            foreach (var tab in tabs)
            {
                bool visible = TabVisible(tab);
                bool render = ShouldRender(tab);
                var core = Core(tab);
                if (tab.Stream != null)
                {
                    // The page lives in its stream window and always renders there.
                    if (core != null && core.IsSuspended) core.Resume();
                    tab.Page.Visible = visible;
                    tab.PausedVisuals.Visible = false;
                    if (visible) tab.Page.BringToFront();
                    if (tab.Mirror != null) tab.Mirror.Sync();
                    tab.HiddenSince = DateTime.MinValue;
                    tab.Select.ForeColor = tab == active ? Accent : Muted;
                    tab.Select.BackColor = tab == active ? UiPalette.Active : Surface;
                    tab.Header.Invalidate();
                    continue;
                }
                if (visible && core != null && core.IsSuspended) core.Resume();
                // Hide the controller before its parent: WinForms may otherwise report
                // Visible=false while the native WebView controller still considers itself visible.
                if (!render && tab.View != null) tab.View.Visible = false;
                tab.Page.Visible = visible;
                tab.PausedVisuals.Visible = visible && !render && core != null;
                if (tab.PausedVisuals.Visible) tab.PausedVisuals.BringToFront();
                if (render && tab.View != null) { tab.View.Visible = true; tab.View.BringToFront(); }
                if (visible) { tab.Page.BringToFront(); tab.SleepAt = DateTime.MinValue; tab.SleepAttempted = false; }
                if (tab == active) tab.HiddenSince = DateTime.MinValue;
                else if (tab.HiddenSince == DateTime.MinValue) tab.HiddenSince = DateTime.UtcNow;
                tab.Select.ForeColor = tab == active ? Accent : Muted;
                tab.Select.BackColor = tab == active ? UiPalette.Active : Surface;
                tab.Header.Invalidate();
            }
            UpdateMusicVisibility();
            ScheduleSleep();
        }

        private async Task OpenAddressAsync(string text)
        {
            // Local paths, also when typed or pasted after the starter "https://".
            string local = LocalPath.TryParseInput(text);
            if (local == null && text != null && text.Trim().StartsWith(AddressPolicy.HttpsPrefix, StringComparison.OrdinalIgnoreCase))
                local = LocalPath.TryParseInput(text.Trim().Substring(AddressPolicy.HttpsPrefix.Length));
            if (local != null) { await OpenLocalPathAsync(active, local); return; }
            Uri uri;
            if (!AddressPolicy.TryParseInput(text, out uri))
            {
                SetStatus("Enter a domain, an HTTPS link or a local path such as D:\\Photos. Arabara does not search for keywords.");
                return;
            }
            var tab = active;
            int version = ++tab.NavigationVersion;
            try
            {
                SetStatus("Opening " + uri.Host + "…");
                await EnsureTabAsync(tab);
                if (closing || tab.Closed || version != tab.NavigationVersion) return;
                tab.Url = uri.AbsoluteUri;
                if (tab == active)
                {
                    address.Text = tab.Url;
                    address.SelectionStart = address.TextLength;
                    address.SelectionLength = 0;
                }
                if (Core(tab).IsSuspended) Core(tab).Resume();
                Core(tab).Navigate(tab.Url);
                if (tab == active) tab.View.Focus();
            }
            catch (Exception ex) { if (!closing) SetStatus("Unable to open page: " + ex.Message); }
        }

        private Task EnsureTabAsync(BrowserTab tab)
        {
            if (tab.Initialize == null) tab.Initialize = InitializeTabAsync(tab);
            return tab.Initialize;
        }

        private async Task InitializeTabAsync(BrowserTab tab)
        {
            environment = await EnsureEnvironmentAsync();
            if (closing || tab.Closed) return;
            var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Canvas, Visible = ShouldRender(tab) };
            tab.View = view;
            tab.Page.Controls.Add(view);
            view.BringToFront();
            await view.EnsureCoreWebView2Async(environment);
            if (closing || tab.Closed) return;
            AudioMeter.OwnAudioProcess = (int)view.CoreWebView2.BrowserProcessId;
            // The ad blocker is in the profile before the first page loads (see AdBlock).
            await EnsureAdBlockerAsync(view.CoreWebView2.Profile);
            if (closing || tab.Closed) return;
            // Release the native start page and its drawing buffer once the web view is ready.
            foreach (var welcome in tab.Page.Controls.OfType<HomeView>().ToArray()) welcome.Dispose();
            var core = view.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = true;
            core.Settings.IsPasswordAutosaveEnabled = true;
            core.Settings.IsReputationCheckingRequired = true;
            core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                if (HandleLocalNavigation(tab, core, e)) return;
                Uri parsed;
                if (e.Uri != "about:blank" && !AddressPolicy.TryParse(e.Uri, out parsed))
                { e.Cancel = true; SetStatus("Blocked a non-HTTPS address."); }
            };
            core.LaunchingExternalUriScheme += delegate(object sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e) { e.Cancel = true; };
            core.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (closing || tab.Closed) return;
                string localText = LocalAddressText(core.Source, localToken);
                if (e.IsSuccess) { RememberPage(tab); SetStatus("Loaded  ·  " + (localText ?? new Uri(core.Source).Host)); }
                else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) SetStatus("Unable to load page: " + e.WebErrorStatus);
                UpdateChrome();
                ScheduleSleep();
            };
            core.SourceChanged += delegate(object sender, CoreWebView2SourceChangedEventArgs e)
            {
                if (closing || tab.Closed) return;
                tab.Url = core.Source;
                if (!e.IsNewDocument) RememberPage(tab);
                UpdateChrome();
            };
            core.DocumentTitleChanged += delegate
            {
                if (closing || tab.Closed) return;
                tab.Title = core.DocumentTitle;
                tab.Select.Text = (tab.Stream != null ? "[LIVE] " : "") + (String.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title);
                tips.SetToolTip(tab.Select, tab.Title);
                if (tab.Stream != null) { tab.Stream.SetTitle(String.IsNullOrWhiteSpace(tab.Title) ? "Arabara" : tab.Title); tab.Mirror.Invalidate(); }
                UpdateChrome();
            };
            core.HistoryChanged += delegate { if (!closing) UpdateChrome(); };
            core.IsDocumentPlayingAudioChanged += delegate { if (!closing) { UpdateChrome(); ScheduleSleep(); } };
            core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { RequestPermission(tab, e); };
            core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { OpenPopup(tab, e); };
            core.ScreenCaptureStarting += delegate(object sender, CoreWebView2ScreenCaptureStartingEventArgs e) { ScreenCaptureStarting(tab, e); };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(TrackShares);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(TrackCalls);
            await InstallYouTubeLightAsync(tab, core);
            core.WindowCloseRequested += delegate { if (!closing && !tab.Closed) BeginInvoke(new Action(delegate { CloseTab(tab); })); };
            core.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs e)
            {
                tab.Downloads++;
                ScheduleSleep();
                var operation = e.DownloadOperation;
                EventHandler<object> handler = null;
                handler = delegate
                {
                    if (operation.State == CoreWebView2DownloadState.InProgress) return;
                    operation.StateChanged -= handler;
                    tab.Downloads = Math.Max(0, tab.Downloads - 1);
                    if (!closing) ScheduleSleep();
                };
                operation.StateChanged += handler;
            };
            core.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e)
            { if (!closing) SetStatus("Page process failed (" + e.ProcessFailedKind + "). Close the tab and reopen the link."); };
            view.KeyDown += delegate(object sender, KeyEventArgs e)
            { if (HandleShortcut(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; } };
            if (smoke) InstallTestPages(core);
            InstallLocalFiles(core);
            UpdateVisibility();
        }

        private void RequestPermission(BrowserTab tab, CoreWebView2PermissionRequestedEventArgs args)
        {
            if (args.PermissionKind == CoreWebView2PermissionKind.Notifications)
            { args.State = CoreWebView2PermissionState.Deny; return; }
            if (args.PermissionKind != CoreWebView2PermissionKind.Microphone && args.PermissionKind != CoreWebView2PermissionKind.Camera) return;
            args.SavesInProfile = false;
            var deferral = args.GetDeferral();
            // Show native prompts after leaving the synchronous WebView callback.
            BeginInvoke(new Action(delegate
            {
                try
                {
                    args.State = CoreWebView2PermissionState.Deny;
                    if (closing || tab.Closed) return;
                    string device = args.PermissionKind == CoreWebView2PermissionKind.Microphone ? "microphone" : "camera";
                    if (MessageBox.Show(this, args.Uri + "\n\nAllow access to your " + device + "? This tab will stay active until you close it.",
                        "Site permission", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        args.State = CoreWebView2PermissionState.Allow;
                        tab.MediaLease = true;
                        if (Core(tab).IsSuspended) Core(tab).Resume();
                        UpdateChrome();
                        ScheduleSleep();
                    }
                }
                finally { deferral.Complete(); }
            }));
        }

        private void OpenPopup(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            Uri uri;
            if (closing || opener.Closed || (!args.IsUserInitiated) ||
                (args.Uri != "about:blank" && !AddressPolicy.TryParse(args.Uri, out uri)))
            { SetStatus("Blocked an automatic popup or a non-HTTPS address."); return; }
            var deferral = args.GetDeferral();
            BeginInvoke(new Action(async delegate
            {
                try
                {
                    if (closing || opener.Closed) return;
                    var tab = AddTab();
                    await EnsureTabAsync(tab);
                    if (!closing && !tab.Closed) args.NewWindow = Core(tab);
                }
                catch (Exception ex) { if (!closing) SetStatus("Unable to open popup: " + ex.Message); }
                finally { deferral.Complete(); }
            }));
        }

        private void RememberPage(BrowserTab tab)
        {
            var core = Core(tab);
            // Local folders and files stay out of browsing history.
            if (core == null || IsLocalPage(core.Source) || IsFileUrl(core.Source)) return;
            store.Record(core.Source, core.DocumentTitle);
            saveTimer.Stop();
            saveTimer.Start();
            if (historyPanel.Visible) PopulateHistory();
        }

        private void SaveHistory()
        {
            try { store.Save(); }
            catch (Exception ex) { SetStatus("Unable to save history: " + ex.Message); }
        }

        // The start page backgrounds from the preferences. The public edition loads no video or
        // picture (the preferences keep them), so the logo cycles MARK + SEA and SOUND WAVE.
        internal static void LoadBackdrops(BrowserPreferences preferences, string root)
        {
            SpinningMark.CacheFolder = Path.Combine(root, "cache");
            // Pictures: the list, or the single picture older versions saved.
            if (preferences.BackdropPictures == null) preferences.BackdropPictures = new List<PictureSetting>();
            preferences.BackdropPictures.RemoveAll(p => p == null || String.IsNullOrEmpty(p.Path));
            if (preferences.BackdropPictures.Count == 0 && !String.IsNullOrEmpty(preferences.BackdropPicture))
                preferences.BackdropPictures.Add(new PictureSetting { Path = preferences.BackdropPicture });
            SpinningMark.VideoPath = Edition.Public ? null : preferences.BackdropVideo;
            SpinningMark.VideoName = preferences.BackdropVideoName;
            SpinningMark.SetPictures(Edition.Public ? null : preferences.BackdropPictures, preferences.BackdropPictureIndex);
            int savedScene = SpinningMark.SceneFromSaved(preferences.BackdropScene);
            if ((savedScene == SpinningMark.VideoScene && String.IsNullOrEmpty(SpinningMark.VideoPath)) ||
                (savedScene == SpinningMark.PictureScene && SpinningMark.PictureCount == 0)) savedScene = 0;
            SpinningMark.SetScene(savedScene, false);
        }

        private void NextBackdrop()
        {
            // PICTURE shows each picture in turn before moving on.
            if (SpinningMark.Scene == SpinningMark.PictureScene && SpinningMark.PictureIndex + 1 < SpinningMark.PictureCount)
            {
                SpinningMark.ShowPicture(SpinningMark.PictureIndex + 1, true);
                SaveBackdrop();
                return;
            }
            int next = SpinningMark.Scene;
            // Without a chosen video or picture, the cycle skips VIDEO LOOP or PICTURE.
            do next = (next + 1) % SpinningMark.SceneNames.Length;
            while ((next == SpinningMark.VideoScene && String.IsNullOrEmpty(SpinningMark.VideoPath)) ||
                   (next == SpinningMark.PictureScene && SpinningMark.PictureCount == 0));
            if (next == SpinningMark.PictureScene) SpinningMark.ShowPicture(0, true);
            else SpinningMark.SetScene(next, true);
            SaveBackdrop();
        }

        // Saves the background choice and says what is shown.
        private void SaveBackdrop()
        {
            preferences.BackdropScene = SpinningMark.SavedScene(SpinningMark.Scene);
            if (!Edition.Public) preferences.BackdropPictureIndex = SpinningMark.PictureIndex;
            preferences.BackdropPicture = preferences.BackdropPictures.Count > 0 ? preferences.BackdropPictures[0].Path : null;
            try { preferences.Save(dataRoot); }
            catch (Exception ex) { SetStatus("Unable to save background preference: " + ex.Message); return; }
            SetStatus("Background: " + SpinningMark.DisplayName + "  \u00b7  click the logo for the next one");
            if (active != null) foreach (var home in active.Page.Controls.OfType<HomeView>()) home.Invalidate();
        }

        // Shift+right-click on the logo: takes the picture shown out of the list (the file
        // itself is not touched).
        private void RemoveBackdropPicture()
        {
            if (SpinningMark.Scene != SpinningMark.PictureScene || SpinningMark.PictureCount == 0) return;
            int index = SpinningMark.PictureIndex;
            string name = SpinningMark.DisplayName;
            preferences.BackdropPictures.RemoveAt(index);
            SpinningMark.SetPictures(preferences.BackdropPictures, Math.Min(index, preferences.BackdropPictures.Count - 1));
            if (preferences.BackdropPictures.Count == 0) SpinningMark.SetScene(0, true);
            else SpinningMark.ShowPicture(SpinningMark.PictureIndex, true);
            SaveBackdrop();
            SetStatus("Background: " + name + " removed from the pictures");
        }

        // Right-click on the logo: a video (VIDEO LOOP) or a picture (PICTURE) for the
        // background, told apart by the file's extension.
        private void ChooseBackdropFile()
        {
            const string Videos = "*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv", Pictures = "*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff";
            bool isPicture;
            using (var dialog = new OpenFileDialog { Title = "Background video (played muted and looping) or picture",
                Filter = "Videos and pictures|" + Videos + ";" + Pictures + "|Videos|" + Videos + "|Pictures|" + Pictures + "|All files (*.*)|*.*" })
            {
                string last = SpinningMark.Scene == SpinningMark.PictureScene ? SpinningMark.PicturePath : SpinningMark.VideoPath ?? SpinningMark.PicturePath;
                if (!String.IsNullOrEmpty(last)) dialog.InitialDirectory = Path.GetDirectoryName(last);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                isPicture = Array.IndexOf(new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" }, extension) >= 0;
                if (isPicture)
                {
                    // A new picture is added to the list (a known one is just shown).
                    int index = preferences.BackdropPictures.FindIndex(p => String.Equals(p.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) { preferences.BackdropPictures.Add(new PictureSetting { Path = dialog.FileName }); index = preferences.BackdropPictures.Count - 1; }
                    SpinningMark.SetPictures(preferences.BackdropPictures, SpinningMark.PictureIndex);
                    SpinningMark.ShowPicture(index, true);
                    SaveBackdrop();
                    return;
                }
                // A new video goes by its file name until it is given another.
                if (!String.Equals(preferences.BackdropVideo, dialog.FileName, StringComparison.OrdinalIgnoreCase)) preferences.BackdropVideoName = null;
                SpinningMark.VideoPath = dialog.FileName; preferences.BackdropVideo = dialog.FileName;
                SpinningMark.VideoName = preferences.BackdropVideoName;
            }
            SpinningMark.SetScene(SpinningMark.VideoScene, true);
            SaveBackdrop();
            SetStatus("Background: " + SpinningMark.DisplayName + "  \u00b7  muted, looping");
        }

        private HomeInfo HomeSnapshot()
        {
            return new HomeInfo { Recent = store.Entries.Take(5).ToList(), HistoryCount = store.Entries.Count, Tabs = tabs.Count,
                EngineOnline = environment != null, LowMemory = lowMemoryMode, LiveVisuals = !pauseBackgroundVisuals, Session = session };
        }

        private void UpdateChrome()
        {
            if (closing || active == null) return;
            foreach (var home in active.Page.Controls.OfType<HomeView>()) home.Invalidate();
            var core = Core(active);
            if (!address.Focused && address.Text != AddressTextFor(active)) address.Text = AddressTextFor(active);
            back.Enabled = core != null && core.CanGoBack;
            forward.Enabled = core != null && core.CanGoForward;
            keep.Text = active.MediaLease ? "MIC / CAM" : active.Capturing ? "SHARING" : active.InCall ? "IN CALL" : active.KeepAlive ? "KEPT ALIVE" : "KEEP ALIVE";
            keep.ForeColor = active.KeepAlive || active.MediaLease || active.Capturing || active.InCall ? Accent : Ink;
            stream.Text = active.Stream != null ? "LIVE" : "STREAM";
            stream.ForeColor = active.Stream != null ? Accent : Ink;
            string tip = KeepTip(active);
            if (tip != keepTip) { keepTip = tip; tips.SetToolTip(keep, tip); }
            tip = StreamTip(active);
            if (tip != streamTip) { streamTip = tip; tips.SetToolTip(stream, tip); }
            economy.Value = lowMemoryMode ? "ON" : "OFF";
            economy.On = lowMemoryMode;
            visuals.Value = pauseBackgroundVisuals ? "AUTO" : "LIVE";
            visuals.On = pauseBackgroundVisuals;
            youtube.Value = youtubeLight ? "LIGHT" : "FULL";
            youtube.On = youtubeLight;
            UpdateAdSite();
            Text = String.IsNullOrWhiteSpace(active.Title) || active.Title == "New tab" ? "Arabara" : active.Title + " — Arabara";
        }

        private static string VersionText(Version version) { return version.Revision > 0 ? version.ToString(4) : version.ToString(3); }

        // Asks GitHub for the latest release and shows UPDATE when it is newer than this build;
        // quietly tries again later when GitHub cannot be reached.
        private async Task CheckForUpdateAsync()
        {
            if (closing || updating) return;
            ReleaseInfo release;
            try { release = await Updater.LatestAsync(); }
            catch (System.Net.WebException) { return; }
            catch (IOException) { return; }
            catch (InvalidOperationException) { return; }
            catch (ArgumentException) { return; }
            catch (FormatException) { return; }
            if (closing || release == null || release.Version <= Updater.Current) return;
            available = release;
            string version = VersionText(release.Version);
            update.Text = "UPDATE " + version;
            update.Width = TextRenderer.MeasureText(update.Text, update.Font).Width + 26;
            tips.SetToolTip(update, "Arabara " + version + " is on GitHub. Click to download it and restart.");
            update.Visible = true;
        }

        // Downloads the newer release, swaps the program's files and restarts (see Updater);
        // a personal build, or a release without the program zip, opens the release page.
        private async Task UpdateNowAsync()
        {
            var release = available;
            if (release == null || updating || closing) return;
            string version = VersionText(release.Version);
            if (!Edition.Public || release.ZipUrl == null || release.ZipName == null)
            {
                AddTab();
                await OpenAddressAsync(release.PageUrl ?? Updater.ReleasesPage);
                return;
            }
            string size = release.ZipSize > 0 ? " (" + (release.ZipSize + 1023) / 1024 + " KB)" : "";
            if (MessageBox.Show(this, "Arabara " + version + " is available on GitHub" + size + ".\n\nDownload it and restart Arabara now? Open tabs will close; history, sign-ins and settings are kept.",
                "Update Arabara", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            updating = true;
            update.Enabled = false;
            string work = Updater.WorkFolder, zip = Path.Combine(work, release.ZipName), folder = AppDomain.CurrentDomain.BaseDirectory;
            Exception failure = null;
            try
            {
                long total = release.ZipSize;
                var progress = new Progress<long>(received => SetStatus("Downloading Arabara " + version + "…  " + (total > 0 ? Math.Min(100, received * 100 / total) + " %" : received / 1024 + " KB")));
                await Updater.DownloadAsync(release, zip, progress, System.Threading.CancellationToken.None);
                SetStatus("Installing Arabara " + version + "…");
                string exe = await Task.Run(() => Updater.Apply(zip, folder, work));
                Process.Start(new ProcessStartInfo(exe, "--after " + Process.GetCurrentProcess().Id) { UseShellExecute = false, WorkingDirectory = folder });
                Close();
                return;
            }
            catch (System.Net.WebException ex) { failure = ex; }
            catch (IOException ex) { failure = ex; }
            catch (UnauthorizedAccessException ex) { failure = ex; }
            catch (InvalidOperationException ex) { failure = ex; }
            catch (System.ComponentModel.Win32Exception ex) { failure = ex; }
            updating = false;
            update.Enabled = true;
            SetStatus("Update failed: " + failure.Message);
            if (MessageBox.Show(this, "Arabara could not update itself:\n" + failure.Message + "\n\nOpen the release page to download it yourself?", "Update Arabara",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                AddTab();
                await OpenAddressAsync(release.PageUrl ?? Updater.ReleasesPage);
            }
        }

        // What the keep-alive and stream icons say about the tab shown.
        private static string KeepTip(BrowserTab tab)
        {
            if (tab != null && tab.MediaLease) return "Microphone/camera: this tab stays running until it is closed.";
            if (tab != null && tab.Capturing) return "Sharing the screen: this tab runs at full priority until it is closed.";
            if (tab != null && tab.InCall) return "In a call: this tab keeps running.";
            if (tab != null && tab.KeepAlive) return "Kept alive (click to release): never frozen or reloaded; out of sight it keeps running with little memory.";
            return "Keep alive: never freeze or reload this tab; out of sight it keeps running with little memory.";
        }

        private static string StreamTip(BrowserTab tab)
        {
            if (tab != null && tab.Stream != null) return "Live (click to bring it back): this tab is in its own window for Discord to share.";
            return "Stream: open this tab in its own window so Discord (web or app) can share just this page. The tab keeps running at full priority. Press again or close the window to bring it back.";
        }

        private void ToggleKeepAlive()
        {
            if (active == null) return;
            if (active.MediaLease)
            { SetStatus("This tab has microphone/camera access and stays active until closed."); return; }
            active.KeepAlive = !active.KeepAlive;
            if (active.KeepAlive && Core(active) != null && Core(active).IsSuspended) Core(active).Resume();
            SetStatus(active.KeepAlive ? "Kept alive: never frozen, suspended or reloaded. Out of sight it is not drawn and keeps little memory, but it keeps running." : "This tab may save memory again when it is in the background.");
            UpdateVisibility();
            UpdateChrome();
            ScheduleSleep();
        }

        private bool EligibleForSleep(BrowserTab tab)
        {
            var core = Core(tab);
            return !lowMemoryMode && core != null && !Protected(tab) && SleepPolicy.CanSuspend(TabVisible(tab), tab.KeepAlive, tab.MediaLease, core.IsDocumentPlayingAudio, tab.Downloads > 0);
        }

        private void SetLowMemoryMode(bool enabled, bool persist)
        {
            lowMemoryMode = enabled;
            // Mode changes explicitly resume and normalize before choosing either
            // MemoryUsageTargetLevel OR TrySuspend. Never apply both concurrently.
            foreach (var tab in tabs)
            {
                tab.SleepAt = DateTime.MinValue;
                tab.SleepAttempted = false;
                if (tab.SuspendPending) continue; // Completion reconciles with the new mode.
                var core = Core(tab);
                if (core == null) continue;
                if (core.IsSuspended) core.Resume();
                ApplyMemoryTarget(tab, CoreWebView2MemoryUsageTargetLevel.Normal);
            }
            if (persist)
            {
                preferences.LowMemoryBackground = enabled;
                try { preferences.Save(dataRoot); }
                catch (Exception ex) { SetStatus("Unable to save memory preference: " + ex.Message); }
            }
            UpdateChrome();
            ScheduleSleep();
        }

        private void ApplyMemoryTarget(BrowserTab tab, CoreWebView2MemoryUsageTargetLevel level)
        {
            var core = Core(tab);
            if (core == null || tab.SuspendPending || tab.MemoryTargetUnavailable) return;
            try
            {
                if (core.IsSuspended) core.Resume();
                if (core.MemoryUsageTargetLevel != level) core.MemoryUsageTargetLevel = level;
            }
            catch (NotImplementedException) { MemoryTargetFailed(tab); }
            catch (NotSupportedException) { MemoryTargetFailed(tab); }
            catch (COMException) { MemoryTargetFailed(tab); }
            catch (InvalidOperationException) { MemoryTargetFailed(tab); }
        }

        private void MemoryTargetFailed(BrowserTab tab)
        {
            tab.MemoryTargetUnavailable = true;
            SetStatus("Low RAM is unavailable for this tab. Reopen the tab or turn Low RAM off.");
        }

        private void ScheduleSleep()
        {
            sleepTimer.Stop();
            if (closing) return;
            ScheduleFreeze();
            if (lowMemoryMode)
            {
                foreach (var tab in tabs)
                    ApplyMemoryTarget(tab, Protected(tab) || (TabVisible(tab) && windowActive) ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low);
                return;
            }
            DateTime next = DateTime.MaxValue;
            foreach (var tab in tabs)
            {
                if (!EligibleForSleep(tab)) { tab.SleepAt = DateTime.MinValue; tab.SleepAttempted = false; continue; }
                if (tab.SuspendPending || tab.SleepAttempted || Core(tab).IsSuspended) continue;
                if (tab.SleepAt == DateTime.MinValue) tab.SleepAt = DateTime.UtcNow.AddSeconds(30);
                if (tab.SleepAt < next) next = tab.SleepAt;
            }
            if (next == DateTime.MaxValue) return;
            sleepTimer.Interval = (int)Math.Max(50, Math.Min(30000, (next - DateTime.UtcNow).TotalMilliseconds));
            sleepTimer.Start();
        }

        private async Task SleepDueTabsAsync()
        {
            sleepTimer.Stop();
            foreach (var tab in tabs.ToArray())
            {
                if (closing) return;
                if (!EligibleForSleep(tab) || tab.SuspendPending || tab.SleepAttempted || tab.SleepAt > DateTime.UtcNow) continue;
                // A call with sound keeps running; asked again in half a minute.
                bool call = await InCallAsync(tab);
                if (closing || tab.Closed) continue;
                tab.InCall = call;
                if (call) { tab.SleepAt = DateTime.UtcNow.AddSeconds(30); continue; }
                if (!EligibleForSleep(tab) || tab.SuspendPending || tab.SleepAttempted) continue;
                tab.SleepAttempted = true;
                tab.SuspendPending = true;
                try
                {
                    var core = Core(tab);
                    tab.LastSuspendResult = "Accepted=" + await core.TrySuspendAsync();
                    // The user can switch tabs or grant microphone access while the request is in flight.
                    if (!closing && !tab.Closed && !EligibleForSleep(tab)) core.Resume();
                }
                catch (InvalidOperationException ex) { tab.LastSuspendResult = ex.ToString(); }
                catch (COMException ex) { tab.LastSuspendResult = ex.ToString(); }
                finally { tab.SuspendPending = false; }
            }
            if (!closing) ScheduleSleep();
        }

        private bool EligibleForFreeze(BrowserTab tab)
        {
            var core = Core(tab);
            return lowMemoryMode && core != null && !Protected(tab) && !tab.SuspendPending &&
                (tab != active ? tab.HiddenSince != DateTime.MinValue : awaySince != DateTime.MinValue) &&
                SleepPolicy.CanSuspend(false, tab.KeepAlive, tab.MediaLease, core.IsDocumentPlayingAudio, tab.Downloads > 0);
        }

        // When a tab freezes: a background tab FreezeAfter after it was hidden, the tab shown
        // AwayFreezeAfter after Arabara went out of sight; under memory pressure both sooner.
        private DateTime FreezeDue(BrowserTab tab, bool pressure)
        {
            if (!EligibleForFreeze(tab)) return DateTime.MaxValue;
            var delay = tab != active ? FreezeAfter : AwayFreezeAfter;
            if (pressure && PressureFreezeAfter < delay) delay = PressureFreezeAfter;
            var due = (tab != active ? tab.HiddenSince : awaySince) + delay;
            return due > tab.CallUntil ? due : tab.CallUntil;
        }

        // One-shot timer for the next tab due to freeze; nothing polls while no tab qualifies.
        private void ScheduleFreeze()
        {
            freezeTimer.Stop();
            if (closing) return;
            DateTime next = DateTime.MaxValue;
            bool pressure = UnderMemoryPressure();
            foreach (var tab in tabs)
            {
                var due = FreezeDue(tab, pressure);
                if (due < next) next = due;
            }
            if (next == DateTime.MaxValue) return;
            freezeTimer.Interval = (int)Math.Max(50, Math.Min(Int32.MaxValue, (next - DateTime.UtcNow).TotalMilliseconds));
            freezeTimer.Start();
        }

        private bool freezing;

        // Freezes the tabs that are due, except those in a call with sound, which are asked again
        // one freeze delay later.
        private async void FreezeDueTabs()
        {
            freezeTimer.Stop();
            if (closing || freezing) return;
            freezing = true;
            try
            {
                foreach (var tab in tabs.ToArray())
                {
                    if (closing) return;
                    if (tab.Closed || DateTime.UtcNow < FreezeDue(tab, UnderMemoryPressure())) continue;
                    bool call = await InCallAsync(tab);
                    if (closing || tab.Closed) continue;
                    tab.InCall = call;
                    if (call) { tab.CallUntil = DateTime.UtcNow + (tab != active ? FreezeAfter : AwayFreezeAfter); continue; }
                    if (DateTime.UtcNow >= FreezeDue(tab, UnderMemoryPressure())) Freeze(tab);
                }
            }
            finally { freezing = false; }
            if (!closing) ScheduleFreeze();
        }

        // Whether the tab's page is in a call with sound (see TrackCalls).
        private async Task<bool> InCallAsync(BrowserTab tab)
        {
            var core = Core(tab);
            if (core == null || core.IsSuspended) return false;
            try { return await core.ExecuteScriptAsync("typeof window.__arabaraCall === 'function' && window.__arabaraCall() === true") == "true"; }
            catch (InvalidOperationException) { return false; }
            catch (COMException) { return false; }
        }

        // Disposes the tab's WebView2 (its renderer and page memory) but keeps its
        // address and title. Selecting the tab loads the page again.
        private void Freeze(BrowserTab tab)
        {
            var view = tab.View;
            if (view == null || tab.Frozen) return;
            tab.InCall = false;
            tab.CallUntil = DateTime.MinValue;
            tab.Frozen = true;
            tab.NavigationVersion++;
            tab.View = null;
            tab.Initialize = null;
            tab.LightScript = null; // registered on the disposed WebView; the next one gets its own
            view.Dispose();
            tab.Select.Text = "~ " + (String.IsNullOrWhiteSpace(tab.Title) ? "Frozen tab" : tab.Title);
            tips.SetToolTip(tab.Select, "Frozen to save memory. Select to reload: " + tab.Url);
        }

        private void Thaw(BrowserTab tab)
        {
            tab.Frozen = false;
            tab.Select.Text = String.IsNullOrWhiteSpace(tab.Title) ? "New tab" : tab.Title;
            tips.SetToolTip(tab.Select, tab.Title);
            if (String.IsNullOrEmpty(tab.Url) || tab.Url == "about:blank") return;
            SetStatus("Reloading a tab that was frozen to save memory…");
            BeginInvoke(new Action(async delegate { if (active == tab && !tab.Closed && !closing) await OpenAddressAsync(tab.Url); }));
        }

        private void ToggleHistory() { historyPanel.Visible = !historyPanel.Visible; if (historyPanel.Visible) PopulateHistory(); }

        private void PopulateHistory()
        {
            historyList.BeginUpdate();
            historyList.Items.Clear();
            historyList.Items.AddRange(store.Entries.Cast<object>().ToArray());
            historyList.EndUpdate();
        }

        private void DrawHistory(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var item = (HistoryEntry)historyList.Items[e.Index];
            Color background = (e.State & DrawItemState.Selected) != 0 ? UiPalette.Active : Surface;
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, item.Title, Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 6, e.Bounds.Width - 12, 23), Ink, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, item.Url, status.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 30, e.Bounds.Width - 12, 20), Muted, TextFormatFlags.EndEllipsis);
        }

        private string MeasureMemory()
        {
            var ids = new HashSet<int> { Process.GetCurrentProcess().Id };
            if (environment != null) foreach (var info in environment.GetProcessInfos()) ids.Add(info.ProcessId);
            long privateWorkingSet = 0, privateBytes = 0;
            int measured = 0;
            foreach (int id in ids)
            {
                long pws, commit;
                if (!ProcessMemory.Read(id, out pws, out commit)) continue;
                privateWorkingSet += pws; privateBytes += commit; measured++;
            }
            int frozen = tabs.Count(t => t.Frozen);
            return String.Format("{0} processes · Memory (Task Manager): {1:N1} MiB · Private bytes: {2:N1} MiB{3}", measured,
                privateWorkingSet / 1048576.0, privateBytes / 1048576.0, frozen > 0 ? " · " + frozen + " frozen tab(s)" : "");
        }

        private void ShowMemory()
        {
            try { MessageBox.Show(this, MeasureMemory() + "\n\nIncludes Arabara and the WebView2 processes in this profile.\nMemory matches the Memory column in Task Manager (private working set).\nPrivate bytes measure committed memory, not physical RAM.\n\nMeasured only when requested; there is no continuous memory polling.", "Arabara memory"); }
            catch (Exception ex) { SetStatus("Unable to measure memory: " + ex.Message); }
        }

        private void SetStatus(string text) { if (!IsDisposed) status.Text = "> " + text; }

        // Custom title bar: the standard frame is kept (resize borders, shadow, snap,
        // animations) and only its caption is removed in WM_NCCALCSIZE. A thin band at
        // the top of the client area resizes the window when it is not maximized.
        protected override void WndProc(ref Message m)
        {
            const int NcCalcSize = 0x0083, NcHitTest = 0x0084, HtClient = 1, HtTop = 12, HtTopLeft = 13, HtTopRight = 14;
            if (m.Msg == NcCalcSize && m.WParam != IntPtr.Zero)
            {
                var original = (NativeRect)Marshal.PtrToStructure(m.LParam, typeof(NativeRect));
                base.WndProc(ref m);
                var proposed = (NativeRect)Marshal.PtrToStructure(m.LParam, typeof(NativeRect));
                // A maximized window extends past the screen by its frame; keep content on screen.
                proposed.Top = original.Top + (IsZoomed(Handle) ? GetSystemMetrics(33) + GetSystemMetrics(92) : 0);
                Marshal.StructureToPtr(proposed, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == NcHitTest && (int)m.Result == HtClient && !IsZoomed(Handle))
            {
                long packed = m.LParam.ToInt64();
                var point = PointToClient(new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF)));
                int band = ResizeBand, corner = band * 3;
                if (point.Y < band) m.Result = (IntPtr)(point.X < corner ? HtTopLeft : point.X >= ClientSize.Width - corner ? HtTopRight : HtTop);
            }
        }

        private int ResizeBand { get { return Math.Max(3, (int)Math.Round(4 * DeviceDpi / 96F)); } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Recalculate the frame so the caption is removed from the first paint.
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0027); // NOSIZE | NOMOVE | NOZORDER | FRAMECHANGED
        }

        private void CaptionMouseDown(Control source, MouseEventArgs e)
        {
            // Only the empty strip (and the mark) drag the window; tabs and buttons keep their clicks.
            // The system move loop swallows the second click, so double-clicks are timed here.
            if (e.Button == MouseButtons.Left)
            {
                var screenPoint = source.PointToScreen(e.Location);
                var slop = SystemInformation.DoubleClickSize;
                bool second = Environment.TickCount - lastCaptionClick <= SystemInformation.DoubleClickTime &&
                    Math.Abs(screenPoint.X - lastCaptionPoint.X) <= slop.Width && Math.Abs(screenPoint.Y - lastCaptionPoint.Y) <= slop.Height;
                lastCaptionClick = second ? 0 : Environment.TickCount;
                lastCaptionPoint = screenPoint;
                if (second) { ToggleMaximized(); return; }
                ReleaseCapture();
                SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
            }
            else if (e.Button == MouseButtons.Right)
            {
                var screen = source.PointToScreen(e.Location);
                SendMessage(Handle, 0x0313, IntPtr.Zero, (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF))); // WM_POPUPSYSTEMMENU
            }
        }

        private void ToggleMaximized()
        { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }

        private void UpdateCaption()
        {
            if (minButton == null) return;
            Padding = new Padding(0, WindowState == FormWindowState.Maximized ? 0 : ResizeBand, 0, 0);
            minButton.Invalidate(); maxButton.Invalidate(); closeButton.Invalidate();
        }

        private bool HandleShortcut(Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.L)) { address.Focus(); address.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.T)) { AddTab(); address.Focus(); return true; }
            if (keyData == (Keys.Control | Keys.W)) { if (active != null) CloseTab(active); return true; }
            if (keyData == (Keys.Control | Keys.H)) { ToggleHistory(); return true; }
            if (keyData == Keys.F5) { if (Core(active) != null) Core(active).Reload(); return true; }
            return false;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (intro != null && msg.Msg == 0x0100) { intro.Skip(); return true; }
            return HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            closing = true;
            sleepTimer.Stop();
            saveTimer.Stop();
            focusTimer.Stop();
            freezeTimer.Stop();
            StopWatchingMemoryPressure();
            if (intro != null) { intro.Dispose(); intro = null; }
            if (curtain != null) { curtain.Dispose(); curtain = null; }
            SaveHistory();
            DisposeMusic();
            foreach (var tab in tabs)
            {
                tab.Closed = true;
                if (tab.Stream != null) { var window = tab.Stream; tab.Stream = null; window.Dispose(); }
                if (tab.View != null && !tab.View.IsDisposed) tab.View.Dispose();
            }
            sleepTimer.Dispose();
            saveTimer.Dispose();
            focusTimer.Dispose();
            freezeTimer.Dispose();
            tips.Dispose();
        }

    }
}
