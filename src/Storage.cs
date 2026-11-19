using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Arabara
{
    internal static class AddressPolicy
    {
        public const string HttpsPrefix = "https://";

        // Only address-bar input may omit the scheme. Website navigation and
        // stored history still use the strict HTTPS parser below.
        public static bool TryParseInput(string text, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase)) return TryParse(text, out uri);
            int scheme = text.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0 && text.Substring(0, scheme).IndexOfAny(new[] { '/', '?', '#' }) < 0) return false;
            Uri candidate;
            if (!TryParse(HttpsPrefix + text, out candidate)) return false;
            bool domain = candidate.HostNameType == UriHostNameType.Dns && candidate.Host.Contains(".");
            bool ip = candidate.HostNameType == UriHostNameType.IPv4 || candidate.HostNameType == UriHostNameType.IPv6;
            if (!domain && !ip) return false;
            uri = candidate;
            return true;
        }

        public static bool TryParse(string text, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (!text.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase) ||
                text.Any(c => Char.IsWhiteSpace(c) || Char.IsControl(c)) || text.Contains("\\")) return false;
            Uri candidate;
            if (!Uri.TryCreate(text, UriKind.Absolute, out candidate) ||
                candidate.Scheme != Uri.UriSchemeHttps || String.IsNullOrEmpty(candidate.Host) ||
                !String.IsNullOrEmpty(candidate.UserInfo)) return false;
            uri = candidate;
            return true;
        }

        public static string SafeHistoryUrl(string url)
        {
            Uri uri;
            if (!TryParse(url, out uri)) return null;
            // OAuth codes and tokens in queries/fragments must not become a second credential store.
            string[] sensitive = { "code", "token", "access_token", "id_token", "refresh_token", "password", "assertion", "ticket" };
            string query = uri.Query.TrimStart('?');
            foreach (string item in query.Split('&'))
            {
                string name = Uri.UnescapeDataString(item.Split('=')[0]).ToLowerInvariant();
                if (sensitive.Contains(name)) return uri.GetLeftPart(UriPartial.Path);
            }
            if (uri.Fragment.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                uri.Fragment.IndexOf("code=", StringComparison.OrdinalIgnoreCase) >= 0)
                return uri.GetLeftPart(UriPartial.Path);
            return uri.AbsoluteUri;
        }
    }

    internal sealed class HistoryEntry
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public DateTime VisitedUtc { get; set; }
    }

    internal sealed class BrowserStore
    {
        public const int HistoryLimit = 1500;
        private readonly string path;
        public List<HistoryEntry> Entries { get; private set; }
        public string LoadWarning { get; private set; }

        public BrowserStore(string dataRoot)
        {
            Directory.CreateDirectory(dataRoot);
            path = Path.Combine(dataRoot, "history.json");
            Entries = new List<HistoryEntry>();
            if (!File.Exists(path)) return;
            try
            {
                if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("History is too large.");
                var data = new JavaScriptSerializer().Deserialize<List<HistoryEntry>>(File.ReadAllText(path));
                if (data != null) Entries = data.Where(e => e != null && AddressPolicy.SafeHistoryUrl(e.Url) != null)
                    .OrderByDescending(e => e.VisitedUtc).Take(HistoryLimit).ToList();
            }
            catch (Exception ex)
            {
                // Preserve the original for recovery; never silently overwrite an unreadable file.
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)) throw;
                string backup = path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                File.Copy(path, backup, false);
                LoadWarning = "Unable to read previous history. A backup has been kept for recovery.";
            }
        }

        public void Record(string url, string title)
        {
            string safe = AddressPolicy.SafeHistoryUrl(url);
            if (safe == null) return;
            Entries.RemoveAll(e => e.Url == safe);
            title = String.IsNullOrWhiteSpace(title) ? new Uri(safe).Host : title;
            Entries.Insert(0, new HistoryEntry { Url = safe, Title = title.Length > 240 ? title.Substring(0, 240) : title, VisitedUtc = DateTime.UtcNow });
            if (Entries.Count > HistoryLimit) Entries.RemoveRange(HistoryLimit, Entries.Count - HistoryLimit);
        }

        public void Save()
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(Entries), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }

        public void Clear()
        {
            Entries.Clear();
            Save();
            // Replace the previous snapshot too, so Clear really clears application history.
            File.WriteAllText(path + ".bak", "[]", new UTF8Encoding(false));
        }
    }

    internal static class SleepPolicy
    {
        public static bool CanSuspend(bool visible, bool keepAlive, bool mediaLease, bool playingAudio, bool downloading)
        {
            return !visible && !keepAlive && !mediaLease && !playingAudio && !downloading;
        }
    }

    // A PICTURE background: the file, its own character size for both layers (Points; 0
    // for the scene's usual 6.5 pt background and 4 pt character) or its own grid of shade
    // blocks over the page (Columns x Rows), and its own motion layout (see PictureMotion;
    // null: automatic). With either, the picture is drawn under its natural light.
    internal sealed class PictureSetting
    {
        public string Path { get; set; }
        public string Name { get; set; }  // shown instead of the file name
        public float Points { get; set; }
        public int Columns { get; set; }
        public int Rows { get; set; }
        public string Motion { get; set; }
    }

    internal sealed class BrowserPreferences
    {
        public bool LowMemoryBackground { get; set; }
        public bool PauseBackgroundVisuals { get; set; }
        public bool LightYouTube { get; set; }
        public List<string> AdsAllowedSites { get; set; } // sites the ad blocker leaves alone (see AdBlock)
        public int BackdropScene { get; set; }
        public string BackdropVideo { get; set; }
        public string BackdropVideoName { get; set; }  // shown instead of the file name
        public string BackdropPicture { get; set; }    // the first picture (kept for older versions)
        public List<PictureSetting> BackdropPictures { get; set; }
        public int BackdropPictureIndex { get; set; }
        public int MusicVolume { get; set; }
        public int MusicRepeat { get; set; }
        // Start page theme music: a YouTube Music playlist and the track it starts from.
        public string ThemePlaylistId { get; set; }
        public string ThemePlaylistTitle { get; set; }
        public string ThemeVideoId { get; set; }
        // Whether to look for a newer release on GitHub (see Updater).
        public bool CheckForUpdates { get; set; }
        public BrowserPreferences() { CheckForUpdates = true; LowMemoryBackground = true; PauseBackgroundVisuals = true; LightYouTube = true; MusicVolume = 60; BackdropPictures = new List<PictureSetting>(); AdsAllowedSites = new List<string>(); }

        public static BrowserPreferences Load(string root)
        {
            string path = Path.Combine(root, "preferences.json");
            if (!File.Exists(path)) return new BrowserPreferences();
            try
            {
                return new JavaScriptSerializer().Deserialize<BrowserPreferences>(File.ReadAllText(path)) ?? new BrowserPreferences();
            }
            catch (IOException) { return new BrowserPreferences(); }
            catch (UnauthorizedAccessException) { return new BrowserPreferences(); }
            catch (ArgumentException) { return new BrowserPreferences(); }
            catch (InvalidOperationException) { return new BrowserPreferences(); }
        }

        public void Save(string root)
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "preferences.json");
            string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
