using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Arabara
{
    // An eye found on the character, for blinking: the centre of its upper lash line, the
    // direction along it, the direction the lid closes towards (the cheek), the lash line's
    // half length and half thickness, and how far below it the lower lid lies, in picture
    // pixels.
    internal sealed class PictureEye
    {
        public float X, Y, AlongX, AlongY, CloseX, CloseY, HalfLength, Lash, Depth;
        // Where (across the eye, below its lower lid) the clearest skin is, for the lid.
        public float SkinAt = float.NaN;
    }

    // A picture's own motion layout, for poses the automatic one does not suit. Written as
    // words in the preferences, in picture pixels:
    //   head=x,y chest=x,y knees=x,y   points that move (each optional): the chest rises
    //                                  with the breath, the head lifts after it, knees rock
    //   eye=x,y,angle,half,lash,depth  an eye (repeatable): the centre of its upper lash
    //                                  line, the line's angle in degrees (the lid closes at
    //                                  90 degrees to it, clockwise), its half length, half
    //                                  thickness, and how far below it the lower lid is
    //   hair=off                       no swaying hair (when the hair cannot be told from skin)
    internal sealed class PictureMotion
    {
        public bool HasHead, HasChest, HasKnees, NoHair;
        public float HeadX, HeadY, ChestX, ChestY, KneesX, KneesY;
        public PictureEye[] Eyes; // null: found automatically

        public bool Anchored { get { return HasHead || HasChest || HasKnees; } }

        public static PictureMotion Parse(string text)
        {
            if (String.IsNullOrEmpty(text)) return null;
            var motion = new PictureMotion();
            var eyes = new List<PictureEye>();
            foreach (string word in text.Split(new[] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = word.IndexOf('=');
                if (equals <= 0) continue;
                string name = word.Substring(0, equals).ToLowerInvariant(), value = word.Substring(equals + 1);
                if (name == "hair") { motion.NoHair = value.Equals("off", StringComparison.OrdinalIgnoreCase); continue; }
                var numbers = new List<float>();
                foreach (string part in value.Split(','))
                {
                    float number;
                    if (float.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number)) numbers.Add(number);
                }
                if (name == "head" && numbers.Count == 2) { motion.HasHead = true; motion.HeadX = numbers[0]; motion.HeadY = numbers[1]; }
                else if (name == "chest" && numbers.Count == 2) { motion.HasChest = true; motion.ChestX = numbers[0]; motion.ChestY = numbers[1]; }
                else if (name == "knees" && numbers.Count == 2) { motion.HasKnees = true; motion.KneesX = numbers[0]; motion.KneesY = numbers[1]; }
                else if (name == "eye" && numbers.Count == 6)
                {
                    double angle = numbers[2] * Math.PI / 180;
                    float ax = (float)Math.Cos(angle), ay = (float)Math.Sin(angle);
                    eyes.Add(new PictureEye { X = numbers[0], Y = numbers[1], AlongX = ax, AlongY = ay, CloseX = -ay, CloseY = ax, HalfLength = numbers[3], Lash = numbers[4], Depth = numbers[5] });
                }
            }
            if (eyes.Count > 0) motion.Eyes = eyes.ToArray();
            return motion;
        }
    }

    // A still picture for the PICTURE background, prepared on a background thread: its
    // brightness, the character cut out of it (GrabCut, see GrabCut.cs), where the hair
    // hangs loose (to sway), the eyes (to blink) and the small bright sparkles of the
    // background (to twinkle). The file is read once and never copied or changed; only
    // its path is kept in the preferences. The cut (a second or a few of work) is kept as a
    // small silhouette file in the cache folder, so it is done once per picture.
    internal sealed class PictureFeed
    {
        private const int MaxPixels = 1200000, CutPixels = 330000;
        private readonly string path, cacheFolder;

        public PictureFeed(string path, string cacheFolder, string motion = null)
        {
            this.path = path;
            this.cacheFolder = cacheFolder;
            Motion = PictureMotion.Parse(motion);
            var worker = new Thread(Run) { IsBackground = true, Name = "Arabara picture", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        public string Path { get { return path; } }
        public volatile bool Done, LumaReady;
        public string Error { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Luma { get; private set; }
        public VideoFigure Figure { get; private set; }
        // Per picture pixel: how loose the hair is there (0..1, strands hanging free).
        public float[] Sway { get; private set; }
        public PictureEye[] Eyes { get; private set; }
        // Per picture pixel: background sparkle strength (0..255).
        public byte[] Sparkle { get; private set; }
        public long CutMilliseconds { get; private set; }
        public PictureMotion Motion { get; private set; }

        private void Run()
        {
            try
            {
                byte[] rgb; int w, h;
                Load(out rgb, out w, out h);
                var luma = new byte[w * h];
                for (int i = 0; i < w * h; i++) luma[i] = (byte)((rgb[i * 3] * 299 + rgb[i * 3 + 1] * 587 + rgb[i * 3 + 2] * 114) / 1000);
                Width = w; Height = h; Luma = luma;
                LumaReady = true;
                var watch = Stopwatch.StartNew();
                string cache = CachePath(w, h);
                bool[] mask = ReadCache(cache, w, h);
                if (mask == null)
                {
                    mask = Cut(rgb, w, h);
                    WriteCache(cache, mask, w, h);
                }
                CutMilliseconds = watch.ElapsedMilliseconds;
                if (mask != null)
                {
                    int area = 0, left = w, top = h, right = -1, bottom = -1;
                    var bytes = new byte[w * h];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            if (!mask[y * w + x]) continue;
                            bytes[y * w + x] = 1; area++;
                            if (x < left) left = x; if (x > right) right = x;
                            if (y < top) top = y; if (y > bottom) bottom = y;
                        }
                    // A cut that is tiny or most of the picture is not a character in front.
                    if (area > w * h / 50 && area < w * h * 3 / 4)
                    {
                        var bounds = new Rectangle(left, top, right - left + 1, bottom - top + 1);
                        var hsv = Hsv(rgb, w * h);
                        var figure = new VideoFigure(w, h, luma, bytes, bounds);
                        float hairHue;
                        bool[] hair = Hair(hsv, figure, out hairHue);
                        var found = Motion != null && Motion.Eyes != null ? Motion.Eyes : FindEyes(hsv, luma, figure, hair, hairHue);
                        Sway = Motion != null && Motion.NoHair ? null : found.Length > 0 ? Hanging(hair, found, w, h) : Loose(hair, w, h);
                        foreach (var eye in found) eye.SkinAt = SkinBelow(eye, luma, bytes, w, h);
                        Eyes = found;
                        // The character is drawn from its own tone map, not the plain brightness.
                        Figure = new VideoFigure(w, h, Tones(luma, hsv, bytes), bytes, bounds) { Gamma = 1.7F, Fit = true, Plain = luma };
                    }
                }
                Sparkle = Sparkles(luma, mask, w, h);
            }
            catch (Exception ex) { Error = ex.Message; Debug.WriteLine("Picture: " + ex); }
            finally { Done = true; }
        }

        // Reads the file (without keeping it open) and scales it to at most MaxPixels.
        private void Load(out byte[] rgb, out int w, out int h)
        {
            using (var stream = new MemoryStream(File.ReadAllBytes(path)))
            using (var image = Image.FromStream(stream))
            {
                double fit = Math.Min(1, Math.Sqrt(MaxPixels / ((double)image.Width * image.Height)));
                w = Math.Max(1, (int)(image.Width * fit)); h = Math.Max(1, (int)(image.Height * fit));
                using (var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(image, new Rectangle(0, 0, w, h));
                    }
                    var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        rgb = new byte[w * h * 3];
                        var row = new byte[data.Stride];
                        for (int y = 0; y < h; y++)
                        {
                            Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                            for (int x = 0; x < w; x++)
                            {
                                // Memory order is blue, green, red.
                                rgb[(y * w + x) * 3] = row[x * 3 + 2];
                                rgb[(y * w + x) * 3 + 1] = row[x * 3 + 1];
                                rgb[(y * w + x) * 3 + 2] = row[x * 3];
                            }
                        }
                    }
                    finally { bitmap.UnlockBits(data); }
                }
            }
        }

        // The cached cut for this file (its path, size and date) at this size, if any.
        private string CachePath(int w, int h)
        {
            if (cacheFolder == null) return null;
            var info = new FileInfo(path);
            string key = path.ToLowerInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + w + "x" + h;
            ulong hash = 14695981039346656037UL;
            foreach (char c in key) { hash ^= c; hash *= 1099511628211UL; }
            return System.IO.Path.Combine(cacheFolder, "picture-" + hash.ToString("x16") + ".mask");
        }

        private static bool[] ReadCache(string file, int w, int h)
        {
            try
            {
                if (file == null || !File.Exists(file)) return null;
                byte[] data = File.ReadAllBytes(file);
                if (data.Length != 12 + (w * h + 7) / 8 || BitConverter.ToInt32(data, 0) != 0x314B4D41 || BitConverter.ToInt32(data, 4) != w || BitConverter.ToInt32(data, 8) != h) return null;
                var mask = new bool[w * h];
                for (int i = 0; i < w * h; i++) mask[i] = (data[12 + i / 8] & (1 << (i & 7))) != 0;
                return mask;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // Writes the silhouette (one bit per pixel; nothing of the picture itself) and keeps
        // only the eight newest in the folder.
        private void WriteCache(string file, bool[] mask, int w, int h)
        {
            if (file == null) return;
            try
            {
                Directory.CreateDirectory(cacheFolder);
                var data = new byte[12 + (w * h + 7) / 8];
                BitConverter.GetBytes(0x314B4D41).CopyTo(data, 0);
                BitConverter.GetBytes(w).CopyTo(data, 4);
                BitConverter.GetBytes(h).CopyTo(data, 8);
                if (mask != null) for (int i = 0; i < w * h; i++) if (mask[i]) data[12 + i / 8] |= (byte)(1 << (i & 7));
                File.WriteAllBytes(file, data);
                var old = new DirectoryInfo(cacheFolder).GetFiles("picture-*.mask");
                Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 8; i < old.Length; i++) old[i].Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // GrabCut on a copy of at most CutPixels, twice: once with the whole rectangle just
        // inside the picture's edges as probably foreground, once with only its middle (so
        // the colour models start from the character rather than its surroundings - this
        // keeps pale skin that resembles the background), and the two cuts are joined. Then
        // the largest piece is kept, enclosed holes under 2% of the picture are filled (eyes,
        // small gaps between strands), and the result is scaled back up.
        private static bool[] Cut(byte[] rgb, int w, int h)
        {
            double fit = Math.Min(1, Math.Sqrt(CutPixels / ((double)w * h)));
            int cw = Math.Max(8, (int)(w * fit)), ch = Math.Max(8, (int)(h * fit));
            byte[] small = rgb;
            if (cw != w || ch != h)
            {
                small = new byte[cw * ch * 3];
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        int sx = Math.Min(w - 1, (int)((x + 0.5) * w / cw)), sy = Math.Min(h - 1, (int)((y + 0.5) * h / ch));
                        for (int c = 0; c < 3; c++) small[(y * cw + x) * 3 + c] = rgb[(sy * w + sx) * 3 + c];
                    }
            }
            int inset = Math.Max(3, Math.Min(cw, ch) / 70);
            var rect = new Rectangle(inset, inset, cw - 2 * inset, ch - 2 * inset);
            bool[] cut = GrabCut.Segment(small, cw, ch, rect, 4);
            bool[] middle = GrabCut.Segment(small, cw, ch, rect, new Rectangle(cw / 5, ch / 5, cw - 2 * (cw / 5), ch - 2 * (ch / 5)), 4);
            for (int i = 0; i < cut.Length; i++) cut[i] |= middle[i];
            int n = cw * ch;
            var label = new int[n]; var queue = new int[n];
            int best = 0, bestSize = 0, id = 0;
            for (int i = 0; i < n; i++)
            {
                if (!cut[i] || label[i] != 0) continue;
                int size = VideoFeed.Flood(cut, true, label, ++id, i, cw, ch, queue);
                if (size > bestSize) { bestSize = size; best = id; }
            }
            if (best == 0) return null;
            for (int i = 0; i < n; i++) cut[i] = label[i] == best;
            var outside = new bool[n];
            for (int i = 0; i < n; i++) outside[i] = !cut[i];
            Array.Clear(label, 0, n); id = 0;
            for (int start = 0; start < n; start++)
            {
                if (!outside[start] || label[start] != 0) continue;
                int size = VideoFeed.Flood(outside, true, label, ++id, start, cw, ch, queue);
                bool border = false;
                for (int q = 0; q < size && !border; q++)
                {
                    int x = queue[q] % cw, y = queue[q] / cw;
                    border = x == 0 || y == 0 || x == cw - 1 || y == ch - 1;
                }
                if (!border && size < n / 50) for (int q = 0; q < size; q++) cut[queue[q]] = true;
            }
            if (cw == w && ch == h) return cut;
            var mask = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    mask[y * w + x] = cut[Math.Min(ch - 1, y * ch / h) * cw + Math.Min(cw - 1, x * cw / w)];
            return mask;
        }

        // The character's tones. Pastel artwork is nearly all light, and hair, skin and
        // clothes can be equally bright, so the brightness is lowered by the saturation
        // (strong colours read darker than skin), then spread: half a plain stretch between
        // the 2nd and 98th percentiles, half an equalisation by rank, which brings out the
        // soft shading inside the fills.
        private static byte[] Tones(byte[] luma, float[] hsv, byte[] mask)
        {
            int n = luma.Length;
            var tone = new float[n];
            var values = new List<float>();
            for (int i = 0; i < n; i++)
            {
                tone[i] = luma[i] - 90 * hsv[i * 3 + 1];
                if (mask[i] != 0) values.Add(tone[i]);
            }
            var result = new byte[n];
            if (values.Count == 0) return result;
            values.Sort();
            float low = values[values.Count * 2 / 100], high = values[Math.Min(values.Count - 1, values.Count * 98 / 100)], range = Math.Max(24, high - low);
            for (int i = 0; i < n; i++)
            {
                if (mask[i] == 0) continue;
                int rank = values.BinarySearch(tone[i]);
                if (rank < 0) rank = ~rank;
                float stretched = Math.Max(0, Math.Min(1, (tone[i] - low) / range)), equalised = rank / (float)values.Count;
                result[i] = (byte)Math.Min(255, (int)((0.5F * stretched + 0.5F * equalised) * 255));
            }
            return result;
        }

        // Hair that hangs below the eyes sways, more the further down it hangs (gravity
        // pulls strands down whatever the tilt of the head); the rest of the hair stays with
        // the head.
        private static float[] Hanging(bool[] hair, PictureEye[] eyes, int w, int h)
        {
            float level = 0;
            foreach (var eye in eyes) level += eye.Y + eye.CloseY * eye.Depth;
            level /= eyes.Length;
            var sway = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                float k = SmoothStep(level + 5, level + 90, y);
                if (k <= 0) continue;
                for (int x = 0; x < w; x++) if (hair[y * w + x]) sway[y * w + x] = k;
            }
            return BoxBlur(sway, w, h, 3);
        }

        // The band just below an eye's lower lid that is brightest along the eye (skin, not
        // a strand of hair or an outline), as a distance across the eye from its lash line.
        private static float SkinBelow(PictureEye eye, byte[] luma, byte[] mask, int w, int h)
        {
            float best = eye.Depth + 4, bestLevel = -1;
            for (float t = eye.Depth + 2; t <= eye.Depth + 16; t += 1)
            {
                float sum = 0; int count = 0;
                for (int k = -2; k <= 2; k++)
                {
                    float s = k * 0.25F * eye.HalfLength;
                    int x = (int)(eye.X + s * eye.AlongX + t * eye.CloseX), y = (int)(eye.Y + s * eye.AlongY + t * eye.CloseY);
                    if (x < 0 || y < 0 || x >= w || y >= h || mask[y * w + x] == 0) continue;
                    sum += luma[y * w + x]; count++;
                }
                if (count >= 3 && sum / count > bestLevel) { bestLevel = sum / count; best = t; }
            }
            return best;
        }

        // Hue (degrees), saturation and value (0..1) per pixel.
        private static float[] Hsv(byte[] rgb, int n)
        {
            var hsv = new float[n * 3];
            for (int i = 0; i < n; i++)
            {
                float r = rgb[i * 3] / 255F, g = rgb[i * 3 + 1] / 255F, b = rgb[i * 3 + 2] / 255F;
                float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                float hue = 0;
                if (d > 1e-5F)
                {
                    if (max == r) hue = 60 * (((g - b) / d) % 6);
                    else if (max == g) hue = 60 * ((b - r) / d + 2);
                    else hue = 60 * ((r - g) / d + 4);
                    if (hue < 0) hue += 360;
                }
                hsv[i * 3] = hue; hsv[i * 3 + 1] = max <= 0 ? 0 : d / max; hsv[i * 3 + 2] = max;
            }
            return hsv;
        }

        private static float HueDistance(float a, float b) { float d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

        // The hair: the most common strong hue in the upper part of the character.
        private static bool[] Hair(float[] hsv, VideoFigure figure, out float hairHue)
        {
            int w = figure.Width, h = figure.Height;
            var bins = new int[36];
            int limit = figure.Bounds.Top + figure.Bounds.Height * 45 / 100;
            for (int y = figure.Bounds.Top; y < limit; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (figure.Mask[i] != 0 && hsv[i * 3 + 1] > 0.2F && hsv[i * 3 + 2] > 0.3F) bins[(int)(hsv[i * 3] / 10) % 36]++;
                }
            int peak = 0; long peakCount = -1;
            for (int b = 0; b < 36; b++)
            {
                long count = bins[(b + 35) % 36] + 2L * bins[b] + bins[(b + 1) % 36];
                if (count > peakCount) { peakCount = count; peak = b; }
            }
            // Refine with the mean direction of the hues near the peak.
            double sx = 0, sy = 0;
            for (int y = figure.Bounds.Top; y < limit; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (figure.Mask[i] == 0 || hsv[i * 3 + 1] <= 0.2F || HueDistance(hsv[i * 3], peak * 10 + 5) > 20) continue;
                    sx += Math.Cos(hsv[i * 3] * Math.PI / 180); sy += Math.Sin(hsv[i * 3] * Math.PI / 180);
                }
            hairHue = (float)((Math.Atan2(sy, sx) * 180 / Math.PI + 360) % 360);
            var hair = new bool[w * h];
            for (int i = 0; i < w * h; i++)
                hair[i] = figure.Mask[i] != 0 && hsv[i * 3 + 1] > 0.15F && HueDistance(hsv[i * 3], hairHue) < 22;
            // Close the gaps the line art leaves between strands.
            var temp = new bool[w * h];
            VideoFeed.Grow(hair, temp, w, h, 2, true);
            VideoFeed.Grow(hair, temp, w, h, 2, false);
            for (int i = 0; i < w * h; i++) hair[i] &= figure.Mask[i] != 0;
            return hair;
        }

        // Loose hair: strands that are thin all along (no thick hair within reach), so the
        // edge of the main mass of hair does not wobble. Thickness is the largest distance to
        // the edge within about 12 pixels; the result is softened over a few pixels.
        private static float[] Loose(bool[] hair, int w, int h)
        {
            int n = w * h;
            var distance = Chamfer(hair, w, h);
            var widest = MaxFilter(distance, w, h, 12);
            var sway = new float[n];
            for (int i = 0; i < n; i++)
                if (hair[i]) sway[i] = 1 - SmoothStep(5F, 11F, widest[i]);
            return BoxBlur(sway, w, h, 3);
        }

        // Distance to the nearest pixel outside the set (3-4 chamfer, in pixels).
        private static float[] Chamfer(bool[] set, int w, int h)
        {
            int n = w * h;
            var d = new int[n];
            const int Big = 1 << 28;
            for (int i = 0; i < n; i++) d[i] = set[i] ? Big : 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (d[i] == 0) continue;
                    int v = d[i];
                    if (x > 0) v = Math.Min(v, d[i - 1] + 3); else v = Math.Min(v, 3);
                    if (y > 0)
                    {
                        v = Math.Min(v, d[i - w] + 3);
                        if (x > 0) v = Math.Min(v, d[i - w - 1] + 4);
                        if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 4);
                    }
                    else v = Math.Min(v, 3);
                    d[i] = v;
                }
            for (int y = h - 1; y >= 0; y--)
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = y * w + x;
                    if (d[i] == 0) continue;
                    int v = d[i];
                    if (x < w - 1) v = Math.Min(v, d[i + 1] + 3); else v = Math.Min(v, 3);
                    if (y < h - 1)
                    {
                        v = Math.Min(v, d[i + w] + 3);
                        if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 4);
                        if (x > 0) v = Math.Min(v, d[i + w - 1] + 4);
                    }
                    else v = Math.Min(v, 3);
                    d[i] = v;
                }
            var result = new float[n];
            for (int i = 0; i < n; i++) result[i] = d[i] / 3F;
            return result;
        }

        private static float[] MaxFilter(float[] values, int w, int h, int r)
        {
            var across = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float m = 0;
                    for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) m = Math.Max(m, values[y * w + k]);
                    across[y * w + x] = m;
                }
            var result = new float[w * h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    float m = 0;
                    for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) m = Math.Max(m, across[k * w + x]);
                    result[y * w + x] = m;
                }
            return result;
        }

        private static float[] BoxBlur(float[] values, int w, int h, int r)
        {
            var across = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                float sum = 0; int count = 0;
                for (int x = -r; x < w; x++)
                {
                    if (x + r < w) { sum += values[y * w + x + r]; count++; }
                    if (x - r - 1 >= 0) { sum -= values[y * w + x - r - 1]; count--; }
                    if (x >= 0) across[y * w + x] = sum / count;
                }
            }
            var result = new float[w * h];
            for (int x = 0; x < w; x++)
            {
                float sum = 0; int count = 0;
                for (int y = -r; y < h; y++)
                {
                    if (y + r < h) { sum += across[(y + r) * w + x]; count++; }
                    if (y - r - 1 >= 0) { sum -= across[(y - r - 1) * w + x]; count--; }
                    if (y >= 0) result[y * w + x] = sum / count;
                }
            }
            return result;
        }

        private static float SmoothStep(float from, float to, float value)
        {
            float k = Math.Max(0F, Math.Min(1F, (value - from) / (to - from)));
            return k * k * (3 - 2 * k);
        }

        // Eyes, as drawn in anime-style pictures: a compact darker stroke of strong colour
        // (lash and iris) that is not the hair's colour nor the main accent colour (a ribbon,
        // a band), with skin around it on one side and hair (the fringe) nearby on the other,
        // in the upper part of the character. Up to two are kept, close together.
        private static PictureEye[] FindEyes(float[] hsv, byte[] luma, VideoFigure figure, bool[] hair, float hairHue)
        {
            int w = figure.Width, h = figure.Height, n = w * h;
            byte[] mask = figure.Mask;
            var skin = new bool[n];
            var stroke = new bool[n];
            int figureArea = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask[i] == 0) continue;
                figureArea++;
                float hue = hsv[i * 3], s = hsv[i * 3 + 1], v = hsv[i * 3 + 2];
                skin[i] = (hue >= 340 || hue <= 44) && s >= 0.05F && s <= 0.43F && v >= 0.84F;
                stroke[i] = luma[i] < 200 && s > 0.31F && HueDistance(hue, hairHue) > 50;
            }
            var temp = new bool[n];
            VideoFeed.Grow(stroke, temp, w, h, 1, false);
            VideoFeed.Grow(stroke, temp, w, h, 1, true);
            var label = new int[n]; var queue = new int[n];
            var pieces = new List<int[]>();
            int id = 0;
            for (int i = 0; i < n; i++)
            {
                if (!stroke[i] || label[i] != 0) continue;
                int size = VideoFeed.Flood(stroke, true, label, ++id, i, w, h, queue);
                var piece = new int[size];
                Array.Copy(queue, piece, size);
                pieces.Add(piece);
            }
            if (pieces.Count == 0) return new PictureEye[0];
            // The accent colour: the hue of the largest stroke piece.
            int largest = 0;
            for (int p = 1; p < pieces.Count; p++) if (pieces[p].Length > pieces[largest].Length) largest = p;
            float accent = MeanHue(hsv, pieces[largest]);
            int minArea = Math.Max(20, figureArea / 5000), maxArea = figureArea / 80;
            var found = new List<KeyValuePair<double, PictureEye>>();
            for (int p = 0; p < pieces.Count; p++)
            {
                int[] piece = pieces[p];
                if (piece.Length < minArea || piece.Length > maxArea) continue;
                if (HueDistance(MeanHue(hsv, piece), accent) < 12) continue;
                double mx = 0, my = 0;
                int x0 = w, y0 = h, x1 = 0, y1 = 0;
                foreach (int i in piece)
                {
                    int x = i % w, y = i / w;
                    mx += x; my += y;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
                mx /= piece.Length; my /= piece.Length;
                if (my > figure.Bounds.Top + figure.Bounds.Height * 0.8) continue;
                // The ring: pixels within 10 of the piece (inside its box grown by 10).
                const int Ring = 10;
                int bx0 = Math.Max(0, x0 - Ring), by0 = Math.Max(0, y0 - Ring), bx1 = Math.Min(w - 1, x1 + Ring), by1 = Math.Min(h - 1, y1 + Ring);
                int bw = bx1 - bx0 + 1, bh = by1 - by0 + 1;
                var local = new bool[bw * bh];
                foreach (int i in piece) local[(i / w - by0) * bw + (i % w - bx0)] = true;
                var grown = (bool[])local.Clone();
                VideoFeed.Grow(grown, new bool[bw * bh], bw, bh, Ring, true);
                int ring = 0, skinCount = 0, hairCount = 0;
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        int l = y * bw + x;
                        if (!grown[l] || local[l]) continue;
                        int i = (y + by0) * w + x + bx0;
                        ring++;
                        if (skin[i]) skinCount++;
                        if (hair[i]) hairCount++;
                    }
                if (ring == 0) continue;
                double skinShare = skinCount / (double)ring, hairShare = hairCount / (double)ring;
                if (skinShare < 0.1 || hairShare < 0.08) continue;
                // Principal axes of the piece: the long one runs along the lash.
                double cxx = 0, cyy = 0, cxy = 0;
                foreach (int i in piece)
                {
                    double dx = i % w - mx, dy = i / w - my;
                    cxx += dx * dx; cyy += dy * dy; cxy += dx * dy;
                }
                cxx /= piece.Length; cyy /= piece.Length; cxy /= piece.Length;
                double angle = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
                double ax = Math.Cos(angle), ay = Math.Sin(angle);
                double spread = Math.Sqrt((cxx - cyy) * (cxx - cyy) + 4 * cxy * cxy);
                double major = (cxx + cyy + spread) / 2, minor = Math.Max(0, (cxx + cyy - spread) / 2);
                // A stroke of length L has a variance of L^2 / 12 along it.
                float halfLength = (float)Math.Max(4, 1.9 * Math.Sqrt(major));
                var eye = new PictureEye
                {
                    X = (float)mx, Y = (float)my, AlongX = (float)ax, AlongY = (float)ay, HalfLength = halfLength,
                    Lash = (float)Math.Max(1.5, 1.8 * Math.Sqrt(minor)), Depth = 0.8F * halfLength
                };
                // The lid closes towards the side with more skin.
                double nx = -ay, ny = ax, plus = 0, minus = 0;
                for (int y = by0; y <= by1; y++)
                    for (int x = bx0; x <= bx1; x++)
                    {
                        if (!skin[y * w + x]) continue;
                        double t = (x - mx) * nx + (y - my) * ny;
                        if (t > 0) plus++; else minus++;
                    }
                if (minus > plus) { nx = -nx; ny = -ny; }
                eye.CloseX = (float)nx; eye.CloseY = (float)ny;
                found.Add(new KeyValuePair<double, PictureEye>(skinShare + hairShare, eye));
            }
            found.Sort((a, b) => b.Key.CompareTo(a.Key));
            var eyes = new List<PictureEye>();
            float reach = (float)Math.Sqrt(figure.Bounds.Width * figure.Bounds.Width + figure.Bounds.Height * figure.Bounds.Height) * 0.3F;
            foreach (var pair in found)
            {
                if (eyes.Count == 2) break;
                var eye = pair.Value;
                if (eyes.Count == 1)
                {
                    if (Math.Abs(eye.X - eyes[0].X) + Math.Abs(eye.Y - eyes[0].Y) > reach) continue;
                    // Both lids of one face close the same way.
                    if (eye.CloseX * eyes[0].CloseX + eye.CloseY * eyes[0].CloseY < 0) { eye.CloseX = -eye.CloseX; eye.CloseY = -eye.CloseY; }
                }
                eyes.Add(eye);
            }
            return eyes.ToArray();
        }

        private static float MeanHue(float[] hsv, int[] pixels)
        {
            double sx = 0, sy = 0;
            foreach (int i in pixels) { sx += Math.Cos(hsv[i * 3] * Math.PI / 180); sy += Math.Sin(hsv[i * 3] * Math.PI / 180); }
            return (float)((Math.Atan2(sy, sx) * 180 / Math.PI + 360) % 360);
        }

        // Sparkles: small spots much brighter than their surroundings, away from the character.
        private static byte[] Sparkles(byte[] luma, bool[] mask, int w, int h)
        {
            int n = w * h, r = Math.Max(6, Math.Min(w, h) / 35);
            var sums = new long[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                long row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += luma[y * w + x];
                    sums[(y + 1) * (w + 1) + x + 1] = sums[y * (w + 1) + x + 1] + row;
                }
            }
            bool[] near = null;
            if (mask != null)
            {
                near = (bool[])mask.Clone();
                VideoFeed.Grow(near, new bool[n], w, h, 3, true);
            }
            var sparkle = new byte[n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (luma[i] < 232 || (near != null && near[i])) continue;
                    // A peak: no brighter neighbour.
                    if ((x > 0 && luma[i - 1] > luma[i]) || (x < w - 1 && luma[i + 1] > luma[i]) || (y > 0 && luma[i - w] > luma[i]) || (y < h - 1 && luma[i + w] > luma[i])) continue;
                    int xa = Math.Max(0, x - r), xb = Math.Min(w, x + r + 1), ya = Math.Max(0, y - r), yb = Math.Min(h, y + r + 1);
                    long sum = sums[yb * (w + 1) + xb] - sums[ya * (w + 1) + xb] - sums[yb * (w + 1) + xa] + sums[ya * (w + 1) + xa];
                    float mean = sum / (float)((xb - xa) * (yb - ya));
                    float s = (luma[i] - mean - 16) / 24;
                    if (s > 0) sparkle[i] = (byte)Math.Min(255, (int)(s * 255));
                }
            return sparkle;
        }
    }
}
