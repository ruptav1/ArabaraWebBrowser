using System;
using System.Collections.Generic;
using System.Drawing;

namespace Arabara
{
    // SOUND WAVE: a 3D wave grid that moves with the music, drawn in ASCII like the other
    // scenes. The surface is a lattice of points fixed to the water and turned 45 degrees
    // (diamonds at an angle), joined by straight lines that are drawn thin and turned into
    // characters by stroke direction (- _ / \ | x ...); farther lines are dimmer and slopes
    // facing the camera brighter. The camera stands high above the water and looks down at
    // it. The water itself moves with the music: a large rolling swell lifted by the bass,
    // slow hills and a fine chop raised by loudness; and pointed peaks: single lattice points
    // pulled up into pyramids. Kicks, snares, hats and the beat clock (from MusicAnalyzer)
    // each throw up a burst of peaks at random points - kicks the most and tallest, hats a
    // light sparkle - as many as the hit and the part of the song call for (a drop fills
    // the wave), and between hits points whose band is strong stand up too.
    internal sealed partial class SpinningMark
    {
        private const float ZNear = 1.5F, ZFar = 14F;
        // Music peaks: lattice points pulled up into pyramids. Each point follows one band of
        // the spectrum (fixed at random), and beats add bursts at random points.
        private readonly float[] fastBands = new float[AudioMeter.BandCount];
        private float[] waveLift = new float[0], wavePulse = new float[0];
        private int[] waveBand = new int[0];
        private float kickPunch;
        private int[] visible = new int[0];
        private int visibleCount;
        private readonly Random peakRandom = new Random();
        // The lattice is fixed to the water and turned 45 degrees about the vertical, so seen
        // at an angle its cells are diamonds; the waves travel through it.
        private const float Yaw = 0.7853982F, Step = 0.85F;
        private int waveCols, waveRows;
        private float[] wavePx = new float[0], wavePy = new float[0], waveLight = new float[0];
        private bool[] waveOk = new bool[0];
        private float[] waveInk = new float[0];
        private int inkWidth, inkHeight;
        private float waveBass, waveLoud;
        private double waveTime = -1;
        // Touching the water (view-only mode, see HomeView): the cursor presses a dent into the
        // water where it rests, and moving it pushes the water along, leaving a wake and
        // rings. The lattice carries a small damped wave simulation for this (height and
        // speed per point; waves run at 4 units a second and die down in about a second);
        // when the water is still and untouched it costs nothing.
        private const float CameraHeight = 5.4F, Pitch = 0.5F;
        private float[] touchH = new float[0], touchV = new float[0];
        private bool touchOn, touchStill = true;
        private Point touchAt;

        public void TouchWater(Point p) { touchAt = p; touchOn = true; }

        // --home-test: the deepest point of the touch waves and where it is drawn.
        internal string TouchReport()
        {
            int deepest = -1;
            for (int k = 0; k < touchH.Length; k++) if (deepest < 0 || touchH[k] < touchH[deepest]) deepest = k;
            if (deepest < 0) return "no touch waves";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "deepest {0:F2} at point {1},{2}, drawn at {3:F0},{4:F0}", touchH[deepest], deepest % waveCols, deepest / waveCols, wavePx[deepest], wavePy[deepest]);
        }
        public void ReleaseWater() { touchOn = false; }

        // The point on the (calm) water under a screen point, or false above the horizon or
        // past the water in view.
        public bool OnWater(Point p)
        {
            float x, z;
            return Scene == SoundScene && !SceneFading && width > 0 && ScreenToWater(p.X, p.Y, out x, out z);
        }

        private bool ScreenToWater(float px, float py, out float x, out float z)
        {
            float cosP = (float)Math.Cos(Pitch), sinP = (float)Math.Sin(Pitch), focal = width * 0.62F;
            float u = (px - width / 2F) / focal, v = (height * 0.36F - py) / focal;
            float below = sinP - v * cosP;
            x = z = 0;
            if (below <= 0.01F) return false;
            z = CameraHeight * (cosP + v * sinP) / below;
            x = u * (z * cosP + CameraHeight * sinP);
            return z >= ZNear && z <= ZFar;
        }

