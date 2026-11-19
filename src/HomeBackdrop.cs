using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Arabara
{
    // The Arabara mark as a solid (an extruded triangle with its triangular
    // opening and striped faces), spinning about X, Y and Z and drawn as dim
    // crimson ASCII. Each axis turns at its own constant speed, picked at random
    // per launch, so the motion is even and every axis makes full revolutions.
    internal sealed partial class SpinningMark : IDisposable
    {
        private const string Ramp = ".,:;-=+*#%@";
        // Charcoal crimson, kept well below the text colours: three tones for the
        // mark, three fainter ones for the sea, then the video's brighter tones.
        private static readonly Color[] Shades =
        {
            Color.FromArgb(44, 12, 16), Color.FromArgb(76, 19, 27), Color.FromArgb(118, 27, 39),
            Color.FromArgb(38, 10, 14), Color.FromArgb(58, 15, 21), Color.FromArgb(86, 21, 30),
            Color.FromArgb(158, 34, 50), Color.FromArgb(214, 56, 76),
            Color.FromArgb(255, 150, 164) // highlight, only for the video's character layer
        };
        // Code column text (old lines, lines, values, newest line), then the glitch red
        // and white used where the interface decays.
        private static readonly Color[] TextColors =
        {
            Color.FromArgb(48, 44, 40), Color.FromArgb(82, 75, 67), Color.FromArgb(156, 44, 58), Color.FromArgb(188, 176, 148),
            Color.FromArgb(255, 52, 80), Color.White
        };
        private const string Glitch = "!/\\|<>[]{}#%&$@01?*+=~^;";
        private const float VeilFront = 0.08F;
        private float[] uiThreshold;
        private bool[] uiCells;
        private struct Face { public int A, B, C; public bool Striped; }

        private readonly float[] vx, vy, vz;
        private readonly Face[] faces;
        private readonly double[] speed = new double[3], phase = new double[3];
        private readonly float[] sx, sy, sz, rx, ry;
        private readonly Dictionary<char, byte[]> masks = new Dictionary<char, byte[]>();
        private int[][] ramps;
        private Font font;
        private int[] pixels;
        private float[] depth;
        private float[] light;
        private float[] seaLevel;
        // Each side column keeps its own rhythm: pauses, normal runs and bursts.
        private sealed class CodeColumn
        {
            public readonly List<string> Log = new List<string>();
            public double LastT = -1, Owed, PhaseEnd, Rate, SinceLine, TypeTime = 0.08;
            public int Count;
        }
        private readonly CodeColumn leftCode = new CodeColumn(), rightCode = new CodeColumn();
        private readonly double[] angles = new double[3];
        private readonly Random noise = new Random();
        private int frames, lit;
        private readonly int frameBase = new Random().Next(0x1000, 0x8000);
        private float lastDepth;
        private int width, height, cellW, cellH, cols, rows, originX, originY;
        private Rectangle markArea;
        private float fontScale;
        private Color canvas;

        public SpinningMark()
        {
            var random = new Random();
            for (int i = 0; i < 3; i++)
            {
                double period = 18 + random.NextDouble() * 12; // seconds per revolution
                speed[i] = (random.Next(2) == 0 ? -1 : 1) * 2 * Math.PI / period;
                phase[i] = random.NextDouble() * 2 * Math.PI;
            }
            // Outer triangle (apex up) and the triangular opening, centred on the centroid.
            float[] ox = { 0F, 1.08F, -1.08F }, oy = { -0.78F, 0.62F, 0.62F };
            float[] ix = { 0F, 0.34F, -0.34F }, iy = { 0.04F, 0.47F, 0.47F };
            const float half = 0.2F, centre = 0.15F;
            vx = new float[12]; vy = new float[12]; vz = new float[12];
            for (int i = 0; i < 3; i++)
            {
                Set(i, ox[i], oy[i] - centre, -half); Set(i + 3, ix[i], iy[i] - centre, -half);
                Set(i + 6, ox[i], oy[i] - centre, half); Set(i + 9, ix[i], iy[i] - centre, half);
            }
            var list = new List<Face>();
            for (int e = 0; e < 3; e++)
            {
                int n = (e + 1) % 3;
                foreach (int side in new[] { 0, 6 })
                {
                    list.Add(new Face { A = side + e, B = side + n, C = side + 3 + n, Striped = true });
                    list.Add(new Face { A = side + e, B = side + 3 + n, C = side + 3 + e, Striped = true });
                }
                foreach (int ring in new[] { 0, 3 })
                {
                    list.Add(new Face { A = ring + e, B = ring + n, C = ring + 6 + n });
                    list.Add(new Face { A = ring + e, B = ring + 6 + n, C = ring + 6 + e });
                }
            }
            faces = list.ToArray();
            sx = new float[12]; sy = new float[12]; sz = new float[12]; rx = new float[12]; ry = new float[12];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern int SetDIBitsToDevice(IntPtr hdc, int xDest, int yDest, int width, int height, int xSrc, int ySrc,
            int startScan, int scanLines, int[] bits, ref BitmapHeader header, uint colorUse);

        internal bool ShowMark = true;

        private void Set(int i, float x, float y, float z) { vx[i] = x; vy[i] = y; vz[i] = z; }

        // Composes one frame for time t (seconds) over the whole client area, in
        // device pixels: the mark inside `area` (dimmer under `panes`), then the
        // cached overlay pixels, and draws it to g.
        public void Render(Graphics g, Size client, Rectangle area, double t, float scale, Color background,
            IList<Rectangle> panes, int[] overlayIndex, int[] overlayColor, int overlayCount, Rectangle leftColumn, Rectangle rightColumn, float veil = 0)
        {
            frames++;
            if (client.Width < 8 || client.Height < 8) return;
            int scene;
            float fade;
            SceneState(out scene, out fade);
            lastRenderer = this;
            ReleaseScenesNotDrawn(false);
            // A picture with its own grid gets cells of that size (shade blocks); one with its
            // own character size uses it for both layers. Either is drawn under natural light.
            var setting = ShownSetting(scene);
            int fixedW = 0, fixedH = 0;
            float points = scene == VideoScene || scene == PictureScene ? 6.5F : 8.5F;
            figurePoints = 4F;
            naturalLight = false;
            if (setting != null && setting.Columns > 0 && setting.Rows > 0)
            {
                fixedW = Math.Max(1, (int)Math.Round(Math.Min(area.Width, client.Width) / (double)setting.Columns));
                fixedH = Math.Max(1, (int)Math.Round(Math.Min(area.Height, client.Height) / (double)setting.Rows));
                naturalLight = true;
            }
            else if (setting != null && setting.Points > 0)
            {
                points = figurePoints = Math.Max(2F, Math.Min(12F, setting.Points));
                naturalLight = true;
            }
            Ensure(client, area, scale, background, points, fixedW, fixedH);
            // The steady spin runs on its own clock, held while the mark is held by hand; the
            // hand's turn (and its momentum after letting go) is applied on top.
            double step = lastRenderT < 0 ? 0 : Math.Max(0, Math.Min(0.25, t - lastRenderT));
            lastRenderT = t;
            if (!dragging) spinTime += step;
            Coast(step);
            double a = phase[0] + speed[0] * spinTime, b = phase[1] + speed[1] * spinTime, c = phase[2] + speed[2] * spinTime;
            double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b), cc = Math.Cos(c), sc = Math.Sin(c);
            float radius = Math.Min(area.Width, area.Height) * 0.6F, cx = area.Width / 2F, cy = area.Height / 2F;
            for (int i = 0; i < 12; i++)
            {
                // Rx, then Ry, then Rz.
                double x = vx[i], y = vy[i] * ca - vz[i] * sa, z = vy[i] * sa + vz[i] * ca;
                double x2 = x * cb + z * sb, z2 = -x * sb + z * cb;
                double x3 = x2 * cc - y * sc, y3 = x2 * sc + y * cc;
                // The turn by hand, in screen axes.
                double hx = hand[0] * x3 + hand[1] * y3 + hand[2] * z2, hy = hand[3] * x3 + hand[4] * y3 + hand[5] * z2, hz = hand[6] * x3 + hand[7] * y3 + hand[8] * z2;
                x3 = hx; y3 = hy; z2 = hz;
                double perspective = 3.4 / (3.4 + z2);
                sx[i] = (float)(cx + x3 * perspective * radius);
                sy[i] = (float)(cy + y3 * perspective * radius);
                sz[i] = (float)z2;
                rx[i] = (float)x3; ry[i] = (float)y3;
            }
            // While the page decodes, the old scene is drawn and kept, then the new one is drawn
            // and each cell shows one or the other, or scrambles between them (see Decode).
            int decodeFrom;
            float decodeProgress;
            bool decodingNow = DecodeState(out decodeFrom, out decodeProgress);
            // Arabara's own sound is heard only for RINGS (it sets it while drawn).
            if (scene != RingScene && !(decodingNow && decodeFrom == RingScene)) AudioMeter.OwnAudio = false;
            if (decodingNow)
            {
                DrawScene(decodeFrom, t, 1F, panes, veil);
                if (decodeOld == null || decodeOld.Length != pixels.Length) decodeOld = new int[pixels.Length];
                Array.Copy(pixels, decodeOld, pixels.Length);
                DrawScene(scene, t, 1F, panes, veil);
                Decode(decodeProgress, t, panes, veil);
            }
            else DrawScene(scene, t, fade, panes, veil);
            angles[0] = a; angles[1] = b; angles[2] = c;
            DrawCode(leftColumn, leftCode, t, true, veil);
            DrawCode(rightColumn, rightCode, t, false, veil);
            if (veil <= 0) for (int i = 0; i < overlayCount; i++) pixels[overlayIndex[i]] = overlayColor[i];
            else ApplyVeiled(overlayIndex, overlayColor, overlayCount, veil);
            // GDI copies the buffer straight to the device context; no intermediate bitmap.
            var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            IntPtr hdc = g.GetHdc();
            try { SetDIBitsToDevice(hdc, 0, 0, width, height, 0, 0, 0, height, pixels, ref header, 0); }
            finally { g.ReleaseHdc(hdc); }
        }

        // Draws one scene's cells (and its own layers) into the frame buffer at brightness fade.
        private void DrawScene(int scene, double t, float fade, IList<Rectangle> panes, float veil)
        {
            for (int i = 0; i < depth.Length; i++) { depth[i] = float.MaxValue; light[i] = -1; }
            lastDepth = 0;
            if (scene == 0)
            {
                if (ShowMark) foreach (var face in faces) Raster(face);
                ComputeSea(t);
            }
            else
            {
                // VIDEO LOOP and PICTURE show their character once it is ready.
                figureShown = false;
                RenderScene(scene, t);
            }
            int fill = canvas.ToArgb();
            lit = 0;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
            if (blockCells && scene != 0) DrawBlockCells(fade, panes, veil);
            else
            for (int r = 0; r < rows; r++)
                for (int col = 0; col < cols; col++)
                {
                    float level = light[r * cols + col];
                    if (level >= 0) lit++;
                    char glyph;
                    int tone;
                    if (level >= 0 && scene != 0)
                    {
                        level *= fade;
                        if (level < (blockCells ? 0.03F : 0.05F)) continue;
                        VideoGlyph(level, out glyph, out tone);
                        glyph = shapeGlyph[r * cols + col];
                        if (blockCells) tone = FineTone(level, glyph);
                        else if (naturalLight && scene == PictureScene) tone = FineLevelTone(level);
                    }
                    else if (level >= 0)
                    {
                        level *= fade;
                        if (level < 0.05F) continue;
                        glyph = Ramp[Math.Min(Ramp.Length - 1, (int)(level * Ramp.Length))];
                        tone = level < 0.4F ? 0 : level < 0.7F ? 1 : 2;
                    }
                    else if (scene != 0) continue;
                    else
                    {
                        float sea = seaLevel[r * cols + col] * fade;
                        if (sea < 0.06F) continue;
                        glyph = SeaRamp[Math.Min(SeaRamp.Length - 1, (int)(sea * SeaRamp.Length))];
                        tone = sea < 0.4F ? 3 : sea < 0.7F ? 4 : 5;
                    }
                    int px = originX + col * cellW + cellW / 2, py = originY + r * cellH + cellH / 2;
                    // Text panes dim what lies under them, except where the interface has decayed away.
                    if (veil <= 0 || CellThreshold(col, r) >= veil * (1 + VeilFront))
                        foreach (var pane in panes) if (pane.Contains(px, py)) { tone += tone >= FineBase ? FineCount : Shades.Length; break; }
                    if (blockCells && glyph == '\u2588') FillCell(originX + col * cellW, originY + r * cellH, cellW, cellH, ramps[tone][255]);
                    else Blit(originX + col * cellW, originY + r * cellH, glyph, tone);
                }
            if (scene == VideoScene && figureShown) DrawFigure(fade, panes, veil);
            if (scene == PictureScene && figureShown) DrawFigure(fade * pictureFade, panes, veil);
            if (scene == SoundScene) DrawSoundWave(t, fade, panes, veil);
            if (scene == RingScene) DrawRings(t, fade, panes, veil);
        }

        // The scrambling glyphs are in the scenes' own dim crimsons (the mark's two brighter
        // tones), and about one in seven flares in the video's brighter crimson.
        private int[] decodeOld;

        private void Decode(float progress, double t, IList<Rectangle> panes, float veil)
        {
            int fill = canvas.ToArgb(), flicker = (int)(t * 25);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    float at = DecodeHash(c, r, decodeSeed) * DecodeSpread;
                    if (progress >= at + DecodeScramble) continue; // the new scene, already drawn
                    int x0 = originX + c * cellW, y0 = originY + r * cellH;
                    if (progress < at) { CopyCell(decodeOld, x0, y0); continue; } // still the old scene
                    // Scrambling where either scene has ink (and in a quarter of the empty cells),
                    // a glyph that changes 25 times a second.
                    bool ink = CellInk(decodeOld, x0, y0, fill) || CellInk(pixels, x0, y0, fill);
                    ClearCell(x0, y0, fill);
                    if (!ink && DecodeHash(c, r, decodeSeed + 1) > 0.25F) continue;
                    float pick = DecodeHash(c, r, flicker);
                    int tone = pick < 0.15F ? 6 : pick < 0.6F ? 2 : 1;
                    int px = x0 + cellW / 2, py = y0 + cellH / 2;
                    if (veil <= 0 || CellThreshold(c, r) >= veil * (1 + VeilFront))
                        foreach (var pane in panes) if (pane.Contains(px, py)) { tone += Shades.Length; break; }
                    Blit(x0, y0, Glitch[(int)(DecodeHash(c, r, flicker + 7919) * Glitch.Length)], tone);
                }
        }

        private static float DecodeHash(int c, int r, int seed)
        {
            unchecked
            {
                uint h = (uint)(c * 73856093) ^ (uint)(r * 19349663) ^ (uint)(seed * 83492791);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216F;
            }
        }

        private void CopyCell(int[] from, int x0, int y0)
        {
            int w = Math.Min(cellW, width - x0);
            if (w <= 0 || x0 < 0) return;
            for (int y = Math.Max(0, y0); y < Math.Min(height, y0 + cellH); y++) Array.Copy(from, y * width + x0, pixels, y * width + x0, w);
        }

        private void ClearCell(int x0, int y0, int fill)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(height, y0 + cellH); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(width, x0 + cellW); x++) pixels[y * width + x] = fill;
        }

        private bool CellInk(int[] frame, int x0, int y0, int fill)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(height, y0 + cellH); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(width, x0 + cellW); x++) if (frame[y * width + x] != fill) return true;
            return false;
        }

        // A 3D sea seen from a camera above the water, tilted down so the horizon
        // stays above the page: each character cell casts a ray to the water plane,
        // where four directional waves (deep-water speeds) give height and slope.
        // Nearer water (lower on the page) shows larger waves; distant water is
        // compressed, loses its short waves and fades.
        private const float SeaFov = 0.96F, SeaPitch = 0.61F, SeaCamera = 3F;
        private static readonly double[,] Swell =
        {
            // amplitude, wavelength, direction (radians; pi/2 runs towards the camera), phase
            { 0.130, 2.4, 1.52, 0.0 },
            { 0.065, 1.5, 1.95, 1.7 },
            { 0.040, 0.95, 1.15, 3.1 },
            { 0.025, 0.6, 2.25, 4.4 }
        };
        private const string SeaRamp = ".-~=+*#%";

        // Fills seaLevel (0..1 brightness per cell). Everything that depends only on
        // the row (ray distance, depth, fog, detail) is computed once per row; along
        // a row each wave's phase grows linearly, so its sine and cosine advance by a
        // fixed rotation instead of being evaluated per cell.
        private void ComputeSea(double t)
        {
            float focal = height / (2 * (float)Math.Tan(SeaFov / 2));
            double cp = Math.Cos(SeaPitch), sp = Math.Sin(SeaPitch);
            int waves = Swell.GetLength(0);
            var sin = new double[waves]; var cos = new double[waves]; var stepSin = new double[waves]; var stepCos = new double[waves];
            var amp = new double[waves]; var kx = new double[waves]; var kz = new double[waves];
            double du = cellW / (double)focal;
            for (int r = 0; r < rows; r++)
            {
                double v = (originY + r * cellH + cellH / 2.0 - height / 2.0) / focal;
                double dy = -v * cp - sp, dz = -v * sp + cp;
                int rowStart = r * cols;
                if (dy > -0.02) { for (int c = 0; c < cols; c++) seaLevel[rowStart + c] = 0; continue; }
                double hit = SeaCamera / -dy, z = dz * hit, fog = 0.35 + 0.65 * Math.Exp(-hit / 22), total = 0;
                double u0 = (originX + cellW / 2.0 - width / 2.0) / focal;
                for (int i = 0; i < waves; i++)
                {
                    double k = 2 * Math.PI / Swell[i, 1];
                    // Short waves fade out with distance instead of aliasing into noise.
                    double detail = Math.Max(0, 1 - hit * k / 60);
                    amp[i] = Swell[i, 0] * detail;
                    total += Swell[i, 0];
                    kx[i] = k * Math.Cos(Swell[i, 2]);
                    kz[i] = k * Math.Sin(Swell[i, 2]);
                    double phase = kx[i] * u0 * hit + kz[i] * z - Math.Sqrt(9.8 * k) * 0.35 * t + Swell[i, 3];
                    double step = kx[i] * du * hit;
                    sin[i] = Math.Sin(phase); cos[i] = Math.Cos(phase);
                    stepSin[i] = Math.Sin(step); stepCos[i] = Math.Cos(step);
                }
                for (int c = 0; c < cols; c++)
                {
                    double h = 0, hx = 0, hz = 0;
                    for (int i = 0; i < waves; i++)
                    {
                        if (amp[i] > 0)
                        {
                            h += amp[i] * sin[i];
                            hx += amp[i] * kx[i] * cos[i];
                            hz += amp[i] * kz[i] * cos[i];
                        }
                        double s0 = sin[i];
                        sin[i] = s0 * stepCos[i] + cos[i] * stepSin[i];
                        cos[i] = cos[i] * stepCos[i] - s0 * stepSin[i];
                    }
                    // Slopes facing the light, plus brighter crests; fog towards the horizon.
                    double length = Math.Sqrt(hx * hx + 1 + hz * hz);
                    double facing = Math.Max(0, (0.35 * hx + 0.55 + 0.75 * hz) / length);
                    double crest = Math.Max(0, h / total);
                    double lit = facing * facing; lit *= lit;
                    double level = (0.55 * lit + 1.3 * crest * crest * crest + 0.04) * fog * 2.2;
                    seaLevel[rowStart + c] = (float)Math.Min(1, level);
                }
            }
        }

        // A side column of code printing lines from the running animation, with live
        // values. It alternates at random between pauses (cursor only), normal runs
        // of about 10 lines per second and bursts of 35-50 lines per second. The
        // newest line is typed out; older lines scroll up and fade.
        private float CellThreshold(int col, int row)
        { return row < rows && col < cols && row >= 0 && col >= 0 ? uiThreshold[row * cols + col] : 0.999F; }

        // A code character under the veil: drawn, replaced by a glitch at the decay front, or gone.
        // The side code columns keep one size whatever the background: their own font and
        // cells (MARK + SEA's 8.5 pt), not the scene's.
        private const float CodePoints = 8.5F;
        private Font codeFont;
        private int codeCellW, codeCellH;
        private readonly Dictionary<char, byte[]> codeMasks = new Dictionary<char, byte[]>();

        private void CodeBlit(int x, int y, char ch, int tone, float veil)
        {
            if (veil > 0)
            {
                float v = veil * (1 + VeilFront), threshold = CellThreshold(Math.Min(cols - 1, Math.Max(0, (x - originX) / cellW)), Math.Min(rows - 1, Math.Max(0, (y - originY) / cellH)));
                if (threshold < v - VeilFront) return;
                if (threshold < v) { CodeGlyph(x, y, Glitch[noise.Next(Glitch.Length)], Shades.Length * 2 + 4 + noise.Next(2)); return; }
            }
            CodeGlyph(x, y, ch, tone);
        }

        private void CodeGlyph(int x0, int y0, char glyph, int tone)
        {
            if (x0 < 0 || y0 < 0 || x0 >= width || y0 >= height) return;
            byte[] mask;
            if (!codeMasks.TryGetValue(glyph, out mask)) codeMasks[glyph] = mask = RenderMask(codeFont, codeCellW, codeCellH, glyph);
            int[] ramp = ramps[tone];
            int w = Math.Min(codeCellW, width - x0), h = Math.Min(codeCellH, height - y0);
            for (int y = 0; y < h; y++)
            {
                int row = (y0 + y) * width + x0, maskRow = y * codeCellW;
                for (int x = 0; x < w; x++)
                {
                    int alpha = mask[maskRow + x];
                    if (alpha != 0) pixels[row + x] = ramp[alpha];
                }
            }
        }

        private void DrawCode(Rectangle column, CodeColumn code, double t, bool mark, float veil)
        {
            if (column.Width < codeCellW * 12 || column.Height < codeCellH * 4) return;
            var log = code.Log;
            if (log.Count == 0)
            {
                // Start with a screenful of earlier-looking lines rather than an empty column.
                var current = (double[])angles.Clone();
                int frameNow = frames;
                for (int i = 0; i < 160; i++)
                {
                    for (int axis = 0; axis < 3; axis++) angles[axis] = current[axis] - speed[axis] * (160 - i) * 0.1;
                    frames = frameNow - (160 - i) * 3;
                    log.Add(mark ? MarkLine(i) : SeaLine(i));
                }
                Array.Copy(current, angles, 3);
                frames = frameNow;
                code.Count = 160;
                code.LastT = t;
                code.SinceLine = 1;
            }
            double dt = Math.Max(0, Math.Min(0.5, t - code.LastT));
            code.LastT = t;
            code.SinceLine += dt;
            if (t >= code.PhaseEnd) NextPhase(code, t);
            code.Owed += code.Rate * dt;
            while (code.Owed >= 1)
            {
                code.Owed -= 1;
                log.Add(mark ? MarkLine(code.Count) : SeaLine(code.Count));
                code.Count++;
                code.SinceLine = 0;
                code.TypeTime = code.Rate > 0 ? Math.Min(0.18, 0.8 / code.Rate) : 0.08;
                if (log.Count > 320) log.RemoveRange(0, 120);
            }
            double typed = code.SinceLine / code.TypeTime;
            int visible = column.Height / codeCellH, maxChars = column.Width / codeCellW, first = Math.Max(0, log.Count - visible);
            for (int i = first; i < log.Count; i++)
            {
                string line = log[i];
                bool newest = i == log.Count - 1;
                int age = log.Count - 1 - i;
                int shown = newest ? Math.Min(line.Length, (int)(typed * line.Length) + 1) : line.Length;
                int y = column.Bottom - (log.Count - i) * codeCellH;
                if (y < column.Top) continue;
                int baseTone = Shades.Length * 2 + (newest ? 3 : age > visible * 2 / 3 ? 0 : 1);
                bool value = false;
                for (int k = 0; k < Math.Min(shown, maxChars); k++)
                {
                    char ch = line[k];
                    if (ch == '=') value = true;
                    if (ch == ' ') continue;
                    int tone = value && !newest && age <= visible * 2 / 3 ? Shades.Length * 2 + 2 : baseTone;
                    CodeBlit(column.X + k * codeCellW, y, ch, tone, veil);
                }
                if (newest && shown < maxChars && (frames / 4) % 2 == 0) CodeBlit(column.X + shown * codeCellW, y, '_', baseTone, veil);
            }
        }

        private void NextPhase(CodeColumn code, double t)
        {
            double roll = noise.NextDouble();
            if (roll < 0.3) { code.Rate = 0; code.PhaseEnd = t + 0.6 + noise.NextDouble() * 1.9; }           // pause
            else if (roll < 0.55) { code.Rate = 35 + noise.NextDouble() * 15; code.PhaseEnd = t + 0.4 + noise.NextDouble() * 0.8; } // burst
            else { code.Rate = 8 + noise.NextDouble() * 4; code.PhaseEnd = t + 1.5 + noise.NextDouble() * 2.5; } // normal
        }

        private string MarkLine(int n)
        {
            if (n % 13 == 12) return "scene = " + DisplayName;
            switch (n % 12)
            {
                case 0: return "frame 0x" + ((frameBase + frames) & 0xFFFF).ToString("X4");
                case 1: return "rot.x = " + Angle(angles[0]);
                case 2: return "rot.y = " + Angle(angles[1]);
                case 3: return "rot.z = " + Angle(angles[2]);
                case 4: return "R = Rz * Ry * Rx";
                case 5: return "persp = 3.4/(3.4+z)";
                case 6: return "raster face[" + noise.Next(faces.Length).ToString("00") + "] z=" + lastDepth.ToString("0.00");
                case 7: return "shade = |n.L| = " + (0.3 + 0.7 * noise.NextDouble()).ToString("0.00");
                case 8: return "cells.lit = " + lit;
                case 9: return "stripe = (y+4)/0.13";
                case 10: return "w.y = " + (speed[1] * 60).ToString("0.0") + " deg/s";
                default: return "zbuf.clear(" + (cols * rows) + ")";
            }
        }

        private string SeaLine(int n)
        {
            int wave = noise.Next(Swell.GetLength(0));
            double k = 2 * Math.PI / Swell[wave, 1];
            switch (n % 10)
            {
                case 0: return "h = sum a*sin(k.x-wt)";
                case 1: return "k[" + wave + "] = 2pi/" + Swell[wave, 1].ToString("0.00");
                case 2: return "phase[" + wave + "] = " + ((frames * 0.083 * Math.Sqrt(9.8 * k) * 0.35 + Swell[wave, 3]) % (2 * Math.PI)).ToString("0.00");
                case 3: return "ray.y = " + (-0.2 - 0.8 * noise.NextDouble()).ToString("0.000");
                case 4: return "hit = " + (3 + 20 * noise.NextDouble()).ToString("0.0");
                case 5: return "fog = exp(-d/22)";
                case 6: return "crest = " + noise.NextDouble().ToString("0.00");
                case 7: return "swell[" + wave + "].dir = " + (Swell[wave, 2] * 180 / Math.PI).ToString("0") + "deg";
                case 8: return "camera.pitch = " + (SeaPitch * 180 / Math.PI).ToString("0.0");
                default: return "sea.rows = " + rows + " x " + cols;
            }
        }

        private static string Angle(double radians)
        {
            double a = radians % (2 * Math.PI);
            if (a < 0) a += 2 * Math.PI;
            return a.ToString("0.000") + " rad";
        }

        // The cached interface layer, partly decayed: cells whose threshold the veil has
        // passed are gone, cells at the front show red/white glitch characters, the
        // rest are drawn normally. veil runs from 0 (all shown) to 1 (all gone).
        private void ApplyVeiled(int[] overlayIndex, int[] overlayColor, int overlayCount, float veil)
        {
            float v = veil * (1 + VeilFront);
            Array.Clear(uiCells, 0, uiCells.Length);
            for (int i = 0; i < overlayCount; i++)
            {
                int index = overlayIndex[i], y = index / width, x = index - y * width;
                int row = y / cellH, col = x / cellW;
                float threshold = row < rows && col < cols ? uiThreshold[row * cols + col] : 0.999F;
                if (row < rows && col < cols) uiCells[row * cols + col] = true;
                if (threshold >= v) pixels[index] = overlayColor[i];
            }
            int red = Shades.Length * 2 + 4, white = red + 1;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int cell = r * cols + c;
                    if (!uiCells[cell]) continue;
                    float threshold = uiThreshold[cell];
                    if (threshold >= v || threshold < v - VeilFront) continue;
                    int x0 = c * cellW, y0 = r * cellH;
                    for (int y = y0; y < Math.Min(height, y0 + cellH); y++)
                        for (int x = x0; x < Math.Min(width, x0 + cellW); x++) pixels[y * width + x] = ramps[0][0];
                    Blit(x0, y0, Glitch[noise.Next(Glitch.Length)], noise.Next(2) == 0 ? red : white);
                }
        }

        // ---- Turning the mark by hand (start page in view-only mode, see HomeView): press on
        // the mark and drag. Across turns it about the screen's vertical axis, up and down
        // about the horizontal one, one radian per the mark's radius dragged; the steady spin
        // waits meanwhile. Let go and it keeps turning with the drag's speed, slowing over
        // about a second, then the steady spin carries on from where it was left.
        private readonly double[] hand = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        private double spinTime, lastRenderT = -1, coastX, coastY, dragTime;
        private bool dragging;
        private Point dragFrom;
        private readonly Stopwatch dragClock = Stopwatch.StartNew();

        public bool Dragging { get { return dragging; } }
        public bool Interacting { get { return dragging || Math.Abs(coastX) + Math.Abs(coastY) > 0.02; } }

        // Whether the point (in the page's pixels) is on the mark as last drawn, with a
        // margin of a cell or two so thin parts are easy to take hold of.
        public bool HitMark(Point p)
        {
            if (Scene != 0 || SceneFading || !ShowMark || depth == null || cellW == 0) return false;
            int c = (p.X - originX) / cellW, r = (p.Y - originY) / cellH;
            for (int dr = -1; dr <= 1; dr++)
                for (int dc = -2; dc <= 2; dc++)
                {
                    int rr = r + dr, cc = c + dc;
                    if (rr >= 0 && cc >= 0 && rr < rows && cc < cols && depth[rr * cols + cc] < float.MaxValue) return true;
                }
            return false;
        }

        public void BeginDrag(Point p)
        {
            dragging = true;
            dragFrom = p;
            coastX = coastY = 0;
            dragTime = dragClock.Elapsed.TotalSeconds;
        }

        public void Drag(Point p)
        {
            if (!dragging) return;
            float radius = Math.Max(40F, Math.Min(markArea.Width, markArea.Height) * 0.6F);
            double across = (p.X - dragFrom.X) / radius, down = (p.Y - dragFrom.Y) / radius;
            dragFrom = p;
            Turn(across, down);
            // The drag's speed, smoothed, for the momentum after letting go.
            double now = dragClock.Elapsed.TotalSeconds, dt = Math.Max(0.004, now - dragTime);
            dragTime = now;
            double k = Math.Min(1, dt / 0.06);
            coastX += (across / dt - coastX) * k;
            coastY += (down / dt - coastY) * k;
        }

        public void EndDrag()
        {
            if (!dragging) return;
            dragging = false;
            // A drag that stopped before letting go does not throw the mark.
            if (dragClock.Elapsed.TotalSeconds - dragTime > 0.08) coastX = coastY = 0;
            coastX = Math.Max(-12, Math.Min(12, coastX));
            coastY = Math.Max(-12, Math.Min(12, coastY));
        }

        private void Coast(double step)
        {
            if (dragging || step <= 0 || Math.Abs(coastX) + Math.Abs(coastY) < 0.001) return;
            Turn(coastX * step, coastY * step);
            double fade = Math.Exp(-step / 0.9);
            coastX *= fade; coastY *= fade;
        }

        // Turns the hand rotation by `across` about the vertical axis and `down` about the
        // horizontal one (screen y points down, depth away from the viewer), keeping it a
        // clean rotation.
        private void Turn(double across, double down)
        {
            double cy = Math.Cos(across), sy = Math.Sin(across), cx = Math.Cos(down), sx = Math.Sin(down);
            // Ry: x' = x cos - z sin, z' = x sin + z cos; then Rx: y' = y cos - z sin, z' = y sin + z cos.
            var r = new double[] { cy, 0, -sy, -sx * sy, cx, -sx * cy, cx * sy, sx, cx * cy };
            var m = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i * 3 + j] = r[i * 3] * hand[j] + r[i * 3 + 1] * hand[3 + j] + r[i * 3 + 2] * hand[6 + j];
            // Gram-Schmidt on the rows.
            for (int i = 0; i < 3; i++)
            {
                for (int k = 0; k < i; k++)
                {
                    double dot = m[i * 3] * m[k * 3] + m[i * 3 + 1] * m[k * 3 + 1] + m[i * 3 + 2] * m[k * 3 + 2];
                    for (int j = 0; j < 3; j++) m[i * 3 + j] -= dot * m[k * 3 + j];
                }
                double length = Math.Sqrt(m[i * 3] * m[i * 3] + m[i * 3 + 1] * m[i * 3 + 1] + m[i * 3 + 2] * m[i * 3 + 2]);
                for (int j = 0; j < 3; j++) m[i * 3 + j] /= length;
            }
            Array.Copy(m, hand, 9);
        }

        // Fills every character cell whose centre lies inside the projected face.
        private void Raster(Face f)
        {
            float x0 = sx[f.A], y0 = sy[f.A], x1 = sx[f.B], y1 = sy[f.B], x2 = sx[f.C], y2 = sy[f.C];
            float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            if (Math.Abs(area) < 1e-3F) return;
            // Face normal from the rotated vertices; lit from the upper left, both sides shaded.
            float ux = rx[f.B] - rx[f.A], uy = ry[f.B] - ry[f.A], uz = sz[f.B] - sz[f.A];
            float wx = rx[f.C] - rx[f.A], wy = ry[f.C] - ry[f.A], wz = sz[f.C] - sz[f.A];
            float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            float length = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            float shade = length < 1e-9F ? 0.5F : Math.Abs((-0.45F * nx - 0.55F * ny - 0.7F * nz) / (length * 1.0005F));
            shade = 0.3F + 0.7F * shade;
            int c0 = Math.Max(0, (int)(Math.Min(x0, Math.Min(x1, x2)) / cellW)), c1 = Math.Min(cols - 1, (int)(Math.Max(x0, Math.Max(x1, x2)) / cellW));
            int r0 = Math.Max(0, (int)(Math.Min(y0, Math.Min(y1, y2)) / cellH)), r1 = Math.Min(rows - 1, (int)(Math.Max(y0, Math.Max(y1, y2)) / cellH));
            for (int r = r0; r <= r1; r++)
            {
                float py = (r + 0.5F) * cellH;
                for (int c = c0; c <= c1; c++)
                {
                    float px = (c + 0.5F) * cellW;
                    float w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) / area;
                    float w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * sz[f.A] + w1 * sz[f.B] + w2 * sz[f.C];
                    int i = r * cols + c;
                    if (z >= depth[i]) continue;
                    depth[i] = z;
                    lastDepth = z;
                    float level = shade;
                    if (f.Striped)
                    {
                        // Horizontal stripes in the mark's own space, like the flat logo.
                        float oy = w0 * vy[f.A] + w1 * vy[f.B] + w2 * vy[f.C];
                        float band = (oy + 4F) / 0.13F;
                        if (band - (float)Math.Floor(band) < 0.38F) level *= 0.5F;
                    }
                    light[i] = level;
                }
            }
        }

        // Fine block grids have hundreds of thousands of cells, so they get their own loop:
        // every cell is a full block whose shade is its level (FineTone), dimmed under a text
        // pane (worked out once per frame for the whole grid, in MarkDimmed).
        private int[] fineColours = new int[0];
        private bool[] dimCells = new bool[0];

        private void MarkDimmed(IList<Rectangle> panes, float veil)
        {
            if (dimCells.Length != cols * rows) dimCells = new bool[cols * rows];
            else Array.Clear(dimCells, 0, dimCells.Length);
            foreach (var pane in panes)
            {
                // Cells whose centre lies inside the pane.
                int c0 = Math.Max(0, (int)Math.Ceiling((pane.Left - originX - cellW / 2) / (double)cellW));
                int c1 = Math.Min(cols - 1, (int)Math.Floor((pane.Right - 1 - originX - cellW / 2) / (double)cellW));
                int r0 = Math.Max(0, (int)Math.Ceiling((pane.Top - originY - cellH / 2) / (double)cellH));
                int r1 = Math.Min(rows - 1, (int)Math.Floor((pane.Bottom - 1 - originY - cellH / 2) / (double)cellH));
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                        if (veil <= 0 || CellThreshold(c, r) >= veil * (1 + VeilFront)) dimCells[r * cols + c] = true;
            }
        }

        private int FineColour(float level, int cell)
        {
            int k = (int)(level * (FineCount - 1) + 0.5F);
            if (k > FineCount - 1) k = FineCount - 1;
            return fineColours[dimCells[cell] ? FineCount + k : k];
        }

        private void DrawBlockCells(float fade, IList<Rectangle> panes, float veil)
        {
            MarkDimmed(panes, veil);
            for (int r = 0; r < rows; r++)
            {
                int y0 = originY + r * cellH;
                if (y0 < 0) continue;
                if (y0 >= height) break;
                int h = Math.Min(cellH, height - y0);
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    float level = light[i];
                    if (level < 0) continue;
                    lit++;
                    level *= fade;
                    if (level < 0.03F) continue;
                    int x0 = originX + c * cellW;
                    if (x0 < 0) continue;
                    if (x0 >= width) break;
                    int w = Math.Min(cellW, width - x0), colour = FineColour(level, i);
                    for (int y = 0; y < h; y++)
                    {
                        int row = (y0 + y) * width + x0;
                        for (int x = 0; x < w; x++) pixels[row + x] = colour;
                    }
                }
            }
        }

        // A full block: the whole cell in one colour, without a glyph mask.
        private void FillCell(int x0, int y0, int w, int h, int colour)
        {
            if (x0 < 0 || y0 < 0 || x0 >= width || y0 >= height) return;
            w = Math.Min(w, width - x0); h = Math.Min(h, height - y0);
            for (int y = 0; y < h; y++)
            {
                int row = (y0 + y) * width + x0;
                for (int x = 0; x < w; x++) pixels[row + x] = colour;
            }
        }

        private void Blit(int x0, int y0, char glyph, int tone)
        {
            if (x0 < 0 || y0 < 0 || x0 >= width || y0 >= height) return;
            byte[] mask = Mask(glyph);
            int[] ramp = ramps[tone];
            int w = Math.Min(cellW, width - x0), h = Math.Min(cellH, height - y0);
            for (int y = 0; y < h; y++)
            {
                int row = (y0 + y) * width + x0, maskRow = y * cellW;
                for (int x = 0; x < w; x++)
                {
                    int alpha = mask[maskRow + x];
                    if (alpha != 0) pixels[row + x] = ramp[alpha];
                }
            }
        }

        private byte[] Mask(char glyph)
        {
            byte[] mask;
            if (masks.TryGetValue(glyph, out mask)) return mask;
            if (blockCells) { mask = BlockMask(glyph, cellW, cellH); masks[glyph] = mask; return mask; }
            mask = new byte[cellW * cellH];
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
            masks[glyph] = mask;
            return mask;
        }

        private float cellPoints;
        // Block cells shade continuously: 32 steps from black through the crimson tones to
        // the highlight, spaced by brightness so a cell looks as light as its level (a
        // partly filled block is lit more, by how much of the cell it fills).
        private const int FineCount = 32;
        private static readonly int FineBase = Shades.Length * 2 + TextColors.Length;
        private static readonly Color[] FineStops =
        {
            Color.FromArgb(8, 4, 5), Color.FromArgb(38, 10, 14), Color.FromArgb(86, 21, 30),
            Color.FromArgb(158, 34, 50), Color.FromArgb(214, 56, 76), Color.FromArgb(255, 150, 164)
        };
        private static readonly float[] FineAt = { 0, 0.09F, 0.2F, 0.35F, 0.53F, 1 };

        private static Color FineShade(float p)
        {
            int k = 0;
            while (k < FineAt.Length - 2 && p > FineAt[k + 1]) k++;
            float f = Math.Max(0, Math.Min(1, (p - FineAt[k]) / (FineAt[k + 1] - FineAt[k])));
            Color a = FineStops[k], b = FineStops[k + 1];
            return Color.FromArgb((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
        }

        private static float BlockCover(char glyph)
        {
            return glyph == '\u2591' ? 0.25F : glyph == '\u2592' ? 0.5F : glyph == '\u2593' ? 0.75F : 1F;
        }

        // A character's fine shade: the glyph already shows the density, but its strokes
        // cover only part of the cell, so the shade is lifted (gamma 0.75, gain 1.15) to
        // keep the picture as light as it is.
        private static int FineLevelTone(float level)
        {
            float shade = Math.Min(1F, (float)Math.Pow(Math.Max(0F, level), 0.75) * 1.15F);
            return FineBase + (int)Math.Round(shade * (FineCount - 1));
        }

        private bool naturalLight; // the picture shown is drawn under its natural light (BlockCurve, fine shades)
        private float figurePoints = 4F;

        private static int FineTone(float level, char glyph)
        {
            float shade = Math.Max(0, Math.Min(1, level / BlockCover(glyph)));
            return FineBase + (int)Math.Round(shade * (FineCount - 1));
        }

        // One light for both layers of a block picture: its brightness stretched between
        // the 1st and 99.5th percentiles with a slight lift of the shadows (gamma 0.95),
        // nothing clipped or equalised, so the picture's own shading stays.
        private static byte[] blockCurveFor;
        private static float[] blockCurve;
        private static float[] BlockCurve(byte[] luma)
        {
            if (luma == blockCurveFor && blockCurve != null) return blockCurve;
            var histogram = new int[256];
            foreach (byte v in luma) histogram[v]++;
            int low = 0, high = 255, seen = 0;
            for (int v = 0; v < 256; v++) { seen += histogram[v]; if (seen >= luma.Length * 0.01) { low = v; break; } }
            seen = 0;
            for (int v = 255; v >= 0; v--) { seen += histogram[v]; if (seen >= luma.Length * 0.005) { high = v; break; } }
            float range = Math.Max(24, high - low);
            var curve = new float[256];
            for (int v = 0; v < 256; v++) curve[v] = (float)Math.Pow(Math.Max(0F, Math.Min(1F, (v - low) / range)), 0.95);
            blockCurveFor = luma; blockCurve = curve;
            return curve;
        }
        private bool blockCells; // a fine fixed grid drawn in shade blocks (see PictureScene)

        private void Ensure(Size size, Rectangle area, float scale, Color background, float points, int fixedW = 0, int fixedH = 0)
        {
            bool blocks = fixedW > 0 && fixedH > 0;
            if (pixels != null && size.Width == width && size.Height == height && area == markArea && scale == fontScale && background == canvas && points == cellPoints
                && blocks == blockCells && (!blocks || (fixedW == cellW && fixedH == cellH))) return;
            Release();
            width = size.Width; height = size.Height; fontScale = scale; canvas = background; markArea = area;
            originX = area.X; originY = area.Y;
            cellPoints = points;
            blockCells = blocks;
            font = UiFonts.Body(points * scale);
            codeFont = UiFonts.Body(CodePoints * scale);
            using (var probe = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(probe))
            {
                codeCellW = Math.Max(4, (int)Math.Ceiling(g.MeasureString("M", codeFont, PointF.Empty, StringFormat.GenericTypographic).Width));
                codeCellH = Math.Max(8, (int)Math.Ceiling(codeFont.GetHeight(g)));
            }
            if (blocks) { cellW = fixedW; cellH = fixedH; }
            else
                using (var probe = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(probe))
                {
                    // Small characters (a picture's own size) may use small cells.
                    cellW = Math.Max(points < 6 ? 2 : 4, (int)Math.Ceiling(g.MeasureString("M", font, PointF.Empty, StringFormat.GenericTypographic).Width));
                    cellH = Math.Max(points < 6 ? 4 : 8, (int)Math.Ceiling(font.GetHeight(g)));
                }
            cols = Math.Max(1, Math.Min(area.Width, width - Math.Max(0, area.X)) / cellW);
            rows = Math.Max(1, Math.Min(area.Height, height - Math.Max(0, area.Y)) / cellH);
            depth = new float[cols * rows];
            light = new float[cols * rows];
            seaLevel = new float[cols * rows];
            // Decay order for hiding the interface: from the centre outwards, with noise, like the intro.
            uiThreshold = new float[cols * rows];
            uiCells = new bool[cols * rows];
            float midX = cols / 2F, midY = rows / 2F, reach = (float)Math.Sqrt(midX * midX * cellW * cellW + midY * midY * cellH * cellH);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    float dx = (c - midX) * cellW, dy = (r - midY) * cellH;
                    uiThreshold[r * cols + c] = Math.Min(1F, (float)Math.Sqrt(dx * dx + dy * dy) / reach * 0.82F + (float)noise.NextDouble() * 0.18F);
                }
            pixels = new int[width * height];
            // Every tone, then the same tones dimmed for cells under a text pane.
            ramps = new int[Shades.Length * 2 + TextColors.Length + FineCount * 2][];
            fineColours = new int[FineCount * 2];
            // Fine shades (block cells): 32 steps along the crimson tones up to the highlight,
            // then the same dimmed for cells under a text pane.
            for (int k = 0; k < FineCount; k++)
            {
                Color full = FineShade(k / (float)(FineCount - 1));
                Color dim = Color.FromArgb(canvas.R + (full.R - canvas.R) * 2 / 5, canvas.G + (full.G - canvas.G) * 2 / 5, canvas.B + (full.B - canvas.B) * 2 / 5);
                foreach (int index in new[] { FineBase + k, FineBase + FineCount + k })
                {
                    Color to = index < FineBase + FineCount ? full : dim;
                    ramps[index] = new int[256];
                    for (int a = 0; a < 256; a++)
                        ramps[index][a] = Color.FromArgb(canvas.R + (to.R - canvas.R) * a / 255, canvas.G + (to.G - canvas.G) * a / 255, canvas.B + (to.B - canvas.B) * a / 255).ToArgb();
                    fineColours[index - FineBase] = ramps[index][255];
                }
            }
            for (int k = Shades.Length * 2; k < FineBase; k++)
            {
                ramps[k] = new int[256];
                Color to = TextColors[k - Shades.Length * 2];
                for (int a = 0; a < 256; a++)
                    ramps[k][a] = Color.FromArgb(canvas.R + (to.R - canvas.R) * a / 255, canvas.G + (to.G - canvas.G) * a / 255, canvas.B + (to.B - canvas.B) * a / 255).ToArgb();
            }
            for (int k = 0; k < Shades.Length * 2; k++)
            {
                ramps[k] = new int[256];
                Color full = Shades[k % Shades.Length];
                Color to = k < Shades.Length ? full : Color.FromArgb(canvas.R + (full.R - canvas.R) * 2 / 5, canvas.G + (full.G - canvas.G) * 2 / 5, canvas.B + (full.B - canvas.B) * 2 / 5);
                for (int a = 0; a < 256; a++)
                    ramps[k][a] = Color.FromArgb(canvas.R + (to.R - canvas.R) * a / 255, canvas.G + (to.G - canvas.G) * a / 255, canvas.B + (to.B - canvas.B) * a / 255).ToArgb();
            }
        }

        // Frees the frame buffers; the next Render rebuilds them.
        public void Release()
        {
            if (font != null) font.Dispose();
            font = null;
            if (codeFont != null) codeFont.Dispose();
            codeFont = null;
            codeMasks.Clear();
            pixels = null; seaLevel = null; depth = null; light = null; ramps = null;
            masks.Clear();
            ReleaseFigure();
            ReleaseRings();
            if (lastRenderer == this || lastRenderer == null) { ReleaseScenesNotDrawn(true); lastRenderer = null; }
            else { videoCells = null; ReleasePictureCells(); }
            width = height = 0;
        }

        public void Dispose() { Release(); }

        // --home-test: sample frames of the mark and the cost of a full start-page paint
        // (--video <file> and --picture <file> also render those scenes from that file; with
        // --grid and --motion the picture gets its own grid and motion layout).
        public static int RenderTest(string folder)
        {
            var log = new System.Text.StringBuilder();
            // --size 1280x620 renders the scenes at another page size.
            var testSize = new Size(1280, 900);
            int sizeArg = Array.IndexOf(Environment.GetCommandLineArgs(), "--size");
            if (sizeArg > 0)
            {
                string[] parts = Environment.GetCommandLineArgs()[sizeArg + 1].Split('x');
                testSize = new Size(int.Parse(parts[0]), int.Parse(parts[1]));
            }
            using (var mark = new SpinningMark())
            using (var frame = new Bitmap(testSize.Width, testSize.Height, PixelFormat.Format32bppRgb))
            using (var g = Graphics.FromImage(frame))
            {
                foreach (double t in new[] { 0.0, 2.5, 5.0, 7.5 })
                {
                    mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), t, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, new Rectangle(14, 40, 240, 820), new Rectangle(1026, 40, 240, 820));
                    frame.Save(System.IO.Path.Combine(folder, "home-mark-" + (int)(t * 10) + ".png"));
                }
                // Turning the mark by hand: take hold of it in the middle, drag right and a little
                // down over a third of a second, let go, and let it coast.
                {
                    mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 10, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                    frame.Save(System.IO.Path.Combine(folder, "home-drag-0.png"));
                    var middle = new Point(testSize.Width / 2, testSize.Height / 2);
                    bool held = mark.HitMark(middle);
                    log.AppendLine("Mark drag: " + (held ? "taken hold of in the middle" : "not on the mark in the middle"));
                    if (held)
                    {
                        mark.BeginDrag(middle);
                        for (int i = 1; i <= 10; i++)
                        {
                            System.Threading.Thread.Sleep(30);
                            mark.Drag(new Point(middle.X + i * 24, middle.Y + i * 8));
                            mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 10 + i * 0.03, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                        }
                        frame.Save(System.IO.Path.Combine(folder, "home-drag-1.png"));
                        mark.EndDrag();
                        for (int i = 1; i <= 15; i++)
                            mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 10.3 + i * 0.033, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                        frame.Save(System.IO.Path.Combine(folder, "home-drag-2.png"));
                        log.AppendLine("Mark drag: still coasting half a second after letting go: " + mark.Interacting);
                    }
                }
                int videoArg = Array.IndexOf(Environment.GetCommandLineArgs(), "--video");
                if (videoArg > 0) VideoPath = Environment.GetCommandLineArgs()[videoArg + 1];
                int pictureArg = Array.IndexOf(Environment.GetCommandLineArgs(), "--picture");
                if (pictureArg > 0) PicturePath = Environment.GetCommandLineArgs()[pictureArg + 1];
                // --grid 600x310 and --motion "<layout>" set the picture's own grid and motion.
                var args = Environment.GetCommandLineArgs();
                int gridArg = Array.IndexOf(args, "--grid"), motionArg = Array.IndexOf(args, "--motion");
                if (CurrentPicture != null && gridArg > 0)
                {
                    string[] size = args[gridArg + 1].Split('x');
                    CurrentPicture.Columns = int.Parse(size[0]); CurrentPicture.Rows = int.Parse(size[1]);
                }
                if (CurrentPicture != null && motionArg > 0) CurrentPicture.Motion = args[motionArg + 1];
                int pointsArg = Array.IndexOf(args, "--points");
                if (CurrentPicture != null && pointsArg > 0) CurrentPicture.Points = float.Parse(args[pointsArg + 1], System.Globalization.CultureInfo.InvariantCulture);
                CacheFolder = System.IO.Path.Combine(folder, "cache");
                // The real loopback capture starts (whatever is playing, possibly silence).
                for (int i = 0; i < 20; i++) { AudioMeter.Read(); System.Threading.Thread.Sleep(100); }
                log.AppendLine("Audio meter: " + (AudioMeter.Capturing ? "capturing at " + AudioMeter.SampleRate + " Hz, level " + AudioMeter.Level.ToString("F2") : "not capturing (" + (AudioMeter.LastError ?? "starting") + ")"));
                // SOUND WAVE renders with fixed bands (bass-heavy music) instead of the speakers.
                AudioMeter.TestBands = new float[AudioMeter.BandCount];
                for (int b = 0; b < AudioMeter.BandCount; b++) AudioMeter.TestBands[b] = (float)Math.Max(0.05, 0.95 - b * 0.016 + 0.25 * Math.Sin(b * 1.7));
                // The window's music bars with the same bands (and a kick), each drawn 12 pixels
                // tall to be seen: the top one grows from the right, the bottom one from the left.
                using (var strip = new Bitmap(1280, 30))
                using (var sg = Graphics.FromImage(strip))
                {
                    sg.Clear(UiPalette.Canvas);
                    for (int i = 0; i < 6; i++) { MusicBar.Update(true); System.Threading.Thread.Sleep(16); }
                    foreach (bool right in new[] { true, false })
                        using (var bar = new MusicBar { FromRight = right, Size = new Size(1280, 12) })
                        using (var one = new Bitmap(1280, 12))
                        {
                            bar.DrawToBitmap(one, new Rectangle(0, 0, 1280, 12));
                            sg.DrawImage(one, 0, right ? 2 : 16);
                        }
                    strip.Save(System.IO.Path.Combine(folder, "music-bar.png"));
                }
                for (int scene = 1; scene < SceneNames.Length; scene++)
                {
                    SetScene(scene, false);
                    if (scene == VideoScene && VideoPath != null)
                    {
                        // Let the decoder start and run for a few seconds.
                        for (int i = 0; i < 40; i++) { mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty); System.Threading.Thread.Sleep(100); }
                        log.AppendLine("Video: " + (VideoError ?? "ok") + " " + video.SourceWidth + "x" + video.SourceHeight);
                        for (int i = 0; i < 100 && !video.FigureDone; i++) System.Threading.Thread.Sleep(100);
                        var figure = video.Figure;
                        log.AppendLine("Figure: " + (figure == null ? "none" : figure.Width + "x" + figure.Height + ", bounds " + figure.Bounds));
                        if (figure != null)
                            using (var dump = new Bitmap(figure.Width, figure.Height))
                            {
                                for (int y = 0; y < figure.Height; y++)
                                    for (int x = 0; x < figure.Width; x++)
                                    {
                                        int at = y * figure.Width + x, l = figure.Mask[at] != 0 ? figure.Luma[at] : 0;
                                        dump.SetPixel(x, y, figure.Mask[at] != 0 ? Color.FromArgb(l, l, l) : Color.FromArgb(40, 0, 60));
                                    }
                                dump.Save(System.IO.Path.Combine(folder, "video-figure.png"));
                            }
                        for (int i = 0; i < 10; i++) { mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty); System.Threading.Thread.Sleep(100); }
                    }
                    if (scene == PictureScene && PicturePath != null)
                    {
                        var wait = System.Diagnostics.Stopwatch.StartNew();
                        for (int i = 0; i < 600 && (Picture == null || !Picture.Done || mark.pictureFigureAt < 0); i++)
                        {
                            mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                            System.Threading.Thread.Sleep(50);
                        }
                        // Past the fade-in.
                        for (int i = 0; i < 20; i++) mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 0.1 * i, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                        var feed = Picture;
                        log.AppendLine("Picture: " + (feed.Error ?? "ok") + " " + feed.Width + "x" + feed.Height + ", ready in " + wait.ElapsedMilliseconds + " ms (cut " + feed.CutMilliseconds + " ms: models " + GrabCut.ModelTicks * 1000 / System.Diagnostics.Stopwatch.Frequency + " ms, max-flow " + GrabCut.FlowTicks * 1000 / System.Diagnostics.Stopwatch.Frequency + " ms)");
                        var figure = feed.Figure;
                        int area = 0;
                        if (figure != null) foreach (byte b in figure.Mask) area += b;
                        log.AppendLine("Picture figure: " + (figure == null ? "none" : "bounds " + figure.Bounds + ", " + (100.0 * area / (figure.Width * figure.Height)).ToString("F1") + "% of the picture"));
                        foreach (var eye in feed.Eyes ?? new PictureEye[0])
                            log.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Picture eye: lash centre {0:F0},{1:F0}, along {2:F2},{3:F2}, closes towards {4:F2},{5:F2}, half length {6:F1}, lash {7:F1}, opening {8:F1}",
                                eye.X, eye.Y, eye.AlongX, eye.AlongY, eye.CloseX, eye.CloseY, eye.HalfLength, eye.Lash, eye.Depth));
                        if (figure != null)
                            using (var dump = new Bitmap(figure.Width, figure.Height))
                            {
                                for (int y = 0; y < figure.Height; y++)
                                    for (int x = 0; x < figure.Width; x++)
                                    {
                                        int at = y * figure.Width + x, l = figure.Luma[at];
                                        int sway = feed.Sway == null ? 0 : (int)(feed.Sway[at] * 255);
                                        dump.SetPixel(x, y, figure.Mask[at] != 0 ? Color.FromArgb(Math.Max(l / 2, sway), l / 2 + 60, l / 2) : Color.FromArgb(Math.Min(255, 40 + feed.Sparkle[at]), 0, 60));
                                    }
                                using (var pen = new Pen(Color.Yellow))
                                using (var dg = Graphics.FromImage(dump))
                                    foreach (var eye in feed.Eyes ?? new PictureEye[0])
                                    {
                                        dg.DrawLine(pen, eye.X - eye.AlongX * eye.HalfLength, eye.Y - eye.AlongY * eye.HalfLength, eye.X + eye.AlongX * eye.HalfLength, eye.Y + eye.AlongY * eye.HalfLength);
                                        dg.DrawLine(Pens.Cyan, eye.X, eye.Y, eye.X + eye.CloseX * eye.Depth, eye.Y + eye.CloseY * eye.Depth);
                                    }
                                dump.Save(System.IO.Path.Combine(folder, "picture-figure.png"));
                            }
                    }
                    mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 3.0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                    frame.Save(System.IO.Path.Combine(folder, "home-scene-" + scene + ".png"));
                    if (scene == PictureScene && Picture != null && Picture.Figure != null)
                    {
                        // The breathing cycle a quarter apart, and a blink (lids shut, then half way).
                        var poseWatch = System.Diagnostics.Stopwatch.StartNew();
                        for (int i = 0; i < FigurePhases; i++) mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 100 + BreathSeconds * i / FigurePhases, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                        log.AppendLine("Picture breathing poses: " + FigurePhases + " built and drawn in " + poseWatch.ElapsedMilliseconds + " ms");
                        for (int q = 0; q < 4; q++)
                        {
                            mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 100 + BreathSeconds * q / 4, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                            frame.Save(System.IO.Path.Combine(folder, "home-picture-breath-" + q + ".png"));
                        }
                        foreach (float closure in new[] { 1F, 0.5F })
                        {
                            mark.BlinkOverride = closure;
                            var blinkWatch = System.Diagnostics.Stopwatch.StartNew();
                            mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 100, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                            if (closure == 1F) log.AppendLine("Picture frame while blinking: " + blinkWatch.ElapsedMilliseconds + " ms");
                            frame.Save(System.IO.Path.Combine(folder, "home-picture-blink-" + (int)(closure * 100) + ".png"));
                        }
                        mark.BlinkOverride = -1;
                        // The side code columns, which keep MARK + SEA's size on every background.
                        for (int i = 0; i < 6; i++)
                            mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 101 + i * 0.1, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, new Rectangle(14, 40, 240, testSize.Height - 80), new Rectangle(testSize.Width - 254, 40, 240, testSize.Height - 80));
                        frame.Save(System.IO.Path.Combine(folder, "home-picture-columns.png"));
                    }
                    if (scene == SoundScene)
                    {
                        // The same moment in silence, to compare how far the music lifts the water.
                        var music = AudioMeter.TestBands;
                        AudioMeter.TestBands = new float[AudioMeter.BandCount];
                        for (int i = 0; i < 20; i++) mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 2.0 + i * 0.05, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                        frame.Save(System.IO.Path.Combine(folder, "home-scene-" + scene + "-silent.png"));
                        // The music starts 0.1 s before the saved frame, so the bumps it raises show.
                        for (int i = 0; i < 20; i++)
                        {
                            AudioMeter.TestBands = i >= 18 ? music : new float[AudioMeter.BandCount];
                            mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 2.0 + i * 0.05, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                        }
                        AudioMeter.TestBands = music;
                        frame.Save(System.IO.Path.Combine(folder, "home-scene-" + scene + "-music.png"));
                        // Touching the water in silence: rest the cursor on it, sweep it across, let go.
                        AudioMeter.TestBands = new float[AudioMeter.BandCount];
                        var rest = new Point(testSize.Width / 2 - 200, testSize.Height * 3 / 4);
                        log.AppendLine("Water touch: " + (mark.OnWater(rest) ? "on the water" : "not on the water"));
                        double touchT = 20;
                        // The same moments untouched, drawn by a second mark, for comparison.
                        var calm = new SpinningMark();
                        var calmFrame = new Bitmap(testSize.Width, testSize.Height, PixelFormat.Format32bppRgb);
                        var calmGraphics = Graphics.FromImage(calmFrame);
                        Action<int> frames = count =>
                        {
                            for (int i = 0; i < count; i++)
                            {
                                touchT += 1 / 30.0;
                                mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), touchT, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                                calm.Render(calmGraphics, testSize, new Rectangle(Point.Empty, testSize), touchT, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                            }
                        };
                        frames(10);
                        frame.Save(System.IO.Path.Combine(folder, "home-touch-0.png"));
                        mark.TouchWater(rest);
                        frames(20);
                        frame.Save(System.IO.Path.Combine(folder, "home-touch-1.png"));
                        calmFrame.Save(System.IO.Path.Combine(folder, "home-touch-1-calm.png"));
                        log.AppendLine("Water touch at " + rest + ": " + mark.TouchReport());
                        for (int i = 1; i <= 18; i++) { mark.TouchWater(new Point(rest.X + i * 22, rest.Y)); frames(1); }
                        frame.Save(System.IO.Path.Combine(folder, "home-touch-2.png"));
                        calmFrame.Save(System.IO.Path.Combine(folder, "home-touch-2-calm.png"));
                        mark.ReleaseWater();
                        frames(12);
                        frame.Save(System.IO.Path.Combine(folder, "home-touch-3.png"));
                        calmFrame.Save(System.IO.Path.Combine(folder, "home-touch-3-calm.png"));
                        calmGraphics.Dispose(); calmFrame.Dispose(); calm.Dispose();
                        AudioMeter.TestBands = music;
                    }
                    if (scene == VideoScene && video != null && video.Figure != null)
                    {
                        mark.DumpFigure(System.IO.Path.Combine(folder, "video-figure-cells.png"));
                        // The breathing cycle: building every pose, then four poses a quarter cycle apart.
                        var poseWatch = System.Diagnostics.Stopwatch.StartNew();
                        for (int i = 0; i < FigurePhases; i++) mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 100 + BreathSeconds * i / FigurePhases, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                        log.AppendLine("Breathing poses: " + FigurePhases + " built and drawn in " + poseWatch.ElapsedMilliseconds + " ms");
                        for (int q = 0; q < 4; q++)
                        {
                            mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 100 + BreathSeconds * q / 4, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                            frame.Save(System.IO.Path.Combine(folder, "home-breath-" + q + ".png"));
                        }
                    }
                    var sceneWatch = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 30; i++) mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 3.0 + i / 12.0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                    log.AppendLine(SceneNames[scene] + " frame: " + (sceneWatch.Elapsed.TotalMilliseconds / 30).ToString("F2") + " ms");
                }
                // RINGS: writing themselves in, written, and just after a kick throws them forward.
                SetScene(RingScene, false);
                mark.SetRingsForTest(3);
                var ringWatch = System.Diagnostics.Stopwatch.StartNew();
                int ringFrames = 0;
                for (int i = 0; i <= 60; i++)
                {
                    mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 20.0 + i / 30.0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                    ringFrames++;
                    if (i == 15) frame.Save(System.IO.Path.Combine(folder, "home-rings-writing.png"));
                }
                frame.Save(System.IO.Path.Combine(folder, "home-rings.png"));
                log.AppendLine("RINGS frame: " + (ringWatch.Elapsed.TotalMilliseconds / ringFrames).ToString("F2") + " ms, " + mark.RingLetters + " letters");
                mark.KickRingsForTest();
                for (int i = 1; i <= 3; i++)
                    mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 22.0 + i / 30.0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                frame.Save(System.IO.Path.Combine(folder, "home-rings-kick.png"));
                // The wall behind the rings on its own.
                mark.WallOnlyForTest = true;
                mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 23.0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                frame.Save(System.IO.Path.Combine(folder, "home-rings-wall.png"));
                log.AppendLine("RINGS wall: " + mark.WallStats);
                // Eight seconds on (half a row's descent; frames are at most a quarter second apart).
                var wallWatch = System.Diagnostics.Stopwatch.StartNew();
                mark.WallLayTime();
                for (int i = 1; i <= 32; i++)
                    mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 23.0 + i * 0.25, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                mark.WallOnlyForTest = false;
                frame.Save(System.IO.Path.Combine(folder, "home-rings-wall-8s.png"));
                log.AppendLine("RINGS wall frame: " + (wallWatch.Elapsed.TotalMilliseconds / 32).ToString("F2") + " ms, laying the facets " + mark.WallLayTime());
                // The four corner rings alone.
                mark.SetRingsForTest(0);
                mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 32.0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                frame.Save(System.IO.Path.Combine(folder, "home-rings-corners.png"));
                // Spinning the text by hand: a slow drag (a fifth of the page's height a second) for
                // a second, a fast one (two heights a second) for a second, then letting go for a
                // second and a half; the extra speed every third of a second.
                {
                    var hand = new System.Text.StringBuilder("RINGS hand (extra speed every 1/3 s): slow");
                    double handT = 40.0, angle = 0;
                    var middle = new Point(testSize.Width / 2, testSize.Height / 2);
                    mark.BeginRingSpin(new Point(middle.X + 200, middle.Y));
                    for (int i = 1; i <= 105; i++)
                    {
                        if (i == 31) hand.Append(" | fast");
                        if (i == 61) { mark.EndRingSpin(); hand.Append(" | let go"); }
                        angle += testSize.Height * (i <= 30 ? 0.2 : 2.0) / 30 / 200;
                        mark.RingSpin(new Point(middle.X + (int)(200 * Math.Cos(angle)), middle.Y + (int)(200 * Math.Sin(angle))));
                        handT += 1 / 30.0;
                        mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), handT, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                        if (i % 10 == 0) hand.Append(" " + mark.RingHandBoost.ToString("F2"));
                    }
                    log.AppendLine(hand.ToString());
                }
                // Decoding from MARK + SEA to SOUND WAVE: frames a quarter of the way, halfway and
                // three quarters, and the cost of a frame while both scenes are drawn.
                SetScene(0, false);
                mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 4.0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                SetScene(SoundScene, true);
                var decodeWatch = System.Diagnostics.Stopwatch.StartNew();
                int decodeFrames = 0;
                double decodeMs = 0;
                foreach (int percent in new[] { 25, 50, 75 })
                {
                    while (decodeWatch.ElapsedMilliseconds < DecodeMs * percent / 100)
                    {
                        var one = System.Diagnostics.Stopwatch.StartNew();
                        mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), 4.0 + decodeWatch.Elapsed.TotalSeconds, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, Rectangle.Empty, Rectangle.Empty);
                        decodeMs += one.Elapsed.TotalMilliseconds; decodeFrames++;
                    }
                    frame.Save(System.IO.Path.Combine(folder, "home-decode-" + percent + ".png"));
                }
                log.AppendLine("Decode MARK + SEA -> SOUND WAVE: " + decodeFrames + " frames, " + (decodeMs / Math.Max(1, decodeFrames)).ToString("F2") + " ms each");
                SetScene(0, false);
                mark.ShowMark = false;
                foreach (double t in new[] { 0.0, 1.5 })
                {
                    mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), t, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, new Rectangle(14, 40, 240, 820), new Rectangle(1026, 40, 240, 820));
                    frame.Save(System.IO.Path.Combine(folder, "home-sea-" + (int)(t * 10) + ".png"));
                }
                mark.ShowMark = true;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 60; i++) mark.Render(g, testSize, new Rectangle(Point.Empty, testSize), i / 15.0, 1F, UiPalette.Canvas, new Rectangle[0], new int[0], new int[0], 0, new Rectangle(14, 40, 240, 820), new Rectangle(1026, 40, 240, 820));
                log.AppendLine("Mark frame: " + (watch.Elapsed.TotalMilliseconds / 60).ToString("F2") + " ms (1280x900 px).");
            }
            using (var home = new HomeView { Size = new Size(1280, 900) })
            using (var page = new Bitmap(1280, 900))
            {
                home.CreateControl();
                home.DrawToBitmap(page, new Rectangle(0, 0, 1280, 900));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 30; i++) home.DrawToBitmap(page, new Rectangle(0, 0, 1280, 900));
                log.AppendLine("Start page paint, cached text layer: " + (watch.Elapsed.TotalMilliseconds / 30).ToString("F2") + " ms (1280x900, DrawToBitmap).");
                watch.Restart();
                for (int i = 0; i < 10; i++) { home.Invalidate(); home.DrawToBitmap(page, new Rectangle(0, 0, 1280, 900)); }
                log.AppendLine("Start page paint, rebuilding text layer: " + (watch.Elapsed.TotalMilliseconds / 10).ToString("F2") + " ms.");
                page.Save(System.IO.Path.Combine(folder, "home-page.png"));
                foreach (float v in new[] { 0.4F, 1F })
                {
                    home.VeilOverride = v;
                    home.DrawToBitmap(page, new Rectangle(0, 0, 1280, 900));
                    page.Save(System.IO.Path.Combine(folder, "home-veil-" + (int)(v * 100) + ".png"));
                }
                home.VeilOverride = null;
            }
            using (var form = new System.Windows.Forms.Form { ClientSize = new Size(1280, 900), ShowInTaskbar = false,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new Point(0, 0), Opacity = 0.01 })
            using (var home = new HomeView())
            {
                form.Controls.Add(home);
                form.Show();
                home.Refresh();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 60; i++) home.PaintFrameNow();

                log.AppendLine("On-screen animation frame: " + (watch.Elapsed.TotalMilliseconds / 60).ToString("F2") + " ms (1280x900 layered test window; includes layered-window composition).");
                form.Close();
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "home-test.txt"), log.ToString());
            return 0;
        }
    }
}
