using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Arabara
{
    // Local paths typed into the address bar: drive paths, file:/// URLs and "~".
    // Network (UNC) paths are rejected so a typed or linked path never sends
    // Windows credentials to another machine.
    internal static class LocalPath
    {
        // "" stands for the computer view (all drives).
        public static string TryParseInput(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim().Trim('"');
            try
            {
                if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = text.Substring(5).TrimStart('/');
                    if (rest.Length == 0) return "";
                    Uri uri;
                    if (!Uri.TryCreate("file:///" + rest, UriKind.Absolute, out uri) || uri.IsUnc || !IsDrivePath(uri.LocalPath)) return null;
                    return Normalize(uri.LocalPath);
                }
                if (text == "~" || text.StartsWith("~\\") || text.StartsWith("~/"))
                    return Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text.Substring(1).TrimStart('\\', '/')));
                if (IsDrivePath(text)) return Normalize(text);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            return null;
        }

        private static bool IsDrivePath(string text)
        {
            return text.Length >= 2 && Char.IsLetter(text[0]) && text[1] == ':' &&
                (text.Length == 2 || text[2] == '\\' || text[2] == '/') && text.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }

        private static string Normalize(string path)
        {
            path = path.Replace('/', '\\');
            if (path.Length == 2) path += "\\";
            return Path.GetFullPath(path);
        }

        public static string ToFileUrl(string path) { return new Uri(path).AbsoluteUri; }

        public static string FromFileUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.IsFile && !uri.IsUnc ? uri.LocalPath : null;
        }

        private static readonly Dictionary<string, string> Kinds = BuildKinds();

        private static Dictionary<string, string> BuildKinds()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string> add = delegate(string kind, string list) { foreach (string e in list.Split(' ')) map["." + e] = kind; };
            add("IMG", "png jpg jpeg gif webp bmp ico svg avif tif tiff heic");
            add("VID", "mp4 webm mkv mov avi wmv m4v");
            add("AUD", "mp3 wav ogg oga flac m4a aac opus wma");
            add("DOC", "pdf doc docx xls xlsx ppt pptx odt rtf epub");
            add("TXT", "txt log md csv ini cfg conf nfo");
            add("CODE", "html htm css js json xml cs py ts java c cpp h ps1 bat cmd sh yml yaml toml sql");
            add("ARC", "zip rar 7z tar gz bz2 xz iso cab");
            add("EXE", "exe msi com scr dll sys appx msix");
            add("LNK", "lnk url");
            return map;
        }

        public static string Kind(string path)
        {
            string kind;
            return Kinds.TryGetValue(Path.GetExtension(path), out kind) ? kind : "BIN";
        }

        // Types the WebView2 engine displays itself when opened as file:///.
        private static readonly HashSet<string> Viewable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg", ".avif", ".pdf",
            ".mp4", ".webm", ".m4v", ".mp3", ".wav", ".ogg", ".oga", ".flac", ".m4a", ".aac", ".opus",
            ".txt", ".log", ".md", ".csv", ".ini", ".cfg", ".conf", ".nfo", ".json", ".xml", ".html", ".htm", ".css", ".js",
            ".cs", ".py", ".ts", ".java", ".c", ".cpp", ".h", ".yml", ".yaml", ".toml", ".sql"
        };

        public static bool IsViewable(string path) { return Viewable.Contains(Path.GetExtension(path)); }

        // Opening these with Windows runs code; the prompt says so.
        private static readonly HashSet<string> Runnable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".com", ".scr", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".lnk", ".url", ".jar", ".reg", ".appx", ".msix", ".cpl" };

        public static bool IsRunnable(string path) { return Runnable.Contains(Path.GetExtension(path)); }

        public static string Mime(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".ico": return "image/x-icon";
                case ".avif": return "image/avif";
                case ".ttf": return "font/ttf";
                default: return "text/plain; charset=utf-8";
            }
        }
    }

    // The folder browser page: a terminal-style listing rendered by the page's own
    // script from JSON. Names are inserted as text, never as markup.
    internal static class LocalFolderPage
    {
        public const int MaxEntries = 20000;

        public static string Render(string path, string root)
        {
            var data = new Dictionary<string, object>();
            var entries = new List<object>();
            string error = null;
            bool computer = String.IsNullOrEmpty(path);
            if (computer)
            {
                foreach (var drive in SafeDrives())
                    entries.Add(new Dictionary<string, object> { { "n", drive.Name.TrimEnd('\\') + (Label(drive).Length > 0 ? "  " + Label(drive) : "") },
                        { "p", drive.Name }, { "d", true }, { "s", drive.TotalSize }, { "f", drive.AvailableFreeSpace }, { "k", "DRV" } });
            }
            else
            {
                try
                {
                    var info = new DirectoryInfo(path);
                    foreach (var item in info.EnumerateFileSystemInfos())
                    {
                        if (entries.Count >= MaxEntries) { error = "Showing the first " + MaxEntries + " items."; break; }
                        try
                        {
                            bool dir = (item.Attributes & FileAttributes.Directory) != 0;
                            entries.Add(new Dictionary<string, object>
                            {
                                { "n", item.Name }, { "p", item.FullName }, { "d", dir },
                                { "s", dir ? -1L : ((FileInfo)item).Length },
                                { "m", Epoch(item.LastWriteTimeUtc) }, { "c", Epoch(item.CreationTimeUtc) },
                                { "h", (item.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 },
                                { "k", dir ? "DIR" : LocalPath.Kind(item.Name) }, { "v", !dir && LocalPath.IsViewable(item.Name) }
                            });
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (UnauthorizedAccessException) { error = "ACCESS DENIED"; }
                catch (IOException ex) { error = ex.Message; }
                catch (System.Security.SecurityException) { error = "ACCESS DENIED"; }
            }
            string parent = null;
            if (!computer)
            {
                var up = Directory.GetParent(path);
                parent = up != null ? up.FullName : "";
            }
            var places = new List<object>();
            foreach (var place in new[]
            {
                new[] { "HOME", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
                new[] { "DESKTOP", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) },
                new[] { "DOWNLOADS", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") },
                new[] { "DOCUMENTS", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
                new[] { "PICTURES", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) },
                new[] { "MUSIC", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) },
                new[] { "VIDEOS", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) }
            })
                if (!String.IsNullOrEmpty(place[1]) && Directory.Exists(place[1])) places.Add(new Dictionary<string, object> { { "n", place[0] }, { "p", place[1] } });
            var drives = new List<object>();
            foreach (var drive in SafeDrives())
                drives.Add(new Dictionary<string, object> { { "n", drive.Name.TrimEnd('\\') }, { "l", Label(drive) }, { "p", drive.Name }, { "t", drive.TotalSize }, { "f", drive.AvailableFreeSpace } });

            data["path"] = path ?? "";
            data["parent"] = parent;
            data["entries"] = entries;
            data["places"] = places;
            data["drives"] = drives;
            data["error"] = error;
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(data).Replace("</", "<\\/");
            string title = computer ? "This PC" : (Path.GetFileName(path.TrimEnd('\\')).Length > 0 ? Path.GetFileName(path.TrimEnd('\\')) : path);
            return Template.Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(title)).Replace("{{ROOT}}", root).Replace("{{DATA}}", json);
        }

        private static IEnumerable<DriveInfo> SafeDrives()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                bool ready;
                try { ready = drive.IsReady && drive.DriveType != DriveType.Network; }
                catch (IOException) { ready = false; }
                if (ready) yield return drive;
            }
        }

        private static string Label(DriveInfo drive)
        {
            try { return drive.VolumeLabel ?? ""; }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        private static long Epoch(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        private const string Template = @"<!doctype html>
<html><head><meta charset='utf-8'><title>{{TITLE}}</title>
<style>
@font-face { font-family: 'White Rabbit'; src: url('{{ROOT}}font/WhiteRabbit.ttf'); }
:root { --bg:#050505; --panel:#0c0b0b; --line:#52403b; --ink:#e5dabd; --muted:#ac9f87; --faint:#4c453e; --red:#ff465c; --sel:#2e1015; --hover:#1c1413; }
* { box-sizing:border-box; }
html,body { margin:0; height:100%; background:var(--bg); color:var(--ink); font:13px 'White Rabbit',Consolas,monospace; overflow:hidden; user-select:none; }
#app { display:grid; grid-template-columns:224px 1fr 300px; grid-template-rows:auto auto 1fr auto; height:100%; }
header { grid-column:1/4; display:flex; align-items:center; gap:10px; padding:10px 14px; border-bottom:1px solid var(--line); }
.tag-app { color:var(--red); letter-spacing:1px; }
#crumbs { flex:1; display:flex; flex-wrap:wrap; gap:2px; min-width:0; }
#crumbs a { color:var(--ink); cursor:pointer; padding:2px 4px; }
#crumbs a:hover { background:var(--hover); color:var(--red); }
#crumbs span { color:var(--faint); padding:2px 0; }
button, .btn { font:inherit; background:var(--panel); color:var(--ink); border:1px solid var(--line); padding:4px 10px; cursor:pointer; }
button:hover { border-color:var(--red); color:var(--red); }
button:disabled { color:var(--faint); border-color:#2a2321; cursor:default; }
#bar { grid-column:2/3; display:flex; align-items:center; gap:8px; padding:8px 14px; border-bottom:1px solid var(--line); color:var(--muted); }
#filter { flex:1; background:transparent; border:none; border-bottom:1px solid var(--faint); color:var(--ink); font:inherit; padding:3px 2px; outline:none; }
#filter:focus { border-bottom-color:var(--red); }
aside { grid-row:2/4; border-right:1px solid var(--line); padding:12px 0; overflow:auto; }
aside h4 { margin:10px 14px 6px; font-weight:normal; color:var(--faint); letter-spacing:1px; }
aside a { display:block; padding:4px 14px; color:var(--muted); cursor:pointer; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
aside a:hover, aside a.here { background:var(--hover); color:var(--red); }
aside .meter { display:block; color:var(--faint); font-size:11px; padding-left:22px; white-space:pre; }
#list { grid-column:2/3; overflow:auto; outline:none; }
.head, .row { display:grid; grid-template-columns:64px 1fr 96px 150px; gap:10px; padding:4px 14px; white-space:nowrap; }
.head { position:sticky; top:0; background:var(--bg); color:var(--faint); border-bottom:1px solid #1e1917; }
.head span { cursor:pointer; } .head span:hover { color:var(--red); }
.row { cursor:default; border-left:2px solid transparent; }
.row:hover { background:var(--hover); }
.row.sel { background:var(--sel); border-left-color:var(--red); }
.row .name { overflow:hidden; text-overflow:ellipsis; }
.row.hidden .name { color:var(--muted); opacity:.65; }
.size, .date { text-align:right; color:var(--muted); }
.k { color:var(--muted); } .k-DIR,.k-DRV { color:var(--red); } .k-IMG { color:#d98fa0; } .k-VID,.k-AUD { color:#c9a26b; } .k-EXE { color:#ff7a6b; } .k-CODE,.k-TXT { color:#9fb8a0; }
#empty { padding:30px 14px; color:var(--faint); }
#side { grid-row:2/4; grid-column:3/4; border-left:1px solid var(--line); padding:14px; overflow:auto; display:flex; flex-direction:column; gap:10px; }
#side h3 { margin:0; font-weight:normal; color:var(--red); word-break:break-all; }
#peek { border:1px solid var(--line); min-height:120px; display:flex; align-items:center; justify-content:center; background:var(--panel); overflow:hidden; }
#peek img { max-width:100%; max-height:260px; image-rendering:auto; }
#peek pre { margin:0; padding:8px; width:100%; max-height:260px; overflow:hidden; font:11px 'White Rabbit',Consolas,monospace; color:var(--muted); white-space:pre-wrap; word-break:break-all; user-select:text; }
#peek .glyph { color:var(--faint); white-space:pre; font-size:12px; }
#info div { display:flex; justify-content:space-between; gap:8px; padding:2px 0; color:var(--muted); }
#info b { font-weight:normal; color:var(--ink); text-align:right; word-break:break-all; }
#actions { display:flex; flex-direction:column; gap:6px; }
footer { grid-column:1/4; display:flex; gap:18px; padding:6px 14px; border-top:1px solid var(--line); color:var(--faint); font-size:11px; }
footer b { color:var(--muted); font-weight:normal; }
::-webkit-scrollbar { width:10px; height:10px; } ::-webkit-scrollbar-thumb { background:#2a2321; } ::-webkit-scrollbar-track { background:var(--bg); }
</style></head>
<body><div id='app'>
<header><span class='tag-app'>ARABARA://FILES</span><button id='up' title='Up (Backspace)'>[ UP ]</button><div id='crumbs'></div></header>
<aside id='places'></aside>
<div id='bar'><span>&gt;</span><input id='filter' placeholder='filter_' spellcheck='false'><button id='hid' title='Show hidden and system items'>[ HIDDEN: OFF ]</button></div>
<div id='list' tabindex='0'></div>
<section id='side'><h3 id='pname'></h3><div id='peek'></div><div id='info'></div><div id='actions'></div></section>
<footer><span id='count'></span><span>[ENTER] OPEN</span><span>[BACKSPACE] UP</span><span>[TYPE] FILTER</span><span>[ESC] CLEAR</span></footer>
</div>
<script>
const ROOT = '{{ROOT}}', D = {{DATA}};
const cmd = (c, p) => ROOT + c + '?p=' + encodeURIComponent(p);
const go = (c, p) => { location.href = cmd(c, p); };
let sortKey = 'n', sortDir = 1, showHidden = false, sel = 0, rows = [];
const $ = id => document.getElementById(id);
const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
function size(b) { if (b < 0) return ''; const u = ['B','KB','MB','GB','TB']; let i = 0; while (b >= 1024 && i < 4) { b /= 1024; i++; } return (i ? b.toFixed(1) : b) + ' ' + u[i]; }
function date(ms) { if (!ms) return ''; const d = new Date(ms), z = n => String(n).padStart(2, '0'); return d.getFullYear() + '-' + z(d.getMonth() + 1) + '-' + z(d.getDate()) + ' ' + z(d.getHours()) + ':' + z(d.getMinutes()); }
function meter(free, total) { const used = total ? Math.round((1 - free / total) * 10) : 0; return '[' + '#'.repeat(used) + '-'.repeat(10 - used) + '] ' + size(free) + ' free'; }
function crumbs() {
  const c = $('crumbs'); c.textContent = '';
  const pc = el('a', null, 'THIS PC'); pc.onclick = () => go('view', ''); c.appendChild(pc);
  if (!D.path) return;
  const parts = D.path.split('\\').filter(Boolean); let acc = '';
  parts.forEach((p, i) => { acc += p + '\\'; const at = acc; c.appendChild(el('span', null, '/')); const a = el('a', null, p); a.onclick = () => go('view', at); c.appendChild(a); });
}
function places() {
  const s = $('places'); s.textContent = '';
  s.appendChild(el('h4', null, 'QUICK ACCESS'));
  D.places.forEach(p => { const a = el('a', p.p.toLowerCase() === D.path.toLowerCase() ? 'here' : null, '> ' + p.n); a.title = p.p; a.onclick = () => go('view', p.p); s.appendChild(a); });
  s.appendChild(el('h4', null, 'DRIVES'));
  D.drives.forEach(d => { const a = el('a', D.path.toLowerCase().startsWith(d.p.toLowerCase()) ? 'here' : null, '> ' + d.n + (d.l ? ' ' + d.l : '')); a.onclick = () => go('view', d.p); a.appendChild(el('span', 'meter', meter(d.f, d.t))); s.appendChild(a); });
}
function visible() {
  const f = $('filter').value.toLowerCase();
  return D.entries.filter(e => (showHidden || !e.h) && (!f || e.n.toLowerCase().includes(f))).sort((a, b) => {
    if (a.d !== b.d) return a.d ? -1 : 1;
    let x = a[sortKey], y = b[sortKey];
    if (sortKey === 'n' || sortKey === 'k') { x = String(x).toLowerCase(); y = String(y).toLowerCase(); }
    return (x < y ? -1 : x > y ? 1 : 0) * sortDir || a.n.localeCompare(b.n);
  });
}
function render() {
  rows = visible(); const list = $('list'); list.textContent = '';
  const head = el('div', 'head');
  [['k', 'TYPE'], ['n', 'NAME'], ['s', 'SIZE'], ['m', 'MODIFIED']].forEach(([k, t], i) => { const h = el('span', i >= 2 ? (i == 2 ? 'size' : 'date') : null, t + (sortKey === k ? (sortDir > 0 ? ' ^' : ' v') : '')); h.onclick = () => { sortDir = sortKey === k ? -sortDir : 1; sortKey = k; render(); }; head.appendChild(h); });
  list.appendChild(head);
  if (!rows.length) { const e = el('div', null, D.error ? '[ ' + D.error + ' ]' : '-- EMPTY --'); e.id = 'empty'; list.appendChild(e); }
  const frag = document.createDocumentFragment();
  rows.forEach((e, i) => {
    const r = el('div', 'row' + (e.h ? ' hidden' : '')); r.dataset.i = i;
    r.appendChild(el('span', 'k k-' + e.k, '[' + e.k + ']'));
    r.appendChild(el('span', 'name', e.n));
    r.appendChild(el('span', 'size', e.k === 'DRV' ? size(e.f) + ' free' : size(e.s)));
    r.appendChild(el('span', 'date', date(e.m)));
    r.onmousedown = () => select(i);
    r.ondblclick = () => open(rows[i]);
    frag.appendChild(r);
  });
  list.appendChild(frag);
  const dirs = D.entries.filter(e => e.d).length, bytes = D.entries.reduce((t, e) => t + (e.s > 0 && !e.d ? e.s : 0), 0);
  $('count').textContent = rows.length + ' SHOWN  //  ' + dirs + ' DIRS  //  ' + (D.entries.length - dirs) + ' FILES  //  ' + size(bytes) + (D.error && D.entries.length ? '  //  ' + D.error : '');
  select(Math.min(sel, rows.length - 1), true);
}
function select(i, quiet) {
  const all = document.querySelectorAll('.row');
  all.forEach(r => r.classList.remove('sel'));
  sel = Math.max(0, i);
  const r = all[sel]; if (!r) { preview(null); return; }
  r.classList.add('sel'); if (!quiet) r.scrollIntoView({ block: 'nearest' });
  preview(rows[sel]);
}
function open(e) { if (!e) return; if (e.d) go('view', e.p); else go(e.v ? 'open' : 'system', e.p); }
let peekToken = 0;
function preview(e) {
  const peek = $('peek'), info = $('info'), actions = $('actions'); peek.textContent = ''; info.textContent = ''; actions.textContent = '';
  $('pname').textContent = e ? e.n : (D.path || 'THIS PC');
  if (!e) { peek.appendChild(el('span', 'glyph', '[ NO SELECTION ]')); return; }
  const token = ++peekToken;
  if (e.k === 'IMG' && e.s < 16 * 1048576 && !/\.(svg|tif|tiff|heic)$/i.test(e.n)) { const img = el('img'); img.src = cmd('raw', e.p); peek.appendChild(img); }
  else if ((e.k === 'TXT' || e.k === 'CODE') && e.s >= 0) { const pre = el('pre', null, 'loading...'); peek.appendChild(pre); fetch(cmd('raw', e.p) + '&head=1').then(r => r.ok ? r.text() : '[ unreadable ]').then(t => { if (token === peekToken) pre.textContent = t; }); }
  else peek.appendChild(el('span', 'glyph', e.d ? ' ___\n|   |___\n|       |\n|_______|' : '  ____\n |    \\\n | ' + e.k.padEnd(3) + ' |\n |______|'));
  const line = (k, v) => { const d = el('div'); d.appendChild(el('span', null, k)); d.appendChild(el('b', null, v)); info.appendChild(d); };
  line('TYPE', e.k + (e.d ? '' : '  ' + (e.n.includes('.') ? e.n.split('.').pop().toUpperCase() : '')));
  if (e.k === 'DRV') { line('TOTAL', size(e.s)); line('FREE', size(e.f)); }
  else { if (!e.d) line('SIZE', size(e.s)); line('MODIFIED', date(e.m)); line('CREATED', date(e.c)); }
  line('PATH', e.p);
  const act = (t, f) => { const b = el('button', null, t); b.onclick = f; actions.appendChild(b); };
  act(e.d ? '[ OPEN FOLDER ]' : e.v ? '[ OPEN IN TAB ]' : '[ OPEN WITH WINDOWS ]', () => open(e));
  if (!e.d && e.v) act('[ OPEN WITH WINDOWS ]', () => go('system', e.p));
  if (e.k !== 'DRV') act('[ SHOW IN EXPLORER ]', () => go('reveal', e.p));
  act('[ COPY PATH ]', () => go('copy', e.p));
}
$('up').disabled = D.parent == null;
$('up').onclick = () => { if (D.parent != null) go('view', D.parent); };
$('filter').oninput = () => { sel = 0; render(); };
$('hid').onclick = () => { showHidden = !showHidden; $('hid').textContent = '[ HIDDEN: ' + (showHidden ? 'ON' : 'OFF') + ' ]'; render(); };
document.addEventListener('keydown', ev => {
  const f = $('filter'), inFilter = document.activeElement === f;
  if (ev.key === 'ArrowDown') { select(Math.min(rows.length - 1, sel + 1)); ev.preventDefault(); }
  else if (ev.key === 'ArrowUp') { select(sel - 1); ev.preventDefault(); }
  else if (ev.key === 'PageDown') { select(Math.min(rows.length - 1, sel + 15)); ev.preventDefault(); }
  else if (ev.key === 'PageUp') { select(sel - 15); ev.preventDefault(); }
  else if (ev.key === 'Home' && !inFilter) { select(0); ev.preventDefault(); }
  else if (ev.key === 'End' && !inFilter) { select(rows.length - 1); ev.preventDefault(); }
  else if (ev.key === 'Enter') { open(rows[sel]); ev.preventDefault(); }
  else if (ev.key === 'Escape') { f.value = ''; sel = 0; render(); $('list').focus(); }
  else if (ev.key === 'Backspace' && !(inFilter && f.value)) { if (D.parent != null) go('view', D.parent); ev.preventDefault(); }
  else if (ev.key.length === 1 && !ev.ctrlKey && !ev.altKey && !inFilter) f.focus();
});
crumbs(); places(); render(); $('list').focus();
</script></body></html>";
    }
}
