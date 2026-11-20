using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Arabara
{
    // One-shot startup animation: the striped mark and wordmark on a darker field,
    // which then decays into ASCII from the centre outwards with red and white
    // glitch characters at the decay front; the ASCII then dissolves into black and
    // the live browser interface fades in under a FadeCurtain. Frames are
    // composed in a managed pixel buffer from pre-rendered glyph masks. Everything
    // it allocates (timer, bitmaps, buffers, glyphs) is released when it finishes
    // or is skipped; nothing of it remains until the next launch.
    internal sealed class StartupIntro : Control
    {
        private const int Hold = 1100, Decay = 1700, Settle = 250, Reveal = 700;
        public const int Duration = Hold + Decay + Settle + Reveal;
        private const float Front = 0.05F;
        private const string Ramp = " .`:-=+*#%@";
        private const string Ash = ".`:-'";
        private const string Glitch = "!/\\|<>[]{}#%&$@01?*+=~^;";
        private static readonly Color Void = Color.FromArgb(0, 0, 0);
        private static readonly Color[] Palette =
        {
            Color.Empty,
            Color.FromArgb(88, 28, 36),   // 1 background ash, red
            Color.FromArgb(70, 66, 61),   // 2 background ash, grey
            Color.FromArgb(228, 44, 66),  // 3 mark red
            UiPalette.Ink,                // 4 wordmark ivory
            Color.FromArgb(255, 52, 80),  // 5 glitch red
            Color.White                   // 6 glitch white
        };

        private readonly Timer timer = new Timer { Interval = 20 };
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Random random = new Random();
        private readonly Dictionary<char, byte[]> masks = new Dictionary<char, byte[]>();
        private int[][] ramps;
        private Font cellFont;
        private Bitmap frame;
        private int[] scenePixels, pixels;
        private int width, height, cellW, cellH, cols, rows;
        private char[] glyphs;
        private byte[] tones, states;
        private float[] thresholds;
        private bool dirty = true, finished;

        public event EventHandler Finished;

        public StartupIntro()
        {
            Dock = DockStyle.Fill;
            BackColor = Void;
            AccessibleName = "Arabara startup animation";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
            timer.Tick += delegate { Advance(); };
        }

        public void Start() { clock.Start(); timer.Start(); }

        // A click or key press ends the animation immediately.
        public void Skip() { Finish(); }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Skip(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            dirty = true;
            Invalidate();
        }

        private void Advance()
        {
            long t = clock.ElapsedMilliseconds;
            if (t >= Duration) { Finish(); return; }
            Invalidate();
        }

        private void Finish()
        {
            if (finished) return;
            finished = true;
            timer.Stop();
            clock.Stop();
            var handler = Finished;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderFrame(clock.ElapsedMilliseconds);
            // Explicit pixel rectangle: DrawImageUnscaled would rescale by DPI.
            e.Graphics.DrawImage(frame, new Rectangle(0, 0, frame.Width, frame.Height), 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
        }

        private void RenderFrame(long t)
        {
            if (dirty) Rebuild();
            int voidColor = Void.ToArgb();
            if (t < Hold)
            {
                Array.Copy(scenePixels, pixels, pixels.Length);
                Publish();
                return;
            }
            float decay = Math.Min(1F, (t - Hold) / (float)Decay);
            float reveal = t < Hold + Decay + Settle ? -1F : Math.Min(1F, (t - Hold - Decay - Settle) / (float)Reveal);
            if (reveal >= 0)
            {
                // The ASCII dissolves into black; the live interface then fades in
                // under a FadeCurtain once this control is removed.
                Fill(voidColor);
            }
            else if (decay < 1) Array.Copy(scenePixels, pixels, pixels.Length);
            else Fill(voidColor);

            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    float threshold = thresholds[i];
                    char glyph = ' ';
                    byte tone = 0, state = 0; // 0 untouched, 1 ASCII on void, 2 glitch over the page
                    if (reveal >= 0)
                    {
                        float past = reveal - threshold;
                        if (past > Front) state = 0;
                        else if (past > 0) { glyph = Glitch[random.Next(Glitch.Length)]; tone = GlitchTone(); state = 2; }
                        else if (-past < Front * 0.6F) { glyph = Glitch[random.Next(Glitch.Length)]; tone = GlitchTone(); state = 1; }
                        else { Settled(i, out glyph, out tone); state = 1; }
                    }
                    else
                    {
                        float past = decay - threshold;
                        if (past < -0.012F) state = 0;
                        else if (past < Front) { glyph = Glitch[random.Next(Glitch.Length)]; tone = GlitchTone(); state = 1; }
                        else { Settled(i, out glyph, out tone); state = 1; }
                    }
                    states[i] = state;
                    if (state == 1) DrawCell(c, r, glyph, tone, true);
                    else if (state == 2 && random.NextDouble() < 0.5) DrawCell(c, r, glyph, tone, false);
                }
            Publish();
        }

        // Writes one character cell; an opaque cell first clears to the void colour.
        private void DrawCell(int c, int r, char glyph, byte tone, bool opaque)
        {
            int x0 = c * cellW, y0 = r * cellH;
            int w = Math.Min(cellW, width - x0), h = Math.Min(cellH, height - y0);
            byte[] mask = glyph == ' ' || tone == 0 ? null : Mask(glyph);
            int[] ramp = tone == 0 ? null : ramps[tone];
            int voidColor = ramps[0][0];
            for (int y = 0; y < h; y++)
            {
                int row = (y0 + y) * width + x0, maskRow = y * cellW;
                for (int x = 0; x < w; x++)
                {
                    int a = mask == null ? 0 : mask[maskRow + x];
                    if (opaque) pixels[row + x] = a == 0 ? voidColor : ramp[a];
                    else if (a > 96) pixels[row + x] = ramp[a];
                }
            }
        }

        private byte GlitchTone() { return (byte)(random.NextDouble() < 0.55 ? 5 : 6); }

        // The cell's sampled ASCII character, with occasional flicker.
        private void Settled(int i, out char glyph, out byte tone)
        {
            glyph = glyphs[i];
            tone = tones[i];
            double roll = random.NextDouble();
            if (tone >= 3 && roll < 0.03) glyph = Ramp[2 + random.Next(Ramp.Length - 2)];
            else if (tone < 3 && roll < 0.004) { glyph = Glitch[random.Next(Glitch.Length)]; tone = GlitchTone(); }
        }

        private void Fill(int color) { for (int i = 0; i < pixels.Length; i++) pixels[i] = color; }

        private void Publish()
        {
            var data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < height; y++) Marshal.Copy(pixels, y * width, data.Scan0 + y * data.Stride, width);
            frame.UnlockBits(data);
        }

        // Anti-aliased coverage of one character, rendered once and reused.
        private byte[] Mask(char glyph)
        {
            byte[] mask;
            if (masks.TryGetValue(glyph, out mask)) return mask;
            mask = new byte[cellW * cellH];
            using (var bitmap = new Bitmap(cellW, cellH, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(glyph.ToString(), cellFont, Brushes.White, 0, 0, StringFormat.GenericTypographic);
                }
                for (int y = 0; y < cellH; y++)
                    for (int x = 0; x < cellW; x++) mask[y * cellW + x] = bitmap.GetPixel(x, y).R;
            }
            masks[glyph] = mask;
            return mask;
        }

        private void Rebuild()
        {
            dirty = false;
            width = Math.Max(1, ClientSize.Width);
            height = Math.Max(1, ClientSize.Height);
            float dpi = DeviceDpi / 96F;
            if (cellFont != null) cellFont.Dispose();
            cellFont = UiFonts.Body(9.5F * dpi);
            masks.Clear();
            using (var probe = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(probe))
            {
                cellW = Math.Max(4, (int)Math.Ceiling(g.MeasureString("M", cellFont, PointF.Empty, StringFormat.GenericTypographic).Width));
                cellH = Math.Max(8, (int)Math.Ceiling(cellFont.GetHeight(g)));
            }
            cols = (width + cellW - 1) / cellW;
            rows = (height + cellH - 1) / cellH;
            ramps = new int[Palette.Length][];
            for (int k = 0; k < Palette.Length; k++)
            {
                ramps[k] = new int[256];
                Color to = k == 0 ? Void : Palette[k];
                for (int a = 0; a < 256; a++)
                    ramps[k][a] = Color.FromArgb(Void.R + (to.R - Void.R) * a / 255, Void.G + (to.G - Void.G) * a / 255, Void.B + (to.B - Void.B) * a / 255).ToArgb();
            }

            if (frame != null) frame.Dispose();
            frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            pixels = new int[width * height];
            using (var scene = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(scene)) { g.Clear(Void); DrawBrand(g, width, height); }
                var data = scene.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                scenePixels = new int[width * height];
                for (int y = 0; y < height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, scenePixels, y * width, width);
                scene.UnlockBits(data);
            }

            glyphs = new char[cols * rows];
            tones = new byte[cols * rows];
            states = new byte[cols * rows];
            thresholds = new float[cols * rows];
            float centreX = width / 2F, centreY = height / 2F, reach = (float)Math.Sqrt(centreX * centreX + centreY * centreY);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c, lit = 0, red = 0, green = 0;
                    for (int sy = 0; sy < 3; sy++)
                        for (int sx = 0; sx < 3; sx++)
                        {
                            int x = Math.Min(width - 1, c * cellW + (sx * 2 + 1) * cellW / 6), y = Math.Min(height - 1, r * cellH + (sy * 2 + 1) * cellH / 6);
                            int argb = scenePixels[y * width + x];
                            int pr = (argb >> 16) & 255, pg = (argb >> 8) & 255;
                            if (pr > 48) { lit++; red += pr; green += pg; }
                        }
                    if (lit == 0)
                    {
                        bool ash = random.NextDouble() < 0.3;
                        glyphs[i] = ash ? Ash[random.Next(Ash.Length)] : ' ';
                        tones[i] = (byte)(ash ? (random.NextDouble() < 0.5 ? 1 : 2) : 0);
                    }
                    else
                    {
                        bool stripe = red > green * 1.5;
                        tones[i] = (byte)(stripe ? 3 : 4);
                        // The striped mark keeps its horizontal stripes as '=' rows.
                        glyphs[i] = stripe ? (lit >= 3 ? '=' : '-') : Ramp[Math.Min(Ramp.Length - 1, 2 + lit * (Ramp.Length - 3) / 9)];
                    }
                    float dx = c * cellW + cellW / 2F - centreX, dy = r * cellH + cellH / 2F - centreY;
                    float distance = (float)Math.Sqrt(dx * dx + dy * dy) / reach;
                    float wobble = 0.05F * (float)Math.Sin(Math.Atan2(dy, dx) * 5);
                    thresholds[i] = Math.Max(0F, distance * 0.8F + wobble + (float)random.NextDouble() * 0.15F);
                }
        }

        // The start-page mark and wordmark, enlarged and centred.
        private static void DrawBrand(Graphics g, int w, int h)
        {
            const float markW = 184, markH = 76, gap = 22;
            using (var font = UiFonts.Wordmark(34F))
            {
                float titleW = HomeView.MeasureWordmark(g, "ARABARA", font, 500);
                float groupW = markW + gap + titleW;
                float s = Math.Max(0.5F, Math.Min(w * 0.72F / groupW, h * 0.26F / markH));
                float ox = (w / s - groupW) / 2, oy = (h / s - markH) / 2;
                HomeView.DrawStripedMark(g, s, ox, oy, markW, markH);
                var saved = g.Save();
                g.ScaleTransform(s, s);
                HomeView.DrawWordmark(g, "ARABARA", font, UiPalette.Ink, ox + markW + gap, oy + 15, 500, 46);
                g.Restore(saved);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (frame != null) frame.Dispose();
                if (cellFont != null) cellFont.Dispose();
                frame = null;
                cellFont = null;
                scenePixels = pixels = null;
                glyphs = null; tones = states = null; thresholds = null; ramps = null;
                masks.Clear();
            }
            base.Dispose(disposing);
        }

        // --intro-test: renders sample frames off screen and records the frame cost.
        public static int RenderTest(string folder)
        {
            Directory.CreateDirectory(folder);
            var log = new StringBuilder();
            using (var intro = new StartupIntro { Size = new Size(1280, 670) })
            {
                foreach (int t in new[] { 900, 1500, 2000, 2500, 3000, 3400, 3700 })
                {
                    intro.RenderFrame(t);
                    intro.frame.Save(Path.Combine(folder, "intro-" + t + ".png"));
                }
                var watch = Stopwatch.StartNew();
                int frames = 0;
                for (int t = Hold; t < Duration; t += 20, frames++) intro.RenderFrame(t);
                watch.Stop();
                log.AppendLine("Intro frames: " + frames + ", average " + (watch.Elapsed.TotalMilliseconds / frames).ToString("F2") +
                    " ms per frame at 1280x670 (" + intro.cols + "x" + intro.rows + " cells, " + intro.cellW + "x" + intro.cellH + " px).");
            }
            File.WriteAllText(Path.Combine(folder, "intro-test.txt"), log.ToString());
            return 0;
        }

        // --preview-image <file>: the intro's last frame (the brand alone on black) at 1280 x 640,
        // the size GitHub shows for a repository's social preview (assets/github-preview.png).
        public static int PreviewImage(string path)
        {
            using (var image = new Bitmap(1280, 640, PixelFormat.Format32bppRgb))
            {
                using (var g = Graphics.FromImage(image)) { g.Clear(Void); DrawBrand(g, image.Width, image.Height); }
                image.Save(path, ImageFormat.Png);
            }
            return 0;
        }
    }

    // A black, click-through window over the client area that fades out, so the
    // live interface (and its running animations) fades in from black. It never
    // takes focus and closes itself when done.
    internal sealed class FadeCurtain : Form
    {
        private readonly Timer timer = new Timer { Interval = 15 };
        private readonly Stopwatch clock = new Stopwatch();
        private readonly int duration;

        public FadeCurtain(Form owner, int duration)
        {
            this.duration = duration;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Owner = owner;
            Bounds = owner.RectangleToScreen(owner.ClientRectangle);
            Opacity = 1;
            timer.Tick += delegate
            {
                float f = Math.Min(1F, clock.ElapsedMilliseconds / (float)this.duration);
                Opacity = (1 - f) * (1 - f) * (1 - f);
                if (f >= 1) { timer.Stop(); Close(); }
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x80 | 0x20; // no-activate, tool window, click-through
                return parameters;
            }
        }

        public void Begin() { Show(); clock.Start(); timer.Start(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
