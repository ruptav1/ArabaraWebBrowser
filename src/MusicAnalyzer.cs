using System;

namespace Arabara
{
    // Listens to music the way the SOUND WAVE scene needs it, from mono samples fed in any
    // block size. Every 512 samples (about 94 times a second at 48 kHz) it takes a 2048-point
    // FFT of the newest samples and works out:
    //  - 48 log-spaced bands (40 Hz to 16 kHz) on a decibel scale, normalised against a
    //    slowly falling ceiling, so quiet and loud passages both move;
    //  - onsets for three kinds of sound - kick (below 130 Hz), snare (body and crack) and
    //    hats (above 6 kHz) - from the rise of the log spectrum against the previous two
    //    frames, picked as local peaks over an adaptive threshold (the recent mean plus a
    //    multiple of the recent deviation), with a short refractory time;
    //  - the tempo, from the autocorrelation of the onset strength over the last 8 seconds,
    //    and a beat clock pulled towards the kicks, which also ticks when the kick is quiet;
    //  - the part of the song: loudness and bass weight over the last ~1 second against the
    //    last 20, and how often the kick hits, so a drop reads high and a bridge low.
    // Nothing is stored beyond these few seconds of statistics.
    internal sealed class MusicAnalyzer
    {
        public const int BandCount = 48, FftSize = 2048, Hop = 512;
        private const int Kick = 0, Snare = 1, Hat = 2, Kinds = 3;
        private const float GateDb = -10F, RangeDb = 40F;
        private static readonly float[] Sensitivity = { 1.6F, 1.8F, 1.8F };   // threshold, in deviations
        private static readonly double[] Refractory = { 0.2, 0.16, 0.09 };    // seconds
        private readonly int rate;
        private readonly float[] ring = new float[FftSize], re = new float[FftSize], im = new float[FftSize], window = new float[FftSize];
        private int ringAt, sinceHop, filled;
        private readonly int[] bandLow = new int[BandCount], bandHigh = new int[BandCount];
        private readonly bool[][] kind = new bool[Kinds][];
        private readonly int[] kindSize = new int[Kinds];
        private readonly float[] db = new float[BandCount], previous = new float[BandCount], previous2 = new float[BandCount];
        private readonly float[] display = new float[BandCount], held = new float[BandCount];
        private float ceiling = GateDb + RangeDb;
        private double time;
        private readonly double hopSeconds;
        // Onsets: per kind, the last two flux values (for peak picking) and running statistics.
        private readonly float[] flux1 = new float[Kinds], flux2 = new float[Kinds], fluxMean = new float[Kinds], fluxDeviation = new float[Kinds];
        private readonly double[] lastOnset = { -9, -9, -9 };
        private readonly int[] eventCount = new int[Kinds + 1];
        private readonly float[] eventStrength = new float[Kinds + 1];
        // Song structure.
        private float loudMid, loudLong, lowMid, lowLong, kickRateMid, kickRateLong;
        private bool structureReady;
        private double silentSince = -1;
        // Tempo.
        private readonly float[] envelope;
        private double[] correlation = new double[0], scores = new double[0], memory = new double[0];
        private int envelopeAt, sinceTempo;
        private double period, confidence, nextBeat = -1, lastKickTime = -9;
        private int agreeing;
        private float onBeatScore, offBeatScore;
        private double soundSince = -1; // start of the current stretch of sound, for warm-up

        public float Section { get; private set; }
        public float Loudness { get; private set; }   // 0..1 against the ceiling
        public double Bpm { get { return period > 0 ? 60 / period : 0; } }
        public double TempoConfidence { get { return confidence; } }
        // The tempo has held for a while (or is clearly periodic): the beat grid can be trusted.
        public bool Locked { get { return period > 0 && nextBeat >= 0 && (agreeing >= 3 || confidence > 0.35); } }
        public double Time { get { return time; } }
        public bool Silent { get; private set; }
        // --audio-test: every onset candidate (time, kind, strength, beat phase, taken).
        internal System.Text.StringBuilder Trace;

