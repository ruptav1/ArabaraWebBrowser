using System;
using System.Collections.Generic;

namespace Arabara
{
    // PICTURE: still pictures chosen by the user (right-click the logo adds one), drawn like
    // VIDEO LOOP as two layers, a little smaller than the page (see SceneScale), centred,
    // with their edges fading softly into the page. Clicking the logo on a picture moves on
    // to the next picture, and after the last one to the next scene. Behind, the picture as
    // ASCII in the scene's characters (inverted when mostly bright, so the page stays dark),
    // its glyphs chosen once per window size; it stays alive with a slow drifting shimmer of
    // light and the picture's own sparkles twinkling. In front, the character cut out of it
    // (PictureFeed) in small characters, breathing and blinking (FigureLayer). A picture
    // may have its own character grid (for example 600 x 310 cells over the page), drawn
    // in shade blocks (see BlockGlyph), and its own motion layout (PictureMotion). Only the
    // file paths and these settings are stored; pictures are never copied.
    internal sealed partial class SpinningMark
    {
        private static List<PictureSetting> pictures = new List<PictureSetting>();
        private static int pictureFrom, pictureTo;
        private static readonly Dictionary<string, PictureFeed> feeds = new Dictionary<string, PictureFeed>(StringComparer.OrdinalIgnoreCase);
        private static PictureFeed picture; // the one being drawn
        private PictureFeed pictureBuilt;
        private long pictureKey;
        private float[] pictureLevel = new float[0], pictureTwinkle = new float[0], pictureRate = new float[0];
        private char[] pictureGlyph = new char[0], pictureSpark = new char[0];
        private float[] shimmerA = new float[0], shimmerB = new float[0], shimmerC = new float[0], shimmerD = new float[0];
        private int shimmerCellH;
        private double pictureShownAt = -1;
        internal double pictureFigureAt = -1;
        private float pictureFade = 1;
        private PictureFeed pictureFaded;

        // Where cut-out silhouettes are kept (null: not kept).
        public static string CacheFolder;

        public static int PictureCount { get { return pictures.Count; } }
        public static int PictureIndex { get { return pictureTo; } }
        public static PictureSetting CurrentPicture { get { return pictureTo < pictures.Count ? pictures[pictureTo] : null; } }

        // The first (or only) picture's path; setting it replaces the list with that picture.
        public static string PicturePath
        {
            get { var current = CurrentPicture; return current != null ? current.Path : null; }
            set
            {
                pictures = String.IsNullOrEmpty(value) ? new List<PictureSetting>() : new List<PictureSetting> { new PictureSetting { Path = value } };
                pictureFrom = pictureTo = 0;
            }
        }

        public static void SetPictures(List<PictureSetting> list, int index)
        {
            pictures = list ?? new List<PictureSetting>();
            pictureFrom = pictureTo = Math.Max(0, Math.Min(pictures.Count - 1, index));
            // Forget prepared pictures that are no longer in the list.
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pictures) keep.Add(FeedKey(p));
            foreach (string key in new List<string>(feeds.Keys)) if (!keep.Contains(key)) feeds.Remove(key);
        }

        // Shows picture index (with the scene fade from what is shown now).
        public static void ShowPicture(int index, bool animate)
        {
            if (pictures.Count == 0) return;
            int shown = ShownPicture;
            SetScene(PictureScene, animate);
            pictureFrom = animate ? shown : index;
            pictureTo = Math.Max(0, Math.Min(pictures.Count - 1, index));
        }

        // The picture on screen: the previous one while the fade is going out.
        private static int ShownPicture
        {
            get { return sceneClock.IsRunning && sceneClock.ElapsedMilliseconds < FadeMs ? pictureFrom : pictureTo; }
        }

        // The settings of the picture being drawn when the scene is PICTURE (else null).
        private static PictureSetting ShownSetting(int scene)
        {
            int i = ShownPicture;
            return scene == PictureScene && i < pictures.Count ? pictures[i] : null;
        }

        private static string FeedKey(PictureSetting p) { return (p.Path ?? "") + "|" + (p.Motion ?? ""); }

        // Off PICTURE: every prepared picture and the cells built from it go (see ReleaseScenesNotDrawn).
        private void ReleasePictures()
        {
            if (feeds.Count > 0) feeds.Clear();
            picture = null;
            ReleasePictureCells();
        }

