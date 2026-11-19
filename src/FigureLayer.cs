using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Arabara
{
    // VIDEO LOOP and PICTURE as two layers: the background behind, and in front the still
    // character cut out of it (VideoFeed.ExtractFigure, or GrabCut for a picture), drawn in
    // much smaller characters for more detail and in its own tones (not inverted). The
    // character breathes through a cycle of poses whose glyphs are chosen once per window
    // size; each frame only blits one. A standing character (taller than wide) breathes
    // from the feet up; a reclining one (wider than tall) breathes in the chest, lifts its
    // head, rocks its knees and lets its loose hair sway; and if eyes were found it blinks
    // now and then (those few cells are redrawn while the lids move).
    internal sealed partial class SpinningMark
    {

        // Darkest to brightest, ending in the highlight, for more contrast than the background.
        private static readonly int[] FigureTones = { 1, 2, 6, 7, 8, 8 };
        private Font figureFont;
        private int figureCellW, figureCellH;
        private readonly Dictionary<char, byte[]> figureMasks = new Dictionary<char, byte[]>();
        private float[][] figureVectors;
        private readonly Dictionary<int, char> figureCache = new Dictionary<int, char>();
        private VideoFigure figureSource;
        private long figureKey;
        // The character breathes: one cycle of FigurePhases poses, each built the first time
        // it is shown and then reused. A pose is a fixed grid of small cells over the
        // character's box (plus a margin for the motion): glyph per cell and level byte
        // (0 outside the character, 1 dark cell that only hides the background, else
        // brightness), and which background cells it covers.
        private const int FigurePhases = 40;
        private const double BreathSeconds = 3.6;
        private int gridC0, gridR0, gridCols, gridRows;
        private char[][] poseGlyph;
        private byte[][] poseLevel;
        private bool[][] poseCovers;
        private float[] figureCurve;
        private int figurePhase;
        private bool[] figureCovers = new bool[0]; // background cells behind the character (current pose)
        private bool figureShown;
        private float[] figureSway;
        private bool figureNatural;  // drawn under the picture's natural light, like the background
        private byte[] figureLuma;
        private PictureMotion figureMotion;
        private bool figureBlocks; // drawn in the scene's fine grid of shade blocks
        private PictureEye[] figureEyes = new PictureEye[0];
        private bool figureReclining;
        private double figureTheta, blinkStart = -10, blinkNext = -1;
        private float figureBlink;
        internal float BlinkOverride = -1; // --home-test: a fixed lid closure
        private readonly Random blinkRandom = new Random();

        private float FigureScale { get { return SceneScale(figureSource.Fit, figureSource.Width, figureSource.Height); } }

        // A video covers the page; a picture a little less (88% of that), centred, so a
        // little more of it shows, its edges fading softly into the page.
        private const float PictureZoom = 0.88F;
        private float SceneScale(bool fit, int w, int h)
        {
            return Math.Max(width / (float)w, height / (float)h) * (fit ? PictureZoom : 1);
        }

        // 0 at a picture's edge, rising to 1 about 5% of its height inside (1 for a video).
        private static float EdgeFade(bool fit, float x, float y, int w, int h)
        {
            if (!fit) return 1;
            float d = Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y)), margin = Math.Max(4F, 0.05F * Math.Min(w, h));
            return Smooth(0, margin, d);
        }

        private void EnsureFigureLayer(VideoFigure figure, float[] sway = null, PictureEye[] eyes = null, PictureMotion motion = null)
        {
            long key = ((long)width << 48) ^ ((long)height << 32) ^ ((long)cols << 16) ^ rows ^ (long)(fontScale * 1000) << 40 ^ ((long)cellW << 8) ^ (blockCells ? 1L << 62 : 0)
                ^ ((long)(figurePoints * 10) << 20) ^ (naturalLight ? 1L << 61 : 0);
            if (figure == figureSource && key == figureKey && figureFont != null && motion == figureMotion) return;
            figureSource = figure;
            figureSway = sway;
            figureEyes = eyes ?? new PictureEye[0];
            figureMotion = motion;
            figureReclining = figure.Bounds.Width > figure.Bounds.Height * 1.25F;
            figureKey = key;
            if (blockCells)
            {
                // The fine grid: the character uses the scene's cells and shade blocks.
                if (!figureBlocks || figureCellW != cellW || figureCellH != cellH) { figureMasks.Clear(); figureCache.Clear(); }
                figureBlocks = true;
                figureCellW = cellW; figureCellH = cellH;
                if (figureFont == null) figureFont = UiFonts.Body(figurePoints * fontScale);
            }
            else if (figureBlocks || figureFont == null || figureFont.Size != figurePoints * fontScale)
            {
                figureBlocks = false;
                if (figureFont != null) figureFont.Dispose();
                figureFont = UiFonts.Body(figurePoints * fontScale);
                using (var probe = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(probe))
                {
                    figureCellW = Math.Max(2, (int)Math.Ceiling(g.MeasureString("M", figureFont, PointF.Empty, StringFormat.GenericTypographic).Width));
                    figureCellH = Math.Max(4, (int)Math.Ceiling(figureFont.GetHeight(g)));
                }
                figureMasks.Clear();
                figureCache.Clear();
                figureVectors = ShapeVectors(FigureMask, figureCellW, figureCellH);
            }
            int W = figure.Width, H = figure.Height;
            byte[] luma = figure.Luma, mask = figure.Mask;
            float scale = FigureScale;
            figureNatural = naturalLight;
            figureLuma = naturalLight && figure.Plain != null ? figure.Plain : figure.Luma;
            // The character's own tones: stretched between its 2nd and 98th percentiles.
            var histogram = new int[256];
            int count = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i] != 0) { histogram[luma[i]]++; count++; }
            int low = 0, high = 255, seen = 0;
            for (int v = 0; v < 256; v++) { seen += histogram[v]; if (seen >= count * 0.02) { low = v; break; } }
            seen = 0;
            for (int v = 255; v >= 0; v--) { seen += histogram[v]; if (seen >= count * 0.02) { high = v; break; } }
            float range = Math.Max(24, high - low);
            figureCurve = new float[256];
            for (int v = 0; v < 256; v++)
            {
                float level = Math.Max(0F, Math.Min(1F, (v - low) / range));
                // Mostly light artwork: a gamma slightly above 1 spreads the light tones, so shading
                // and grey line art inside the white areas stay visible.
                figureCurve[v] = Math.Max(0F, Math.Min(1F, ((float)Math.Pow(level, figure.Gamma) - 0.04F) * 1.25F));
            }
            // Small cells over the character's box and a 12 source pixel margin.
            const int Margin = 12;
            int gx0 = (int)((figure.Bounds.Left - Margin - W / 2F) * scale + width / 2F), gx1 = (int)((figure.Bounds.Right + Margin - W / 2F) * scale + width / 2F);
            int gy0 = (int)((figure.Bounds.Top - Margin - H / 2F) * scale + height / 2F), gy1 = (int)((figure.Bounds.Bottom + Margin - H / 2F) * scale + height / 2F);
            gridC0 = Math.Max(0, gx0 / figureCellW);
            gridR0 = Math.Max(0, gy0 / figureCellH);
            gridCols = Math.Max(0, Math.Min(cols * cellW / figureCellW - 1, gx1 / figureCellW) - gridC0 + 1);
            gridRows = Math.Max(0, Math.Min(rows * cellH / figureCellH - 1, gy1 / figureCellH) - gridR0 + 1);
            poseGlyph = new char[FigurePhases][];
            poseLevel = new byte[FigurePhases][];
            poseCovers = new bool[FigurePhases][];
        }

        // How far the artwork at source point (x, y) moves in a pose, in source pixels. The
        // feet stay put; everything above bobs with the breath (a gentle squash towards the
        // feet), the chest swells, shoulders, arms and hands lift a moment later, the hips
        // sway and the skirt follows them with a lag, flaring a little.
        private void Displace(float x, float y, double theta, out float dx, out float dy)
        {
            if (figureMotion != null && figureMotion.Anchored) { DisplaceAnchored(x, y, theta, out dx, out dy); return; }
            if (figureReclining) { DisplaceReclining(x, y, theta, out dx, out dy); return; }
            var b = figureSource.Bounds;
            float u = (x - (b.Left + b.Width / 2F)) / b.Width, v = (y - b.Top) / (float)b.Height;
            float breath = (float)Math.Sin(theta);
            dx = 0;
            dy = -5F * breath * Math.Max(0F, 1 - v);
            float chest = (float)Math.Exp(-(u * u / 0.04F + (v - 0.47F) * (v - 0.47F) / 0.0081F));
            dx += 2.5F * breath * chest * Math.Max(-1F, Math.Min(1F, u / 0.2F));
            dy -= 1.5F * breath * chest;
            float arms = Smooth(0.12F, 0.3F, Math.Abs(u)) * (1 - Smooth(0.42F, 0.55F, v));
            dy -= 3F * (float)Math.Sin(theta - 0.5) * arms;
            float hips = (float)Math.Exp(-(v - 0.7F) * (v - 0.7F) / 0.0064F);
            dx += 3F * (float)Math.Cos(theta) * hips;
            float skirt = Smooth(0.7F, 0.76F, v) * (1 - Smooth(0.86F, 0.92F, v));
            dx += 4.5F * (float)Math.Cos(theta - 0.7) * skirt * (0.5F + (v - 0.7F) * 5F);
            dx += 3F * (float)Math.Sin(theta - 0.4) * skirt * Math.Max(-1F, Math.Min(1F, u / 0.15F));
            dy -= 1.5F * (float)Math.Sin(2 * theta) * skirt;
            dx *= 1 - Smooth(0.86F, 1F, v);
        }

        // A character lying down (laid out after the reclining figure it was made for, in
        // the character's box): the chest rises and widens with each breath, the head lifts
        // a moment later and tips a little, the knees rock, and loose strands of hair (see
        // PictureFeed.Loose) sway after the breath as a wave running down them.
        private void DisplaceReclining(float x, float y, double theta, out float dx, out float dy)
        {
            var b = figureSource.Bounds;
            float u = (x - b.Left) / b.Width, v = (y - b.Top) / (float)b.Height;
            float breath = (float)Math.Sin(theta), late = (float)Math.Sin(theta - 0.35), rock = (float)Math.Sin(theta - 1.8);
            float cu = u - 0.53F, cv = v - 0.7F;
            float chest = (float)Math.Exp(-(cu * cu / 0.02F + cv * cv / 0.0288F));
            dx = 1.4F * breath * chest * Math.Max(-1F, Math.Min(1F, cu / 0.1F));
            dy = -3F * breath * chest;
            float hu = u - 0.36F, hv = v - 0.4F;
            float head = (float)Math.Exp(-(hu * hu / 0.08F + hv * hv / 0.125F));
            dy -= 2.2F * late * head;
            dx += 0.8F * late * head;
            float ku = u - 0.72F, kv = v - 0.45F;
            float knees = (float)Math.Exp(-(ku * ku + kv * kv) / 0.0288F);
            dx += 1.2F * rock * knees;
            dy -= 0.6F * rock * knees;
            int ix = (int)x, iy = (int)y;
            if (figureSway != null && ix >= 0 && iy >= 0 && ix < figureSource.Width && iy < figureSource.Height)
            {
                float sway = figureSway[iy * figureSource.Width + ix];
                if (sway > 0.01F)
                {
                    double along = 0.03 * (y - b.Top);
                    dx += 3F * sway * (float)Math.Sin(theta - 1.1 - along);
                    dy += 0.9F * sway * (float)Math.Sin(2 * theta - 0.6 - along);
                }
            }
        }

        // A picture's own layout (PictureMotion): soft round areas around its points, sized
        // by the character's box. The chest rises and widens a little with each breath, the
        // head lifts a moment later and tips, the knees rock; loose hair sways as usual.
        private void DisplaceAnchored(float x, float y, double theta, out float dx, out float dy)
        {
            var b = figureSource.Bounds;
            var m = figureMotion;
            float size = (float)Math.Sqrt(b.Width * (float)b.Width + b.Height * (float)b.Height);
            float breath = (float)Math.Sin(theta), late = (float)Math.Sin(theta - 0.35), rock = (float)Math.Sin(theta - 1.8);
            dx = 0; dy = 0;
            if (m.HasChest)
            {
                float sigma = 0.11F * size, ox = x - m.ChestX, oy = y - m.ChestY;
                float chest = (float)Math.Exp(-(ox * ox + oy * oy) / (2 * sigma * sigma));
                dy -= 2.4F * breath * chest;
                dx += 1.1F * breath * chest * Math.Max(-1F, Math.Min(1F, ox / sigma));
            }
            if (m.HasHead)
            {
                float sigma = 0.15F * size, ox = x - m.HeadX, oy = y - m.HeadY;
                float head = (float)Math.Exp(-(ox * ox + oy * oy) / (2 * sigma * sigma));
                dy -= 2F * late * head;
                dx += 0.7F * late * head;
            }
            if (m.HasKnees)
            {
                float sigma = 0.12F * size, ox = x - m.KneesX, oy = y - m.KneesY;
                float knees = (float)Math.Exp(-(ox * ox + oy * oy) / (2 * sigma * sigma));
                dx += 1.2F * rock * knees;
                dy -= 0.6F * rock * knees;
            }
            int ix = (int)x, iy = (int)y;
            if (figureSway != null && ix >= 0 && iy >= 0 && ix < figureSource.Width && iy < figureSource.Height)
            {
                float sway = figureSway[iy * figureSource.Width + ix];
                if (sway > 0.01F)
                {
                    double along = 0.03 * (y - b.Top);
                    dx += 3F * sway * (float)Math.Sin(theta - 1.1 - along);
                    dy += 0.9F * sway * (float)Math.Sin(2 * theta - 0.6 - along);
                }
            }
        }

        // A blink: the upper lid comes down under the lash line and covers the eye. The lash
        // line itself stays where it is (so nothing of it is left behind as a dark streak);
        // from just under it down to the lower lid (t = depth, rounder towards the corners)
        // the eye is covered with skin, taken from the clearest band just below the eye, as
        // far as the lid has come. Shut, only the lash line remains, like closed anime eyes.
        // Closure 0 is open, 1 shut.
        private void Blink(ref float x, ref float y, float closure)
        {
            foreach (var eye in figureEyes)
            {
                float rx = x - eye.X, ry = y - eye.Y;
                float s = rx * eye.AlongX + ry * eye.AlongY, t = rx * eye.CloseX + ry * eye.CloseY;
                float k = s / eye.HalfLength;
                if (k * k >= 1) continue;
                float bottom = eye.Depth * (float)Math.Sqrt(1 - k * k) + 2.5F, top = eye.Lash;
                if (t < top || t > bottom) continue;
                if (t >= top + closure * (bottom - top)) return;
                float skin = float.IsNaN(eye.SkinAt) ? eye.Depth + 4 : eye.SkinAt;
                float from = skin + (t - top) * 0.15F;
                x = eye.X + s * eye.AlongX + from * eye.CloseX;
                y = eye.Y + s * eye.AlongY + from * eye.CloseY;
                return;
            }
        }

        private static float Smooth(float from, float to, float value)
        {
            float k = Math.Max(0F, Math.Min(1F, (value - from) / (to - from)));
            return k * k * (3 - 2 * k);
        }

        // Builds one pose: each small cell samples the still artwork where its content came
        // from (its position minus the displacement there).
        private void EnsurePose(int phase)
        {
            if (poseGlyph[phase] != null) return;
            var figure = figureSource;
            int W = figure.Width, H = figure.Height;
            byte[] mask = figure.Mask;
            float scale = FigureScale;
            double theta = 2 * Math.PI * phase / FigurePhases;
            var glyphs = new char[gridCols * gridRows];
            var levels = new byte[gridCols * gridRows];
            var vector = new float[6];
            for (int r = 0; r < gridRows; r++)
                for (int c = 0; c < gridCols; c++)
                {
                    int i = r * gridCols + c;
                    SampleFigureCell((gridC0 + c) * figureCellW, (gridR0 + r) * figureCellH, theta, 0, vector, out glyphs[i], out levels[i]);
                }
            // Background cells whose centre falls on the character in this pose are left empty.
            var covers = new bool[cols * rows];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    float sx = (c * cellW + cellW / 2F - width / 2F) / scale + W / 2F, sy = (r * cellH + cellH / 2F - height / 2F) / scale + H / 2F;
                    if (sx < figure.Bounds.Left - 16 || sx > figure.Bounds.Right + 16 || sy < figure.Bounds.Top - 16) continue;
                    float dx, dy;
                    Displace(sx, sy, theta, out dx, out dy);
                    int vx = (int)(sx - dx), vy = (int)(sy - dy);
                    covers[r * cols + c] = vx >= 0 && vy >= 0 && vx < W && vy < H && mask[vy * W + vx] != 0;
                }
            poseGlyph[phase] = glyphs;
            poseLevel[phase] = levels;
            poseCovers[phase] = covers;
        }

        // One small cell at screen position (x0, y0) in a pose: level 0 off the character,
        // 1 for a dark cell that only hides the background, else its brightness and glyph.
        private void SampleFigureCell(float x0, float y0, double theta, float blink, float[] vector, out char glyph, out byte level)
        {
            var figure = figureSource;
            int W = figure.Width, H = figure.Height;
            byte[] luma = figureLuma ?? figure.Luma, mask = figure.Mask;
            float[] curve = figureNatural ? BlockCurve(luma) : figureCurve;
            float scale = FigureScale;
            glyph = '\0'; level = 0;
            float sx = (x0 + figureCellW / 2F - width / 2F) / scale + W / 2F, sy = (y0 + figureCellH / 2F - height / 2F) / scale + H / 2F;
            float dx, dy;
            Displace(sx, sy, theta, out dx, out dy);
            float px = sx - dx, py = sy - dy;
            if (blink > 0) Blink(ref px, ref py, blink);
            int cx = (int)px, cy = (int)py;
            if (cx < 0 || cy < 0 || cx >= W || cy >= H || mask[cy * W + cx] == 0) return;
            if (figureBlocks)
            {
                // Shade blocks: the level of four points in the cell, under the same light as
                // the background (BlockCurve of the plain picture).
                byte[] plain = figure.Plain ?? luma;
                float[] shades = BlockCurve(plain);
                float sum = 0;
                for (int q = 0; q < 4; q++)
                {
                    float fx = (x0 + (q % 2 == 0 ? 0.25F : 0.75F) * figureCellW - width / 2F) / scale + W / 2F - dx;
                    float fy = (y0 + (q < 2 ? 0.25F : 0.75F) * figureCellH - height / 2F) / scale + H / 2F - dy;
                    if (blink > 0) Blink(ref fx, ref fy, blink);
                    int vx = (int)fx, vy = (int)fy;
                    if (vx < 0 || vy < 0 || vx >= W || vy >= H) continue;
                    int at = vy * W + vx;
                    if (mask[at] != 0) sum += shades[plain[at]];
                }
                float shade = sum / 4 * EdgeFade(figure.Fit, px, py, W, H);
                level = 1;
                if (shade < 0.03F) return;
                level = (byte)Math.Max(2, Math.Min(255, (int)(shade * 255)));
                glyph = BlockGlyph(shade);
                return;
            }
            float peak = 0, total = 0;
            for (int k = 0; k < 6; k++)
            {
                // 3 x 3 points across the region, so thin outlines are not missed.
                float s = 0;
                for (int bb = -1; bb <= 1; bb++)
                {
                    float fy = (y0 + (ShapeY[k] + bb * 0.66F * ShapeRegionY) * figureCellH - height / 2F) / scale + H / 2F - dy;
                    for (int a = -1; a <= 1; a++)
                    {
                        float fx = (x0 + (ShapeX[k] + a * 0.66F * ShapeRegionX) * figureCellW - width / 2F) / scale + W / 2F - dx, gy = fy;
                        if (blink > 0) Blink(ref fx, ref gy, blink);
                        int vx = (int)fx, vy = (int)gy;
                        if (vx < 0 || vy < 0 || vx >= W || vy >= H) continue;
                        int at = vy * W + vx;
                        if (mask[at] != 0) s += curve[luma[at]];
                    }
                }
                vector[k] = s / 9;
                if (vector[k] > peak) peak = vector[k];
                total += vector[k];
            }
            level = 1;
            float edge = EdgeFade(figure.Fit, px, py, W, H);
            if (peak * edge < (figureNatural ? 0.06F : 0.1F)) return;
            for (int k = 0; k < 6; k++) { float n = vector[k] / peak; vector[k] = n * n * peak; }
            level = (byte)Math.Max(2, Math.Min(255, (int)(total / 6 * 255 * edge)));
            glyph = MatchShape(vector, figureVectors, figureCache);
        }

        // Picks the pose for time t (seconds) and makes it current, and how far the lids are
        // closed: a blink every 2.5 to 6 seconds (now and then two in a row), closing in
        // 0.07 s, shut for 0.05 s and opening in 0.11 s.
        private void PoseFigure(double t)
        {
            double cycle = t / BreathSeconds;
            figurePhase = (int)((cycle - Math.Floor(cycle)) * FigurePhases) % FigurePhases;
            figureTheta = 2 * Math.PI * figurePhase / FigurePhases;
            EnsurePose(figurePhase);
            figureCovers = poseCovers[figurePhase];
            figureBlink = 0;
            if (figureEyes.Length == 0) return;
            if (blinkNext < 0 || t < blinkStart - 1) { blinkNext = t + 1.5 + blinkRandom.NextDouble() * 2; blinkStart = -10; }
            if (t >= blinkNext)
            {
                blinkStart = blinkNext;
                blinkNext = blinkRandom.NextDouble() < 0.15 ? blinkStart + 0.32 : blinkStart + 2.5 + blinkRandom.NextDouble() * 3.5;
            }
            double e = t - blinkStart;
            float closure = e < 0 ? 0 : e < 0.07 ? (float)(e / 0.07) : e < 0.12 ? 1 : e < 0.23 ? 1 - (float)((e - 0.12) / 0.11) : 0;
            figureBlink = closure * closure * (3 - 2 * closure);
            if (BlinkOverride >= 0) figureBlink = BlinkOverride;
        }

        // Clears each small cell of the current pose (hiding the background behind the
        // character) and draws its glyph; then, during a blink, redraws the cells around the
        // eyes with the lids where they are now.
        private void DrawFigure(float fade, IList<Rectangle> panes, float veil)
        {
            int fill = canvas.ToArgb();
            char[] glyphs = poseGlyph[figurePhase];
            byte[] levels = poseLevel[figurePhase];
            if (figureBlocks) DrawFigureBlocks(levels, fade, fill);
            else
            for (int r = 0; r < gridRows; r++)
                for (int c = 0; c < gridCols; c++)
                {
                    int i = r * gridCols + c;
                    if (levels[i] != 0) DrawFigureCell(c, r, glyphs[i], levels[i], fade, panes, veil, fill);
                }
            if (figureBlink <= 0) return;
            var vector = new float[6];
            float scale = FigureScale;
            int W = figureSource.Width, H = figureSource.Height;
            foreach (var eye in figureEyes)
            {
                // The eye's box plus the furthest a pose moves it.
                float reach = eye.HalfLength + eye.Depth + 5;
                int c0 = (int)(((eye.X - reach - W / 2F) * scale + width / 2F) / figureCellW) - gridC0, c1 = (int)(((eye.X + reach - W / 2F) * scale + width / 2F) / figureCellW) - gridC0;
                int r0 = (int)(((eye.Y - reach - H / 2F) * scale + height / 2F) / figureCellH) - gridR0, r1 = (int)(((eye.Y + reach - H / 2F) * scale + height / 2F) / figureCellH) - gridR0;
                for (int r = Math.Max(0, r0); r <= Math.Min(gridRows - 1, r1); r++)
                    for (int c = Math.Max(0, c0); c <= Math.Min(gridCols - 1, c1); c++)
                    {
                        char glyph; byte level;
                        SampleFigureCell((gridC0 + c) * figureCellW, (gridR0 + r) * figureCellH, figureTheta, figureBlink, vector, out glyph, out level);
                        if (level != 0) DrawFigureCell(c, r, glyph, level, fade, panes, veil, fill);
                    }
            }
        }

        // The character in a fine block grid: its cells are the scene's cells, so the panes'
        // dimming worked out for the background (MarkDimmed) applies cell for cell.
        private void DrawFigureBlocks(byte[] levels, float fade, int fill)
        {
            for (int r = 0; r < gridRows; r++)
            {
                int row = gridR0 + r, y0 = originY + row * cellH;
                if (row >= rows || y0 >= height) break;
                if (y0 < 0) continue;
                int h = Math.Min(cellH, height - y0);
                for (int c = 0; c < gridCols; c++)
                {
                    int i = r * gridCols + c;
                    byte value = levels[i];
                    if (value == 0) continue;
                    int col = gridC0 + c, x0 = originX + col * cellW;
                    if (col >= cols || x0 >= width) break;
                    if (x0 < 0) continue;
                    float shade = value / 255F * fade;
                    int colour = value == 1 || shade < 0.03F ? fill : FineColour(shade, row * cols + col);
                    int w = Math.Min(cellW, width - x0);
                    for (int y = 0; y < h; y++)
                    {
                        int line = (y0 + y) * width + x0;
                        for (int x = 0; x < w; x++) pixels[line + x] = colour;
                    }
                }
            }
        }

        private void DrawFigureCell(int c, int r, char glyph, byte levelByte, float fade, IList<Rectangle> panes, float veil, int fill)
        {
            int x0 = originX + (gridC0 + c) * figureCellW, y0 = originY + (gridR0 + r) * figureCellH;
            if (x0 < 0 || y0 < 0 || x0 >= width || y0 >= height) return;
            int w = Math.Min(figureCellW, width - x0), h = Math.Min(figureCellH, height - y0);
            if (figureBlocks && glyph == '\u2588' && levelByte != 1)
            {
                // A full block in its shade, in one pass (it covers the background anyway).
                float shade = levelByte / 255F * fade;
                int colour = fill;
                if (shade >= 0.03F)
                {
                    int fine = FineTone(shade, glyph);
                    int cx = x0 + figureCellW / 2, cy = y0 + figureCellH / 2;
                    int cc = Math.Min(cols - 1, Math.Max(0, (cx - originX) / cellW)), cr = Math.Min(rows - 1, Math.Max(0, (cy - originY) / cellH));
                    if (veil <= 0 || CellThreshold(cc, cr) >= veil * (1 + VeilFront))
                        foreach (var pane in panes) if (pane.Contains(cx, cy)) { fine += FineCount; break; }
                    colour = ramps[fine][255];
                }
                FillCell(x0, y0, w, h, colour);
                return;
            }
            for (int y = 0; y < h; y++)
            {
                int row = (y0 + y) * width + x0;
                for (int x = 0; x < w; x++) pixels[row + x] = fill;
            }
            float level = levelByte / 255F * fade;
            if (levelByte == 1 || level < (figureBlocks ? 0.03F : 0.05F)) return;
            int tone = figureBlocks ? FineTone(level, glyph) : figureNatural ? FineLevelTone(level) : FigureTones[Math.Min(FigureTones.Length - 1, (int)(level * FigureTones.Length))];
            int px = x0 + figureCellW / 2, py = y0 + figureCellH / 2;
            int col = Math.Min(cols - 1, Math.Max(0, (px - originX) / cellW)), row0 = Math.Min(rows - 1, Math.Max(0, (py - originY) / cellH));
            if (veil <= 0 || CellThreshold(col, row0) >= veil * (1 + VeilFront))
                foreach (var pane in panes) if (pane.Contains(px, py)) { tone += tone >= FineBase ? FineCount : Shades.Length; break; }
            byte[] mask = FigureMask(glyph);
            int[] ramp = ramps[tone];
            for (int y = 0; y < h; y++)
            {
                int row = (y0 + y) * width + x0, maskRow = y * figureCellW;
                for (int x = 0; x < w; x++)
                {
                    int alpha = mask[maskRow + x];
                    if (alpha != 0) pixels[row + x] = ramp[alpha];
                }
            }
        }

        private byte[] FigureMask(char glyph)
        {
            byte[] mask;
            if (figureMasks.TryGetValue(glyph, out mask)) return mask;
            mask = figureBlocks ? BlockMask(glyph, figureCellW, figureCellH) : RenderMask(figureFont, figureCellW, figureCellH, glyph);
            figureMasks[glyph] = mask;
            return mask;
        }

        private static byte[] RenderMask(Font font, int cellW, int cellH, char glyph)
        {
            var mask = new byte[cellW * cellH];
            using (var cell = new Bitmap(cellW, cellH, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(cell))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(glyph.ToString(), font, Brushes.White, 0, 0, StringFormat.GenericTypographic);
                }
                for (int y = 0; y < cellH; y++)
                    for (int x = 0; x < cellW; x++) mask[y * cellW + x] = cell.GetPixel(x, y).R;
            }
            return mask;
        }

        // --home-test: the current pose's cell brightness as an image (one pixel per cell).
        internal void DumpFigure(string file)
        {
            if (poseLevel == null || poseLevel[figurePhase] == null || gridCols == 0) return;
            using (var image = new Bitmap(gridCols, gridRows))
            {
                byte[] levels = poseLevel[figurePhase];
                for (int i = 0; i < levels.Length; i++) image.SetPixel(i % gridCols, i / gridCols, Color.FromArgb(levels[i], levels[i], levels[i]));
                image.Save(file);
            }
        }

        private void ReleaseFigure()
        {
            if (figureFont != null) figureFont.Dispose();
            figureFont = null;
            figureMasks.Clear();
            figureCache.Clear();
            figureVectors = null;
            figureSource = null;
            figureBlocks = false;
            poseGlyph = null; poseLevel = null; poseCovers = null;
            figureShown = false;
        }
    }
}
