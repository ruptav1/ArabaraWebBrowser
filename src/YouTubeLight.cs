using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Arabara
{
    // YouTube light mode (YT:LIGHT): on YouTube domains only, VP8/VP9/AV1 are reported
    // as unsupported so the player streams H.264, which nearly every GPU decodes in
    // hardware. Measured with --memory-bench it roughly halved CPU while playing;
    // the trade-off is a 1080p maximum. Applies from the next page load.
    internal sealed partial class BrowserForm
    {
        internal const string LightYouTubeScript =
            "if (/(^|\\.)(youtube\\.com|youtube-nocookie\\.com)$/i.test(location.hostname)) " + PageTweaks.H264Only;

        private bool youtubeLight;

        private async Task InstallYouTubeLightAsync(BrowserTab tab, CoreWebView2 core)
        {
            if (youtubeLight && tab.LightScript == null) tab.LightScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(LightYouTubeScript);
        }

        private async void SetYouTubeLight(bool enabled, bool persist)
        {
            youtubeLight = enabled;
            if (persist)
            {
                preferences.LightYouTube = enabled;
                try { preferences.Save(dataRoot); }
                catch (Exception ex) { SetStatus("Unable to save YouTube preference: " + ex.Message); }
            }
            foreach (var tab in tabs.ToArray())
            {
                var core = Core(tab);
                if (core == null) continue;
                if (enabled) await InstallYouTubeLightAsync(tab, core);
                else if (tab.LightScript != null) { core.RemoveScriptToExecuteOnDocumentCreated(tab.LightScript); tab.LightScript = null; }
            }
            UpdateChrome();
            if (persist) SetStatus(enabled ? "YouTube light: H.264 up to 1080p, lower CPU. Reload YouTube to apply." : "YouTube full: VP9/AV1 up to 4K. Reload YouTube to apply.");
        }
    }
}
