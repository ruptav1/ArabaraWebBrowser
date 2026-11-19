using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;

namespace Arabara
{
    internal sealed partial class BrowserForm
    {
        // Isolated smoke fixture only. Silent synthetic audio, a local WebRTC pair,
        // and an animated canvas; no real microphone, account or external server.
        private const string StartVisualProbe = @"(async () => {
            const p = window.visualProbe = { frames: 0, messages: 0, stopped: false };
            p.canvas = document.createElement('canvas');
            p.canvas.width = 720; p.canvas.height = 400;
            document.body.appendChild(p.canvas);
            const graphics = p.canvas.getContext('2d');
            const draw = () => {
                if (p.stopped) return;
                p.frames++;
                for (let n = 0; n < 180; n++) {
                    graphics.fillStyle = 'hsl(' + ((p.frames + n) % 360) + ',70%,35%)';
                    graphics.fillRect((p.frames * 3 + n * 17) % 720, (n * 23) % 400, 35, 35);
                }
                p.raf = requestAnimationFrame(draw);
            };
            p.raf = requestAnimationFrame(draw);
            p.context = new AudioContext();
            p.oscillator = p.context.createOscillator();
            p.gain = p.context.createGain(); p.gain.gain.value = 0;
            p.output = p.context.createMediaStreamDestination();
            p.oscillator.connect(p.gain).connect(p.output);
            p.oscillator.start(); await p.context.resume();
            p.video = document.createElement('video');
            p.video.srcObject = p.output.stream;
            document.body.appendChild(p.video);
            await p.video.play();
            p.sender = new RTCPeerConnection({ iceServers: [] });
            p.receiver = new RTCPeerConnection({ iceServers: [] });
            p.sender.onicecandidate = e => { if (e.candidate) p.receiver.addIceCandidate(e.candidate).catch(() => {}); };
            p.receiver.onicecandidate = e => { if (e.candidate) p.sender.addIceCandidate(e.candidate).catch(() => {}); };
            p.receiver.ondatachannel = e => { e.channel.onmessage = () => p.messages++; };
            p.sender.addTrack(p.output.stream.getAudioTracks()[0], p.output.stream);
            p.channel = p.sender.createDataChannel('local-test');
            await p.sender.setLocalDescription(await p.sender.createOffer());
            await p.receiver.setRemoteDescription(p.sender.localDescription);
            await p.receiver.setLocalDescription(await p.receiver.createAnswer());
            await p.sender.setRemoteDescription(p.receiver.localDescription);
            await new Promise((resolve, reject) => {
                const timeout = setTimeout(() => reject(new Error('Local WebRTC connection timed out')), 8000);
                if (p.channel.readyState === 'open') { clearTimeout(timeout); resolve(); }
                else p.channel.onopen = () => { clearTimeout(timeout); resolve(); };
            });
            p.heartbeat = setInterval(() => { if (p.channel.readyState === 'open') p.channel.send('heartbeat'); }, 250);
            return { ready: true };
        })()";

        private const string ReadVisualProbe = @"(async () => {
            const p = window.visualProbe;
            let packets = 0;
            const reports = await p.receiver.getStats();
            reports.forEach(r => { if (r.type === 'inbound-rtp' && r.kind === 'audio') packets += r.packetsReceived; });
            return { frames: p.frames, ticks: window.ticks, pings: window.pings, messages: p.messages,
                packets: packets, audioTime: p.context.currentTime, videoTime: p.video.currentTime,
                videoPaused: p.video.paused, audioState: p.context.state,
                connected: p.sender.connectionState, visibility: document.visibilityState };
        })()";

        private async Task<Dictionary<string, object>> EvaluateVisualProbeAsync(BrowserTab tab, string expression, bool gesture)
        {
            var json = new JavaScriptSerializer();
            string parameters = json.Serialize(new { expression = expression, awaitPromise = true, returnByValue = true, userGesture = gesture });
            var result = json.Deserialize<Dictionary<string, object>>(await Core(tab).CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters));
            SelfTests.Require(!result.ContainsKey("exceptionDetails"), "Visual fixture script: " + json.Serialize(result));
            return (Dictionary<string, object>)((Dictionary<string, object>)result["result"])["value"];
        }

        private double ReadCpuMilliseconds()
        {
            double total = 0;
            var ids = new HashSet<int> { Process.GetCurrentProcess().Id };
            foreach (var info in environment.GetProcessInfos()) ids.Add(info.ProcessId);
            foreach (int id in ids)
            {
                try { using (var process = Process.GetProcessById(id)) total += process.TotalProcessorTime.TotalMilliseconds; }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return total;
        }

        private async Task TestBackgroundVisualsAsync(BrowserTab tab, Action<string> record)
        {
            SetPauseBackgroundVisuals(true, false);
            SetWindowActive(true);
            tab.MediaLease = true;
            Exception failure = null;
            try
            {
                await EvaluateVisualProbeAsync(tab, StartVisualProbe, true);
                await Task.Delay(500);
                var before = await EvaluateVisualProbeAsync(tab, ReadVisualProbe, false);
                double cpu = ReadCpuMilliseconds();
                var elapsed = Stopwatch.StartNew();
                await Task.Delay(2500);
                double liveCpu = (ReadCpuMilliseconds() - cpu) / elapsed.Elapsed.TotalSeconds;
                var live = await EvaluateVisualProbeAsync(tab, ReadVisualProbe, false);
                int liveFrames = Convert.ToInt32(live["frames"]) - Convert.ToInt32(before["frames"]);
                record("Visual fixture / foreground: " + liveFrames + " animation callbacks; " + liveCpu.ToString("F1") + " CPU ms/s (host + engine).");

                SetWindowActive(false);
                await Task.Delay(300);
                SelfTests.Require(!tab.View.Visible && tab.Page.Visible && tab.PausedVisuals.Visible, "Background rendering is replaced by the native placeholder");
                SelfTests.Require(!Core(tab).IsSuspended && !focusTimer.Enabled, "Visual pause leaves page active without focus polling");
                using (var bitmap = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                    bitmap.Save(Path.Combine(SelfTests.ResultsRoot, "rendering-paused.png"));
                }
                var hidden = await EvaluateVisualProbeAsync(tab, ReadVisualProbe, false);
                cpu = ReadCpuMilliseconds(); elapsed.Restart();
                await Task.Delay(2500);
                double hiddenCpu = (ReadCpuMilliseconds() - cpu) / elapsed.Elapsed.TotalSeconds;
                var after = await EvaluateVisualProbeAsync(tab, ReadVisualProbe, false);
                int hiddenFrames = Convert.ToInt32(after["frames"]) - Convert.ToInt32(hidden["frames"]);
                SelfTests.Require((string)after["visibility"] == "hidden" && hiddenFrames <= 1, "Hidden page stops animation frame callbacks");
                foreach (string field in new[] { "ticks", "pings", "messages", "packets", "audioTime", "videoTime" })
                    SelfTests.Require(Convert.ToDouble(after[field]) > Convert.ToDouble(hidden[field]), "Background activity continues: " + field);
                SelfTests.Require((string)after["audioState"] == "running" && !(bool)after["videoPaused"] && (string)after["connected"] == "connected", "Media playback and local WebRTC stay active");
                record("Visual fixture / background: " + hiddenFrames + " animation callbacks; " + hiddenCpu.ToString("F1") + " CPU ms/s (host + engine).");
                record("PASS: Rendering paused while JS timers, fetch, data channel, WebRTC audio packets and silent media playback continued.");

                SetLowMemoryMode(false, false);
                SelfTests.Require(!Core(tab).IsSuspended && !EligibleForSleep(tab), "Visual pause does not suspend the selected page in sleep mode");
                SetLowMemoryMode(true, false);
                SetPauseBackgroundVisuals(false, false);
                await Task.Delay(150);
                SelfTests.Require(tab.View.Visible && !tab.PausedVisuals.Visible, "Live option restores background rendering");
                SetPauseBackgroundVisuals(true, false);
                SelfTests.Require(!tab.View.Visible, "Auto option immediately pauses background rendering again");
                SetWindowActive(true);
                await Task.Delay(500);
                var resumed = await EvaluateVisualProbeAsync(tab, ReadVisualProbe, false);
                SelfTests.Require(tab.View.Visible && !tab.PausedVisuals.Visible && (string)resumed["visibility"] == "visible", "Foreground restores the existing page");
                if (liveFrames > 0)
                    SelfTests.Require(Convert.ToInt32(resumed["frames"]) > Convert.ToInt32(after["frames"]), "Animation resumes in foreground");
                else record("NOTE: No foreground animation callbacks were observable in the hidden smoke window; CPU values are diagnostic only.");
                record("PASS: Visuals Live/Auto toggle, foreground restore and protected sleep-mode interaction.");
            }
            catch (Exception ex) { failure = ex; }
            try
            {
                if (Core(tab) != null)
                    await Core(tab).ExecuteScriptAsync("(() => { const p = window.visualProbe; if (!p) return; p.stopped = true; cancelAnimationFrame(p.raf); clearInterval(p.heartbeat); if(p.sender) p.sender.close(); if(p.receiver) p.receiver.close(); if(p.video) { p.video.pause(); p.video.srcObject = null; p.video.remove(); } if(p.output) p.output.stream.getTracks().forEach(t => t.stop()); if(p.context) p.context.close(); if(p.canvas) p.canvas.remove(); })()");
            }
            finally
            {
                tab.MediaLease = false;
                SetPauseBackgroundVisuals(true, false);
                SetWindowActive(true);
            }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
