using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Arabara
{
    // Start-page background scenes (the mark over the sea, a video, the sound wave or a
    // picture), all ASCII in the
    // same charcoal crimson, chosen with the logo at the top-left. Switching fades the current scene out (characters
    // thin and darken) and the next one in. Each scene fills light[] with a level
    // 0..1 per character cell (or -1 for empty).
    internal sealed partial class SpinningMark
    {
        // In the order the logo cycles through them: the default first, then the sound wave,
        // the video and the pictures.
        public static readonly string[] SceneNames = { "MARK + SEA", "SOUND WAVE", "VIDEO LOOP", "PICTURE", "RINGS" };
        public const int SoundScene = 1, VideoScene = 2, PictureScene = 3, RingScene = 4;

        // The video's own name (null: its file name), and the name shown for what is on now.
        public static string VideoName;
        public static string DisplayName
        {
            get
            {
                if (Scene == VideoScene && !String.IsNullOrEmpty(videoPath))
                    return !String.IsNullOrEmpty(VideoName) ? VideoName : System.IO.Path.GetFileNameWithoutExtension(videoPath);
                if (Scene == PictureScene && CurrentPicture != null)
                    return !String.IsNullOrEmpty(CurrentPicture.Name) ? CurrentPicture.Name : System.IO.Path.GetFileNameWithoutExtension(CurrentPicture.Path);
                return SceneNames[Scene];
            }
        }
        private const int FadeMs = 400;
        // Between MARK + SEA and SOUND WAVE the page decodes (DecodeMs): every character cell
        // keeps the old scene until its own moment (spread at random over the first three
        // quarters), scrambles through glyphs for a moment and settles on the new scene. Scenes
        // drawn in other cells (the video and the pictures) fade out and in instead.
        private const int DecodeMs = 1100;
        private const float DecodeSpread = 0.75F, DecodeScramble = 0.12F;
        private static bool decoding;
        private static int decodeSeed;
        private static readonly Random decodeRandom = new Random();
        private static int sceneFrom, sceneTo;
        private static readonly Stopwatch sceneClock = new Stopwatch();

        public static int Scene { get { return sceneTo; } }
        public static bool SceneFading { get { return sceneClock.IsRunning && sceneClock.ElapsedMilliseconds < (decoding ? DecodeMs : 2 * FadeMs); } }

        private static bool Decodes(int scene) { return scene == 0 || scene == SoundScene || scene == RingScene; }

        // While the page decodes: the scene it comes from and how far it is (0..1).
        private static bool DecodeState(out int from, out float progress)
        {
            from = sceneFrom;
            progress = 1F;
            if (!decoding || !sceneClock.IsRunning) return false;
            progress = Math.Min(1F, sceneClock.ElapsedMilliseconds / (float)DecodeMs);
            return progress < 1F;
        }

        // Saved preferences keep the numbering from when TUNNEL (1) and TORUS (2) existed:
        // 3 is VIDEO LOOP, 4 SOUND WAVE, 5 PICTURE, 6 RINGS, anything else falls back to MARK + SEA.
        public static int SceneFromSaved(int saved) { return saved == 3 ? VideoScene : saved == 4 ? SoundScene : saved == 5 ? PictureScene : saved == 6 ? RingScene : 0; }
        public static int SavedScene(int scene) { return scene == VideoScene ? 3 : scene == SoundScene ? 4 : scene == PictureScene ? 5 : scene == RingScene ? 6 : 0; }

        public static void SetScene(int scene, bool animate)
        {
            scene = ((scene % SceneNames.Length) + SceneNames.Length) % SceneNames.Length;
            if (!animate) { sceneFrom = sceneTo = scene; sceneClock.Reset(); decoding = false; return; }
            int shown; float ignored;
            SceneState(out shown, out ignored);
            sceneFrom = shown;
            sceneTo = scene;
            decoding = sceneFrom != sceneTo && Decodes(sceneFrom) && Decodes(sceneTo);
            decodeSeed = decodeRandom.Next();
            sceneClock.Restart();
        }

        // Which scene to draw now, and its brightness (fade out, then fade in). While the page
        // decodes this is the new scene at full brightness (see DecodeState for the old one).
        private static void SceneState(out int scene, out float fade)
        {
            scene = sceneTo;
            fade = 1F;
            if (!sceneClock.IsRunning) return;
            long elapsed = sceneClock.ElapsedMilliseconds;
            if (decoding) { if (elapsed >= DecodeMs) { sceneClock.Stop(); decoding = false; } return; }
            if (elapsed < FadeMs) { scene = sceneFrom; fade = 1F - elapsed / (float)FadeMs; }
            else if (elapsed < 2 * FadeMs) fade = (elapsed - FadeMs) / (float)FadeMs;
            else sceneClock.Stop();
        }

        // A scene's data (the video's decoder and frames, the pictures' cut-outs, the character's
        // poses, the glyph match cache) is let go once the scene is no longer drawn, and all of it
        // with the frame buffers (Release); drawing the scene again reads it again (a picture's
        // cut-out from the cache in about a second, a video in a few seconds).
        // The page that drew last: only it lets go of the shared video and pictures (another
        // start page, hidden, keeps its hands off what the one on screen is drawing).
        private static SpinningMark lastRenderer;

        private void ReleaseScenesNotDrawn(bool all)
        {
            bool fading = sceneClock.IsRunning;
            bool videoDrawn = !all && (sceneTo == VideoScene || (fading && sceneFrom == VideoScene));
            bool pictureDrawn = !all && (sceneTo == PictureScene || (fading && sceneFrom == PictureScene));
            bool large = false;
            if (!videoDrawn)
            {
                if (video != null) { video.Dispose(); video = null; large = true; }
                videoCells = null;
            }
            if (pictureDrawn) TrimPictures();
            else if (feeds.Count > 0 || pictureLevel.Length > 0) { ReleasePictures(); large = true; }
            if (!videoDrawn && !pictureDrawn)
            {
                if (poseGlyph != null || figureSource != null) { ReleaseFigure(); large = true; }
                if (shapeCache.Count > 0) shapeCache.Clear();
            }
            // What went is mostly large arrays: a compacting collection, once, lets the runtime
            // hand that memory back to Windows instead of keeping it for later.
            if (large)
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            }
        }

        private void RenderScene(int scene, double t)
        {
            if (scene == VideoScene) SceneVideo(t);
            else if (scene == PictureScene) ScenePicture(t);
        }

        // VIDEO LOOP uses a longer ramp and two brighter crimson tones for clearer shading.
        private const string VideoRamp = ".,:;irsXA253hMHGS#9B&@";
        private static readonly int[] VideoTones = { 3, 0, 4, 1, 5, 2, 6, 7 };

        private static void VideoGlyph(float level, out char glyph, out int tone)
        {
            glyph = VideoRamp[Math.Min(VideoRamp.Length - 1, (int)(level * VideoRamp.Length))];
            tone = VideoTones[Math.Min(VideoTones.Length - 1, (int)(level * VideoTones.Length))];
        }

        // ---- VIDEO LOOP: a local video chosen by the user (right-click the logo),
        // played muted and looping, its brightness drawn as ASCII and scaled to cover
        // the page. Only the file path is stored; the video is never copied.
        private static VideoFeed video;
        private static bool videoInvert;
        private readonly float[] videoCurve = new float[256];

        // Six sampling regions in cell units (2 columns x 3 rows, left column lowered and
        // right raised), each sampled at three points across its width.
        private static readonly float[] ShapeX = { 0.28F, 0.72F, 0.28F, 0.72F, 0.28F, 0.72F };
        private static readonly float[] ShapeY = { 0.25F, 0.19F, 0.53F, 0.47F, 0.81F, 0.75F };
        private static readonly float[] ShapeDX = { -1F, 0F, 1F };
        private const float ShapeRX = 0.13F, ShapeRegionX = 0.22F, ShapeRegionY = 0.15F;
        private const string ShapeChars = "!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";
        private float[][] shapeVectors;
        private int shapeCellW, shapeCellH;
        private readonly Dictionary<int, char> shapeCache = new Dictionary<int, char>();
        private char[] shapeGlyph = new char[0];

        // Video sample positions for every column (6 regions x 3 points) and row (6 regions),
        // rebuilt only when the grid or the video size changes: the frame loop only looks up.
        private int[] sampleCol = new int[0], sampleRow = new int[0];
        private long sampleKey;

        private void SampleTables(int w, int h, float scale, float halfW, float halfH)
        {
            long key = ((long)cols << 48) ^ ((long)rows << 32) ^ ((long)w << 16) ^ h ^ ((long)width << 40) ^ ((long)height << 24);
            if (key == sampleKey && sampleCol.Length == cols * 18) return;
            sampleKey = key;
            sampleCol = new int[cols * 18];
            sampleRow = new int[rows * 6];
            for (int c = 0; c < cols; c++)
                for (int k = 0; k < 6; k++)
                    for (int j = 0; j < 3; j++)
                    {
                        float px = c * cellW + (ShapeX[k] + ShapeDX[j] * ShapeRX) * cellW;
                        int vx = (int)((px - width / 2F) / scale + halfW);
                        sampleCol[c * 18 + k * 3 + j] = vx < 0 || vx >= w ? -1 : vx;
                    }
            for (int r = 0; r < rows; r++)
                for (int k = 0; k < 6; k++)
                {
                    float py = r * cellH + ShapeY[k] * cellH;
                    int vy = (int)((py - height / 2F) / scale + halfH);
                    sampleRow[r * 6 + k] = vy < 0 || vy >= h ? -1 : vy * w;
                }
        }

        // Ink coverage of every glyph in the six regions, from the same masks used to draw,
        // each component normalised by its maximum over all glyphs.
        private void EnsureShapes()
        {
            if (shapeGlyph.Length != cols * rows) shapeGlyph = new char[cols * rows];
            if (shapeVectors != null && shapeCellW == cellW && shapeCellH == cellH) return;
            shapeCellW = cellW; shapeCellH = cellH;
            shapeCache.Clear();
            shapeVectors = ShapeVectors(Mask, cellW, cellH);
        }

        private static float[][] ShapeVectors(Func<char, byte[]> maskOf, int cellW, int cellH, string chars = ShapeChars)
        {
            var vectors = new float[chars.Length][];
            var max = new float[6];
            for (int g = 0; g < chars.Length; g++)
            {
                byte[] mask = maskOf(chars[g]);
                var v = new float[6];
                for (int k = 0; k < 6; k++)
                {
                    float sum = 0; int n = 0;
                    for (int y = 0; y < cellH; y++)
                        for (int x = 0; x < cellW; x++)
                        {
                            float dx = ((x + 0.5F) / cellW - ShapeX[k]) / ShapeRegionX, dy = ((y + 0.5F) / cellH - ShapeY[k]) / ShapeRegionY;
                            if (dx * dx + dy * dy > 1) continue;
                            sum += mask[y * cellW + x] / 255F; n++;
                        }
                    // Tiny cells may have no pixel inside a region: use the nearest one.
                    if (n == 0)
                    {
                        int x = Math.Min(cellW - 1, (int)(ShapeX[k] * cellW)), y = Math.Min(cellH - 1, (int)(ShapeY[k] * cellH));
                        sum = mask[y * cellW + x] / 255F; n = 1;
                    }
                    v[k] = sum / n;
                    if (v[k] > max[k]) max[k] = v[k];
                }
                vectors[g] = v;
            }
            foreach (var v in vectors) for (int k = 0; k < 6; k++) if (max[k] > 0) v[k] /= max[k];
            return vectors;
        }

        private char MatchShape(float[] vector) { return MatchShape(vector, shapeVectors, shapeCache); }

        // Nearest glyph by Euclidean distance, cached on a 4-bit-per-component key.
        private static char MatchShape(float[] vector, float[][] vectors, Dictionary<int, char> cache, string chars = ShapeChars)
        {
            int key = 0;
            for (int k = 0; k < 6; k++) key = (key << 4) | Math.Min(15, (int)(vector[k] * 15.99F));
            char cached;
            if (cache.TryGetValue(key, out cached)) return cached;
            int best = 0; float bestDistance = float.MaxValue;
            for (int g = 0; g < vectors.Length; g++)
            {
                float d = 0;
                for (int k = 0; k < 6; k++) { float e = vectors[g][k] - vector[k]; d += e * e; }
                if (d < bestDistance) { bestDistance = d; best = g; }
            }
            if (cache.Count < 200000) cache[key] = chars[best];
            return chars[best];
        }
        private short[] videoCells;
        private static string videoPath;

        public static string VideoPath
        {
            get { return videoPath; }
            set
            {
                if (String.Equals(value, videoPath, StringComparison.OrdinalIgnoreCase)) return;
                videoPath = value;
                if (video != null) { video.Dispose(); video = null; }
            }
        }

        public static string VideoError { get { return video != null ? video.Error : null; } }

        private void SceneVideo(double t)
        {
            if (String.IsNullOrEmpty(videoPath)) return;
            if (video == null) video = new VideoFeed(videoPath);
            byte[] luma; int w, h;
            if (!video.TryGetFrame(out luma, out w, out h) || w == 0 || h == 0) return;
            // The still character, once found, is drawn as its own layer in front.
            var figure = video.Figure;
            figureShown = figure != null;
            if (figureShown) { EnsureFigureLayer(figure); PoseFigure(t); }
            float scale = Math.Max(width / (float)w, height / (float)h), halfW = w / 2F, halfH = h / 2F;
            // First pass: the luma under each cell, and the frame's range.
            if (videoCells == null || videoCells.Length != cols * rows) videoCells = new short[cols * rows];
            var histogram = new int[256];
            long sum = 0; int count = 0;
            for (int r = 0; r < rows; r++)
            {
                int vy = (int)((r * cellH + cellH / 2F - height / 2F) / scale + halfH);
                for (int c = 0; c < cols; c++)
                {
                    int vx = (int)((c * cellW + cellW / 2F - width / 2F) / scale + halfW);
                    int value = vy < 0 || vy >= h || vx < 0 || vx >= w ? -1 : luma[vy * w + vx];
                    videoCells[r * cols + c] = (short)value;
                    if (value < 0) continue;
                    sum += value; count++;
                    histogram[value]++;
                }
            }
            if (count == 0) return;
            // Mostly bright frames are inverted so the page stays dark and the drawing lines glow
            // (with hysteresis, so the choice does not flicker between frames).
            float mean = sum / (float)count / 255F;
            if (mean > 0.55F) videoInvert = true;
            else if (mean < 0.45F) videoInvert = false;
            // Stretch between the 2nd and 98th percentiles, then a gamma that lifts thin lines,
            // as a lookup from luma to brightness.
            int low = 0, high = 255, seen = 0;
            for (int v = 0; v < 256; v++) { seen += histogram[v]; if (seen >= count * 0.02) { low = v; break; } }
            seen = 0;
            for (int v = 255; v >= 0; v--) { seen += histogram[v]; if (seen >= count * 0.02) { high = v; break; } }
            float range = Math.Max(24, high - low);
            for (int v = 0; v < 256; v++)
            {
                float level = Math.Max(0F, Math.Min(1F, (v - low) / range));
                if (videoInvert) level = 1 - level;
                videoCurve[v] = Math.Max(0F, Math.Min(1F, ((float)Math.Pow(level, 0.75) - 0.17F) * 1.2F));
            }
            EnsureShapes();
            // Shape-aware glyphs (after Alex Harri, "ASCII characters are not pixels"): each
            // cell is sampled in six regions (2 x 3), contrast-enhanced, and the character
            // whose ink layout is closest is chosen, so edges keep their direction.
            SampleTables(w, h, scale, halfW, halfH);
            var vector = new float[6];
            for (int r = 0; r < rows; r++)
            {
                int rowBase = r * 6;
                for (int c = 0; c < cols; c++)
                {
                    if (figureShown && figureCovers[r * cols + c]) continue;
                    int colBase = c * 18;
                    float peak = 0, total = 0;
                    bool inside = true;
                    for (int k = 0; k < 6; k++)
                    {
                        int line = sampleRow[rowBase + k];
                        if (line < 0) { inside = false; break; }
                        float s = 0;
                        for (int j = 0; j < 3; j++)
                        {
                            int x = sampleCol[colBase + k * 3 + j];
                            if (x < 0) { inside = false; break; }
                            s += videoCurve[luma[line + x]];
                        }
                        if (!inside) break;
                        vector[k] = s / 3;
                        if (vector[k] > peak) peak = vector[k];
                        total += vector[k];
                    }
                    if (!inside || peak < 0.1F) continue;
                    // Global contrast: (v / max)^2 * max sharpens where the ink sits.
                    for (int k = 0; k < 6; k++) { float n = vector[k] / peak; vector[k] = n * n * peak; }
                    int i = r * cols + c;
                    light[i] = Math.Min(1F, total / 6 * 1.2F);
                    shapeGlyph[i] = MatchShape(vector);
                }
            }
        }
    }
}
