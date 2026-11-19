// SOUND WAVE, as on the app's start page: an ASCII wireframe sea seen from above, a diamond
// lattice fixed to the water and drawn as continuous strokes. On the website there is no
// sound to follow, so the water simply rolls: a long swell, slow hills and a fine chop.
(function () {
  "use strict";
  var canvas = document.getElementById("wave");
  if (!canvas) return;
  var ctx = canvas.getContext("2d");
  // The animation is the point of the page, so it always runs; when reduced motion is asked
  // for (for example with Windows animation effects turned off) it runs at half speed.
  var pace = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches ? 0.5 : 1;
  var FONT = "13px 'White Rabbit', Consolas, monospace";
  // Darkest to brightest, ending in the highlight (the app's tones for this scene).
  var TONES = ["#3a0f15", "#4c131b", "#56151e", "#761b27", "#9e2232", "#d6384c", "#ff96a4"];
  var ZNEAR = 1.5, ZFAR = 14, YAW = Math.PI / 4, STEP = 0.85;
  var CAM = 5.4, PITCH = 0.5, cosP = Math.cos(PITCH), sinP = Math.sin(PITCH);
  var W, H, dpr, cw, ch = 15, cols, rows, iw, ih, ink, focal, cx, cy;
  var lastT = -1;
  var px = [], py = [], lit = [], ok = [], latCols = 0, latRows = 0;

  function resize() {
    dpr = Math.min(2, window.devicePixelRatio || 1);
    W = window.innerWidth; H = window.innerHeight;
    canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.font = FONT; ctx.textBaseline = "top";
    cw = Math.max(5, ctx.measureText("MMMMMMMMMM").width / 10);
    cols = Math.ceil(W / cw); rows = Math.ceil(H / ch);
    iw = Math.ceil(W / 2); ih = Math.ceil(H / 2); ink = new Float32Array(iw * ih);
    focal = W * 0.62; cx = W / 2; cy = H * 0.36;
    draw(lastT < 0 ? 0 : lastT);
  }

  function reach(z) { return (z * cosP + CAM * sinP) * W / (2 * focal) * 1.15; }

  function height(x, z, t, out) {
    var nearness = Math.max(0, Math.min(1, 1 - (z - ZNEAR) / (ZFAR - ZNEAR)));
    var sp = 0.34 * z - 0.85 * t + 0.2 * x, hp = 0.3 * z - 0.15 * t + 0.5 * Math.sin(0.15 * x);
    var sa = 0.8, ha = 0.55 * Math.sin(0.36 * x + 0.2 * t);
    var chop = 0.08 * Math.sin(1.4 * x + 1.1 * z - 2.4 * t) * 0.5 * nearness;
    out.slope = sa * 0.34 * Math.cos(sp) - ha * 0.3 * Math.sin(hp);
    return sa * Math.sin(sp) + ha * Math.cos(hp) + chop;
  }

  function inkPoint(x, y, v) {
    if (x < 0 || y < 0 || x >= iw || y >= ih) return;
    var i = y * iw + x;
    if (v > ink[i]) ink[i] = v;
  }

  // Xiaolin Wu's anti-aliased line into the half-resolution ink buffer.
  function inkLine(x0, y0, x1, y1, v) {
    x0 /= 2; y0 /= 2; x1 /= 2; y1 /= 2;
    if ((x0 < 0 && x1 < 0) || (x0 >= iw && x1 >= iw) || (y0 < 0 && y1 < 0) || (y0 >= ih && y1 >= ih)) return;
    var steep = Math.abs(y1 - y0) > Math.abs(x1 - x0), s;
    if (steep) { s = x0; x0 = y0; y0 = s; s = x1; x1 = y1; y1 = s; }
    if (x0 > x1) { s = x0; x0 = x1; x1 = s; s = y0; y0 = y1; y1 = s; }
    var dx = x1 - x0, g = dx < 0.001 ? 1 : (y1 - y0) / dx, limit = steep ? ih : iw;
    var start = Math.max(0, Math.round(x0)), end = Math.min(limit - 1, Math.round(x1)), y = y0 + g * (start - x0);
    for (var x = start; x <= end; x++, y += g) {
      var iy = Math.floor(y), f = y - iy;
      if (steep) { inkPoint(iy, x, (1 - f) * v); inkPoint(iy + 1, x, f * v); }
      else { inkPoint(x, iy, (1 - f) * v); inkPoint(x, iy + 1, f * v); }
    }
  }

  var layers = [];
  function draw(t) {
    lastT = t;
    var i, j;

    // The lattice covers the water in view, in its own turned axes.
    var cosY = Math.cos(YAW), sinY = Math.sin(YAW), centre = (ZNEAR + ZFAR) / 2;
    var minA = 1e9, maxA = -1e9, minB = 1e9, maxB = -1e9;
    [ZNEAR, ZFAR].forEach(function (z) {
      [-1, 1].forEach(function (side) {
        var x = side * reach(z), dz = z - centre, la = x * cosY + dz * sinY, lb = -x * sinY + dz * cosY;
        minA = Math.min(minA, la); maxA = Math.max(maxA, la); minB = Math.min(minB, lb); maxB = Math.max(maxB, lb);
      });
    });
    latCols = Math.min(90, Math.ceil((maxA - minA) / STEP) + 1);
    latRows = Math.min(90, Math.ceil((maxB - minB) / STEP) + 1);
    var out = { slope: 0 };
    for (j = 0; j < latRows; j++) for (i = 0; i < latCols; i++) {
      var k = j * latCols + i, la2 = minA + i * STEP, lb2 = minB + j * STEP;
      var x = la2 * cosY - lb2 * sinY, z = centre + la2 * sinY + lb2 * cosY, r = reach(z);
      if (z < ZNEAR - STEP || z > ZFAR + STEP || Math.abs(x) > r + STEP * 2) { ok[k] = false; continue; }
      var y = height(x, z, t, out);
      var nearness = Math.max(0, Math.min(1, 1 - (z - ZNEAR) / (ZFAR - ZNEAR)));
      var dy = y - CAM, yc = dy * cosP + z * sinP, zc = z * cosP - dy * sinP;
      ok[k] = zc > 0.3;
      px[k] = cx + x / zc * focal; py[k] = cy - yc / zc * focal;
      var shade = Math.max(0.55, Math.min(1.2, 0.85 - out.slope * 0.45));
      lit[k] = Math.max(0, Math.min(1, (0.12 + 0.88 * nearness * Math.sqrt(nearness)) * shade * (0.9 + 0.08 * y)));
    }

    ink.fill(0);
    for (j = latRows - 1; j >= 0; j--) for (i = 0; i < latCols; i++) {
      k = j * latCols + i;
      if (!ok[k]) continue;
      if (i + 1 < latCols && ok[k + 1]) segment(k, k + 1);
      if (j + 1 < latRows && ok[k + latCols]) segment(k, k + latCols);
    }

    // Each cell shows the stroke its ink forms: its spread gives the direction.
    ctx.clearRect(0, 0, W, H);
    for (var row = 0; row < rows; row++) {
      for (var tn = 0; tn < TONES.length; tn++) layers[tn] = null;
      for (var col = 0; col < cols; col++) {
        var left = col * cw, top = row * ch;
        var ix0 = Math.max(0, Math.floor(left / 2)), ix1 = Math.min(iw, Math.ceil((left + cw) / 2));
        var iy0 = Math.max(0, Math.floor(top / 2)), iy1 = Math.min(ih, Math.ceil((top + ch) / 2));
        var sx = Math.max(1, ix1 - ix0), sy = Math.max(1, iy1 - iy0);
        var wsum = 0, peak = 0, mx = 0, my = 0, mxx = 0, myy = 0, mxy = 0;
        for (var yy = iy0; yy < iy1; yy++) {
          var fy = (yy - iy0 + 0.5) / sy, line = yy * iw;
          for (var xx = ix0; xx < ix1; xx++) {
            var v = ink[line + xx];
            if (v <= 0.02) continue;
            var fx = (xx - ix0 + 0.5) / sx;
            wsum += v; mx += v * fx; my += v * fy; mxx += v * fx * fx; myy += v * fy * fy; mxy += v * fx * fy;
            if (v > peak) peak = v;
          }
        }
        if (wsum <= 0 || peak < 0.05) continue;
        var level = Math.min(1.15, peak * 1.5);
        mx /= wsum; my /= wsum;
        var cxx = mxx / wsum - mx * mx, cyy = myy / wsum - my * my, cxy = mxy / wsum - mx * my, spread = cxx + cyy;
        var ani = spread <= 1e-6 ? 0 : Math.sqrt((cxx - cyy) * (cxx - cyy) + 4 * cxy * cxy) / spread, glyph;
        if (spread < 0.004) glyph = ".";
        else if (ani < 0.45) glyph = "x";
        else {
          var angle = 0.5 * Math.atan2(2 * cxy, cxx - cyy) * 180 / Math.PI, a = Math.abs(angle);
          glyph = a < 22 ? (my > 0.72 ? "_" : "-") : a > 68 ? "|" : angle > 0 ? "\\" : "/";
        }
        var tone = Math.min(TONES.length - 1, Math.floor(level / 1.15 * TONES.length));
        if (!layers[tone]) layers[tone] = new Array(cols + 1).join(" ").split("");
        layers[tone][col] = glyph;
      }
      for (tn = 0; tn < TONES.length; tn++) if (layers[tn]) { ctx.fillStyle = TONES[tn]; ctx.fillText(layers[tn].join(""), 0, row * ch); }
    }
  }

  function segment(a, b) {
    var v = (lit[a] + lit[b]) / 2;
    if (v >= 0.04) inkLine(px[a], py[a], px[b], py[b], v);
  }

  // About 30 frames per second; requestAnimationFrame pauses while the page is hidden.
  var t0 = performance.now(), last = 0;
  function loop(now) {
    // 30 rather than 33 ms: at 60 Hz this lands on every second display frame instead of
    // sometimes slipping a frame, which would make the motion stutter.
    if (now - last >= 30) { last = now; draw((now - t0) / 1000 * pace); }
    requestAnimationFrame(loop);
  }
  // The wave fills the first screen; under text it fades back so the text reads clearly.
  var hero = document.querySelector(".hero");
  function fade() {
    var r = hero ? Math.min(1, window.scrollY / (window.innerHeight * 0.8)) : 1;
    canvas.style.opacity = (1 - 0.7 * r).toFixed(2);
  }
  window.addEventListener("scroll", fade, { passive: true });
  fade();
  resize();
  window.addEventListener("resize", resize);
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(resize);
  requestAnimationFrame(loop);
})();
