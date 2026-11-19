using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Arabara
{
    // Ad blocking: uBlock Origin Lite (GPL-3.0, by the uBlock Origin team), loaded unpacked from
    // %LOCALAPPDATA%\Arabara\extensions\ublock-lite when that folder is there; Arabara neither
    // ships nor downloads it. It blocks ads and trackers with Chromium's declarative rules, so
    // Arabara does no work per request. Measured with --memory-bench on three ad-heavy news
    // sites (three runs each): CPU 61% -> 39% of one core, memory 967 -> 837 MiB, half the
    // processes; on YouTube, YouTube Music and Discord no difference, and it costs about 25 MiB
    // in the browser process. It is always on. The striped mark at the right of the address
    // box allows ads on the site shown (its host without "www.", and the hosts below it) and
    // blocks them again; the sites are kept in the preferences (AdsAllowedSites) and handed to
    // the extension, which keeps them as its own "no filtering" sites. The extension is added
    // to the profile once, and again only when the folder holds another version.
    internal sealed partial class BrowserForm
    {
        private CoreWebView2BrowserExtension adBlocker;
        private Task adBlockerReady;
        private AdSiteButton adSite;
        private string adSiteShown;

        private string AdBlockerFolder { get { return Path.Combine(dataRoot, "extensions", "ublock-lite"); } }
        private bool AdBlockerPresent { get { return File.Exists(Path.Combine(AdBlockerFolder, "manifest.json")); } }

        // The site a page belongs to for the ad blocker, or null for Arabara's own pages.
        internal static string AdSiteOf(string url)
        {
            Uri uri;
            if (String.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || uri.Host == LocalHost) return null;
            string host = uri.Host.ToLowerInvariant();
            return host.StartsWith("www.") ? host.Substring(4) : host;
        }

        private Task EnsureAdBlockerAsync(CoreWebView2Profile profile)
        {
            if (adBlockerReady == null) adBlockerReady = InstallAdBlockerAsync(profile);
            return adBlockerReady;
        }

        private async Task InstallAdBlockerAsync(CoreWebView2Profile profile)
        {
            if (!AdBlockerPresent) return;
            try
            {
                // The marker holds the installed extension's id and version, and the extra
                // filter lists turned on in it.
                string version = ManifestVersion(AdBlockerFolder), rulesets = String.Join(",", AdBlockerPage.ExtraRulesets);
                string marker = Path.Combine(dataRoot, "extensions", "ublock-lite.installed");
                string[] saved = File.Exists(marker) ? File.ReadAllText(marker).Trim().Split(' ') : new string[0];
                var installed = await profile.GetBrowserExtensionsAsync();
                adBlocker = saved.Length >= 2 && saved[1] == version ? installed.FirstOrDefault(e => e.Id == saved[0]) : null;
                bool fresh = adBlocker == null;
                if (fresh)
                {
                    foreach (var old in installed.Where(e => saved.Length >= 2 && e.Id == saved[0]).ToList()) await old.RemoveAsync();
                    adBlocker = await profile.AddBrowserExtensionAsync(Path.GetFullPath(AdBlockerFolder));
                    // A fresh install starts without the allowed sites.
                    if (preferences.AdsAllowedSites.Count > 0) await SetAdsAllowedAsync(preferences.AdsAllowedSites, true);
                }
                if (!adBlocker.IsEnabled) await adBlocker.EnableAsync(true);
                if (fresh || saved.Length < 3 || saved[2] != rulesets)
                {
                    string result = await AdBlockerPage.RunAsync(this, await EnsureEnvironmentAsync(), adBlocker.Id, AdBlockerPage.EnableRulesetsScript(AdBlockerPage.ExtraRulesets));
                    if (result != "ok") throw new InvalidOperationException("its filter lists were not turned on (" + result + ")");
                }
                File.WriteAllText(marker, adBlocker.Id + " " + version + " " + rulesets);
            }
            catch (Exception ex) { adBlocker = null; SetStatus("Ad blocker unavailable: " + ex.Message); }
            UpdateChrome();
        }

        private static string ManifestVersion(string folder)
        {
            var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            object version;
            return manifest != null && manifest.TryGetValue("version", out version) && version != null ? version.ToString() : "?";
        }

        // Tells the extension to leave the sites unfiltered (allow) or to filter them as all others.
        private async Task SetAdsAllowedAsync(IEnumerable<string> sites, bool allow)
        {
            string result = await AdBlockerPage.RunAsync(this, await EnsureEnvironmentAsync(), adBlocker.Id, AdBlockerPage.AllowScript(sites, allow));
            if (result != "ok") throw new InvalidOperationException("it refused the change (" + result + ")");
        }

        // The mark in the address box: allows ads on the site shown, or blocks them again.
        private async void ToggleAdsOnSite()
        {
            var tab = active;
            string site = tab != null && Core(tab) != null ? AdSiteOf(Core(tab).Source) : null;
            if (adBlocker == null || site == null || adSite.Busy) return;
            bool allow = !preferences.AdsAllowedSites.Contains(site);
            adSite.Busy = true;
            adSite.Invalidate();
            try
            {
                await SetAdsAllowedAsync(new[] { site }, allow);
                preferences.AdsAllowedSites.RemoveAll(s => s == site);
                if (allow) preferences.AdsAllowedSites.Add(site);
                try { preferences.Save(dataRoot); }
                catch (Exception ex) { SetStatus("Unable to save the ad blocker sites: " + ex.Message); }
                SetStatus(allow ? "Ads allowed on " + site + ". Reloading." : "Ads blocked again on " + site + ". Reloading.");
                if (!tab.Closed && Core(tab) != null) Core(tab).Reload();
            }
            catch (Exception ex) { SetStatus("Ad blocker: " + ex.Message); }
            finally { adSite.Busy = false; adSiteShown = null; UpdateChrome(); }
        }

        // Called from UpdateChrome: the mark shows the state for the active tab's site.
        private void UpdateAdSite()
        {
            string site = adBlocker != null && active != null && Core(active) != null ? AdSiteOf(Core(active).Source) : null;
            bool allowed = site != null && preferences.AdsAllowedSites.Contains(site);
            string state = site == null ? null : site + (allowed ? " allowed" : " blocked");
            adSite.Visible = site != null;
            adSite.LineHeight = address.Height;
            if (state == adSiteShown) return;
            adSiteShown = state;
            adSite.Allowed = allowed;
            adSite.Invalidate();
            if (site != null)
                tips.SetToolTip(adSite, allowed ? "Ads are allowed on " + site + ". Click to block them again (the page reloads)."
                    : "Ads and trackers are blocked on " + site + " (uBlock Origin Lite). Click to allow ads on this site (the page reloads).");
        }
    }

    // uBlock Origin Lite takes settings only from its own pages: one of them is opened in a
    // hidden view for the moment and a script runs there through the DevTools protocol (so
    // its promise is awaited), returning the script's string result.
    internal static class AdBlockerPage
    {
        // Filter lists turned on besides the extension's defaults: ABPVN for Vietnamese sites (the
        // extension picks regional lists by the engine's language, which is English here). On
        // kenh14, 24h and dantri (--memory-bench, three runs each) it took the pages' renderer
        // memory from 475 to 377 MiB. The annoyance lists (social and chat widgets, notification
        // prompts, cookie notices) saved nothing more there and break sites more often.
        internal static readonly string[] ExtraRulesets = { "vie-1" };

        internal static string AllowScript(IEnumerable<string> sites, bool allow)
        {
            return "(async () => { const sites = " + new JavaScriptSerializer().Serialize(sites.ToArray()) + "; " +
                "const level = " + (allow ? "0" : "await chrome.runtime.sendMessage({ what: 'getDefaultFilteringMode' })") + "; " +
                "for (const hostname of sites) { const set = await chrome.runtime.sendMessage({ what: 'setFilteringMode', hostname, level }); if (set !== level) return 'refused ' + hostname; } " +
                "return 'ok'; })()";
        }

        internal static string EnableRulesetsScript(IEnumerable<string> ids)
        {
            return "(async () => { const extra = " + new JavaScriptSerializer().Serialize(ids.ToArray()) + "; " +
                "const before = await chrome.runtime.sendMessage({ what: 'getEnabledRulesets' }); " +
                "const after = Array.from(new Set(before.concat(extra))); " +
                "if (after.length !== before.length) await chrome.runtime.sendMessage({ what: 'applyRulesets', enabledRulesets: after }); " +
                "const now = await chrome.runtime.sendMessage({ what: 'getEnabledRulesets' }); " +
                "const missing = extra.filter(id => !now.includes(id)); return missing.length ? 'missing ' + missing.join(' ') : 'ok'; })()";
        }

        internal static async Task<string> RunAsync(Control host, CoreWebView2Environment environment, string extensionId, string script)
        {
            var json = new JavaScriptSerializer();
            // Never shown, so it gets no drawing surface in the GPU process.
            var view = new WebView2 { Visible = false, Size = new Size(1, 1), Location = Point.Empty, TabStop = false };
            host.Controls.Add(view);
            try
            {
                await view.EnsureCoreWebView2Async(environment);
                var loaded = new TaskCompletionSource<bool>();
                view.CoreWebView2.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) { loaded.TrySetResult(e.IsSuccess); };
                view.CoreWebView2.Navigate("chrome-extension://" + extensionId + "/popup.html");
                if (await Task.WhenAny(loaded.Task, Task.Delay(10000)) != loaded.Task || !loaded.Task.Result) throw new InvalidOperationException("its settings page did not open");
                var request = new Dictionary<string, object> { { "expression", script }, { "awaitPromise", true }, { "returnByValue", true } };
                var call = view.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", json.Serialize(request));
                if (await Task.WhenAny(call, Task.Delay(15000)) != call) throw new TimeoutException("it did not answer");
                var reply = json.Deserialize<Dictionary<string, object>>(await call);
                object result, value = null;
                var inner = reply.TryGetValue("result", out result) ? result as Dictionary<string, object> : null;
                return inner != null && inner.TryGetValue("value", out value) && value != null ? value.ToString() : "no answer";
            }
            finally { host.Controls.Remove(view); view.Dispose(); }
        }
    }

    // The ad blocker's switch in the address box: the striped Arabara mark, crimson while ads
    // are blocked on the site shown, grey once they are allowed there (or while it changes).
    internal sealed class AdSiteButton : Control
    {
        public bool Allowed, Busy;
        public int LineHeight; // the address line's height: the mark is centred on it
        private bool hot;

        public AdSiteButton()
        {
            AccessibleName = "Ad blocking on this site";
            AccessibleRole = AccessibleRole.PushButton;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            float scale = DeviceDpi / 96F, width = ClientSize.Width / scale, height = ClientSize.Height / scale;
            float markHeight = Math.Min(14F, height - 2), markWidth = markHeight * 184F / 76F;
            float line = LineHeight > 0 ? Math.Min(height, Math.Max(markHeight, LineHeight / scale)) : height;
            bool grey = Allowed || Busy;
            HomeView.DrawStripedMark(e.Graphics, scale, (width - markWidth) / 2, (line - markHeight) / 2, markWidth, markHeight,
                grey ? UiPalette.Muted : UiPalette.Accent, grey ? Color.FromArgb(62, 56, 52) : Color.FromArgb(150, 16, 34), 2);
            if (hot)
                using (var pen = new Pen(UiPalette.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }
}
