using System;
using System.Collections.Generic;
using System.Drawing;

namespace Arabara
{
    // GrabCut (Rother, Kolmogorov and Blake, 2004): cuts the foreground out of a still
    // picture given only a rectangle around it. Foreground and background colours are each
    // modelled by a mixture of five Gaussians; every pixel inside the rectangle starts as
    // "probably foreground", everything outside is background, and each round re-learns
    // the two colour models and finds the cheapest cut between them with a max-flow, where
    // cutting between two similar neighbouring pixels costs more than between different
    // ones (so the cut follows the drawing's edges). Written for Arabara after the paper,
    // with OpenCV's grabcut.cpp (Apache-2.0) as a reference for the constants; no code or
    // library is added. The max-flow is Boykov and Kolmogorov's (2004) search-tree method
    // on the pixel grid.
    internal static class GrabCut
    {
        private const byte Background = 0, Foreground = 1, ProbablyBackground = 2, ProbablyForeground = 3;
        private const double Gamma = 50, Lambda = 9 * Gamma;
        internal static long FlowTicks, ModelTicks;

        // rgb: 3 bytes per pixel. Returns true for foreground pixels.
        public static bool[] Segment(byte[] rgb, int w, int h, Rectangle rect, int rounds) { return Segment(rgb, w, h, rect, rect, rounds); }

        // As above, but only the core starts as probably foreground; the rest of the
        // rectangle starts as probably background (and may still turn out foreground).
        public static bool[] Segment(byte[] rgb, int w, int h, Rectangle rect, Rectangle core, int rounds)
        {
            int n = w * h;
            var state = new byte[n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    state[y * w + x] = !rect.Contains(x, y) ? Background : core.Contains(x, y) ? ProbablyForeground : ProbablyBackground;
            var colours = new double[n * 3];
            for (int i = 0; i < n * 3; i++) colours[i] = rgb[i];

            // Neighbour weights (to the left, up-left, up and up-right of each pixel): high
            // between similar colours. Beta adapts them to the picture's overall contrast.
            double total = 0; long pairs = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (x > 0) { total += Distance(colours, i, i - 1); pairs++; }
                    if (y > 0)
                    {
                        if (x > 0) { total += Distance(colours, i, i - w - 1); pairs++; }
                        total += Distance(colours, i, i - w); pairs++;
                        if (x < w - 1) { total += Distance(colours, i, i - w + 1); pairs++; }
                    }
                }
            double beta = total <= 1e-9 ? 0 : 1 / (2 * total / pairs);
            var flow = new GridMaxFlow(w, h);
            float diagonal = (float)(Gamma / Math.Sqrt(2));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (x > 0) flow.SetEdge(i, GridMaxFlow.Left, (float)(Gamma * Math.Exp(-beta * Distance(colours, i, i - 1))));
                    if (y > 0)
                    {
                        if (x > 0) flow.SetEdge(i, GridMaxFlow.UpLeft, (float)(diagonal * Math.Exp(-beta * Distance(colours, i, i - w - 1))));
                        flow.SetEdge(i, GridMaxFlow.Up, (float)(Gamma * Math.Exp(-beta * Distance(colours, i, i - w))));
                        if (x < w - 1) flow.SetEdge(i, GridMaxFlow.UpRight, (float)(diagonal * Math.Exp(-beta * Distance(colours, i, i - w + 1))));
                    }
                }

