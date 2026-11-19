using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Arabara
{
    // A two-pixel line that jumps with whatever is playing, like a level meter: one under
    // the address bar growing from the right edge to the left, one above the buttons at
    // the bottom growing from the left to the right. Its length follows the music's hits rather than
    // its loudness, so it shoots out on the beat and drops back between beats even when a
    // drop keeps the bass loud: the bass rising above where it was a fifth of a second ago,
    // every kick (most) and snare, and a little of the overall level. It jumps out at once
    // and falls back in about a sixth of a second, brightening towards its moving tip. Both bars show
    // one shared state (Update), read from AudioMeter.Peek, which takes nothing away from the
    // SOUND WAVE scene. Sound is only listened to while the Arabara window is active and not
    // minimized; otherwise, and in silence, the line is gone.
    internal sealed class MusicBar : Control
    {
        private static readonly float[] bands = new float[AudioMeter.BandCount];
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static double lastTime = -1;
        private static int kicks = -1, snares = -1;
        private static float meter, pulse, snap, bassSlow, quiet = 99, drawn = -1;
        internal static double TestNow = -1; // --audio-test --bars: the song's time instead of the clock
        internal static float Meter { get { return meter; } }
        private static readonly Color Low = Color.FromArgb(86, 21, 30), Mid = Color.FromArgb(214, 56, 76), High = Color.FromArgb(255, 150, 164);

        public MusicBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = UiPalette.Canvas;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
        }

        // Grows from the right edge (the top bar) rather than the left (the bottom bar).
        public bool FromRight { get; set; }

        // True while the bars are moving (sound heard recently, window listening).
        public static bool Lively { get { return quiet < 3; } }

        // Advances the shared state; listening says whether the window wants sound now.
        // Returns whether the bars changed enough to be drawn again.
        public static bool Update(bool listening)
        {
            double now = TestNow >= 0 ? TestNow : clock.Elapsed.TotalSeconds;
            float dt = lastTime < 0 ? 0.016F : (float)Math.Max(0, Math.Min(0.25, now - lastTime));
            lastTime = now;
            float target = 0;
            if (listening)
            {
                int snareCount;
                int count = AudioMeter.Peek(bands, out snareCount);
                if (kicks >= 0 && count > kicks) pulse = 1;
                if (snares >= 0 && snareCount > snares) snap = 1;
                kicks = count; snares = snareCount;
                float bass = 0, sum = 0;
                for (int b = 0; b < bands.Length; b++) { if (b < 8) bass = Math.Max(bass, bands[b]); sum += bands[b]; }
                float mean = sum / bands.Length;
                float rise = Math.Max(0, bass - bassSlow);
                bassSlow += (bass - bassSlow) * (float)(1 - Math.Exp(-dt / 0.2));
                target = Math.Min(1F, 0.18F * mean + 2.5F * rise + 0.6F * pulse + 0.35F * snap);
                quiet = mean > 0.02F ? 0 : quiet + dt;
            }
            else { kicks = snares = -1; quiet += dt; }
            pulse *= (float)Math.Exp(-dt / 0.13);
            snap *= (float)Math.Exp(-dt / 0.11);
            // Out at once, back in about a sixth of a second.
            if (target > meter) meter = target;
            else meter += (target - meter) * (float)(1 - Math.Exp(-dt / 0.15));
            if (meter < 0.002F) meter = 0;
            bool changed = Math.Abs(meter - drawn) > 0.002F;
            if (changed) drawn = meter;
            return changed;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int length = (int)Math.Round(meter * Width);
            if (length < 1) return;
            var lit = FromRight ? new Rectangle(Width - length, 0, length, Height) : new Rectangle(0, 0, length, Height);
            // Dim at the edge it grows from, bright at its tip; the tip whitens on a strong hit.
            Color tip = meter > 0.75F ? Mix(Mid, High, (meter - 0.75F) / 0.25F) : Mix(Low, Mid, meter / 0.75F);
            using (var brush = new LinearGradientBrush(new Rectangle(lit.X - 1, 0, lit.Width + 2, Math.Max(1, Height)), FromRight ? tip : Low, FromRight ? Low : tip, LinearGradientMode.Horizontal))
                g.FillRectangle(brush, lit);
        }

        private static Color Mix(Color a, Color b, float k)
        {
            k = Math.Max(0, Math.Min(1, k));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
        }
    }
}
