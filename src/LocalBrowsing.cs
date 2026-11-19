using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Arabara
{
    // Local files and folders. Folders are listed by a page served from the virtual
    // host https://local.arabara/<token>/, where the token is new for every launch;
    // requests without it are refused, and file contents (previews, the page font)
    // are served only to that page itself. Files the engine can display open as
    // file:///; anything else needs confirmation before Windows opens it.
    internal sealed partial class BrowserForm
    {
        internal const string LocalHost = "local.arabara";
        private readonly string localToken = Guid.NewGuid().ToString("N");
        private const int PreviewHeadBytes = 32 * 1024, PreviewImageBytes = 16 * 1024 * 1024;

        internal string LocalRoot { get { return "https://" + LocalHost + "/" + localToken + "/"; } }
        private string LocalViewUrl(string path) { return LocalRoot + "view?p=" + Uri.EscapeDataString(path ?? ""); }

        private static bool IsLocalPage(string url)
        { return url != null && url.StartsWith("https://" + LocalHost + "/", StringComparison.OrdinalIgnoreCase); }

        private static bool IsFileUrl(string url)
        { return url != null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase); }

        // Splits https://local.arabara/<token>/<command>?p=<path>; false when the token is wrong.
        private bool TryLocalCommand(string url, out string command, out string path)
        {
            command = path = null;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !String.Equals(uri.Host, LocalHost, StringComparison.OrdinalIgnoreCase)) return false;
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length < 2 || segments[0] != localToken) return false;
            command = segments[1];
            if (segments.Length > 2) command += "/" + String.Join("/", segments, 2, segments.Length - 2);
            path = "";
            foreach (string pair in uri.Query.TrimStart('?').Split('&'))
                if (pair.StartsWith("p=")) path = Uri.UnescapeDataString(pair.Substring(2).Replace('+', ' '));
            return true;
        }

        // Address-bar text for a local page: the folder or file path.
        private static string LocalAddressText(string url, string token)
        {
            if (IsFileUrl(url)) return LocalPath.FromFileUrl(url) ?? url;
            if (!IsLocalPage(url)) return null;
            int at = url.IndexOf("?p=", StringComparison.Ordinal);
            string path = at < 0 ? "" : Uri.UnescapeDataString(url.Substring(at + 3).Split('&')[0]);
            return path.Length == 0 ? "file:///" : path;
        }

        private async Task OpenLocalPathAsync(BrowserTab tab, string path)
        {
            string target;
            if (path.Length == 0 || Directory.Exists(path)) target = LocalViewUrl(path);
            else if (File.Exists(path))
            {
                if (!LocalPath.IsViewable(path)) { OpenWithWindows(path); return; }
                target = LocalPath.ToFileUrl(path);
                tab.AllowedFile = target;
            }
            else { SetStatus("Path not found: " + path); return; }
            int version = ++tab.NavigationVersion;
            try
            {
                SetStatus("Opening " + (path.Length == 0 ? "This PC" : path) + "…");
                await EnsureTabAsync(tab);
                if (closing || tab.Closed || version != tab.NavigationVersion) return;
                tab.Url = target;
                if (tab == active)
                {
                    address.Text = path.Length == 0 ? "file:///" : path;
                    address.SelectionStart = address.TextLength;
                    address.SelectionLength = 0;
                }
                if (Core(tab).IsSuspended) Core(tab).Resume();
                Core(tab).Navigate(target);
                if (tab == active) tab.View.Focus();
            }
            catch (Exception ex) { if (!closing) SetStatus("Unable to open: " + ex.Message); }
        }

        // Handles local navigations; returns false for anything else (left to the HTTPS policy).
        private bool HandleLocalNavigation(BrowserTab tab, CoreWebView2 core, CoreWebView2NavigationStartingEventArgs e)
        {
            if (IsFileUrl(e.Uri))
            {
                Uri uri;
                bool expected = e.Uri == tab.AllowedFile;
                tab.AllowedFile = null;
                // A local file opened by the user may link to other local files; websites may not.
                bool allowed = Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) && uri.IsFile && !uri.IsUnc &&
                    (expected || (IsFileUrl(core.Source) && e.IsUserInitiated));
                if (!allowed) { e.Cancel = true; SetStatus("Blocked a local file link. Type the path in the address bar to open it."); }
                return true;
            }
            if (!IsLocalPage(e.Uri)) return false;
            string command, path;
            if (!TryLocalCommand(e.Uri, out command, out path)) { e.Cancel = true; SetStatus("Blocked an invalid local page."); return true; }
            if (command == "view") return true;
            e.Cancel = true;
            // Commands come only from Arabara's own folder page.
            if (!IsLocalPage(core.Source) || !core.Source.Contains("/" + localToken + "/")) return true;
            BeginInvoke(new Action(async delegate { await RunLocalCommandAsync(tab, command, path); }));
            return true;
        }

        private async Task RunLocalCommandAsync(BrowserTab tab, string command, string path)
        {
            if (closing || tab.Closed) return;
            try
            {
                switch (command)
                {
                    case "open": await OpenLocalPathAsync(tab, path); break;
                    case "system": OpenWithWindows(path); break;
                    case "reveal":
                        if (File.Exists(path) || Directory.Exists(path))
                            Process.Start("explorer.exe", "/select,\"" + path + "\"");
                        break;
                    case "copy": Clipboard.SetText(path); SetStatus("Copied path: " + path); break;
                }
            }
            catch (Exception ex) { SetStatus("Unable to " + command + ": " + ex.Message); }
        }

        private void OpenWithWindows(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) { SetStatus("Path not found: " + path); return; }
            string message = LocalPath.IsRunnable(path)
                ? path + "\n\nThis file can run programs on your computer. Open it with Windows only if you trust it.\n\nOpen it?"
                : path + "\n\nOpen this file with its Windows app?";
            if (MessageBox.Show(this, message, "Open with Windows", MessageBoxButtons.YesNo,
                LocalPath.IsRunnable(path) ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
            SetStatus("Opened with Windows: " + Path.GetFileName(path));
        }

        private void InstallLocalFiles(CoreWebView2 core)
        {
            core.AddWebResourceRequestedFilter("https://" + LocalHost + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
            {
                // The event fires for every registered filter; only answer this host.
                if (!IsLocalPage(e.Request.Uri)) return;
                string command, path;
                if (!TryLocalCommand(e.Request.Uri, out command, out path)) { e.Response = Respond(404, "Not found"); return; }
                try
                {
                    if (command == "view")
                    {
                        var html = Encoding.UTF8.GetBytes(LocalFolderPage.Render(path, LocalRoot));
                        e.Response = environment.CreateWebResourceResponse(new MemoryStream(html), 200, "OK",
                            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n" +
                            "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'");
                        return;
                    }
                    // File contents only for Arabara's own folder page.
                    if (!IsLocalPage(core.Source) || !core.Source.Contains("/" + localToken + "/")) { e.Response = Respond(403, "Forbidden"); return; }
                    if (command == "font/WhiteRabbit.ttf")
                    {
                        string font = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts", "WhiteRabbit.ttf");
                        e.Response = File.Exists(font) ? Bytes(File.ReadAllBytes(font), "font/ttf") : Respond(404, "Not found");
                        return;
                    }
                    if (command == "raw" && File.Exists(path))
                    {
                        bool head = e.Request.Uri.Contains("&head=1");
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            if (!head && stream.Length > PreviewImageBytes) { e.Response = Respond(413, "Too large"); return; }
                            var buffer = new byte[head ? Math.Min(stream.Length, PreviewHeadBytes) : stream.Length];
                            int read = 0, n;
                            while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
                            e.Response = Bytes(buffer, head ? "text/plain; charset=utf-8" : LocalPath.Mime(path));
                        }
                        return;
                    }
                    e.Response = Respond(404, "Not found");
                }
                catch (UnauthorizedAccessException) { e.Response = Respond(403, "Forbidden"); }
                catch (IOException) { e.Response = Respond(404, "Not found"); }
            };
        }

        private CoreWebView2WebResourceResponse Bytes(byte[] data, string type)
        {
            return environment.CreateWebResourceResponse(new MemoryStream(data), 200, "OK",
                "Content-Type: " + type + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
        }

        private CoreWebView2WebResourceResponse Respond(int status, string reason)
        {
            return environment.CreateWebResourceResponse(new MemoryStream(new byte[0]), status, reason, "Cache-Control: no-store");
        }
    }
}