            var background = new Mixture(); var foreground = new Mixture();
            var component = new int[n];
            InitialModels(colours, state, n, background, foreground, component);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int round = 0; round < rounds; round++)
            {
                long started = clock.ElapsedTicks;
                // Each pixel joins its most likely component, then both models are re-learnt.
                for (int i = 0; i < n; i++)
                {
                    var model = IsForeground(state[i]) ? foreground : background;
                    component[i] = model.Which(colours, i * 3);
                }
                Learn(colours, state, n, component, background, foreground);
                flow.ResetEdges();
                for (int i = 0; i < n; i++)
                {
                    double fromSource, toSink;
                    if (state[i] == ProbablyBackground || state[i] == ProbablyForeground)
                    {
                        fromSource = -Math.Log(Math.Max(1e-300, background.Probability(colours, i * 3)));
                        toSink = -Math.Log(Math.Max(1e-300, foreground.Probability(colours, i * 3)));
                    }
                    else if (state[i] == Background) { fromSource = 0; toSink = Lambda; }
                    else { fromSource = Lambda; toSink = 0; }
                    flow.Terminal[i] = (float)(fromSource - toSink);
                }
                long modelled = clock.ElapsedTicks;
                ModelTicks += modelled - started;
                flow.Solve();
                FlowTicks += clock.ElapsedTicks - modelled;
                for (int i = 0; i < n; i++)
                    if (state[i] == ProbablyBackground || state[i] == ProbablyForeground)
                        state[i] = flow.InSource(i) ? ProbablyForeground : ProbablyBackground;
            }
            var result = new bool[n];
            for (int i = 0; i < n; i++) result[i] = IsForeground(state[i]);
            return result;
        }

        private static bool IsForeground(byte s) { return s == Foreground || s == ProbablyForeground; }

        private static double Distance(double[] c, int a, int b)
        {
            double d0 = c[a * 3] - c[b * 3], d1 = c[a * 3 + 1] - c[b * 3 + 1], d2 = c[a * 3 + 2] - c[b * 3 + 2];
            return d0 * d0 + d1 * d1 + d2 * d2;
        }

        // First models: k-means (k-means++ seeding, ten rounds) of each side's colours.
        private static void InitialModels(double[] colours, byte[] state, int n, Mixture background, Mixture foreground, int[] component)
        {
            var random = new Random(7);
            foreach (bool side in new[] { false, true })
            {
                var members = new List<int>();
                for (int i = 0; i < n; i++) if (IsForeground(state[i]) == side) members.Add(i);
                if (members.Count == 0) continue;
                int k = Mixture.Components;
                var centres = new double[k * 3];
                var nearest = new double[members.Count];
                int first = members[random.Next(members.Count)];
                for (int c = 0; c < 3; c++) centres[c] = colours[first * 3 + c];
                for (int m = 0; m < members.Count; m++) nearest[m] = double.MaxValue;
                for (int j = 1; j < k; j++)
                {
                    double sum = 0;
                    for (int m = 0; m < members.Count; m++)
                    {
                        nearest[m] = Math.Min(nearest[m], CentreDistance(colours, members[m], centres, j - 1));
                        sum += nearest[m];
                    }
                    double pick = random.NextDouble() * sum;
                    int chosen = members[members.Count - 1];
                    for (int m = 0; m < members.Count; m++) { pick -= nearest[m]; if (pick <= 0) { chosen = members[m]; break; } }
                    for (int c = 0; c < 3; c++) centres[j * 3 + c] = colours[chosen * 3 + c];
                }
                var sums = new double[k * 3]; var counts = new int[k];
                for (int round = 0; round < 10; round++)
                {
                    Array.Clear(sums, 0, sums.Length); Array.Clear(counts, 0, k);
                    foreach (int i in members)
                    {
                        int best = 0; double bestDistance = double.MaxValue;
                        for (int j = 0; j < k; j++)
                        {
                            double d = CentreDistance(colours, i, centres, j);
                            if (d < bestDistance) { bestDistance = d; best = j; }
                        }
                        component[i] = best;
                        counts[best]++;
                        for (int c = 0; c < 3; c++) sums[best * 3 + c] += colours[i * 3 + c];
                    }
                    for (int j = 0; j < k; j++)
                        if (counts[j] > 0) for (int c = 0; c < 3; c++) centres[j * 3 + c] = sums[j * 3 + c] / counts[j];
                }
            }
            Learn(colours, state, n, component, background, foreground);
        }

        private static double CentreDistance(double[] colours, int i, double[] centres, int j)
        {
            double d0 = colours[i * 3] - centres[j * 3], d1 = colours[i * 3 + 1] - centres[j * 3 + 1], d2 = colours[i * 3 + 2] - centres[j * 3 + 2];
            return d0 * d0 + d1 * d1 + d2 * d2;
        }

        private static void Learn(double[] colours, byte[] state, int n, int[] component, Mixture background, Mixture foreground)
        {
            background.Begin(); foreground.Begin();
            for (int i = 0; i < n; i++) (IsForeground(state[i]) ? foreground : background).Add(component[i], colours, i * 3);
            background.End(); foreground.End();
        }

        // A mixture of five full-covariance Gaussians over RGB.
        private sealed class Mixture
        {
            public const int Components = 5;
            private readonly double[] weight = new double[Components], mean = new double[Components * 3];
            private readonly double[] inverse = new double[Components * 9], scale = new double[Components];
            private readonly double[] sums = new double[Components * 3], products = new double[Components * 9];
            private readonly int[] counts = new int[Components];
            private int total;

            public void Begin()
            {
                Array.Clear(sums, 0, sums.Length); Array.Clear(products, 0, products.Length);
                Array.Clear(counts, 0, counts.Length); total = 0;
            }

            public void Add(int k, double[] c, int at)
            {
                for (int a = 0; a < 3; a++)
                {
                    sums[k * 3 + a] += c[at + a];
                    for (int b = 0; b < 3; b++) products[k * 9 + a * 3 + b] += c[at + a] * c[at + b];
                }
                counts[k]++; total++;
            }

            public void End()
            {
                for (int k = 0; k < Components; k++)
                {
                    if (counts[k] == 0 || total == 0) { weight[k] = 0; continue; }
                    weight[k] = counts[k] / (double)total;
                    var m = new double[3]; var cov = new double[9];
                    for (int a = 0; a < 3; a++) m[a] = sums[k * 3 + a] / counts[k];
                    for (int a = 0; a < 3; a++)
                        for (int b = 0; b < 3; b++) cov[a * 3 + b] = products[k * 9 + a * 3 + b] / counts[k] - m[a] * m[b];
                    double det = Determinant(cov);
                    if (det <= 1e-9)
                    {
                        // A flat component (one colour): a little noise keeps it invertible.
                        cov[0] += 0.01; cov[4] += 0.01; cov[8] += 0.01;
                        det = Determinant(cov);
                    }
                    for (int a = 0; a < 3; a++) mean[k * 3 + a] = m[a];
                    inverse[k * 9 + 0] = (cov[4] * cov[8] - cov[5] * cov[7]) / det;
                    inverse[k * 9 + 1] = -(cov[1] * cov[8] - cov[2] * cov[7]) / det;
                    inverse[k * 9 + 2] = (cov[1] * cov[5] - cov[2] * cov[4]) / det;
                    inverse[k * 9 + 3] = -(cov[3] * cov[8] - cov[5] * cov[6]) / det;
                    inverse[k * 9 + 4] = (cov[0] * cov[8] - cov[2] * cov[6]) / det;
                    inverse[k * 9 + 5] = -(cov[0] * cov[5] - cov[2] * cov[3]) / det;
                    inverse[k * 9 + 6] = (cov[3] * cov[7] - cov[4] * cov[6]) / det;
                    inverse[k * 9 + 7] = -(cov[0] * cov[7] - cov[1] * cov[6]) / det;
                    inverse[k * 9 + 8] = (cov[0] * cov[4] - cov[1] * cov[3]) / det;
                    scale[k] = 1 / Math.Sqrt(det);
                }
            }

            private static double Determinant(double[] c)
            {
                return c[0] * (c[4] * c[8] - c[5] * c[7]) - c[1] * (c[3] * c[8] - c[5] * c[6]) + c[2] * (c[3] * c[7] - c[4] * c[6]);
            }

            private double Density(int k, double[] c, int at)
            {
                if (weight[k] <= 0) return 0;
                double d0 = c[at] - mean[k * 3], d1 = c[at + 1] - mean[k * 3 + 1], d2 = c[at + 2] - mean[k * 3 + 2];
                int q = k * 9;
                double m = d0 * (d0 * inverse[q] + d1 * inverse[q + 3] + d2 * inverse[q + 6])
                         + d1 * (d0 * inverse[q + 1] + d1 * inverse[q + 4] + d2 * inverse[q + 7])
                         + d2 * (d0 * inverse[q + 2] + d1 * inverse[q + 5] + d2 * inverse[q + 8]);
                return scale[k] * Math.Exp(-0.5 * m);
            }

            public double Probability(double[] c, int at)
            {
                double p = 0;
                for (int k = 0; k < Components; k++) p += weight[k] * Density(k, c, at);
                return p;
            }

            public int Which(double[] c, int at)
            {
                int best = 0; double bestDensity = -1;
                for (int k = 0; k < Components; k++)
                {
                    double d = Density(k, c, at);
                    if (d > bestDensity) { bestDensity = d; best = k; }
                }
                return best;
            }
        }
    }

    // Max-flow / min-cut on an 8-connected pixel grid, by Boykov and Kolmogorov's method
    // ("An experimental comparison of min-cut/max-flow algorithms", 2004): two search trees
    // grow from the source and the sink through arcs with spare capacity until they touch,
    // the path found is saturated, and the nodes it cuts off are re-attached (or freed).
    // Every pixel has eight arcs, numbered by direction, so an arc's reverse is found by
    // arithmetic; Terminal[i] > 0 is capacity from the source, < 0 to the sink.
    internal sealed class GridMaxFlow
    {
        public const int Right = 0, Left = 1, Down = 2, Up = 3, DownRight = 4, UpLeft = 5, DownLeft = 6, UpRight = 7;
        private const int None = -1, TerminalParent = -2, Orphan = -3;
        private static readonly int[] StepX = { 1, -1, 0, 0, 1, -1, -1, 1 }, StepY = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private readonly int w, h, n;
        private readonly int[] offset = new int[8];
        public readonly float[] Terminal;
        private readonly float[] edge, capacity;   // the set weights, and the residual capacities
        private readonly byte[] valid;             // bit d: the arc in direction d exists
        private readonly int[] parent, stamp, distance, queue;
        private readonly bool[] sink, queued;
        private readonly int[] orphans;
        private int queueHead, queueCount, orphanCount, time;

        public GridMaxFlow(int width, int height)
        {
            w = width; h = height; n = w * h;
            for (int d = 0; d < 8; d++) offset[d] = StepX[d] + StepY[d] * w;
            Terminal = new float[n];
            edge = new float[n * 8]; capacity = new float[n * 8];
            valid = new byte[n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte bits = 0;
                    for (int d = 0; d < 8; d++)
                    {
                        int nx = x + StepX[d], ny = y + StepY[d];
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h) bits |= (byte)(1 << d);
                    }
                    valid[y * w + x] = bits;
                }
            parent = new int[n]; stamp = new int[n]; distance = new int[n]; queue = new int[n];
            sink = new bool[n]; queued = new bool[n];
            orphans = new int[n];
        }

        // Sets the weight of the edge between pixel i and its neighbour in direction d (both ways).
        public void SetEdge(int i, int d, float weight)
        {
            edge[i * 8 + d] = weight;
            edge[(i + offset[d]) * 8 + (d ^ 1)] = weight;
        }

        public void ResetEdges() { Buffer.BlockCopy(edge, 0, capacity, 0, edge.Length * 4); }

        // After Solve: pixels on the source side of the minimum cut (unreached ones count as source).
        public bool InSource(int i) { return parent[i] == None || !sink[i]; }

        private int Reverse(int arc) { return ((arc >> 3) + offset[arc & 7]) * 8 + ((arc & 7) ^ 1); }
        private int HeadOf(int arc) { return (arc >> 3) + offset[arc & 7]; }

        private void Activate(int i)
        {
            if (queued[i]) return;
            queued[i] = true;
            int at = queueHead + queueCount;
            queue[at >= n ? at - n : at] = i;
            queueCount++;
        }

        private int NextActive()
        {
            while (queueCount > 0)
            {
                int i = queue[queueHead];
                if (++queueHead == n) queueHead = 0;
                queueCount--;
                queued[i] = false;
                if (parent[i] != None) return i;
            }
            return -1;
        }

        public void Solve()
        {
            queueHead = queueCount = orphanCount = 0;
            for (int i = 0; i < n; i++)
            {
                queued[i] = false; stamp[i] = 0; distance[i] = 1;
                if (Terminal[i] > 0) { sink[i] = false; parent[i] = TerminalParent; Activate(i); }
                else if (Terminal[i] < 0) { sink[i] = true; parent[i] = TerminalParent; Activate(i); }
                else parent[i] = None;
            }
            time = 0;
            int current = -1;
            while (true)
            {
                int i = current >= 0 && parent[current] != None ? current : NextActive();
                current = -1;
                if (i < 0) break;
                int bridge = -1; // an arc from the source tree to the sink tree
                int bits = valid[i];
                if (!sink[i])
                {
                    for (int d = 0; d < 8 && bridge < 0; d++)
                    {
                        if ((bits & (1 << d)) == 0) continue;
                        int arc = i * 8 + d;
                        if (capacity[arc] <= 0) continue;
                        int j = i + offset[d];
                        if (parent[j] == None)
                        {
                            sink[j] = false; parent[j] = j * 8 + (d ^ 1); stamp[j] = stamp[i]; distance[j] = distance[i] + 1; Activate(j);
                        }
                        else if (sink[j]) bridge = arc;
                        else if (stamp[j] <= stamp[i] && distance[j] > distance[i])
                        {
                            parent[j] = j * 8 + (d ^ 1); stamp[j] = stamp[i]; distance[j] = distance[i] + 1;
                        }
                    }
                }
                else
                {
                    for (int d = 0; d < 8 && bridge < 0; d++)
                    {
                        if ((bits & (1 << d)) == 0) continue;
                        int j = i + offset[d], back = j * 8 + (d ^ 1);
                        if (capacity[back] <= 0) continue;
                        if (parent[j] == None)
                        {
                            sink[j] = true; parent[j] = back; stamp[j] = stamp[i]; distance[j] = distance[i] + 1; Activate(j);
                        }
                        else if (!sink[j]) bridge = back;
                        else if (stamp[j] <= stamp[i] && distance[j] > distance[i])
                        {
                            parent[j] = back; stamp[j] = stamp[i]; distance[j] = distance[i] + 1;
                        }
                    }
                }
                time++;
                if (bridge >= 0)
                {
                    current = i; // it may have more paths
                    Augment(bridge);
                    while (orphanCount > 0)
                    {
                        int o = orphans[--orphanCount];
                        if (sink[o]) AdoptSink(o); else AdoptSource(o);
                    }
                }
            }
        }

        private void MakeOrphan(int i) { parent[i] = Orphan; orphans[orphanCount++] = i; }

        private void Augment(int bridge)
        {
            int from = bridge >> 3, to = HeadOf(bridge);
            float bottleneck = capacity[bridge];
            for (int i = from; ; )
            {
                int p = parent[i];
                if (p == TerminalParent) { bottleneck = Math.Min(bottleneck, Terminal[i]); break; }
                bottleneck = Math.Min(bottleneck, capacity[Reverse(p)]);
                i = HeadOf(p);
            }
            for (int i = to; ; )
            {
                int p = parent[i];
                if (p == TerminalParent) { bottleneck = Math.Min(bottleneck, -Terminal[i]); break; }
                bottleneck = Math.Min(bottleneck, capacity[p]);
                i = HeadOf(p);
            }
            capacity[bridge] -= bottleneck;
            capacity[Reverse(bridge)] += bottleneck;
            for (int i = from; ; )
            {
                int p = parent[i];
                if (p == TerminalParent)
                {
                    Terminal[i] -= bottleneck;
                    if (Terminal[i] <= 0) { Terminal[i] = 0; MakeOrphan(i); }
                    break;
                }
                int down = Reverse(p), next = HeadOf(p);
                capacity[p] += bottleneck;
                capacity[down] -= bottleneck;
                if (capacity[down] <= 0) { capacity[down] = 0; MakeOrphan(i); }
                i = next;
            }
            for (int i = to; ; )
            {
                int p = parent[i];
                if (p == TerminalParent)
                {
                    Terminal[i] += bottleneck;
                    if (Terminal[i] >= 0) { Terminal[i] = 0; MakeOrphan(i); }
                    break;
                }
                int next = HeadOf(p);
                capacity[Reverse(p)] += bottleneck;
                capacity[p] -= bottleneck;
                if (capacity[p] <= 0) { capacity[p] = 0; MakeOrphan(i); }
                i = next;
            }
        }

        // Distance from j to its tree's terminal, or -1 if j's chain ends at an orphan; the
        // nodes walked are stamped with the current time and their distance, so later
        // walks stop early.
        private int Origin(int j)
        {
            int d = 0;
            for (int k = j; ; )
            {
                if (stamp[k] == time) { d += distance[k]; break; }
                int p = parent[k];
                d++;
                if (p == TerminalParent) { stamp[k] = time; distance[k] = 1; break; }
                if (p == Orphan || p == None) return -1;
                k = HeadOf(p);
            }
            int left = d;
            for (int k = j; stamp[k] != time; k = HeadOf(parent[k])) { stamp[k] = time; distance[k] = left--; }
            return d;
        }

        private void AdoptSource(int i)
        {
            int best = -1, bestDistance = int.MaxValue, bits = valid[i];
            for (int d = 0; d < 8; d++)
            {
                if ((bits & (1 << d)) == 0) continue;
                int j = i + offset[d];
                if (sink[j] || parent[j] == None || capacity[j * 8 + (d ^ 1)] <= 0) continue;
                int dj = Origin(j);
                if (dj >= 0 && dj < bestDistance) { bestDistance = dj; best = i * 8 + d; }
            }
            if (best >= 0) { parent[i] = best; stamp[i] = time; distance[i] = bestDistance + 1; return; }
            for (int d = 0; d < 8; d++)
            {
                if ((bits & (1 << d)) == 0) continue;
                int j = i + offset[d];
                if (sink[j] || parent[j] == None) continue;
                if (capacity[j * 8 + (d ^ 1)] > 0) Activate(j);
                int p = parent[j];
                if (p != TerminalParent && p != Orphan && HeadOf(p) == i) MakeOrphan(j);
            }
            parent[i] = None;
        }

        private void AdoptSink(int i)
        {
            int best = -1, bestDistance = int.MaxValue, bits = valid[i];
            for (int d = 0; d < 8; d++)
            {
                if ((bits & (1 << d)) == 0) continue;
                int j = i + offset[d];
                if (!sink[j] || parent[j] == None || capacity[i * 8 + d] <= 0) continue;
                int dj = Origin(j);
                if (dj >= 0 && dj < bestDistance) { bestDistance = dj; best = i * 8 + d; }
            }
            if (best >= 0) { parent[i] = best; stamp[i] = time; distance[i] = bestDistance + 1; return; }
            for (int d = 0; d < 8; d++)
            {
                if ((bits & (1 << d)) == 0) continue;
                int j = i + offset[d];
                if (!sink[j] || parent[j] == None) continue;
                if (capacity[i * 8 + d] > 0) Activate(j);
                int p = parent[j];
                if (p != TerminalParent && p != Orphan && HeadOf(p) == i) MakeOrphan(j);
            }
            parent[i] = None;
        }
    }
}
