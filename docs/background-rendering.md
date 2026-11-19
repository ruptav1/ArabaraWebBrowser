# Background rendering in Arabara 0.3.0

## Behavior

**Visuals:Auto** hides the selected WebView2 control when focus moves to another application. Its place is occupied by a static native label: **Rendering paused**. No page screenshot or preview bitmap is retained. Returning to Arabara restores the same page without reloading it.

A single 200 ms timer checks the foreground process after deactivation. Windows belonging to Arabara or its WebView2 environment count as internal focus. The timer then stops; there is no continuous focus polling. **Visuals:Live** disables this automatic rendering pause. The choice is saved in preferences. Version 0.3.1 uses these compact terminal-style button labels.

Selection and rendering visibility are tracked separately. Pausing rendering does not make the selected page eligible for idle-tab suspension. Low RAM remains an independent setting; existing microphone, camera, audio, download and Keep alive protections remain in place.

## Scope and limitations

This uses WebView2's supported visibility behavior. Arabara does not inject changes into website animation APIs, stop JavaScript timers, pause media elements or stop media tracks. Scripts, connections and media may continue while the view is hidden. Websites can respond to visibility changes themselves; choose Live if a site pauses playback or behaves incorrectly in Auto mode.

Hidden rendering does not guarantee that all video decoding, GIF processing, DOM updates or JavaScript work stops. Stopping a media element can also stop its audio. Real Discord voice, YouTube Music, camera and screen sharing need separate manual verification. This feature does not guarantee a total memory footprint below 100 MB.

See Microsoft's [WebView2 visibility documentation](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.isvisible) and [performance guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/performance).

## Validation on 2026-10-05

Tested with WebView2 Runtime 154.0.4258.53. The isolated fixture includes an animated canvas, timers, fetch requests, a silent audio-only MediaStream played through a video element, and a local WebRTC peer pair with an audio track and data channel. The peer pair uses an empty ICE-server list, without a real microphone, external signaling server or STUN service.

| Diagnostic sample | Foreground | Rendering paused |
| --- | ---: | ---: |
| Animation callbacks over approximately 2.5 seconds | 151 | 0 |
| Host + engine CPU time per wall-clock second | 586.6 ms | 112.2 ms |

The short synthetic sample used about 81% less aggregate CPU time while hidden. This is not a controlled Discord benchmark or a GPU measurement. Startup, system load and the independent Low RAM mode can affect the result.

While rendering was paused, timers, network requests, data-channel messages, received WebRTC audio packets and the silent media timeline continued advancing. The tests also passed for the Live/Auto toggle, foreground restoration, and interaction with idle-tab suspension. Actual video decoding was not measured by the audio-only media fixture.

The hidden smoke-test window explicitly simulates application focus states. It does not test physical desktop Alt+Tab behavior. See the [0.3.0 diagnostic results](../test-results/smoke-tests-0.3.0.txt), [latest smoke-test results](../test-results/smoke-tests.txt) and the [native placeholder snapshot](../test-results/rendering-paused.png).

## Reuse decision

USE/ADAPT: the existing Microsoft.Web.WebView2 1.0.4258.31 dependency, its WinForms visibility support and native focus events. Local SDK documentation and Microsoft's documentation were reviewed. No additional third-party package or runtime was installed. The existing pinned package, hash verification and bundled license remain unchanged.

Full page suspension and blanket JavaScript monkey-patching were rejected because they can interrupt timers and call logic. This decision is recorded here manually; the reuse-first Python helper was unavailable on PATH.