        // Advances the touch waves by dt (in steps of at most a sixtieth of a second).
        private void StepTouch(double dt, float minA, float minB, float cosY, float sinY, float centre)
        {
            int w = waveCols, h = waveRows, n = w * h;
            if (touchH.Length != n) { touchH = new float[n]; touchV = new float[n]; touchStill = true; }
            float x = 0, z = 0, ta = 0, tb = 0;
            bool pressing = touchOn && ScreenToWater(touchAt.X, touchAt.Y, out x, out z);
            if (pressing)
            {
                float dz = z - centre;
                ta = (x * cosY + dz * sinY - minA) / Step; tb = (-x * sinY + dz * cosY - minB) / Step;
            }
            if (!pressing && touchStill) return;
            int steps = Math.Max(1, (int)Math.Ceiling(dt * 60));
            float sdt = (float)(dt / steps);
            const float Speed = 4F, Damping = 1.2F, Depth = 1.8F, Sigma = 1.5F, Grip = 40F;
            float c2 = Speed * Speed / (Step * Step), sigma2 = 2 * (Sigma / Step) * (Sigma / Step);
            float largest = 0;
            for (int s = 0; s < steps; s++)
            {
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int k = j * w + i;
                        float here = touchH[k];
                        float left = i > 0 ? touchH[k - 1] : here, right = i < w - 1 ? touchH[k + 1] : here;
                        float up = j > 0 ? touchH[k - w] : here, down = j < h - 1 ? touchH[k + w] : here;
                        float accel = c2 * (left + right + up + down - 4 * here) - Damping * touchV[k];
                        if (pressing)
                        {
                            // The dent under the cursor: the water there is pulled towards it.
                            float di = i - ta, dj = j - tb, r2 = di * di + dj * dj;
                            if (r2 < sigma2 * 4)
                            {
                                float g = (float)Math.Exp(-r2 / sigma2);
                                accel += (-Depth * g - here) * Grip * g;
                            }
                        }
                        touchV[k] += accel * sdt;
                    }
                for (int k = 0; k < n; k++)
                {
                    touchH[k] += touchV[k] * sdt;
                    largest = Math.Max(largest, Math.Abs(touchH[k]) + Math.Abs(touchV[k]) * 0.1F);
                }
            }
            touchStill = !pressing && largest < 0.003F;
            if (touchStill) { Array.Clear(touchH, 0, n); Array.Clear(touchV, 0, n); }
        }

        // The rise of the touch waves towards the camera at point (i, j), for shading.
        private float TouchSlope(int i, int j, float cosY, float sinY)
        {
            if (touchStill) return 0;
            int w = waveCols, k = j * w + i;
            float da = ((i < w - 1 ? touchH[k + 1] : touchH[k]) - (i > 0 ? touchH[k - 1] : touchH[k])) / (2 * Step);
            float db = ((j < waveRows - 1 ? touchH[k + w] : touchH[k]) - (j > 0 ? touchH[k - w] : touchH[k])) / (2 * Step);
            return -(da * sinY + db * cosY);
        }
        // Darkest to brightest, ending in the highlight.
        private static readonly int[] WaveTones = { 4, 1, 5, 2, 6, 7, 8 };

        private void DrawSoundWave(double t, float fade, IList<Rectangle> panes, float veil)
        {
            float[] bands = AudioMeter.Read();
            // The swell and the chop follow the music quickly: up in about 0.03 s, back in
            // about 0.15 s, timed by the clock so the frame rate does not matter.
            double dt = waveTime < 0 ? 1 : Math.Max(0, Math.Min(0.25, t - waveTime));
            waveTime = t;
            float rise = (float)(1 - Math.Exp(-dt / 0.03)), fall = (float)(1 - Math.Exp(-dt / 0.15));
            float peakFall = (float)(1 - Math.Exp(-dt / 0.12));
            for (int b = 0; b < fastBands.Length; b++) fastBands[b] += (bands[b] - fastBands[b]) * (bands[b] > fastBands[b] ? rise : peakFall);
            waveBass += (AudioMeter.Bass - waveBass) * (AudioMeter.Bass > waveBass ? rise : fall);
            waveLoud += (AudioMeter.Level - waveLoud) * (AudioMeter.Level > waveLoud ? rise : fall);

            // Camera high above the water, pitched well down (CameraHeight, Pitch).
            float cosP = (float)Math.Cos(Pitch), sinP = (float)Math.Sin(Pitch);
            float focal = width * 0.62F, cx = width / 2F, cy = height * 0.36F;
            // The lattice covers the visible stretch of water (a trapezoid from the near to the
            // far edge), measured in the turned lattice's own axes.
            float cosY = (float)Math.Cos(Yaw), sinY = (float)Math.Sin(Yaw), centre = (ZNear + ZFar) / 2;
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            foreach (float z in new[] { ZNear, ZFar })
                foreach (float side in new[] { -1F, 1F })
                {
                    float x = side * Reach(z, cosP, sinP, CameraHeight, focal), dz = z - centre;
                    float la = x * cosY + dz * sinY, lb = -x * sinY + dz * cosY;
                    minA = Math.Min(minA, la); maxA = Math.Max(maxA, la); minB = Math.Min(minB, lb); maxB = Math.Max(maxB, lb);
                }
            waveCols = Math.Min(90, (int)Math.Ceiling((maxA - minA) / Step) + 1);
            waveRows = Math.Min(90, (int)Math.Ceiling((maxB - minB) / Step) + 1);
            int points = waveCols * waveRows;
            if (wavePx.Length != points)
            {
                wavePx = new float[points]; wavePy = new float[points]; waveLight = new float[points]; waveOk = new bool[points];
                waveLift = new float[points]; wavePulse = new float[points]; waveBand = new int[points];
                // Each point listens to one band, mostly low ones.
                for (int k = 0; k < points; k++) waveBand[k] = Math.Min(AudioMeter.BandCount - 1, (int)(Math.Pow(peakRandom.NextDouble(), 1.4) * AudioMeter.BandCount));
            }
            UpdatePeaks(dt);
            StepTouch(dt, minA, minB, cosY, sinY, centre);
            for (int j = 0; j < waveRows; j++)
                for (int i = 0; i < waveCols; i++)
                {
                    float la = minA + i * Step, lb = minB + j * Step;
                    float x = la * cosY - lb * sinY, z = centre + la * sinY + lb * cosY;
                    int k = j * waveCols + i;
                    // Only the water in view (with a margin) is drawn.
                    float reach = Reach(z, cosP, sinP, CameraHeight, focal);
                    if (z < ZNear - Step || z > ZFar + Step || Math.Abs(x) > reach + Step * 2) { waveOk[k] = false; continue; }
                    double slope;
                    float y = WaveHeight(x, z, t, out slope) + waveLift[k];
                    if (!touchStill) { y += touchH[k]; slope += TouchSlope(i, j, cosY, sinY); }
                    float nearness = Math.Max(0F, Math.Min(1F, 1 - (z - ZNear) / (ZFar - ZNear)));
                    float dy = y - CameraHeight;
                    float yc = dy * cosP + z * sinP, zc = z * cosP - dy * sinP;
                    waveOk[k] = zc > 0.3F;
                    wavePx[k] = cx + x / zc * focal;
                    wavePy[k] = cy - yc / zc * focal;
                    float shade = (float)Math.Max(0.55, Math.Min(1.2, 0.85 - slope * 0.45));
                    // The far field fades quickly, where the diamonds crowd together.
                    waveLight[k] = Math.Max(0F, Math.Min(1F, (0.12F + 0.88F * nearness * (float)Math.Sqrt(nearness)) * shade * (0.9F + 0.08F * y) * (1 + 0.35F * waveLift[k])));
                }

            // The lattice is drawn as thin anti-aliased lines into an ink buffer,
            // which is then turned into characters by shape (the same matching as VIDEO LOOP):
            // each cell picks the glyph whose strokes sit where the lines cross it.
            // At half resolution: plenty to tell a line's direction within a character cell.
            inkWidth = (width + 1) / 2; inkHeight = (height + 1) / 2;
            int pixelCount = inkWidth * inkHeight;
            if (waveInk.Length != pixelCount) waveInk = new float[pixelCount];
            else Array.Clear(waveInk, 0, pixelCount);
            for (int j = waveRows - 1; j >= 0; j--)
                for (int i = 0; i < waveCols; i++)
                {
                    int k = j * waveCols + i;
                    if (i + 1 < waveCols) InkSegment(k, k + 1);
                    if (j + 1 < waveRows) InkSegment(k, k + waveCols);
                }
            // Each cell becomes the stroke its ink shows: the spread of the ink (its second
            // moments, measured in cell proportions) gives the direction of the line through
            // the cell, and that picks - _ | / \ ; ink spread both ways is a crossing (x),
            // a speck is a dot. The level sets the tone.
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int left = originX + c * cellW, top = originY + r * cellH;
                    float w = 0, peak = 0, mx = 0, my = 0, mxx = 0, myy = 0, mxy = 0;
                    int ix0 = Math.Max(0, left / 2), ix1 = Math.Min(inkWidth, (left + cellW + 1) / 2);
                    int iy0 = Math.Max(0, top / 2), iy1 = Math.Min(inkHeight, (top + cellH + 1) / 2);
                    float spanX = Math.Max(1, ix1 - ix0), spanY = Math.Max(1, iy1 - iy0);
                    for (int py = iy0; py < iy1; py++)
                    {
                        float fy = (py - iy0 + 0.5F) / spanY;
                        int line = py * inkWidth;
                        for (int px = ix0; px < ix1; px++)
                        {
                            float v = waveInk[line + px];
                            if (v <= 0.02F) continue;
                            float fx = (px - ix0 + 0.5F) / spanX;
                            w += v; mx += v * fx; my += v * fy; mxx += v * fx * fx; myy += v * fy * fy; mxy += v * fx * fy;
                            if (v > peak) peak = v;
                        }
                    }
                    if (w <= 0 || peak < 0.05F) continue;
                    float level = Math.Min(1.15F, peak * 1.5F) * fade;
                    if (level < 0.06F) continue;
                    mx /= w; my /= w;
                    float cxx = mxx / w - mx * mx, cyy = myy / w - my * my, cxy = mxy / w - mx * my;
                    float spread = cxx + cyy, ani = spread <= 1e-6F ? 0 : (float)Math.Sqrt((cxx - cyy) * (cxx - cyy) + 4 * cxy * cxy) / spread;
                    char glyph;
                    if (spread < 0.004F) glyph = '.';
                    else if (ani < 0.45F) glyph = 'x'; // the turned lattice's lines cross diagonally
                    else
                    {
                        double angle = 0.5 * Math.Atan2(2 * cxy, cxx - cyy) * 180 / Math.PI; // 0 = across, 90 = up/down
                        double a = Math.Abs(angle);
                        if (a < 22) glyph = my > 0.72F ? '_' : '-';
                        else if (a > 68) glyph = '|';
                        else glyph = angle > 0 ? '\\' : '/';
                    }
                    int tone = WaveTones[Math.Min(WaveTones.Length - 1, (int)(level / 1.15F * WaveTones.Length))];
                    int cx2 = left + cellW / 2, cy2 = top + cellH / 2;
                    if (veil <= 0 || CellThreshold(c, r) >= veil * (1 + VeilFront))
                        foreach (var pane in panes) if (pane.Contains(cx2, cy2)) { tone += Shades.Length; break; }
                    Blit(left, top, glyph, tone);
                }
        }

        private void InkSegment(int a, int b)
        {
            if (!waveOk[a] || !waveOk[b]) return;
            float light = (waveLight[a] + waveLight[b]) / 2;
            if (light < 0.04F) return;
            InkLine(wavePx[a], wavePy[a], wavePx[b], wavePy[b], light);
        }

        // Xiaolin Wu's anti-aliased line into the ink buffer (brightest stroke wins a pixel).
        private void InkLine(float x0, float y0, float x1, float y1, float ink)
        {
            x0 *= 0.5F; y0 *= 0.5F; x1 *= 0.5F; y1 *= 0.5F; // screen pixels to ink pixels
            if ((x0 < 0 && x1 < 0) || (x0 >= inkWidth && x1 >= inkWidth) || (y0 < 0 && y1 < 0) || (y0 >= inkHeight && y1 >= inkHeight)) return;
            bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
            if (steep) { float s = x0; x0 = y0; y0 = s; s = x1; x1 = y1; y1 = s; }
            if (x0 > x1) { float s = x0; x0 = x1; x1 = s; s = y0; y0 = y1; y1 = s; }
            float dx = x1 - x0, gradient = dx < 0.001F ? 1F : (y1 - y0) / dx;
            int limit = steep ? inkHeight : inkWidth;
            int start = Math.Max(0, (int)Math.Round(x0)), end = Math.Min(limit - 1, (int)Math.Round(x1));
            float y = y0 + gradient * (start - x0);
            for (int x = start; x <= end; x++, y += gradient)
            {
                int iy = (int)Math.Floor(y);
                float f = y - iy;
                if (steep) { Ink(iy, x, (1 - f) * ink); Ink(iy + 1, x, f * ink); }
                else { Ink(x, iy, (1 - f) * ink); Ink(x, iy + 1, f * ink); }
            }
        }

        private void Ink(int x, int y, float value)
        {
            if (x < 0 || y < 0 || x >= inkWidth || y >= inkHeight) return;
            int i = y * inkWidth + x;
            if (value > waveInk[i]) waveInk[i] = value;
        }

        // Half the width of water in view at distance z, a little beyond the screen edges.
        private float Reach(float z, float cosP, float sinP, float cameraHeight, float focal)
        {
            return (z * cosP + cameraHeight * sinP) * width / (2 * focal) * 1.15F;
        }

        // Water height at (x, z): a rolling swell (lifted by the bass), slow hills, a fine
        // chop (raised by loudness, near the camera only). Slope is the rise towards the
        // camera, for shading. The music peaks are added per lattice point.
        private float WaveHeight(float x, float z, double t, out double slope)
        {
            float nearness = Math.Max(0F, Math.Min(1F, 1 - (z - ZNear) / (ZFar - ZNear)));
            double swellPhase = 0.34 * z - 0.85 * t + 0.2 * x, hillPhase = 0.3 * z - 0.15 * t + 0.5 * Math.Sin(0.15 * x);
            double swellAmp = 0.8 * (1 + 1.1 * waveBass * Math.Min(2F, Math.Max(0.4F, AudioMeter.Section))) * (1 + 0.5 * kickPunch), hillAmp = 0.55 * Math.Sin(0.36 * x + 0.2 * t);
            double chop = 0.08 * Math.Sin(1.4 * x + 1.1 * z - 2.4 * t) * (0.35 + 1.1 * waveLoud) * nearness;
            slope = swellAmp * 0.34 * Math.Cos(swellPhase) - hillAmp * 0.3 * Math.Sin(hillPhase);
            double y = swellAmp * Math.Sin(swellPhase) + hillAmp * Math.Cos(hillPhase) + chop;
            return (float)y;
        }

        // The peaks. Every kick throws up a burst of random points - more and taller the
        // stronger the kick and the bigger the part of the song (at a drop about half the
        // water in view) - and gives the big swell a short punch; snares throw up a smaller
        // burst, hats a light sparkle of low peaks, and a clock beat with no kick of its own a
        // medium one, so a four-on-the-floor drop keeps pumping even when the kick is buried.
        // In a bridge the bursts shrink to a few. Peaks fall back in about 0.16 s. Between
        // hits, points whose band passes a threshold stand up; the threshold drops as the
        // song builds.
        private void UpdatePeaks(double dt)
        {
            int points = waveLift.Length;
            if (points == 0) return;
            float drive = Math.Max(0.3F, Math.Min(3F, AudioMeter.Section)), power = (float)Math.Pow(drive, 1.5);
            float decay = (float)Math.Exp(-dt / 0.16);
            for (int k = 0; k < points; k++) wavePulse[k] *= decay;
            kickPunch *= (float)Math.Exp(-dt / 0.15);
            // Bursts pick among the points in view (from the last frame), so the share is of
            // the water on screen.
            if (visible.Length != points) visible = new int[points];
            visibleCount = 0;
            for (int k = 0; k < points; k++) if (waveOk[k]) visible[visibleCount++] = k;
            var events = AudioMeter.TakeEvents();
            if (events.Kicks > 0)
            {
                Burst(Math.Min(0.5F, (0.08F + 0.14F * events.Kick) * power), (0.8F + 0.5F * drive) * (0.7F + 0.5F * events.Kick));
                kickPunch = Math.Max(kickPunch, 0.4F + 0.6F * Math.Min(1F, events.Kick));
            }
            else if (events.Beats > 0)
            {
                Burst(Math.Min(0.3F, 0.1F * power), 0.6F + 0.35F * drive);
                kickPunch = Math.Max(kickPunch, 0.3F);
            }
            if (events.Snares > 0) Burst(Math.Min(0.3F, (0.05F + 0.08F * events.Snare) * power), (0.55F + 0.35F * drive) * (0.7F + 0.5F * events.Snare));
            if (events.Hats > 0) Burst(Math.Min(0.12F, (0.02F + 0.03F * events.Hat) * drive), 0.3F + 0.15F * drive);
            float threshold = Math.Max(0.25F, Math.Min(0.7F, 0.5F - 0.15F * (drive - 1)));
            for (int k = 0; k < points; k++)
            {
                float v = fastBands[waveBand[k]], sustain = v > threshold ? (v - threshold) / (1 - threshold) * (0.6F + 0.45F * drive) : 0;
                waveLift[k] = wavePulse[k] + sustain * (1.4F - 0.8F * waveBand[k] / AudioMeter.BandCount);
            }
        }

        // Raises a share of the points in view, chosen at random, to about the given height.
        private void Burst(float share, float height)
        {
            int count = (int)(visibleCount * share);
            for (int n = 0; n < count; n++)
            {
                int k = visible[peakRandom.Next(visibleCount)];
                float h = height * (0.5F + 0.5F * (float)peakRandom.NextDouble());
                if (h > wavePulse[k]) wavePulse[k] = h;
            }
        }
    }
}
