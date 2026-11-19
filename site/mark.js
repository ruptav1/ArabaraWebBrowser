// The Arabara mark as a 3D ASCII solid, as behind the app's start page, here alone in the
// middle at a medium size and brighter: the extruded triangle with its opening, striped
// faces, turning about all three axes at even speeds. It only animates while on screen.
(function () {
  "use strict";
  var canvas = document.getElementById("mark3d");
  if (!canvas) return;
  var ctx = canvas.getContext("2d");
  // The animation is the point of the page, so it always runs; when reduced motion is asked
  // for (for example with Windows animation effects turned off) it runs at half speed.
  var pace = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches ? 0.5 : 1;
  var FONT = "11px 'White Rabbit', Consolas, monospace";
  var RAMP = ".,:;-=+*#%@";
  var TONES = ["#5c1520", "#8e2232", "#c8344a", "#ff5068", "#ffa0ae"];
  var W, H, cw, ch = 12, cols, rows, light, depth, visible = false;

  var ox = [0, 1.08, -1.08], oy = [-0.78, 0.62, 0.62], ix = [0, 0.34, -0.34], iy = [0.04, 0.47, 0.47];
  var vx = [], vy = [], vz = [], i;
  for (i = 0; i < 3; i++) {
    put(i, ox[i], oy[i] - 0.15, -0.2); put(i + 3, ix[i], iy[i] - 0.15, -0.2);
    put(i + 6, ox[i], oy[i] - 0.15, 0.2); put(i + 9, ix[i], iy[i] - 0.15, 0.2);
  }
  function put(k, x, y, z) { vx[k] = x; vy[k] = y; vz[k] = z; }
  var faces = [];
  for (var e = 0; e < 3; e++) {
    var n = (e + 1) % 3;
    [0, 6].forEach(function (s) { faces.push([s + e, s + n, s + 3 + n, true], [s + e, s + 3 + n, s + 3 + e, true]); });
    [0, 3].forEach(function (r) { faces.push([r + e, r + n, r + 6 + n, false], [r + e, r + 6 + n, r + 6 + e, false]); });
  }
  var speed = [], phase = [];
  for (i = 0; i < 3; i++) {
    speed[i] = (Math.random() < 0.5 ? -1 : 1) * 2 * Math.PI / (14 + Math.random() * 8);
    phase[i] = Math.random() * 2 * Math.PI;
  }
  var sx = [], sy = [], sz = [], rx = [], ry = [], lastT = 0;

  function resize() {
    var dpr = Math.min(2, window.devicePixelRatio || 1), box = canvas.getBoundingClientRect();
    W = Math.max(1, box.width); H = Math.max(1, box.height);
    canvas.width = Math.round(W * dpr); canvas.height = Math.round(H * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.font = FONT; ctx.textBaseline = "top";
    cw = Math.max(4, ctx.measureText("MMMMMMMMMM").width / 10);
    cols = Math.floor(W / cw); rows = Math.floor(H / ch);
    light = new Float32Array(cols * rows); depth = new Float32Array(cols * rows);
    draw(lastT);
  }

  function raster(f) {
    var a = f[0], b = f[1], c = f[2];
    var x0 = sx[a], y0 = sy[a], x1 = sx[b], y1 = sy[b], x2 = sx[c], y2 = sy[c];
    var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
    if (Math.abs(area) < 1e-3) return;
    var ux = rx[b] - rx[a], uy = ry[b] - ry[a], uz = sz[b] - sz[a], wx = rx[c] - rx[a], wy = ry[c] - ry[a], wz = sz[c] - sz[a];
    var nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx, len = Math.sqrt(nx * nx + ny * ny + nz * nz);
    var shade = 0.3 + 0.7 * (len < 1e-9 ? 0.5 : Math.abs((-0.45 * nx - 0.55 * ny - 0.7 * nz) / (len * 1.0005)));
    var c0 = Math.max(0, Math.floor(Math.min(x0, x1, x2) / cw)), c1 = Math.min(cols - 1, Math.floor(Math.max(x0, x1, x2) / cw));
    var r0 = Math.max(0, Math.floor(Math.min(y0, y1, y2) / ch)), r1 = Math.min(rows - 1, Math.floor(Math.max(y0, y1, y2) / ch));
    for (var r = r0; r <= r1; r++) {
      var py = (r + 0.5) * ch;
      for (var col = c0; col <= c1; col++) {
        var px = (col + 0.5) * cw;
        var w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) / area;
        var w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) / area, w2 = 1 - w0 - w1;
        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
        var z = w0 * sz[a] + w1 * sz[b] + w2 * sz[c], k = r * cols + col;
        if (z >= depth[k]) continue;
        depth[k] = z;
        var level = shade;
        if (f[3]) {
          // Horizontal stripes in the mark's own space, like the flat logo.
          var band = (w0 * vy[a] + w1 * vy[b] + w2 * vy[c] + 4) / 0.13;
          if (band - Math.floor(band) < 0.38) level *= 0.45;
        }
        light[k] = level;
      }
    }
  }

  var layers = [];
  function draw(t) {
    lastT = t;
    var a = phase[0] + speed[0] * t, b = phase[1] + speed[1] * t, c = phase[2] + speed[2] * t;
    var ca = Math.cos(a), sa = Math.sin(a), cb = Math.cos(b), sb = Math.sin(b), cc = Math.cos(c), sc = Math.sin(c);
    var radius = Math.min(W, H) * 0.4;
    for (var k = 0; k < 12; k++) {
      var x = vx[k], y = vy[k] * ca - vz[k] * sa, z = vy[k] * sa + vz[k] * ca;
      var x2 = x * cb + z * sb, z2 = -x * sb + z * cb, x3 = x2 * cc - y * sc, y3 = x2 * sc + y * cc, p = 3.4 / (3.4 + z2);
      sx[k] = W / 2 + x3 * p * radius; sy[k] = H / 2 + y3 * p * radius; sz[k] = z2; rx[k] = x3; ry[k] = y3;
    }
    depth.fill(1e9); light.fill(-1);
    faces.forEach(raster);
    ctx.clearRect(0, 0, W, H);
    for (var r = 0; r < rows; r++) {
      for (var tn = 0; tn < TONES.length; tn++) layers[tn] = null;
      for (var col = 0; col < cols; col++) {
        var level = light[r * cols + col];
        if (level < 0.05) continue;
        var tone = Math.min(TONES.length - 1, Math.floor(level * TONES.length));
        if (!layers[tone]) layers[tone] = new Array(cols + 1).join(" ").split("");
        layers[tone][col] = RAMP[Math.min(RAMP.length - 1, Math.floor(level * RAMP.length))];
      }
      for (tn = 0; tn < TONES.length; tn++) if (layers[tn]) { ctx.fillStyle = TONES[tn]; ctx.fillText(layers[tn].join(""), 0, r * ch); }
    }
  }

  var t0 = performance.now(), last = 0, running = false;
  function loop(now) {
    if (!visible) { running = false; return; }
    if (now - last >= 30) { last = now; draw((now - t0) / 1000 * pace); }
    requestAnimationFrame(loop);
  }
  if ("IntersectionObserver" in window) {
    new IntersectionObserver(function (entries) {
      visible = entries[0].isIntersecting;
      if (visible && !running) { running = true; requestAnimationFrame(loop); }
    }).observe(canvas);
  }
  resize();
  window.addEventListener("resize", resize);
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(resize);
})();
