namespace Arabara
{
    // Optional page scripts measured with --memory-bench before any is enabled in the app.
    internal static class PageTweaks
    {
        // YouTube light mode: report VP8/VP9/AV1 as unsupported so the player picks H.264,
        // which nearly every GPU decodes in hardware (YouTube then tops out at 1080p).
        public const string H264Only = @"(() => {
  const blocked = t => /vp0?8|vp0?9|av01/i.test(t || '');
  if (window.MediaSource) { const o = MediaSource.isTypeSupported.bind(MediaSource); MediaSource.isTypeSupported = t => !blocked(t) && o(t); }
  const c = HTMLMediaElement.prototype.canPlayType;
  HTMLMediaElement.prototype.canPlayType = function (t) { return blocked(t) ? '' : c.call(this, t); };
  if (navigator.mediaCapabilities && navigator.mediaCapabilities.decodingInfo) {
    const d = navigator.mediaCapabilities.decodingInfo.bind(navigator.mediaCapabilities);
    navigator.mediaCapabilities.decodingInfo = cfg => blocked(cfg && cfg.video && cfg.video.contentType)
      ? Promise.resolve({ supported: false, smooth: false, powerEfficient: false }) : d(cfg);
  }
})();";
    }
}
