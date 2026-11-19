using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Arabara
{
    // Hosts the music terminal on the start page and the start page's theme music: a
    // playlist (from a chosen track) that plays while a start page is on screen and
    // pauses when a website or a minimized window hides it. Pressing a playback control
    // turns theme playback into ordinary playback, which then continues everywhere.
    internal sealed partial class BrowserForm
    {
        private MusicEngine music;
        private MusicPanel musicPanel;

        private Task<CoreWebView2Environment> EnsureEnvironmentAsync()
        {
            if (environmentTask == null)
            {
                // Default Chromium security, process isolation, GPU and codec settings remain enabled.
                var options = new CoreWebView2EnvironmentOptions();
                options.Language = "en-US";
                // Extensions only for the ad blocker, when its folder is there (see AdBlock).
                options.AreBrowserExtensionsEnabled = AdBlockerPresent;
                // A stream window must keep rendering while other windows cover it, or a
                // window capture freezes. Arabara pauses hidden pages itself (Visuals Auto).
                options.AdditionalBrowserArguments = "--disable-features=CalculateNativeWinOcclusion";
                environmentTask = CoreWebView2Environment.CreateAsync(null, System.IO.Path.Combine(dataRoot, "Profile"), options);
            }
            return environmentTask;
        }

        private void EnsureMusic()
        {
            if (music != null) return;
            music = new MusicEngine(this, async delegate
            {
                environment = await EnsureEnvironmentAsync();
                return environment;
            }, preferences.MusicVolume);
            // Low RAM: with nothing playing and the music terminal closed, the hidden page is
            // released after the same delay as an idle background tab.
            music.RestAfter = () => lowMemoryMode ? FreezeDelay : TimeSpan.MaxValue;
            music.InUse = () => musicPanel != null && musicPanel.IsOpen;
            if (Enum.IsDefined(typeof(MusicRepeat), preferences.MusicRepeat)) music.Queue.Repeat = (MusicRepeat)preferences.MusicRepeat;
            musicPanel = new MusicPanel(music) { ThemePlaylistId = preferences.ThemePlaylistId };
            musicPanel.CloseRequested += delegate { CloseMusic(); };
            musicPanel.SignInRequested += async delegate
            {
                CloseMusic();
                if (active != null) await OpenAddressAsync("https://music.youtube.com/");
            };
            musicPanel.ThemeToggled += ToggleTheme;
            viewport.Controls.Add(musicPanel);
            viewport.Resize += delegate { PlaceMusicPanel(); };
        }

        // Opened from the [ MUSIC ] chip on a start page.
        private async void OpenMusic()
        {
            if (closing) return;
            EnsureMusic();
            PlaceMusicPanel();
            if (musicPanel.IsOpen) { CloseMusic(); return; }
            await musicPanel.OpenAsync();
        }

        private void CloseMusic()
        {
            if (musicPanel == null) return;
            musicPanel.CloseView();
            SaveMusicPreferences();
            if (active != null) foreach (var home in active.Page.Controls.OfType<HomeView>()) home.Focus();
        }

        private void PlaceMusicPanel()
        {
            if (musicPanel == null) return;
            float scale = musicPanel.DeviceDpi / 96F;
            int width = Math.Min(viewport.ClientSize.Width - 8, (int)(MusicPanel.LogicalWidth * scale));
            int height = Math.Min(viewport.ClientSize.Height - 8, (int)(MusicPanel.LogicalHeight * scale));
            musicPanel.Bounds = new Rectangle((viewport.ClientSize.Width - width) / 2, Math.Max(4, (viewport.ClientSize.Height - height) / 2), Math.Max(1, width), Math.Max(1, height));
        }

        private bool StartPageShown()
        {
            return active != null && !active.Closed && WindowState != FormWindowState.Minimized &&
                active.Page.Controls.OfType<HomeView>().Any(home => !home.IsDisposed);
        }

        // Called whenever tabs or the window change: the music terminal only lives on a
        // start page, and theme music follows the start page.
        private void UpdateMusicVisibility()
        {
            if (music == null || closing) return;
            bool home = StartPageShown();
            if (musicPanel.Visible && !home) musicPanel.CloseView(false);
            if (musicPanel.Visible) musicPanel.BringToFront();
            if (music.ThemeMode)
            {
                if (home) { var _ = music.ResumeAsync(); }
                else { var _ = music.PauseAsync(); }
            }
        }

        private void ToggleTheme()
        {
            var playlist = music.Playlist;
            bool remove = preferences.ThemePlaylistId != null && (playlist == null || playlist.Id == preferences.ThemePlaylistId);
            if (remove)
            {
                preferences.ThemePlaylistId = preferences.ThemePlaylistTitle = preferences.ThemeVideoId = null;
                SetStatus("Start page theme music removed.");
            }
            else if (playlist != null)
            {
                preferences.ThemePlaylistId = playlist.Id;
                preferences.ThemePlaylistTitle = playlist.Title;
                preferences.ThemeVideoId = music.Queue.Current != null ? music.Queue.Current.VideoId : null;
                SetStatus("Start page theme music: " + playlist.Title + (music.Queue.Current != null ? " from " + music.Queue.Current.Title : "") + ".");
            }
            musicPanel.ThemePlaylistId = preferences.ThemePlaylistId;
            SaveMusicPreferences();
            musicPanel.Invalidate();
        }

        // At startup: plays the theme playlist from its track while the start page is shown.
        private async Task StartThemeMusicAsync()
        {
            if (smoke || !MusicEngine.IsSafeId(preferences.ThemePlaylistId)) return;
            EnsureMusic();
            try
            {
                var tracks = await music.LoadTracksAsync(preferences.ThemePlaylistId);
                if (closing || tracks.Count == 0 || music.Queue.Current != null) return;
                int index = Math.Max(0, tracks.FindIndex(t => t.VideoId == preferences.ThemeVideoId));
                await music.PlayAsync(new MusicPlaylist { Id = preferences.ThemePlaylistId, Title = preferences.ThemePlaylistTitle ?? "THEME" }, tracks, index);
                music.ThemeMode = true;
                UpdateMusicVisibility();
            }
            catch (Exception ex) { SetStatus("Theme music unavailable: " + ex.Message); }
        }

        private void SaveMusicPreferences()
        {
            if (music == null) return;
            preferences.MusicVolume = music.Volume;
            preferences.MusicRepeat = (int)music.Queue.Repeat;
            try { preferences.Save(dataRoot); }
            catch (Exception ex) { SetStatus("Unable to save music preferences: " + ex.Message); }
        }

        private void DisposeMusic()
        {
            if (music == null) return;
            SaveMusicPreferences();
            music.Dispose();
            musicPanel.Dispose();
            music = null;
            musicPanel = null;
        }
    }
}