        // This page's cells built from a picture (the pictures themselves are shared).
        private void ReleasePictureCells()
        {
            pictureBuilt = null; pictureFaded = null; pictureKey = 0;
            if (pictureLevel.Length == 0) return;
            pictureLevel = pictureTwinkle = pictureRate = new float[0];
            pictureGlyph = pictureSpark = new char[0];
            shimmerA = shimmerB = shimmerC = shimmerD = new float[0];
            pictureShownAt = pictureFigureAt = -1;
        }

        // On PICTURE: only the pictures being drawn (two during the fade from one to the next) stay prepared.
        private static void TrimPictures()
        {
            if (feeds.Count <= 1) return;
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int shown = ShownPicture;
            if (shown < pictures.Count) keep.Add(FeedKey(pictures[shown]));
            if (pictureTo < pictures.Count) keep.Add(FeedKey(pictures[pictureTo]));
            if (feeds.Count > keep.Count)
                foreach (string key in new List<string>(feeds.Keys)) if (!keep.Contains(key)) feeds.Remove(key);
        }

        private static PictureFeed Feed(PictureSetting setting)
        {
            PictureFeed feed;
            string key = FeedKey(setting);
            if (!feeds.TryGetValue(key, out feed)) feeds[key] = feed = new PictureFeed(setting.Path, CacheFolder, setting.Motion);
            return feed;
        }

        public static string PictureError { get { var current = CurrentPicture; return current != null && Feed(current).Done ? Feed(current).Error : null; } }
        internal static PictureFeed Picture { get { var current = CurrentPicture; return current != null ? Feed(current) : null; } }

