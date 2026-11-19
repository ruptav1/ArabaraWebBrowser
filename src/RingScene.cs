using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Arabara
{
    // RINGS: rings of text turning in 3D, after the text rings of esports title cards. ARABARA's
    // name runs around each ring as a band, its letters upright along the band's height, so the
    // near half reads normally and the far half shows the band from behind, mirrored and dimmer;
    // nearer letters are larger and brighter, and a band seen edge-on squeezes its letters. Each
    // letter is drawn by mapping the screen back onto its glyph (an affine map from the ring's
    // projected tangent and axis), which also mirrors it exactly where the band turns away.
    // Two rings around the middle at first, and one more every 8 bars of the music (32 beats),
    // or every 16 seconds without it, up to six, which then stay; a new ring writes itself in
    // letter by letter. Four more rings sit at the corners of the page and stay where they are:
    // larger, ovals rather than circles, facing the viewer with the tip of the oval pointing at
    // the middle and leaning out a little in 3D, so the same part of each is always on the page;
    // only their text moves, running along the outline, its letters spaced evenly by arc length,
    // growing as they come near and shrinking as they go away. Behind them all a faceted wall of rock keeps coming down (see
    // DrawWall).
    // With the music terminal playing (not an ad) the capture hears only Arabara's own sound
    // (AudioMeter.OwnAudio, WebView2's processes), and every beat of the analyzer's beat clock
    // (every kick while the tempo is not held) throws the rings forward: their speed jumps six
    // times and eases back within about a third of a beat. Without the music the rings turn at
    // their own pace and nothing is captured for them.
    internal sealed partial class SpinningMark
    {
        // Whether the music terminal is playing music (not an ad); set by the browser window.
        public static Func<bool> MusicPlaying;

        private const string RingText = "ARABARA // WEB TERMINAL // ";
        private const double RingIdleSeconds = 16, RingWriteSeconds = 1.2;
        // Two to six rings around the middle (one more with each change, up to six), and one ring
        // fixed at each corner of the page (in halves of the page's width and height), showing
        // only part of itself (see AddCornerLetters).
        private const int RingFewest = 2, RingMost = 6;
        private static readonly double[,] RingCorners = { { -0.9, -0.85 }, { 0.9, -0.85 }, { -0.9, 0.85 }, { 0.9, 0.85 } };
        // The corner rings' size (of half the page's shorter side) and their minor axis as a share of the major.
        private const double CornerRadius = 1.05, CornerSquash = 0.62;
        // How far a corner ring's tip leans out towards the viewer, and how far it is turned about its long axis (radians).
        private const double CornerTip = 0.5, CornerLean = 0.6;

        private sealed class Ring
        {
            public double Radius;           // of half the page's shorter side (beyond 1 it runs off the page)
            public double OffsetX, OffsetY; // where its centre sits, in halves of the page's width and height
            public int Corner = -1;         // which of RingCorners, or -1 around the middle
            public double Tilt, Turn, Roll; // radians: leaning back, turned about the vertical, rolled on the page
            public double TurnRate;         // radians a second
            public double Phase, Speed;     // where the text is along the ring, and its pace (radians a second)
            public double Presence, Target; // how much of the ring is written (0..1), and where that is heading
            // An oval: its minor axis is Squash times its radius, and Arc (the share of the outline
            // reached at 2 pi i / N) spaces its letters evenly along the outline; null for a circle.
            public double Squash = 1, Perimeter = 2 * Math.PI;
            public double[] Arc;

            public void Shape(double squash)
            {
                const int N = 256;
                Squash = squash;
                Arc = new double[N + 1];
                double total = 0;
                for (int i = 1; i <= N; i++)
                {
                    double th = 2 * Math.PI * (i - 0.5) / N;
                    total += Math.Sqrt(Math.Sin(th) * Math.Sin(th) + squash * squash * Math.Cos(th) * Math.Cos(th)) * 2 * Math.PI / N;
                    Arc[i] = total;
                }
                Perimeter = total;
                for (int i = 1; i <= N; i++) Arc[i] /= total;
            }

            // The angle at a share f (0..1) of the way round the outline.
            public double AngleAt(double f)
            {
                if (Arc == null) return 2 * Math.PI * f;
                int lo = 0, hi = Arc.Length - 1;
                while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Arc[mid] <= f) lo = mid; else hi = mid; }
                double span = Arc[hi] - Arc[lo];
                return 2 * Math.PI * (lo + (span > 0 ? (f - Arc[lo]) / span : 0)) / (Arc.Length - 1);
            }
        }

        private struct RingGlyph
        {
            public float X, Y, Depth, Rx, Ry, Dx, Dy;
            public char Glyph;
            public int Tone;
        }

        private readonly List<Ring> rings = new List<Ring>();
        private readonly List<RingGlyph> ringGlyphs = new List<RingGlyph>();
        private readonly Random ringRandom = new Random();
        private readonly Dictionary<char, byte[]> ringMasks = new Dictionary<char, byte[]>();
        private Font ringFont;
        private int ringMaskW, ringMaskH, ringFill;
        private double ringLastT = -1, ringChangeAt;
        private readonly RingDrive drive = new RingDrive();

        // ---- Spinning the text by hand (start page in view-only mode, see HomeView): press
        // anywhere and move the mouse, and the text runs faster along every ring, in proportion
        // to the mouse's speed and without limit: HandGain times its own speed more for each page
        // height a second the mouse moves (smoothed over a tenth of a second). The speed follows
        // through two smoothing stages, so it gathers pace gently and with a slight delay (about
        // half a second), and slips back over about a second once the mouse stops or lets go.
        private const double HandGain = 6;
        private bool ringHeld;
        private Point ringHandFrom;
        private double ringHandTravel, ringHandSpeed, ringHandMid, ringHandBoost;

        public bool RingSpinning { get { return ringHeld; } }
        public bool OnRings { get { return Scene == RingScene && !SceneFading; } }
        public void BeginRingSpin(Point p) { ringHeld = true; ringHandFrom = p; ringHandTravel = 0; }
        public void EndRingSpin() { ringHeld = false; }

        public void RingSpin(Point p)
        {
            if (!ringHeld) return;
            double dx = p.X - ringHandFrom.X, dy = p.Y - ringHandFrom.Y;
            ringHandTravel += Math.Sqrt(dx * dx + dy * dy);
            ringHandFrom = p;
        }

        // Each frame: the mouse's travel since the last one becomes its speed, and the extra speed follows.
        private void FollowHand(double dt)
        {
            if (dt <= 0) return;
            double moved = ringHandTravel / Math.Max(1, height) / dt;
            ringHandTravel = 0;
            ringHandSpeed += (moved - ringHandSpeed) * (1 - Math.Exp(-dt / 0.1));
            double target = ringHeld ? HandGain * ringHandSpeed : 0;
            double ease = 1 - Math.Exp(-dt / (target > ringHandBoost ? 0.22 : 0.45));
            ringHandMid += (target - ringHandMid) * ease;
            ringHandBoost += (ringHandMid - ringHandBoost) * ease;
            if (!ringHeld && ringHandBoost < 0.001 && ringHandMid < 0.001) ringHandBoost = ringHandMid = ringHandSpeed = 0;
        }

        // --home-test: how much faster the hand makes the text run.
        internal double RingHandBoost { get { return ringHandBoost; } }

        // --home-test: the rings' count (letters drawn) in the last frame.
        internal int RingLetters { get { return ringGlyphs.Count; } }
        internal void KickRingsForTest() { drive.Boost = RingDrive.Jump; }
        // --home-test: draw only the wall, and how it lies on the page.
        internal bool WallOnlyForTest;
        private long wallLayTicks, wallLays;
        // --home-test: the average time laying the wall into the cells took, since last asked.
        internal string WallLayTime()
        {
            double ms = wallLays == 0 ? 0 : wallLayTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / wallLays;
            wallLayTicks = 0; wallLays = 0;
            return ms.ToString("F2") + " ms";
        }
        internal string WallStats
        {
            get
            {
                if (wallCell == null) return "not laid";
                int covered = 0, creases = 0;
                for (int i = 0; i < wallCell.Length; i++) { if (wallCell[i] >= 0) covered++; if (wallAcross[i] >= 0) creases++; }
                return wallShade.Length + " facets, " + (100.0 * covered / wallCell.Length).ToString("F0") + "% of cells covered, " + (100.0 * creases / wallCell.Length).ToString("F0") + "% on edges";
            }
        }
        internal void SetRingsForTest(int count)
        {
            rings.Clear();
            for (int i = 0; i < count + 4; i++) { var ring = NewRing(1, i < 4 ? i : -1); ring.Presence = 1; rings.Add(ring); }
        }

        private void DrawRings(double t, float fade, IList<Rectangle> panes, float veil)
        {
            double dt = ringLastT < 0 || t < ringLastT ? 0 : Math.Min(0.25, t - ringLastT);
            ringLastT = t;
            if (rings.Count == 0) { for (int i = 0; i < 4 + RingFewest; i++) rings.Add(NewRing(1, i < 4 ? i : -1)); ringChangeAt = t + RingIdleSeconds; }

            // The music terminal's music, heard alone, drives the rings beat by beat.
            bool playing = MusicPlaying != null && MusicPlaying() && AudioMeter.OwnAudioProcess != 0;
            AudioMeter.OwnAudio = playing;
            if (playing)
            {
                AudioMeter.Read();
                if (drive.Update(dt, AudioMeter.TakeEvents(), AudioMeter.Locked, AudioMeter.Bpm)) NextRingCount();
                ringChangeAt = t + RingIdleSeconds;
            }
            else
            {
                drive.Update(dt, new MusicEvents(), false, 0);
                if (t >= ringChangeAt) { ringChangeAt = t + RingIdleSeconds; NextRingCount(); }
            }
            double ringBoost = drive.Boost;
            FollowHand(dt);

            for (int i = rings.Count - 1; i >= 0; i--)
            {
                var ring = rings[i];
                ring.Phase += ring.Speed * (1 + ringBoost + ringHandBoost) * dt;
                // Kept within a turn (it can run very fast by hand), where it reads the same.
                if (Math.Abs(ring.Phase) > 2 * Math.PI) ring.Phase = Math.IEEERemainder(ring.Phase, 2 * Math.PI);
                ring.Turn += ring.TurnRate * (1 + 0.25 * ringBoost) * dt;
                double step = dt / RingWriteSeconds;
                ring.Presence = ring.Target > ring.Presence ? Math.Min(ring.Target, ring.Presence + step) : Math.Max(ring.Target, ring.Presence - step);
                if (ring.Target <= 0 && ring.Presence <= 0) rings.RemoveAt(i);
            }

            EnsureRingFont();
            DrawWall(dt, ringBoost, fade, panes, veil);
            ringFill = canvas.ToArgb();
            if (WallOnlyForTest) return;
            float cx = width / 2F, cy = height / 2F, half = Math.Min(width, height) / 2F;
            double camera = 1.8 * Math.Max(width, height);
            // A letter's size on the band at the ring's centre depth: a little larger than the page's cells.
            double letterW = cellW * 1.35, letterH = cellH * 1.35, advance = letterW * 1.04;
            ringGlyphs.Clear();
            foreach (var ring in rings)
            {
                if (ring.Corner >= 0) { AddCornerLetters(ring, cx, cy, half, letterW, letterH, advance, fade, ringBoost); continue; }
                // The ring's frame: x and y span its plane, z is its axis (the band's height).
                double ct = Math.Cos(ring.Tilt), st = Math.Sin(ring.Tilt), cu = Math.Cos(ring.Turn), su = Math.Sin(ring.Turn), cr = Math.Cos(ring.Roll), sr = Math.Sin(ring.Roll);
                // M = Rz(roll) * Ry(turn) * Rx(tilt), as columns.
                double[] e0 = Rotate(1, 0, 0, ct, st, cu, su, cr, sr), e1 = Rotate(0, 1, 0, ct, st, cu, su, cr, sr), axis = Rotate(0, 0, 1, ct, st, cu, su, cr, sr);
                double radius = ring.Radius * half, minor = radius * ring.Squash;
                int letters = RingLetterCount(radius * ring.Perimeter, advance);
                int written = (int)Math.Round(ring.Presence * letters);
                // Which way the letters must face so the near half reads normally (see below).
                bool reverse = false;
                {
                    // Sample the nearest point: the outward direction most towards the viewer.
                    double best = double.MaxValue, bt = 0;
                    for (int k = 0; k < 16; k++)
                    {
                        double th = k * Math.PI / 8, z = radius * Math.Cos(th) * e0[2] + minor * Math.Sin(th) * e1[2];
                        if (z < best) { best = z; bt = th; }
                    }
                    double tx = -radius * Math.Sin(bt) * e0[0] + minor * Math.Cos(bt) * e1[0], ty = -radius * Math.Sin(bt) * e0[1] + minor * Math.Cos(bt) * e1[1];
                    // Letters are right along +tangent and down along -axis; on screen (y down) that
                    // reads normally when right x down is positive.
                    reverse = tx * -axis[1] - ty * -axis[0] < 0;
                }
                double sign = reverse ? -1 : 1;
                for (int k = 0; k < written; k++)
                {
                    int index = reverse ? (RingText.Length - k % RingText.Length) % RingText.Length : k % RingText.Length;
                    char glyph = RingText[index];
                    if (glyph == ' ') continue;
                    double share = (double)k / letters + ring.Phase / (2 * Math.PI);
                    double th = ring.AngleAt(share - Math.Floor(share));
                    double c = Math.Cos(th), s = Math.Sin(th);
                    double px = radius * c * e0[0] + minor * s * e1[0], py = radius * c * e0[1] + minor * s * e1[1], pz = radius * c * e0[2] + minor * s * e1[2];
                    double scale = camera / (camera + pz);
                    if (scale <= 0) continue;
                    // The outline's direction there (unit length in the ring's plane).
                    double ux = -radius * s, uy = minor * c, ul = Math.Sqrt(ux * ux + uy * uy);
                    double tx = sign * (ux * e0[0] + uy * e1[0]) / ul, ty = sign * (ux * e0[1] + uy * e1[1]) / ul;
                    // Nearness 0 (far) .. 1 (near); the band's far half is seen from behind.
                    double near = 0.5 - pz / (2 * radius);
                    bool behind = pz > 0;
                    double level = (0.25 + 0.75 * near) * (behind ? 0.55 : 1) * fade;
                    // The letter being written flares as the ring writes itself in.
                    if (written < letters && k == written - 1) level = Math.Max(level, 0.95 * fade);
                    level *= 1 + 0.25 * Math.Min(1, ringBoost / RingDrive.Jump);
                    if (level < 0.08) continue;
                    ringGlyphs.Add(new RingGlyph
                    {
                        X = (float)(cx + ring.OffsetX * cx + px * scale), Y = (float)(cy + ring.OffsetY * cy + py * scale), Depth = (float)pz,
                        Rx = (float)(tx * scale * letterW), Ry = (float)(ty * scale * letterW),
                        Dx = (float)(-axis[0] * scale * letterH), Dy = (float)(-axis[1] * scale * letterH),
                        Glyph = glyph, Tone = level < 0.35 ? 0 : level < 0.6 ? 1 : level < 0.9 ? 2 : 6
                    });
                }
            }
            // Far letters first, so nearer ones are drawn over them.
            ringGlyphs.Sort((a, b) => b.Depth.CompareTo(a.Depth));
            foreach (var g in ringGlyphs) DrawRingGlyph(g, panes, veil);
        }

        // A corner ring stays where it is, centred on its corner, the tip of its oval pointing at
        // the middle of the page; only its text moves, running along the outline. It is turned a
        // little in 3D, always the same way: its tip leans out towards the viewer by CornerTip and
        // the oval turns about its long axis by CornerLean (mirrored from corner to corner), seen
        // from a nearer camera than the middle rings, so letters coming round towards the viewer
        // grow and brighten and those going away shrink and dim. Its letters lie in the ring's
        // plane, standing on the outline, their tops towards the corner for the top corners and
        // towards the middle for the bottom ones, so the part on the page reads upright.
        private void AddCornerLetters(Ring ring, float cx, float cy, float half, double letterW, double letterH, double advance, float fade, double ringBoost)
        {
            double ox = cx + ring.OffsetX * cx, oy = cy + ring.OffsetY * cy;
            double ux = cx - ox, uy = cy - oy, ul = Math.Sqrt(ux * ux + uy * uy);
            if (ul < 1) return;
            ux /= ul; uy /= ul;
            // The plane: the long axis (u) towards the middle, tipped out towards the viewer (z
            // runs away from the viewer, y down the page); the short axis (v) turned about it.
            double ct = Math.Cos(CornerTip), st = Math.Sin(CornerTip);
            double lean = CornerLean * ((ring.OffsetX < 0) == (ring.OffsetY < 0) ? 1 : -1), cl = Math.Cos(lean), sl = Math.Sin(lean);
            double ax = ux * ct, ay = uy * ct, az = -st;                     // u'
            double nx = ux * st, ny = uy * st, nz = ct;                      // the plane's normal after the tip
            double bx = -uy * cl + nx * sl, by = ux * cl + ny * sl, bz = nz * sl; // v'
            double major = ring.Radius * half, minor = major * ring.Squash;
            double camera = Math.Max(width, height);
            int letters = RingLetterCount(major * ring.Perimeter, advance);
            int written = (int)Math.Round(ring.Presence * letters);
            double down = ring.OffsetY < 0 ? 1 : -1;               // a letter's foot: outward for the top corners, inward for the bottom ones
            // Reading along the outline the way the angle grows, or against it (the same all round).
            bool reverse = down > 0;
            double sign = reverse ? -1 : 1;
            for (int k = 0; k < written; k++)
            {
                int index = reverse ? (RingText.Length - k % RingText.Length) % RingText.Length : k % RingText.Length;
                char glyph = RingText[index];
                if (glyph == ' ') continue;
                double share = (double)k / letters + ring.Phase / (2 * Math.PI);
                double th = ring.AngleAt(share - Math.Floor(share));
                double c = Math.Cos(th), s = Math.Sin(th);
                double px = major * c * ax + minor * s * bx, py = major * c * ay + minor * s * by, pz = major * c * az + minor * s * bz;
                double scale = camera / (camera + pz);
                if (scale <= 0) continue;
                // The outline's direction and outward normal there, in the ring's plane (unit length).
                double tx = -major * s * ax + minor * c * bx, ty = -major * s * ay + minor * c * by, tz = -major * s * az + minor * c * bz;
                double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                double qx = minor * c * ax + major * s * bx, qy = minor * c * ay + major * s * by, qz = minor * c * az + major * s * bz;
                double ql = Math.Sqrt(qx * qx + qy * qy + qz * qz);
                // Nearness -1 (far) .. 1 (near).
                double near = Math.Max(-1, Math.Min(1, -pz / (major * st + minor * Math.Abs(sl))));
                double level = (0.58 + 0.3 * near) * fade;
                if (written < letters && k == written - 1) level = 0.95 * fade;
                level *= 1 + 0.25 * Math.Min(1, ringBoost / RingDrive.Jump);
                ringGlyphs.Add(new RingGlyph
                {
                    X = (float)(ox + px * scale), Y = (float)(oy + py * scale), Depth = (float)pz,
                    Rx = (float)(sign * tx / tl * scale * letterW), Ry = (float)(sign * ty / tl * scale * letterW),
                    Dx = (float)(down * qx / ql * scale * letterH), Dy = (float)(down * qy / ql * scale * letterH),
                    Glyph = glyph, Tone = level < 0.35 ? 0 : level < 0.6 ? 1 : level < 0.9 ? 2 : 6
                });
            }
        }

        // How many letters go round an outline this long: a whole number of times the text, so it
        // joins up without a seam (the spacing gives a little).
        private static int RingLetterCount(double length, double advance)
        {
            return RingText.Length * Math.Max(1, (int)Math.Round(length / advance / RingText.Length));
        }

        private static double[] Rotate(double x, double y, double z, double ct, double st, double cu, double su, double cr, double sr)
        {
            double y1 = y * ct - z * st, z1 = y * st + z * ct;   // Rx(tilt)
            double x2 = x * cu + z1 * su, z2 = -x * su + z1 * cu; // Ry(turn)
            double x3 = x2 * cr - y1 * sr, y3 = x2 * sr + y1 * cr; // Rz(roll)
            return new[] { x3, y3, z2 };
        }

        private Ring NewRing(double target, int corner)
        {
            bool middle = corner < 0;
            if (!middle)
            {
                var fixedRing = new Ring
                {
                    Corner = corner, Radius = CornerRadius, OffsetX = RingCorners[corner, 0], OffsetY = RingCorners[corner, 1],
                    Speed = (0.12 + ringRandom.NextDouble() * 0.1) * (ringRandom.Next(2) == 0 ? 1 : -1),
                    Phase = ringRandom.NextDouble() * Math.PI * 2,
                    Presence = 0, Target = target
                };
                fixedRing.Shape(CornerSquash);
                return fixedRing;
            }
            var ring = new Ring
            {
                Corner = corner,
                Radius = middle ? 0.7 + ringRandom.NextDouble() * 0.8 : 0.85 + ringRandom.NextDouble() * 0.35,
                OffsetX = (middle ? 0 : RingCorners[corner, 0]) + (ringRandom.NextDouble() - 0.5) * (middle ? 0.2 : 0.1),
                OffsetY = (middle ? 0 : RingCorners[corner, 1]) + (ringRandom.NextDouble() - 0.5) * (middle ? 0.2 : 0.1),
                Tilt = (middle ? 0.6 + ringRandom.NextDouble() * 0.75 : 0.15 + ringRandom.NextDouble() * 0.45) * (ringRandom.Next(2) == 0 ? 1 : -1),
                Turn = ringRandom.NextDouble() * Math.PI * 2,
                Roll = (ringRandom.NextDouble() - 0.5) * 2.8,
                TurnRate = (0.04 + ringRandom.NextDouble() * 0.06) * (ringRandom.Next(2) == 0 ? 1 : -1),
                Speed = (0.12 + ringRandom.NextDouble() * 0.1) * (ringRandom.Next(2) == 0 ? 1 : -1),
                Phase = ringRandom.NextDouble() * Math.PI * 2,
                Presence = 0, Target = target
            };
            // The corner rings are ovals, their minor axis half to two thirds of the major.
            if (!middle) ring.Shape(0.5 + ringRandom.NextDouble() * 0.17);
            return ring;
        }

        // One more ring around the middle, writing itself in, until there are RingMost; they all
        // stay, as do the corner rings.
        private void NextRingCount()
        {
            int count = 0;
            foreach (var ring in rings) if (ring.Target > 0 && ring.Corner < 0) count++;
            if (count < RingMost) rings.Add(NewRing(1, -1));
        }

        private void EnsureRingFont()
        {
            int w = cellW * 2, h = cellH * 2;
            if (ringFont != null && ringMaskW == w && ringMaskH == h) return;
            if (ringFont != null) ringFont.Dispose();
            ringFont = UiFonts.Body(font.Size * 2);
            ringMaskW = w; ringMaskH = h;
            ringMasks.Clear();
        }

        // A letter's coverage (0..255), then the same grown by three pixels each way: the dark
        // outline that keeps the letter clear of the wall and of farther letters behind it.
        private byte[] RingMask(char glyph)
        {
            byte[] mask;
            if (ringMasks.TryGetValue(glyph, out mask)) return mask;
            int size = ringMaskW * ringMaskH;
            mask = new byte[size * 2];
            using (var cell = new Bitmap(ringMaskW, ringMaskH, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(cell))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(glyph.ToString(), ringFont, Brushes.White, 0, 0, StringFormat.GenericTypographic);
                }
                for (int y = 0; y < ringMaskH; y++)
                    for (int x = 0; x < ringMaskW; x++) mask[y * ringMaskW + x] = cell.GetPixel(x, y).R;
            }
            const int Grow = 3;
            var across = new byte[size];
            for (int y = 0; y < ringMaskH; y++)
                for (int x = 0; x < ringMaskW; x++)
                {
                    byte most = 0;
                    for (int k = Math.Max(0, x - Grow); k <= Math.Min(ringMaskW - 1, x + Grow); k++) most = Math.Max(most, mask[y * ringMaskW + k]);
                    across[y * ringMaskW + x] = most;
                }
            for (int y = 0; y < ringMaskH; y++)
                for (int x = 0; x < ringMaskW; x++)
                {
                    byte most = 0;
                    for (int k = Math.Max(0, y - Grow); k <= Math.Min(ringMaskH - 1, y + Grow); k++) most = Math.Max(most, across[k * ringMaskW + x]);
                    mask[size + y * ringMaskW + x] = most;
                }
            ringMasks[glyph] = mask;
            return mask;
        }

        // Draws a letter whose right edge runs along (Rx, Ry) and whose height runs down along
        // (Dx, Dy), centred on (X, Y): each pixel in its box is mapped back onto the glyph.
        private void DrawRingGlyph(RingGlyph g, IList<Rectangle> panes, float veil)
        {
            float det = g.Rx * g.Dy - g.Ry * g.Dx;
            if (Math.Abs(det) < 0.5F) return; // seen edge-on
            float ax = Math.Abs(g.Rx) + Math.Abs(g.Dx), ay = Math.Abs(g.Ry) + Math.Abs(g.Dy);
            int x0 = Math.Max(0, (int)(g.X - ax / 2)), x1 = Math.Min(width - 1, (int)(g.X + ax / 2) + 1);
            int y0 = Math.Max(0, (int)(g.Y - ay / 2)), y1 = Math.Min(height - 1, (int)(g.Y + ay / 2) + 1);
            if (x0 > x1 || y0 > y1) return;
            int tone = g.Tone;
            // Text panes dim what lies under them, except where the interface has decayed away.
            int col = Math.Min(cols - 1, Math.Max(0, ((int)g.X - originX) / Math.Max(1, cellW))), row = Math.Min(rows - 1, Math.Max(0, ((int)g.Y - originY) / Math.Max(1, cellH)));
            if (veil <= 0 || CellThreshold(col, row) >= veil * (1 + VeilFront))
                foreach (var pane in panes) if (pane.Contains((int)g.X, (int)g.Y)) { tone += Shades.Length; break; }
            int[] ramp = ramps[tone];
            byte[] mask = RingMask(g.Glyph);
            int halo = ringMaskW * ringMaskH;
            float iRx = g.Dy / det, iRy = -g.Dx / det, iDx = -g.Ry / det, iDy = g.Rx / det;
            for (int y = y0; y <= y1; y++)
            {
                float dy = y + 0.5F - g.Y;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5F - g.X;
                    // (u, v) in -0.5..0.5 across the letter's width and height.
                    float u = dx * iRx + dy * iRy, v = dx * iDx + dy * iDy;
                    if (!(u >= -0.5F && u < 0.5F && v >= -0.5F && v < 0.5F)) continue;
                    // u + 0.5 can round up to 1 in float arithmetic, so the last row and column are clamped.
                    int mu = Math.Min(ringMaskW - 1, (int)((u + 0.5F) * ringMaskW)), mv = Math.Min(ringMaskH - 1, (int)((v + 0.5F) * ringMaskH));
                    int at = mv * ringMaskW + mu;
                    byte alpha = mask[at];
                    if (alpha > 40) pixels[y * width + x] = ramp[alpha];
                    else if (mask[halo + at] > 40) pixels[y * width + x] = ringFill;
                }
            }
        }

        // ---- Behind the rings, a wall of rock seen from its foot, after the faceted backdrops of
        // the title cards the rings come from: a low-poly surface of triangles large and small,
        // each flat and in its own dark shade, rough with ridges like a mountainside, so the
        // creases between facets read as jagged lines. Looking up, each row of facets is 0.86
        // times as tall as the one beneath it and its columns as narrow, closing in on a point
        // above the page, so the facets are large at the bottom and small at the top. The wall
        // keeps coming down towards the viewer, as if climbing it: everything grows away from
        // that point, a row's worth every 16 seconds and faster after each beat (with the rings'
        // speed), new rows arriving small at the top and the nearest leaving at the bottom.
        // Growing about that point keeps the facets' shapes, so a facet keeps its shade as it
        // comes down, only a little brighter as it nears. The wall is made row by row from the
        // row's number (a jittered grid of quads cut along a diagonal, two triangles in five split
        // around a point inside that stands out or sinks in, a quarter of those again; the relief
        // from ridged value noise running up the wall), so it comes out the same wherever it is
        // rebuilt, which happens each time it has come down a whole row; in between, the facets
        // are laid into the page's cells every frame. One fixed light from the upper left rakes
        // across the ridges (Lambert on each facet's normal). Facets are filled by brightness
        // (. : ; + *) in the scene's darkest crimsons, and a cell on a crease between facets lit
        // differently is a stroke along it (- / | \), a step brighter.
        private const string WallFill = ".:;+*";
        // The crimsons from dark to bright (see Shades).
        private static readonly int[] WallTones = { 3, 0, 4, 1, 5 };
        private const int WallUp = 10, WallHalf = 32;
        private const double WallShrink = 0.86, WallSpeed = 1 / 16.0;
        // A row's height and a column's width at the bottom of the page (in page heights), and
        // the height the rows close in on: the lowest row starts just below the page.
        private static readonly double WallRowH = 1.06 * (1 - WallShrink) / (1 - Math.Pow(WallShrink, WallUp)), WallColW = WallRowH * 1.3;
        private static readonly double WallPeak = -0.02 + WallRowH / (1 - WallShrink);

        private int wallSeed = -1, wallBase;
        private double wallClimb;                            // rows the wall has come down
        // Vertices as built (row wallBase at the bottom), in page heights: x from the middle, y up
        // from the bottom, z out towards the viewer; and where they are on the page this frame.
        private readonly List<double> wallX = new List<double>(), wallY = new List<double>(), wallZ = new List<double>();
        private double[] wallSX, wallSY;
        private int[] wallTris;                              // three vertices a facet
        private double[] wallShade, wallLevel;               // per facet: its light, and this frame's brightness
        private int[] wallCell, wallAcross;                  // per cell: its facet, and the facet across its nearest edge (-1 for none)
        private char[] wallStroke;                           // per cell: the stroke along that edge
        private double[] wallEdgeD, wallEdgeNx, wallEdgeNy, wallEdgeAngle;

        private void DrawWall(double dt, double boost, float fade, IList<Rectangle> panes, float veil)
        {
            if (wallSeed < 0) wallSeed = ringRandom.Next(1 << 20);
            wallClimb += WallSpeed * (1 + boost) * dt;
            int bottom = (int)Math.Floor(wallClimb);
            if (wallTris == null || bottom != wallBase) BuildWall(bottom);
            long laying = System.Diagnostics.Stopwatch.GetTimestamp();
            LayWall(Math.Pow(WallShrink, -(wallClimb - bottom)), fade);
            wallLayTicks += System.Diagnostics.Stopwatch.GetTimestamp() - laying; wallLays++;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c, facet = wallCell[i];
                    if (facet < 0) continue;
                    double level = wallLevel[facet];
                    int across = wallAcross[i];
                    char glyph;
                    if (across >= 0 && Math.Abs(wallLevel[across] - level) > 0.08 * fade)
                    {
                        glyph = wallStroke[i];
                        level = Math.Max(level, wallLevel[across]) + 0.08 * fade;
                    }
                    else
                    {
                        if (level < 0.1) continue;
                        glyph = WallFill[Math.Min(WallFill.Length - 1, (int)(level * WallFill.Length))];
                    }
                    int tone = WallTones[Math.Max(0, Math.Min(WallTones.Length - 1, (int)(level * WallTones.Length)))];
                    int x0 = originX + c * cellW, y0 = originY + r * cellH;
                    if (veil <= 0 || CellThreshold(c, r) >= veil * (1 + VeilFront))
                        foreach (var pane in panes) if (pane.Contains(x0 + cellW / 2, y0 + cellH / 2)) { tone += Shades.Length; break; }
                    Blit(x0, y0, glyph, tone);
                }
        }

        // The wall with row `bottom` at the bottom of the page: rows from one below it to one
        // above the top, each 0.86 times as tall as the one below and its columns as narrow, wide
        // enough for pages up to 2.7 times as wide as tall (quads wholly beyond that, even grown
        // by a row, are left out). Everything comes from the rows' and columns' numbers.
        private void BuildWall(int bottom)
        {
            wallBase = bottom;
            wallX.Clear(); wallY.Clear(); wallZ.Clear();
            const int Across = WallHalf * 2 + 1;
            for (int q = -1; q <= WallUp + 1; q++)
            {
                int j = bottom + q;
                for (int i = -WallHalf; i <= WallHalf; i++)
                {
                    double ji = (DecodeHash(i, j, wallSeed) - 0.5) * 0.7, jj = (DecodeHash(i, j, wallSeed + 1) - 0.5) * 0.6;
                    double scale = Math.Pow(WallShrink, q + jj);
                    wallX.Add((i + ji) * WallColW * scale);
                    wallY.Add(WallPeak - (WallPeak + 0.02) * scale);
                    // Two octaves of ridged noise over the grid (so its ridges narrow with the
                    // facets), running up the wall, standing out in proportion to the facets' size.
                    double r1 = 1 - Math.Abs(2 * Noise(i * 0.2 + wallSeed % 97, j * 0.09 + 1.9) - 1);
                    double r2 = 1 - Math.Abs(2 * Noise(i * 0.5 + 2.1, j * 0.25 + wallSeed % 89) - 1);
                    wallZ.Add((0.75 * r1 * r1 + 0.25 * r2 * r2 + (DecodeHash(i, j, wallSeed + 2) - 0.5) * 0.15) * WallColW * scale * 2.5);
                }
            }
            var tris = new List<int>();
            var shades = new List<double>();
            for (int q = 0; q <= WallUp + 1; q++)
                for (int i = 0; i < Across - 1; i++)
                {
                    int a = q * Across + i, b = a + 1, c = a + Across, d = c + 1;
                    if (Math.Min(Math.Min(Math.Abs(wallX[a]), Math.Abs(wallX[b])), Math.Min(Math.Abs(wallX[c]), Math.Abs(wallX[d]))) / WallShrink > 1.4) continue;
                    uint state = (uint)(DecodeHash(i - WallHalf, bottom + q - 1, wallSeed + 3) * 16777216) * 2654435761u + 1u;
                    if (WallNext(ref state) < 0.5) { AddFacet(tris, shades, a, b, d, ref state, 0); AddFacet(tris, shades, a, d, c, ref state, 0); }
                    else { AddFacet(tris, shades, a, b, c, ref state, 0); AddFacet(tris, shades, b, d, c, ref state, 0); }
                }
            wallTris = tris.ToArray();
            wallShade = shades.ToArray();
            wallLevel = new double[wallShade.Length];
        }

        // Adds a facet, two times in five split around a point inside it into three (and a
        // quarter of those again), so large and small facets mix; the point stands out or sinks
        // in by up to a third of the facet's size, and lies inside, so neighbours stay joined. A
        // facet's shade is its light (the fixed light from the upper left) and a little of its own.
        private void AddFacet(List<int> tris, List<double> shades, int a, int b, int c, ref uint state, int depth)
        {
            if (depth < 2 && WallNext(ref state) < (depth == 0 ? 0.4 : 0.25))
            {
                double wa = 0.5 + WallNext(ref state), wb = 0.5 + WallNext(ref state), wc = 0.5 + WallNext(ref state), sum = wa + wb + wc;
                double size = (Math.Sqrt((wallX[b] - wallX[a]) * (wallX[b] - wallX[a]) + (wallY[b] - wallY[a]) * (wallY[b] - wallY[a]))
                    + Math.Sqrt((wallX[c] - wallX[b]) * (wallX[c] - wallX[b]) + (wallY[c] - wallY[b]) * (wallY[c] - wallY[b]))
                    + Math.Sqrt((wallX[a] - wallX[c]) * (wallX[a] - wallX[c]) + (wallY[a] - wallY[c]) * (wallY[a] - wallY[c]))) / 3;
                double rise = (WallNext(ref state) - 0.5) * 0.66 * size;
                wallX.Add((wallX[a] * wa + wallX[b] * wb + wallX[c] * wc) / sum);
                wallY.Add((wallY[a] * wa + wallY[b] * wb + wallY[c] * wc) / sum);
                wallZ.Add((wallZ[a] * wa + wallZ[b] * wb + wallZ[c] * wc) / sum + rise);
                int p = wallX.Count - 1;
                AddFacet(tris, shades, a, b, p, ref state, depth + 1);
                AddFacet(tris, shades, b, c, p, ref state, depth + 1);
                AddFacet(tris, shades, c, a, p, ref state, depth + 1);
                return;
            }
            tris.Add(a); tris.Add(b); tris.Add(c);
            double ex = wallX[b] - wallX[a], ey = wallY[b] - wallY[a], ez = wallZ[b] - wallZ[a];
            double fx = wallX[c] - wallX[a], fy = wallY[c] - wallY[a], fz = wallZ[c] - wallZ[a];
            double nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-12) length = 1;
            if (nz < 0) length = -length; // facing the viewer
            // The light, from the upper left and in front: (-0.65, 0.55, 0.6) made unit length.
            double lambert = Math.Max(0, (nx * -0.62402 + ny * 0.52802 + nz * 0.57602) / length);
            shades.Add(0.04 + 0.58 * Math.Pow(lambert, 1.4) + (WallNext(ref state) - 0.5) * 0.1);
        }

        // A small random sequence (xorshift) for the wall, so each quad's facets come from its own numbers.
        private static double WallNext(ref uint state)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state & 0xFFFFFF) / 16777216.0;
        }

        // Lays the wall, grown by `zoom` about the point its rows close in on, into the page's
        // cells: each cell takes the facet under its centre; a cell within half a cell of its
        // facet's nearest edge lies on that edge, and keeps the facet a cell further across it
        // and the stroke that runs along it. Also works out each facet's brightness: its shade,
        // a little dimmer the higher it is.
        private void LayWall(double zoom, float fade)
        {
            int count = cols * rows;
            if (wallCell == null || wallCell.Length != count)
            {
                wallCell = new int[count]; wallAcross = new int[count]; wallStroke = new char[count];
                wallEdgeD = new double[count]; wallEdgeNx = new double[count]; wallEdgeNy = new double[count]; wallEdgeAngle = new double[count];
            }
            for (int i = 0; i < count; i++) { wallCell[i] = -1; wallAcross[i] = -1; }
            int n = wallX.Count;
            if (wallSX == null || wallSX.Length < n) { wallSX = new double[n + n / 4]; wallSY = new double[n + n / 4]; }
            for (int k = 0; k < n; k++)
            {
                wallSX[k] = width / 2.0 + wallX[k] * zoom * height;
                wallSY[k] = height * (1 - (WallPeak + (wallY[k] - WallPeak) * zoom));
            }
            double x0, x1, x2, y0, y1, y2;
            for (int f = 0; f < wallShade.Length; f++)
            {
                int a = wallTris[f * 3], b = wallTris[f * 3 + 1], c = wallTris[f * 3 + 2];
                x0 = wallSX[a]; x1 = wallSX[b]; x2 = wallSX[c]; y0 = wallSY[a]; y1 = wallSY[b]; y2 = wallSY[c];
                double rise = Math.Max(0, Math.Min(1, 1 - (y0 + y1 + y2) / (3.0 * height)));
                wallLevel[f] = wallShade[f] * (1 - 0.35 * rise) * fade;
                int c0 = Math.Max(0, (int)Math.Floor((Math.Min(x0, Math.Min(x1, x2)) - originX) / cellW)), c1 = Math.Min(cols - 1, (int)Math.Floor((Math.Max(x0, Math.Max(x1, x2)) - originX) / cellW));
                int r0 = Math.Max(0, (int)Math.Floor((Math.Min(y0, Math.Min(y1, y2)) - originY) / cellH)), r1 = Math.Min(rows - 1, (int)Math.Floor((Math.Max(y0, Math.Max(y1, y2)) - originY) / cellH));
                if (c0 > c1 || r0 > r1) continue;
                double area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
                if (Math.Abs(area) < 1e-9) continue;
                double turn = area > 0 ? 1 : -1;
                double l0 = Math.Max(1e-9, Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
                double l1 = Math.Max(1e-9, Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)));
                double l2 = Math.Max(1e-9, Math.Sqrt((x0 - x2) * (x0 - x2) + (y0 - y2) * (y0 - y2)));
                for (int r = r0; r <= r1; r++)
                    for (int col = c0; col <= c1; col++)
                    {
                        double px = originX + col * cellW + cellW / 2.0, py = originY + r * cellH + cellH / 2.0;
                        // Distance inside each edge (positive inside the triangle).
                        double d0 = turn * ((x1 - x0) * (py - y0) - (y1 - y0) * (px - x0)) / l0;
                        if (d0 < 0) continue;
                        double d1 = turn * ((x2 - x1) * (py - y1) - (y2 - y1) * (px - x1)) / l1;
                        if (d1 < 0) continue;
                        double d2 = turn * ((x0 - x2) * (py - y2) - (y0 - y2) * (px - x2)) / l2;
                        if (d2 < 0) continue;
                        int i = r * cols + col;
                        wallCell[i] = f;
                        // The nearest edge: its outward normal, and its direction on the page (y runs down).
                        double ax, ay, bx, by, l;
                        if (d0 <= d1 && d0 <= d2) { wallEdgeD[i] = d0; ax = x0; ay = y0; bx = x1; by = y1; l = l0; }
                        else if (d1 <= d2) { wallEdgeD[i] = d1; ax = x1; ay = y1; bx = x2; by = y2; l = l1; }
                        else { wallEdgeD[i] = d2; ax = x2; ay = y2; bx = x0; by = y0; l = l2; }
                        wallEdgeNx[i] = turn * (by - ay) / l;
                        wallEdgeNy[i] = -turn * (bx - ax) / l;
                        double angle = Math.Atan2(by - ay, bx - ax) * 180 / Math.PI;
                        wallEdgeAngle[i] = angle < 0 ? angle + 180 : angle;
                    }
            }
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    if (wallCell[i] < 0) continue;
                    // Half the spacing of cells across the edge, along a row for a steep edge and down a
                    // column for a shallow one: one cell a row (or column) is that close, so the stroke is one cell thick.
                    double band = 0.5 * Math.Max(Math.Abs(wallEdgeNx[i]) * cellW, Math.Abs(wallEdgeNy[i]) * cellH);
                    if (wallEdgeD[i] >= band) continue;
                    int qc = (int)Math.Floor((c * cellW + cellW / 2.0 + wallEdgeNx[i] * band * 2) / cellW);
                    int qr = (int)Math.Floor((r * cellH + cellH / 2.0 + wallEdgeNy[i] * band * 2) / cellH);
                    if (qc < 0 || qr < 0 || qc >= cols || qr >= rows) continue;
                    int across = wallCell[qr * cols + qc];
                    if (across < 0 || across == wallCell[i]) continue;
                    wallAcross[i] = across;
                    double a = wallEdgeAngle[i];
                    wallStroke[i] = a < 28 || a >= 152 ? '-' : a < 76 ? '\\' : a < 104 ? '|' : '/';
                }
        }

        // Smooth value noise 0..1 on a lattice (hashed corners, smoothstep between them).
        private static double Noise(double x, double y)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
            double fx = x - xi, fy = y - yi;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            double a = Lattice(xi, yi), b = Lattice(xi + 1, yi), c = Lattice(xi, yi + 1), d = Lattice(xi + 1, yi + 1);
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }

        private static double Lattice(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) + (uint)(y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0;
            }
        }

        private void ReleaseRings()
        {
            if (ringFont != null) ringFont.Dispose();
            ringFont = null;
            ringMasks.Clear();
            ringGlyphs.Clear();
            // The wall is built and laid out again when drawn next (it comes out the same).
            wallTris = null; wallShade = null; wallLevel = null; wallSX = null; wallSY = null;
            wallCell = null; wallAcross = null; wallStroke = null;
            wallEdgeD = null; wallEdgeNx = null; wallEdgeNy = null; wallEdgeAngle = null;
            wallX.Clear(); wallY.Clear(); wallZ.Clear();
            wallX.TrimExcess(); wallY.TrimExcess(); wallZ.TrimExcess();
        }
    }

    // How the rings follow the music: every beat of the analyzer's beat clock (every kick while
    // it does not hold the tempo) makes their speed jump to Jump + 1 times its own, easing back
    // with a time constant of a third of a beat; the beats are counted towards the next change
    // of the number of rings, every 8 bars (32 beats). Separate from the drawing so a song can
    // be followed offline (--audio-test --rings).
    internal sealed class RingDrive
    {
        public const double Jump = 5;
        public const int Bars = 32;
        public double Boost;
        public int Beats;

        // Returns true when the number of rings should change.
        public bool Update(double dt, MusicEvents events, bool locked, double bpm)
        {
            bool held = locked && bpm > 0;
            int beats = held ? events.Beats : events.Kicks;
            if (beats > 0) { Boost = Jump; Beats += beats; }
            Boost *= Math.Exp(-dt / (0.35 * (held ? 60 / bpm : 0.45)));
            if (Beats < Bars) return false;
            Beats -= Bars;
            return true;
        }
    }
}