        public MusicAnalyzer(int sampleRate)
        {
            rate = Math.Max(8000, sampleRate);
            hopSeconds = Hop / (double)rate;
            for (int i = 0; i < FftSize; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
            float binHz = rate / (float)FftSize;
            for (int k = 0; k < Kinds; k++) kind[k] = new bool[BandCount];
            for (int b = 0; b < BandCount; b++)
            {
                double f0 = 40 * Math.Pow(16000.0 / 40, b / (double)BandCount), f1 = 40 * Math.Pow(16000.0 / 40, (b + 1) / (double)BandCount);
                bandLow[b] = Math.Max(1, (int)(f0 / binHz));
                bandHigh[b] = Math.Min(FftSize / 2 - 1, Math.Max(bandLow[b], (int)(f1 / binHz)));
                double mid = Math.Sqrt(f0 * f1);
                kind[Kick][b] = mid < 130;
                kind[Snare][b] = (mid > 150 && mid < 450) || (mid > 1800 && mid < 4500);
                kind[Hat][b] = mid > 6000;
            }
            for (int k = 0; k < Kinds; k++) foreach (bool on in kind[k]) if (on) kindSize[k]++;
            envelope = new float[(int)(8 / hopSeconds)];
        }

        // Feeds mono samples; analyses every hop.
        public void Push(float[] samples, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ring[ringAt] = samples[offset + i];
                ringAt = (ringAt + 1) % FftSize;
                if (filled < FftSize) filled++;
                if (++sinceHop >= Hop) { sinceHop = 0; if (filled == FftSize) Frame(); }
            }
        }

        // The bands since the last call (the highest value each band reached), 0..1.
        public void TakeBands(float[] into)
        {
            for (int b = 0; b < BandCount; b++) { into[b] = held[b]; held[b] = display[b]; }
        }

        // Onsets since the last call: counts and the strongest strength (0..1.5) per kind,
        // and beats ticked by the beat clock without a kick of their own.
        public void TakeEvents(out MusicEvents events)
        {
            events = new MusicEvents
            {
                Kicks = eventCount[Kick], Snares = eventCount[Snare], Hats = eventCount[Hat], Beats = eventCount[Kinds],
                Kick = eventStrength[Kick], Snare = eventStrength[Snare], Hat = eventStrength[Hat], Beat = eventStrength[Kinds]
            };
            Array.Clear(eventCount, 0, eventCount.Length);
            Array.Clear(eventStrength, 0, eventStrength.Length);
        }