        private void ScenePicture(double t)
        {
            pictureFade = 0;
            var setting = ShownSetting(PictureScene);
            if (setting == null || String.IsNullOrEmpty(setting.Path)) return;
            picture = Feed(setting);
            // The background fades in as soon as the picture is read; the character, once cut
            // out (the first time a few seconds, later from the cache), fades in over it.
            if (picture != pictureFaded) { pictureFaded = picture; pictureShownAt = pictureFigureAt = -1; }
            if (!picture.LumaReady) { pictureShownAt = pictureFigureAt = -1; return; }
            if (pictureShownAt < 0 || t < pictureShownAt) pictureShownAt = t;
            var figure = picture.Done ? picture.Figure : null;
            if (figure == null) pictureFigureAt = -1;
            else if (pictureFigureAt < 0 || t < pictureFigureAt) pictureFigureAt = t;
            float background = (float)Math.Min(1, (t - pictureShownAt) / 0.6);
            pictureFade = figure == null ? 0 : (float)Math.Min(1, (t - pictureFigureAt) / 0.8);
            figureShown = figure != null;
            if (figureShown) { EnsureFigureLayer(figure, picture.Sway, picture.Eyes, picture.Motion); PoseFigure(t); }
            EnsurePictureCells();
            if (shapeGlyph.Length != cols * rows) shapeGlyph = new char[cols * rows];
            // Two slow waves of light drifting across the picture, by angle addition: per
            // column and per row terms, so each cell costs a few multiplications.
            if (shimmerA.Length != cols * 2) { shimmerA = new float[cols * 2]; shimmerC = new float[cols * 2]; }
            if (shimmerB.Length != rows * 2 || shimmerCellH != cellH)
            {
                shimmerCellH = cellH;
                shimmerB = new float[rows * 2]; shimmerD = new float[rows * 2];
                for (int r = 0; r < rows; r++)
                {
                    double y = (r + 0.5) * cellH, b = 0.012 * y, d = -0.0145 * y;
                    shimmerB[r * 2] = (float)Math.Sin(b); shimmerB[r * 2 + 1] = (float)Math.Cos(b);
                    shimmerD[r * 2] = (float)Math.Sin(d); shimmerD[r * 2 + 1] = (float)Math.Cos(d);
                }
            }
            for (int c = 0; c < cols; c++)
            {
                double x = (c + 0.5) * cellW, a = 0.034 * x - 0.8 * t, e = 0.012 * x + 0.45 * t;
                shimmerA[c * 2] = (float)Math.Sin(a); shimmerA[c * 2 + 1] = (float)Math.Cos(a);
                shimmerC[c * 2] = (float)Math.Sin(e); shimmerC[c * 2 + 1] = (float)Math.Cos(e);
            }
            for (int r = 0; r < rows; r++)
            {
                float sb = shimmerB[r * 2], cb = shimmerB[r * 2 + 1], sd = shimmerD[r * 2], cd = shimmerD[r * 2 + 1];
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    if (figureShown && figureCovers[i]) continue;
                    float level = pictureLevel[i];
                    char glyph = pictureGlyph[i];
                    if (level > 0)
                    {
                        float first = shimmerA[c * 2] * cb + shimmerA[c * 2 + 1] * sb, second = shimmerC[c * 2] * cd + shimmerC[c * 2 + 1] * sd;
                        level *= 0.86F + 0.14F * first * second;
                    }
                    float spark = pictureTwinkle[i];
                    if (spark > 0)
                    {
                        // Each sparkle flares now and then at its own pace.
                        double wave = Math.Sin(t * pictureRate[i] + i * 2.399);
                        float flare = spark * (0.2F + 0.8F * (float)Math.Pow(Math.Max(0, wave), 6));
                        if (flare > level) { level = flare; glyph = pictureSpark[i]; }
                    }
                    level *= background;
                    if (level < (blockCells ? 0.03F : naturalLight ? 0.06F : 0.1F)) continue;
                    light[i] = Math.Min(1F, level);
                    shapeGlyph[i] = glyph;
                }
            }
        }

        // The background's glyphs and levels for this grid (as SceneVideo does each frame),
        // and the sparkle cells with their own, non-inverted glyphs.
        private void EnsurePictureCells()
        {
            int w = picture.Width, h = picture.Height;
            var cutOut = picture.Done ? picture.Figure : null;
            long key = ((long)cols << 48) ^ ((long)rows << 32) ^ ((long)width << 16) ^ height ^ ((long)cellW << 40) ^ ((long)cellH << 56) ^ (cutOut != null ? 1L << 62 : 0);
            if (pictureBuilt == picture && key == pictureKey && pictureLevel.Length == cols * rows) return;
            pictureBuilt = picture; pictureKey = key;
            int n = cols * rows;
            pictureLevel = new float[n]; pictureGlyph = new char[n];
            pictureTwinkle = new float[n]; pictureSpark = new char[n]; pictureRate = new float[n];
            byte[] luma = picture.Luma, sparkle = picture.Done && picture.Sparkle != null ? picture.Sparkle : new byte[luma.Length];
            byte[] mask = cutOut != null ? cutOut.Mask : null;
            var histogram = new int[256];
            long sum = 0; int count = 0;
            for (int i = 0; i < luma.Length; i++)
            {
                if (mask != null && mask[i] != 0) continue;
                histogram[luma[i]]++; sum += luma[i]; count++;
            }
            if (count == 0) return;
            bool invert = sum / (float)count / 255F > 0.5F;
            int low = 0, high = 255, seen = 0;
            for (int v = 0; v < 256; v++) { seen += histogram[v]; if (seen >= count * 0.02) { low = v; break; } }
            seen = 0;
            for (int v = 255; v >= 0; v--) { seen += histogram[v]; if (seen >= count * 0.02) { high = v; break; } }
            float range = Math.Max(24, high - low);
            var curve = new float[256]; var plain = new float[256];
            for (int v = 0; v < 256; v++)
            {
                float level = Math.Max(0F, Math.Min(1F, (v - low) / range));
                plain[v] = Math.Max(0F, Math.Min(1F, ((float)Math.Pow(level, 0.75) - 0.17F) * 1.2F));
                if (invert) level = 1 - level;
                curve[v] = Math.Max(0F, Math.Min(1F, ((float)Math.Pow(level, 0.75) - 0.17F) * 1.2F));
            }
            float scale = SceneScale(true, w, h), halfW = w / 2F, halfH = h / 2F;
            // Under natural light (a picture's own size or grid): one light for both layers,
            // not inverted, the shadows kept.
            float gate = 0.1F;
            if (naturalLight) { curve = plain = BlockCurve(luma); gate = 0.06F; }
            if (blockCells)
            {
                // Shade blocks: four points per cell give its level; no shape to match.
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        int i = r * cols + c;
                        pictureLevel[i] = -1;
                        float s = 0, b = 0, spark = 0; int inside = 0;
                        for (int q = 0; q < 4; q++)
                        {
                            int vx = (int)((c * cellW + (q % 2 == 0 ? 0.25F : 0.75F) * cellW - width / 2F) / scale + halfW);
                            int vy = (int)((r * cellH + (q < 2 ? 0.25F : 0.75F) * cellH - height / 2F) / scale + halfH);
                            if (vx < 0 || vy < 0 || vx >= w || vy >= h) continue;
                            int at = vy * w + vx;
                            s += curve[luma[at]]; b += plain[luma[at]]; spark = Math.Max(spark, sparkle[at] / 255F);
                            inside++;
                        }
                        if (inside < 4) continue;
                        float edge = EdgeFade(true, (c * cellW + cellW / 2F - width / 2F) / scale + halfW, (r * cellH + cellH / 2F - height / 2F) / scale + halfH, w, h);
                        float level = s / 4 * edge;
                        if (level >= 0.03F) { pictureLevel[i] = Math.Min(1F, level); pictureGlyph[i] = BlockGlyph(pictureLevel[i]); }
                        spark *= edge;
                        if (spark > 0.15F && b / 4 * edge > 0.1F)
                        {
                            pictureTwinkle[i] = Math.Min(1F, 0.45F + spark * 0.6F);
                            pictureSpark[i] = BlockGlyph(1);
                            pictureRate[i] = 0.8F + 1.4F * (float)(((i * 2654435761L) >> 8 & 1023) / 1023.0);
                        }
                    }
                return;
            }
            EnsureShapes();
            SampleTables(w, h, scale, halfW, halfH);
            var vector = new float[6]; var bright = new float[6];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    float peak = 0, total = 0, brightPeak = 0, spark = 0;
                    bool inside = true;
                    for (int k = 0; k < 6 && inside; k++)
                    {
                        int line = sampleRow[r * 6 + k];
                        if (line < 0) { inside = false; break; }
                        float s = 0, b = 0;
                        for (int j = 0; j < 3; j++)
                        {
                            int x = sampleCol[c * 18 + k * 3 + j];
                            if (x < 0) { inside = false; break; }
                            s += curve[luma[line + x]]; b += plain[luma[line + x]];
                            spark = Math.Max(spark, sparkle[line + x] / 255F);
                        }
                        vector[k] = s / 3; bright[k] = b / 3;
                        peak = Math.Max(peak, vector[k]); brightPeak = Math.Max(brightPeak, bright[k]);
                        total += vector[k];
                    }
                    pictureLevel[i] = -1;
                    if (!inside) continue;
                    float edge = EdgeFade(true, (c * cellW + cellW / 2F - width / 2F) / scale + halfW, (r * cellH + cellH / 2F - height / 2F) / scale + halfH, w, h);
                    peak *= edge; total *= edge; brightPeak *= edge; spark *= edge;
                    for (int k = 0; k < 6; k++) { vector[k] *= edge; bright[k] *= edge; }
                    if (peak >= gate)
                    {
                        for (int k = 0; k < 6; k++) { float q = vector[k] / peak; vector[k] = q * q * peak; }
                        pictureLevel[i] = Math.Min(1F, total / 6 * 1.2F);
                        pictureGlyph[i] = MatchShape(vector);
                    }
                    if (spark > 0.15F && brightPeak > 0.1F)
                    {
                        for (int k = 0; k < 6; k++) { float q = bright[k] / brightPeak; bright[k] = q * q * brightPeak; }
                        pictureTwinkle[i] = Math.Min(1F, 0.45F + spark * 0.6F);
                        pictureSpark[i] = MatchShape(bright);
                        // 0.8 to 2.2 radians a second, fixed per cell.
                        pictureRate[i] = 0.8F + 1.4F * (float)(((i * 2654435761L) >> 8 & 1023) / 1023.0);
                    }
                }
        }

        // The block for a fine grid cell. In cells of a few pixels the light, medium and dark
        // shade patterns cannot be seen (as in BBCode ASCII art viewed at that size), and
        // they would cost shading, so every cell is a full block and its shade carries the
        // level (FineTone).
        private static char BlockGlyph(float level) { return '\u2588'; }

        // A shade block as an ordered-dither pattern filling the cell (a quarter, half,
        // three quarters or all of its pixels), so it reads even when the cell is 2 x 2.
        private static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        private static byte[] BlockMask(char glyph, int cellWidth, int cellHeight)
        {
            float cover = glyph == '\u2591' ? 0.25F : glyph == '\u2592' ? 0.5F : glyph == '\u2593' ? 0.75F : glyph == '\u2588' ? 1F : 0F;
            var mask = new byte[cellWidth * cellHeight];
            bool small = cellWidth * cellHeight <= 4;
            for (int y = 0; y < cellHeight; y++)
                for (int x = 0; x < cellWidth; x++)
                {
                    // 2 x 2 cells use the 2 x 2 order so a quarter is exactly one pixel.
                    float threshold = small ? new[] { 0, 2, 3, 1 }[(y % 2) * 2 + x % 2] / 4F : Bayer[(y % 4) * 4 + x % 4] / 16F;
                    mask[y * cellWidth + x] = threshold < cover ? (byte)255 : (byte)0;
                }
            return mask;
        }
    }
}
