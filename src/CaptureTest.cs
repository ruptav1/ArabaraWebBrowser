using System;
using System.Linq;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Arabara
{
    // --capture-test: can a page in one WebView2 capture a window that hosts another
    // WebView2 of the same app? A clicked button calls getDisplayMedia, the source is
    // auto-selected by window title, and the page reports the frames it received.
    internal static class CaptureTest
    {
        private static System.Threading.Timer shotTimer;
        private const string Sender = @"<!doctype html><html><body style='margin:0;background:#111;color:#eee;font:14px sans-serif'>
<button id='b' style='position:fixed;left:0;top:0;width:240px;height:120px'>share</button>
<script>
window.r = 'idle';
b.onclick = async () => {
  window.r = 'pending';
  try {
    const s = await navigator.mediaDevices.getDisplayMedia({ video: true });
    const t = s.getVideoTracks()[0];
    t.addEventListener('ended', () => window.ended = true);
    const v = document.createElement('video'); v.srcObject = s; v.muted = true; document.body.appendChild(v); await v.play();
    await new Promise(r => setTimeout(r, 2000));
    const c = document.createElement('canvas'); c.width = v.videoWidth; c.height = v.videoHeight;
    const g = c.getContext('2d'); g.drawImage(v, 0, 0);
    const d = g.getImageData(0, 0, c.width, c.height).data; let red = 0, n = 0;
    for (let i = 0; i < d.length; i += 4 * 101) { n++; if (d[i] > 150 && d[i + 1] < 90) red++; }
    window.r = 'ok ' + c.width + 'x' + c.height + ' red=' + red + '/' + n + ' label=' + t.label + ' surface=' + (t.getSettings().displaySurface || '?');
  } catch (e) { window.r = 'error ' + e.name + ': ' + e.message; }
};
</script></body></html>";

        private const string Source = @"<!doctype html><html><body style='margin:0;background:#d01830;color:#fff;font:40px sans-serif'>STREAMED PAGE<div id='t'></div>
<script>setInterval(() => t.textContent = Date.now(), 50);</script></body></html>";

        private static void Serve(CoreWebView2Environment environment, CoreWebView2 core, string html)
        {
            core.AddWebResourceRequestedFilter("https://capture.test/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += delegate(object s, CoreWebView2WebResourceRequestedEventArgs e)
            {
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8");
            };
        }

        public static int Run(string[] args)
        {
            UiFonts.Initialize();
            var log = new StringBuilder();
            try
            {
                string profile = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".test-profile-capture", DateTime.Now.Ticks.ToString()));
                var main = new Form { Text = "Arabara capture test", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(SystemInformation.VirtualScreen.Right + 100, SystemInformation.VirtualScreen.Top, 900, 640), ShowInTaskbar = false };
                main.Shown += async delegate
                {
                    try { await RunAsync(main, profile, log); }
                    catch (Exception ex) { log.AppendLine("EXCEPTION " + ex); }
                    finally { main.Close(); }
                };
                Application.Run(main);
            }
            finally { UiFonts.Shutdown(); }
            Step(log, "end");
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr data);
        private delegate bool EnumProc(IntPtr window, IntPtr data);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeRect { public int L, T, R, B; }

        // Top-level windows of this process and of WebView2 processes, in z-order.
        private static string ListWindows()
        {
            var own = new System.Collections.Generic.HashSet<uint> { (uint)System.Diagnostics.Process.GetCurrentProcess().Id };
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("msedgewebview2")) own.Add((uint)p.Id);
            var text = new StringBuilder();
            var foreground = GetForegroundWindow();
            int z = 0;
            EnumWindows(delegate(IntPtr w, IntPtr d)
            {
                z++;
                uint pid; GetWindowThreadProcessId(w, out pid);
                if (!own.Contains(pid)) return true;
                var title = new StringBuilder(256); GetWindowText(w, title, 256);
                var cls = new StringBuilder(256); GetClassName(w, cls, 256);
                NativeRect r; GetWindowRect(w, out r);
                if (!IsWindowVisible(w) && title.Length == 0) return true;
                text.AppendLine("  z=" + z + " pid=" + pid + " visible=" + IsWindowVisible(w) + (w == foreground ? " FOREGROUND" : "") + " class=" + cls + " title='" + title + "' rect=" + r.L + "," + r.T + "," + r.R + "," + r.B);
                return true;
            }, IntPtr.Zero);
            return text.ToString();
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ThumbnailProperties { public int Flags; public NativeRect Destination, Source; public byte Opacity; public int Visible, ClientOnly; }

        // Shows the stream window as a DWM thumbnail inside the (topmost) test window
        // and counts red pixels in that area of the screen; no image is kept.
        private static string ThumbnailRed(Form main, Form stream)
        {
            IntPtr thumb;
            int hr = DwmRegisterThumbnail(main.Handle, stream.Handle, out thumb);
            if (hr != 0) return "register failed 0x" + hr.ToString("X");
            var props = new ThumbnailProperties { Flags = 0x1 | 0x4 | 0x8 | 0x10, Destination = new NativeRect { L = 0, T = 150, R = 500, B = 450 }, Opacity = 255, Visible = 1, ClientOnly = 1 };
            DwmUpdateThumbnailProperties(thumb, ref props);
            System.Threading.Thread.Sleep(600);
            var origin = main.PointToScreen(new Point(0, 150));
            int red = 0, total = 0;
            using (var shot = new Bitmap(500, 300))
            {
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(origin, Point.Empty, shot.Size);
                for (int y = 0; y < 300; y += 10) for (int x = 0; x < 500; x += 10) { var c = shot.GetPixel(x, y); total++; if (c.R > 150 && c.G < 90) red++; }
            }
            DwmUnregisterThumbnail(thumb);
            return red + "/" + total;
        }

        private static void Step(StringBuilder log, string text) { log.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + text); SelfTests.WriteResult("capture-test.txt", log.ToString()); }

        private static async Task RunAsync(Form main, string profile, StringBuilder log)
        {
            Step(log, "start");
            var options = new CoreWebView2EnvironmentOptions(Environment.GetCommandLineArgs().Contains("--auto") ? "--disable-features=CalculateNativeWinOcclusion --auto-select-desktop-capture-source=ARABARACAPTURE" : "--disable-features=CalculateNativeWinOcclusion");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
            var sender = new WebView2 { Dock = DockStyle.Fill };
            main.Controls.Add(sender);
            await sender.EnsureCoreWebView2Async(environment);
            sender.CoreWebView2.ScreenCaptureStarting += delegate { Step(log, "ScreenCaptureStarting raised"); };
            Serve(environment, sender.CoreWebView2, Sender);
            await sender.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BrowserForm.TrackShares);
            sender.CoreWebView2.Navigate("https://capture.test/");

            bool offscreen = Environment.GetCommandLineArgs().Contains("--offscreen");
            var stream = new StreamWindow("Capture source", null);
            stream.Text = "ARABARACAPTURE";
            stream.Park(new Size(500, 300));
            var source = new WebView2 { Dock = DockStyle.Fill };
            bool reparent = Environment.GetCommandLineArgs().Contains("--reparent");
            if (reparent)
            {
                // As PopOut: the page starts in the browser window and is then moved.
                main.Controls.Add(source);
                source.SendToBack();
                await source.EnsureCoreWebView2Async(environment);
                Serve(environment, source.CoreWebView2, Source);
                source.CoreWebView2.Navigate("https://capture.test/");
                await Task.Delay(1500);
                main.Controls.Remove(source);
                stream.Controls.Add(source);
                stream.Show();
                Step(log, "source moved into the stream window");
            }
            else
            {
                stream.Controls.Add(source);
                stream.Show();
                await source.EnsureCoreWebView2Async(environment);
                Serve(environment, source.CoreWebView2, Source);
                source.CoreWebView2.Navigate("https://capture.test/");
            }
            await Task.Delay(2500);
            Step(log, "pages ready: " + await sender.CoreWebView2.ExecuteScriptAsync("location.href + ' secure=' + isSecureContext + ' md=' + !!navigator.mediaDevices") + " stream at " + stream.Bounds);


            IntPtr mirror = IntPtr.Zero;
            if (Environment.GetCommandLineArgs().Contains("--mirror"))
            {
                // As LiveMirror: a DWM thumbnail of the stream window inside the browser window.
                DwmRegisterThumbnail(main.Handle, stream.Handle, out mirror);
                var props = new ThumbnailProperties { Flags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10, Destination = new NativeRect { L = 0, T = 150, R = 500, B = 450 },
                    Source = new NativeRect { L = 0, T = 0, R = 500, B = 300 }, Opacity = 255, Visible = 1, ClientOnly = 1 };
                DwmUpdateThumbnailProperties(mirror, ref props);
                Step(log, "mirror registered");
            }
            int frames = 0;
            if (Environment.GetCommandLineArgs().Contains("--screencast"))
            {
                // As a screencast-based mirror: frames of the live page, acknowledged as they arrive.
                var receiver = source.CoreWebView2.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
                receiver.DevToolsProtocolEventReceived += delegate(object o, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
                {
                    frames++;
                    var json = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(e.ParameterObjectAsJson);
                    source.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", "{\"sessionId\":" + json["sessionId"] + "}");
                };
                await source.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.startScreencast", "{\"format\":\"jpeg\",\"quality\":75,\"everyNthFrame\":2}");
                Step(log, "screencast started");
            }
            foreach (string type in new[] { "mousePressed", "mouseReleased" })
                await sender.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                    "{\"type\":\"" + type + "\",\"x\":60,\"y\":40,\"button\":\"left\",\"clickCount\":1}");
            Step(log, "clicked");
            if (Environment.GetCommandLineArgs().Contains("--hide-sender"))
            {
                // As switching from the sharing tab to the live tab.
                await Task.Delay(300);
                sender.Visible = false;
                Step(log, "sender hidden");
                await Task.Delay(2600);
                sender.Visible = true;
                Step(log, "sender shown");
            }
            shotTimer = new System.Threading.Timer(delegate
            {
                try
                {

                }
                catch (Exception ex) { Step(log, "window list failed " + ex.Message); }
            }, null, 3000, System.Threading.Timeout.Infinite);
            string result = "";
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(200);
                result = await sender.CoreWebView2.ExecuteScriptAsync("window.r");
                if (i % 10 == 0) Step(log, "poll " + result);
                if (!result.Contains("pending") && !result.Contains("idle")) break;
            }
            if (result.Contains("ok "))
            {
                await sender.CoreWebView2.ExecuteScriptAsync(BrowserForm.EndSharesScript);
                await Task.Delay(300);
                Step(log, "after ending the share: ended=" + await sender.CoreWebView2.ExecuteScriptAsync("!!window.ended") +
                    " live=" + await sender.CoreWebView2.ExecuteScriptAsync("window.__arabaraShares.length + ':' + document.querySelector('video').srcObject.getVideoTracks()[0].readyState"));
            }
            log.AppendLine("Result: " + result + (frames > 0 ? "  screencast frames=" + frames : ""));
            stream.Close();
        }
    }
}
