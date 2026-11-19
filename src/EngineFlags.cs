using System;
using System.Collections.Generic;
using System.Linq;

namespace Arabara
{
    // Chromium switch sets compared by --memory-bench. Measured on 2026-10-05 with
    // YouTube (playing), YouTube Music and Discord, none saved memory beyond run-to-run
    // noise except low-end mode (about 6%, with possible rendering trade-offs), so the
    // app keeps Chromium's defaults. Security features are never changed here.
    internal static class EngineFlags
    {
        // Features that only cost memory or background work in a single-window browser.
        private static readonly string[] Lean =
        {
            "SpareRendererForSitePerProcess", // idle pre-started renderer process
            "BackForwardCache",               // keeps previous pages alive in memory
            "OptimizationHints", "OptimizationGuideModelDownloading", "MediaRouter", "Translate"
        };

        // Benchmark variants. Returns null for an unknown name.
        public static string Variant(string name)
        {
            // Only the back-forward cache, measured by browsing in one tab (--navigate).
            if (name == "nobfcache") return "--disable-features=BackForwardCache";
            var disable = new List<string>(Lean);
            var enable = new List<string>();
            var extra = new List<string> { "--disable-component-update" };
            switch (name)
            {
                case "lean": break;
                case "inproc": disable.Add("AudioServiceOutOfProcess"); enable.Add("NetworkServiceInProcess2"); break;
                case "lowend": extra.Add("--enable-low-end-device-mode"); break;
                case "limit": extra.Add("--renderer-process-limit=2"); break;
                default: return null;
            }
            var parts = new List<string> { "--disable-features=" + string.Join(",", disable) };
            if (enable.Count > 0) parts.Add("--enable-features=" + string.Join(",", enable));
            parts.AddRange(extra);
            return string.Join(" ", parts);
        }
    }
}
