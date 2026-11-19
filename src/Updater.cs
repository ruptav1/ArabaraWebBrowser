using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Arabara
{
    // A release on GitHub: its version (from the tag, v0.7.1), its page, and the program zip.
    internal sealed class ReleaseInfo
    {
        public Version Version;
        public string Tag, PageUrl, ZipName, ZipUrl, Sha256;
        public long ZipSize;
    }

    // Updates from the project's GitHub Releases. The browser asks the GitHub API for the latest
    // release a minute after it starts and then every 12 hours (one small HTTPS request; nothing
    // about the user is sent beyond what any request carries), and when its tag is newer than this
    // build the toolbar shows UPDATE. In the public edition the update happens in place: the
    // release's Arabara-<version>.zip is downloaded into %LOCALAPPDATA%\Arabara\update, checked
    // against the SHA-256 GitHub records for it, unpacked, and each program file is swapped: the
    // file in use is renamed to *.old (Windows lets a running program and its loaded DLLs be
    // renamed) and the new one copied in its place, rolled back if anything fails. Then the new
    // Arabara starts, waits for this one to exit (it holds the single-instance mutex) and removes
    // the *.old files. History, sign-ins and settings live in %LOCALAPPDATA%\Arabara and are not
    // touched. A personal build opens the release page instead. "CheckForUpdates": false in
    // preferences.json turns the check off.
    internal static class Updater
    {
        public const string Repository = "ruptav1/ArabaraWebBrowser";
        public static readonly string ReleasesPage = "https://github.com/" + Repository + "/releases";

        public static Version Current { get { return Normalize(Assembly.GetExecutingAssembly().GetName().Version); } }

        // v0.7.1 -> 0.7.1.0; null when the tag is not a version.
        public static Version ParseTag(string tag)
        {
            if (String.IsNullOrWhiteSpace(tag)) return null;
            string text = tag.Trim().TrimStart('v', 'V');
            Version version;
            return Version.TryParse(text, out version) ? Normalize(version) : null;
        }

        private static Version Normalize(Version v)
        {
            return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        // The release described by the API's JSON (one release object), or null for a draft,
        // a pre-release or a tag that is not a version.
        public static ReleaseInfo Parse(string json)
        {
            var release = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (release == null || Flag(release, "draft") || Flag(release, "prerelease")) return null;
            var info = new ReleaseInfo { Tag = Text(release, "tag_name"), PageUrl = Text(release, "html_url") };
            info.Version = ParseTag(info.Tag);
            if (info.Version == null) return null;
            var assets = release.ContainsKey("assets") ? release["assets"] as IEnumerable : null;
            if (assets != null)
                foreach (var item in assets)
                {
                    var asset = item as Dictionary<string, object>;
                    string name = asset == null ? null : Text(asset, "name");
                    if (name == null || !name.StartsWith("Arabara-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    info.ZipName = name;
                    info.ZipUrl = Text(asset, "browser_download_url");
                    object size;
                    if (asset.TryGetValue("size", out size) && size != null) info.ZipSize = Convert.ToInt64(size);
                    string digest = Text(asset, "digest");
                    if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) info.Sha256 = digest.Substring(7).ToUpperInvariant();
                    break;
                }
            return info;
        }

        private static string Text(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) ? value as string : null;
        }

        private static bool Flag(Dictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static void UseModernTls()
        {
            // Built without a target framework attribute, the runtime would otherwise offer old
            // TLS versions only; GitHub needs TLS 1.2 or newer.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } // TLS 1.3
            catch (NotSupportedException) { }
        }

        private static HttpWebRequest Request(string url, string accept)
        {
            UseModernTls();
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Arabara/" + Current;
            request.Accept = accept;
            request.Timeout = 20000;
            request.ReadWriteTimeout = 30000;
            return request;
        }

        // The latest published release, or null when the repository has none yet.
        public static async Task<ReleaseInfo> LatestAsync()
        {
            var request = Request("https://api.github.com/repos/" + Repository + "/releases/latest", "application/vnd.github+json");
            try
            {
                using (var response = (HttpWebResponse)await request.GetResponseAsync())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return Parse(await reader.ReadToEndAsync());
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }
        }

        // Downloads the release's zip to `path`, reporting the bytes received, and checks it
        // against the SHA-256 GitHub records for it.
        public static async Task DownloadAsync(ReleaseInfo release, string path, IProgress<long> progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var request = Request(release.ZipUrl, "application/octet-stream");
            using (var response = await request.GetResponseAsync())
            using (var input = response.GetResponseStream())
            using (var output = File.Create(path))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancel)) > 0)
                {
                    await output.WriteAsync(buffer, 0, read, cancel);
                    total += read;
                    if (progress != null) progress.Report(total);
                }
            }
            if (release.Sha256 != null && !String.Equals(Sha256Of(path), release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("The download does not match the checksum GitHub recorded for it.");
            }
        }

        public static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        // Unpacks the release into `work` and swaps the program's files in `appFolder` (see
        // above); returns the new Arabara.exe. On failure every swapped file is put back.
        public static string Apply(string zip, string appFolder, string work)
        {
            string unpacked = Path.Combine(work, "unpacked");
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            ZipFile.ExtractToDirectory(zip, unpacked);
            // The zip holds one folder, Arabara-<version>, with the program in it.
            string root = Directory.GetFiles(unpacked, "Arabara.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).OrderBy(p => p.Length).FirstOrDefault();
            if (root == null) throw new InvalidDataException("The release has no Arabara.exe.");
            var swapped = new List<KeyValuePair<string, string>>();
            var added = new List<string>();
            try
            {
                foreach (string source in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string relative = source.Substring(root.Length).TrimStart('\\', '/');
                    string target = Path.Combine(appFolder, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target))
                    {
                        string aside = MoveAside(target);
                        swapped.Add(new KeyValuePair<string, string>(target, aside));
                    }
                    else added.Add(target);
                    File.Copy(source, target, false);
                }
            }
            catch
            {
                foreach (var pair in swapped)
                {
                    try { if (File.Exists(pair.Key)) File.Delete(pair.Key); File.Move(pair.Value, pair.Key); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                foreach (string file in added)
                {
                    try { File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
            try { Directory.Delete(unpacked, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Path.Combine(appFolder, "Arabara.exe");
        }

        // Renames a file out of the way (it may be running); returns its new name.
        private static string MoveAside(string target)
        {
            string aside = target + ".old";
            if (File.Exists(aside))
            {
                try { File.Delete(aside); }
                catch (IOException) { aside = target + "." + DateTime.UtcNow.Ticks + ".old"; }
                catch (UnauthorizedAccessException) { aside = target + "." + DateTime.UtcNow.Ticks + ".old"; }
            }
            File.Move(target, aside);
            return aside;
        }

        // Removes the *.old files an update left in the program folder (and its fonts folder),
        // and the downloaded release; anything still in use is left for next time.
        public static void CleanUp(string appFolder, string work)
        {
            foreach (string folder in new[] { appFolder, Path.Combine(appFolder, "fonts") })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.GetFiles(folder, "*.old"))
                {
                    try { File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            if (work != null && Directory.Exists(work))
            {
                try { Directory.Delete(work, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // Started by an update with --after <id>: waits (up to 30 seconds) for the old Arabara
        // to exit, so this one can take the single-instance mutex, then tidies up.
        public static void AfterUpdate(string[] args, string work)
        {
            int at = Array.IndexOf(args, "--after");
            int id;
            if (at < 0 || at + 1 >= args.Length || !Int32.TryParse(args[at + 1], out id)) return;
            try
            {
                using (var old = Process.GetProcessById(id)) old.WaitForExit(30000);
            }
            catch (ArgumentException) { }       // already gone
            catch (InvalidOperationException) { }
            CleanUp(AppDomain.CurrentDomain.BaseDirectory, work);
        }

        public static string WorkFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arabara", "update"); }
        }

        // --update-check: asks GitHub for the latest release and writes what it found to
        // test-results\update-check.txt (read-only; nothing is downloaded).
        public static int CheckTest()
        {
            try
            {
                var release = LatestAsync().GetAwaiter().GetResult();
                SelfTests.WriteResult("update-check.txt", release == null ? "No release published yet." :
                    "Latest: " + release.Tag + " (" + release.Version + "), this build " + Current + ", newer: " + (release.Version > Current) +
                    "\nZip: " + (release.ZipName ?? "none") + ", " + release.ZipSize + " bytes, SHA-256 " + (release.Sha256 ?? "not given") +
                    "\nPage: " + release.PageUrl + "\nDownload: " + release.ZipUrl);
                return 0;
            }
            catch (Exception ex) { SelfTests.WriteResult("update-check.txt", ex.ToString()); return 1; }
        }

        // --apply-update-test <zip>: swaps this copy's files for the zip's and starts the new
        // one (which checks its edition and exits), as an update does. Only in a folder holding
        // update-test.marker, so it can never touch a real installation.
        public static int ApplyTest(string[] args)
        {
            string folder = AppDomain.CurrentDomain.BaseDirectory;
            if (!File.Exists(Path.Combine(folder, "update-test.marker"))) return 2;
            int at = Array.IndexOf(args, "--apply-update-test");
            string zip = args[at + 1];
            string work = Path.Combine(folder, "update-work");
            string exe = Apply(zip, folder, work);
            Process.Start(new ProcessStartInfo(exe, "--after " + Process.GetCurrentProcess().Id + " --update-test-done") { UseShellExecute = false });
            return 0;
        }
    }
}