        private void Frame()
        {
            time += hopSeconds;
            for (int i = 0; i < FftSize; i++) { re[i] = ring[(ringAt + i) % FftSize] * window[i]; im[i] = 0; }
            Fft(re, im);
            float loudest = -200, power = 0, lowPower = 0;
            for (int b = 0; b < BandCount; b++)
            {
                float m = 0;
                for (int i = bandLow[b]; i <= bandHigh[b]; i++) m = Math.Max(m, re[i] * re[i] + im[i] * im[i]);
                m = (float)Math.Sqrt(m) * (1 + b * 0.06F); // higher bands carry less energy; tilt them up
                previous2[b] = previous[b]; previous[b] = db[b];
                db[b] = (float)(20 * Math.Log10(m + 1e-4));
                loudest = Math.Max(loudest, db[b]);
                power += m * m;
                if (b < 10) lowPower += m * m;
            }
            Silent = loudest < GateDb;
            if (Silent && silentSince < 0) silentSince = time;
            if (!Silent) silentSince = -1;

            // Display bands: decibels against a ceiling that jumps up and falls 4 dB a second.
            ceiling = Math.Max(GateDb + RangeDb, loudest > ceiling ? loudest : ceiling - (float)(4 * hopSeconds));
            for (int b = 0; b < BandCount; b++)
            {
                float v = Silent ? 0 : Math.Max(0F, Math.Min(1F, (db[b] - (ceiling - RangeDb)) / RangeDb));
                v = (float)Math.Pow(v, 1.5);
                display[b] = v;
                if (v > held[b]) held[b] = v;
            }
            float level = 10 * (float)Math.Log10(power / BandCount + 1e-8), low = 10 * (float)Math.Log10(lowPower / 10 + 1e-8);
            Loudness = Silent ? 0 : Math.Max(0F, Math.Min(1F, (level - (ceiling - 30)) / 30));

            // Onsets per kind.
            float onsetEnergy = 0;
            for (int k = 0; k < Kinds; k++)
            {
                float flux = 0;
                if (!Silent)
                    for (int b = 0; b < BandCount; b++)
                        if (kind[k][b]) flux += Math.Max(0F, db[b] - Math.Max(previous[b], previous2[b]));
                flux /= Math.Max(1, kindSize[k]);
                // The previous frame is an onset if it was a local peak over the threshold.
                float candidate = flux1[k], threshold = fluxMean[k] + Sensitivity[k] * fluxDeviation[k] + 0.6F;
                float strength = Math.Max(0F, Math.Min(1.5F, (candidate - fluxMean[k]) / (4 * fluxDeviation[k] + 1.5F)));
                bool picked = candidate > flux2[k] && candidate >= flux && candidate > threshold && time - lastOnset[k] > Refractory[k];
                bool taken = picked && OnGrid(k, time - hopSeconds, strength);
                if (picked && Trace != null && k != Hat)
                    Trace.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F3},{1},{2:F2},{3:F2},{4},{5}",
                        time - hopSeconds, k, strength, period > 0 && nextBeat >= 0 ? ((time - hopSeconds - nextBeat) / period) - Math.Floor((time - hopSeconds - nextBeat) / period) : -1, Locked ? 1 : 0, taken ? 1 : 0));
                if (taken)
                {
                    lastOnset[k] = time - hopSeconds;
                    eventCount[k]++;
                    eventStrength[k] = Math.Max(eventStrength[k], strength);
                    if (k == Kick) KickHit(time - hopSeconds, strength);
                    else if (k == Snare) SnareCount++;
                }
                float a = (float)(1 - Math.Exp(-hopSeconds / 1.2));
                fluxMean[k] += (flux - fluxMean[k]) * a;
                fluxDeviation[k] += (Math.Abs(flux - fluxMean[k]) - fluxDeviation[k]) * a;
                flux2[k] = flux1[k]; flux1[k] = flux;
                onsetEnergy += Math.Max(0F, flux - fluxMean[k]) * (k == Kick ? 2F : k == Snare ? 1F : 0.5F);
            }
            Tempo(onsetEnergy);
            Structure(level, low);
        }

        // Once the tempo holds, kicks and snares are checked against the beat grid: on a beat
        // they always count, half way between two beats only when strong, anywhere else only
        // when very strong - so a bass or synth note between beats is not taken for a kick.
        // Kicks also vote on where the beat is, and if most land half way between the clock
        // beats, the clock was locked on the off-beat and moves half a beat.
        private bool OnGrid(int k, double at, float strength)
        {
            if (k == Hat || !Locked) return true;
            double phase = (at - nextBeat) / period;
            phase -= Math.Floor(phase);
            double offBeat = Math.Abs(phase - 0.5), onBeat = Math.Min(phase, 1 - phase);
            if (k == Kick)
            {
                float fade = (float)Math.Exp(-1 / 16.0);
                onBeatScore *= fade; offBeatScore *= fade;
                if (onBeat < 0.12) onBeatScore += strength * strength;
                else if (offBeat < 0.12) offBeatScore += strength * strength;
                if (offBeatScore > 2 && offBeatScore > 1.6F * onBeatScore)
                {
                    nextBeat += period / 2;
                    float swap = onBeatScore; onBeatScore = offBeatScore; offBeatScore = swap;
                    double t = onBeat; onBeat = offBeat; offBeat = t;
                }
            }
            if (onBeat < 0.14) return true;
            if (offBeat < 0.1) return strength > (k == Kick ? 0.5F : 0.35F);
            return strength > 0.9F;
        }

        // Kicks heard so far, and the bands as they are now (for readers that must not
        // take the held values or the events, such as the window's music bars).
        public int KickCount { get; private set; }
        public int SnareCount { get; private set; }
        public void PeekBands(float[] into) { Array.Copy(display, into, BandCount); }

        private void KickHit(double at, float strength)
        {
            KickCount++;
            // Kick rate for the structure, and the beat clock pulled towards the kick.
            kickRateMid += 1; kickRateLong += 1;
            if (period > 0 && nextBeat > 0)
            {
                double error = at - nextBeat;
                if (Math.Abs(error) < period * 0.3) nextBeat += error * 0.35;
                else if (Math.Abs(error + period) < period * 0.3) nextBeat += (error + period) * 0.35;
            }
            else if (period > 0) nextBeat = at + period;
            lastKickTime = at;
        }

        // Tempo: autocorrelation of the onset strength (its mean taken out) over 8 s, twice a
        // second. Each candidate beat period is scored by how well it and its multiples (2, 3,
        // 4 beats) repeat, so the true beat wins over a 3:4 or 4:5 relative (which repeats only
        // at some multiples), with a mild preference for 80-160 BPM. The scores are summed
        // into a memory that fades over about 15 s, and the tempo is the memory's best: a fill
        // or a triplet bar does not move it, a new song does within several seconds. Then a
        // beat clock ticks every period and counts a beat when no kick did.
        private void Tempo(float onsetEnergy)
        {
            envelope[envelopeAt] = onsetEnergy;
            envelopeAt = (envelopeAt + 1) % envelope.Length;
            if (++sinceTempo >= (int)(0.5 / hopSeconds))
            {
                sinceTempo = 0;
                int n = envelope.Length, minLag = (int)(60 / 180.0 / hopSeconds), maxLag = (int)(60 / 70.0 / hopSeconds);
                int top = Math.Min(n - 1, 4 * maxLag + 2);
                if (correlation.Length != top + 1) correlation = new double[top + 1];
                double mean = 0;
                for (int i = 0; i < n; i++) mean += envelope[i];
                mean /= n;
                for (int lag = 0; lag <= top; lag++)
                {
                    double sum = 0;
                    for (int i = 0; i < n; i++) sum += (envelope[i] - mean) * (envelope[(i + lag) % n] - mean);
                    correlation[lag] = sum;
                }
                double zero = correlation[0], best = double.MinValue;
                int bestLag = 0;
                if (scores.Length != maxLag + 2) scores = new double[maxLag + 2];
                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    double score = 0;
                    for (int m = 1; m <= 4; m++)
                    {
                        int at = m * lag;
                        if (at + 1 > top) break;
                        // Rounding: the best of the nearest lags to m times this one.
                        score += Math.Max(correlation[at], Math.Max(correlation[at - 1], correlation[at + 1]) * 0.9) / m;
                    }
                    double bpm = 60 / (lag * hopSeconds), prefer = Math.Exp(-Math.Pow(Math.Log(bpm / 120) / 0.6, 2));
                    scores[lag] = score * prefer;
                    if (score * prefer > best) { best = score * prefer; bestLag = lag; }
                }
                if (zero > 1e-6)
                {
                    if (memory.Length != scores.Length) memory = new double[scores.Length];
                    double fade = Math.Exp(-0.5 / 15), top2 = 0;
                    int memoryLag = 0;
                    for (int lag = minLag; lag <= maxLag; lag++)
                    {
                        memory[lag] = memory[lag] * fade + Math.Max(0, scores[lag]) / zero;
                        if (memory[lag] > top2) { top2 = memory[lag]; memoryLag = lag; }
                    }
                    int held = period > 0 ? (int)Math.Round(period / hopSeconds) : 0;
                    double heldMemory = held > minLag && held < maxLag ? Math.Max(memory[held], Math.Max(memory[held - 1], memory[held + 1])) : 0;
                    // A small margin keeps the current tempo through near ties.
                    if (heldMemory > 0 && Math.Abs(memoryLag - period / hopSeconds) > 1.5 && top2 < 1.15 * heldMemory) memoryLag = held;
                    if (memoryLag > minLag && memoryLag < maxLag)
                    {
                        // Refine the peak between lags with a parabola through its neighbours.
                        double before = memory[memoryLag - 1], at = memory[memoryLag], after = memory[memoryLag + 1];
                        double curve = before - 2 * at + after, shift = curve < 0 ? Math.Max(-0.5, Math.Min(0.5, 0.5 * (before - after) / curve)) : 0;
                        double found = (memoryLag + shift) * hopSeconds;
                        confidence = Math.Max(0, Math.Min(1, correlation[memoryLag] / zero));
                        if (Trace != null)
                            Trace.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F3},T,{1:F1},{2:F3},{3:F1},{4:F3},{5}",
                                time, 60 / found, best / zero, period > 0 ? 60 / period : 0, top2, agreeing));
                        if (period > 0 && Math.Abs(found - period) <= period * 0.04) { period += (found - period) * 0.3; agreeing++; }
                        else { period = found; agreeing = 0; }
                    }
                }
                else confidence *= 0.5;
            }
            if (period <= 0 || nextBeat < 0) return;
            while (time >= nextBeat)
            {
                bool kicked = Math.Abs(lastKickTime - nextBeat) < 0.07;
                if (!kicked && Locked && !Silent && Section > 0.6)
                {
                    eventCount[Kinds]++;
                    eventStrength[Kinds] = Math.Max(eventStrength[Kinds], 0.6F + 0.4F * (float)Math.Min(1, confidence * 2));
                }
                nextBeat += period;
            }
        }

        // Part of the song: decibel differences of the last ~2 s against the last ~12 s, for
        // all bands and for the bass, plus the kick rate; 1 is the song's usual level.
        private void Structure(float level, float low)
        {
            if (Silent)
            {
                double gap = silentSince >= 0 ? time - silentSince : 0;
                // A pause in the song keeps what was learnt (so the drop after it still reads
                // as a drop); a longer gap is the end of the song, and the next one starts
                // afresh with its own level, tempo and beat.
                if (gap > 1) Section = Math.Max(0, Section - (float)(hopSeconds * 2));
                if (gap > 2.5 && structureReady)
                {
                    structureReady = false; soundSince = -1;
                    period = 0; nextBeat = -1; agreeing = 0; onBeatScore = offBeatScore = 0;
                    Array.Clear(envelope, 0, envelope.Length);
                    Array.Clear(memory, 0, memory.Length);
                }
                kickRateMid *= (float)Math.Exp(-hopSeconds / 2); kickRateLong *= (float)Math.Exp(-hopSeconds / 20);
                return;
            }
            if (!structureReady) { loudMid = loudLong = level; lowMid = lowLong = low; structureReady = true; soundSince = time; }
            // Warm-up: until 20 s of sound have passed, the long view is the running mean so far,
            // so a quiet intro does not make everything after it look like a drop.
            double heard = Math.Max(hopSeconds, time - soundSince);
            float aMid = (float)(1 - Math.Exp(-hopSeconds / Math.Min(1.2, heard))), aLong = (float)(1 - Math.Exp(-hopSeconds / Math.Min(20, heard)));
            loudMid += (level - loudMid) * aMid; loudLong += (level - loudLong) * aLong;
            lowMid += (low - lowMid) * aMid; lowLong += (low - lowLong) * aLong;
            kickRateMid *= (float)Math.Exp(-hopSeconds / 1.5); kickRateLong *= (float)Math.Exp(-hopSeconds / 20);
            // Kicks per second over each window (the decayed counts divided by their time constant).
            float midRate = kickRateMid / (float)Math.Min(1.5, heard), longRate = kickRateLong / (float)Math.Min(20, heard);
            float density = longRate > 0.2F ? Math.Max(-1F, Math.Min(1.5F, midRate / longRate - 1)) : 0;
            // Above the usual level it climbs straight (a drop reaches 2-3); below it eases off
            // (a bridge sits around 0.3-0.6, never quite 0, so the sea still breathes).
            float x = ((loudMid - loudLong) + 0.6F * (lowMid - lowLong)) / 10 + 0.35F * density;
            Section = x >= 0 ? Math.Min(3F, 1 + x) : 1 / (1 - x);
        }

        // In-place radix-2 FFT.
        private static void Fft(float[] x, float[] y)
        {
            int n = x.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { float t = x[i]; x[i] = x[j]; x[j] = t; t = y[i]; y[i] = y[j]; y[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len;
                float wr = (float)Math.Cos(angle), wi = (float)Math.Sin(angle);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        float tr = x[b] * cr - y[b] * ci, ti = x[b] * ci + y[b] * cr;
                        x[b] = x[a] - tr; y[b] = y[a] - ti;
                        x[a] += tr; y[a] += ti;
                        float nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
        }
    }

    // What happened in the music since the last look.
    internal struct MusicEvents
    {
        public int Kicks, Snares, Hats, Beats;
        public float Kick, Snare, Hat, Beat;
    }
}
