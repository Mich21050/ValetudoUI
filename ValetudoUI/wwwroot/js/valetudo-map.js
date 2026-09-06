// Canvas renderer for Valetudo maps. Used for the live map and for stored history maps.
// Coordinates: map JSON entities are in cm; layers are in pixels (cm / pixelSize).
// Everything is drawn in "raster space" = map pixels relative to the layers' bounding box.

const PALETTES = {
  light: {
    floor: [0, 118, 255], wall: [51, 51, 51],
    segments: [[25, 161, 161], [122, 192, 55], [223, 86, 24], [247, 200, 65], [153, 102, 204]],
    path: '#ffffff', predicted: 'rgba(255,255,255,0.7)', label: '#111111', labelHalo: 'rgba(255,255,255,0.85)'
  },
  dark: {
    floor: [0, 94, 204], wall: [51, 51, 51],
    segments: [[20, 129, 129], [98, 154, 44], [178, 69, 19], [198, 160, 52], [122, 82, 163]],
    path: '#000000', predicted: 'rgba(0,0,0,0.7)', label: '#ffffff', labelHalo: 'rgba(0,0,0,0.7)'
  }
};

const COLORS = {
  noGoStroke: 'rgba(239,68,68,0.75)', noGoFill: 'rgba(239,68,68,0.15)',
  noMopStroke: 'rgba(217,70,239,0.6)', noMopFill: 'rgba(217,70,239,0.15)',
  activeStroke: 'rgb(53,145,26)', activeFill: 'rgba(107,244,66,0.3)',
  carpetFill: 'rgba(255,255,255,0.18)', rampFill: 'rgba(255,255,255,0.1)',
  virtualWall: 'rgba(239,68,68,0.75)', threshold: 'rgba(255,255,255,0.5)',
  zoneStroke: '#ffd60a', zoneFill: 'rgba(255,214,10,0.25)',
  goto: '#ff375f', gotoTarget: '#ff9f0a',
  robotFill: '#ffffff', robotStroke: '#1c1c1e', charger: '#34c759', obstacle: '#ff453a'
};

const DIM_ALPHA = 0.42;

export function create(canvas, dotnetRef, options) {
  return new MapRenderer(canvas, dotnetRef, options || {});
}

function forEachRun(layer, fn) {
  const c = layer.compressedPixels;
  if (c && c.length) {
    for (let i = 0; i + 2 < c.length; i += 3) fn(c[i], c[i + 1], c[i + 2]);
    return;
  }
  const p = layer.pixels;
  if (p) for (let i = 0; i + 1 < p.length; i += 2) fn(p[i], p[i + 1], 1);
}

function layerKey(map) {
  const parts = [];
  for (const l of map.layers || []) {
    const m = l.metaData || {};
    parts.push(l.type, m.segmentId ?? '', l.dimensions ? l.dimensions.pixelCount : 0);
  }
  return (map.metaData && map.metaData.nonce || '') + '|' + parts.join(',');
}

function currentTheme() {
  const attr = document.documentElement.getAttribute('data-theme');
  if (attr === 'dark' || attr === 'light') return attr;
  return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

/** Decode the layers into a bounding box, a pixel→segment index and colour assignment (adjacent rooms differ). */
function buildRaster(map) {
  const layers = (map.layers || []).filter(l => l.dimensions && l.dimensions.pixelCount > 0);
  if (!layers.length) return null;
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const l of layers) {
    minX = Math.min(minX, l.dimensions.x.min); maxX = Math.max(maxX, l.dimensions.x.max);
    minY = Math.min(minY, l.dimensions.y.min); maxY = Math.max(maxY, l.dimensions.y.max);
  }
  const w = maxX - minX + 1, h = maxY - minY + 1;
  const segIdx = new Int16Array(w * h).fill(-1);
  const segments = layers.filter(l => l.type === 'segment').map(l => ({
    id: String((l.metaData || {}).segmentId),
    name: (l.metaData || {}).name || null,
    active: !!(l.metaData || {}).active,
    layer: l
  }));
  segments.forEach((s, i) => forEachRun(s.layer, (x, y, count) => {
    const row = (y - minY) * w;
    for (let k = 0; k < count; k++) {
      const xx = x - minX + k;
      if (xx >= 0 && xx < w) segIdx[row + xx] = i;
    }
  }));

  // Adjacency within a few pixels (rooms are separated by wall pixels), then greedy colouring so neighbours differ.
  const adj = segments.map(() => new Set());
  const reach = [[1, 0], [2, 0], [3, 0], [4, 0], [5, 0], [0, 1], [0, 2], [0, 3], [0, 4], [0, 5], [2, 2], [-2, 2], [4, 4], [-4, 4]];
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const a = segIdx[y * w + x];
      if (a < 0) continue;
      for (const [dx, dy] of reach) {
        const xx = x + dx, yy = y + dy;
        if (xx < 0 || xx >= w || yy >= h) continue;
        const b = segIdx[yy * w + xx];
        if (b >= 0 && b !== a) { adj[a].add(b); adj[b].add(a); }
      }
    }
  }
  const order = segments.map((s, i) => i).sort((i, j) => (parseInt(segments[i].id) || 0) - (parseInt(segments[j].id) || 0));
  const colorIdx = new Array(segments.length).fill(-1);
  for (const i of order) {
    const used = new Set();
    for (const n of adj[i]) if (colorIdx[n] >= 0) used.add(colorIdx[n]);
    let c = 0;
    while (used.has(c) && c < 4) c++;
    colorIdx[i] = used.has(c) ? i % 5 : c;
  }
  return { key: layerKey(map), layers, segments, colorIdx, segIdx, minX, minY, w, h, images: new Map() };
}

/** Paint the raster to a canvas for a given theme + selection (cached). */
function rasterImage(raster, theme, selected) {
  const selKey = theme + '|' + (selected && selected.length ? [...selected].sort().join(',') : '');
  let img = raster.images.get(selKey);
  if (img) return img;
  const pal = PALETTES[theme] || PALETTES.light;
  const { w, h, minX, minY } = raster;
  const data = new ImageData(w, h);
  const d = data.data;
  const put = (x, y, count, rgb, alpha) => {
    let p = ((y - minY) * w + (x - minX)) * 4;
    for (let k = 0; k < count; k++) {
      d[p] = rgb[0]; d[p + 1] = rgb[1]; d[p + 2] = rgb[2]; d[p + 3] = alpha;
      p += 4;
    }
  };
  const dim = selected && selected.length ? new Set(selected) : null;
  for (const l of raster.layers) if (l.type === 'floor') forEachRun(l, (x, y, c) => put(x, y, c, pal.floor, 255));
  raster.segments.forEach((s, i) => {
    const alpha = dim && !dim.has(s.id) ? Math.round(255 * DIM_ALPHA) : 255;
    forEachRun(s.layer, (x, y, c) => put(x, y, c, pal.segments[raster.colorIdx[i] % pal.segments.length], alpha));
  });
  for (const l of raster.layers) if (l.type === 'wall') forEachRun(l, (x, y, c) => put(x, y, c, pal.wall, 255));
  const canvas = document.createElement('canvas');
  canvas.width = w; canvas.height = h;
  canvas.getContext('2d').putImageData(data, 0, 0);
  raster.images.set(selKey, canvas);
  return canvas;
}

class MapRenderer {
  constructor(canvas, dotnetRef, opts) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.dotnet = dotnetRef;
    this.opts = Object.assign({ liveUrl: null, staticUrl: null, mode: 'view', angleOffset: 0, maxZones: 1 }, opts);
    this.theme = currentTheme();
    this.map = null;
    this.raster = null;
    this.nonce = null;
    this.view = { scale: 1, tx: 0, ty: 0 };
    this.fitted = false;
    this.mode = this.opts.mode;
    this.selected = [];
    this.zones = [];
    this.draft = null;
    this.gotoPoint = null;
    this.pointers = new Map();
    this.gesture = null;
    this.etag = null;
    this.fetching = false;
    this.dirty = false;
    this.dpr = window.devicePixelRatio || 1;

    this._onPointerDown = e => this._pointerDown(e);
    this._onPointerMove = e => this._pointerMove(e);
    this._onPointerUp = e => this._pointerUp(e);
    this._onWheel = e => this._wheel(e);
    this._onVisibility = () => { if (document.visibilityState === 'visible') this.refresh(); };
    this._onTheme = () => { this.theme = currentTheme(); this._draw(); };
    canvas.addEventListener('pointerdown', this._onPointerDown);
    canvas.addEventListener('pointermove', this._onPointerMove);
    canvas.addEventListener('pointerup', this._onPointerUp);
    canvas.addEventListener('pointercancel', this._onPointerUp);
    canvas.addEventListener('wheel', this._onWheel, { passive: false });
    document.addEventListener('visibilitychange', this._onVisibility);
    this.mq = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    if (this.mq) this.mq.addEventListener('change', this._onTheme);

    this.ro = new ResizeObserver(() => this._resize());
    this.ro.observe(canvas);
    this._resize();

    if (this.opts.staticUrl) this._fetchStatic();
    else if (this.opts.liveUrl) this.refresh();
  }

  // ---- data ------------------------------------------------------------------

  async refresh() {
    if (!this.opts.liveUrl) return;
    if (this.fetching) { this.dirty = true; return; }
    this.fetching = true;
    try {
      do {
        this.dirty = false;
        const headers = this.etag ? { 'If-None-Match': this.etag } : {};
        const r = await fetch(this.opts.liveUrl, { headers, cache: 'no-store' });
        if (r.status === 200) {
          this.etag = r.headers.get('ETag');
          this.setMap(await r.json());
        }
      } while (this.dirty);
    } catch (e) {
      console.warn('map fetch failed', e);
    } finally {
      this.fetching = false;
    }
  }

  async _fetchStatic() {
    try {
      const r = await fetch(this.opts.staticUrl);
      if (r.ok) this.setMap(await r.json());
      else this._showMessage(r.status === 404 ? 'No map stored for this run' : 'Failed to load map');
    } catch (e) {
      console.warn('map fetch failed', e);
      this._showMessage('Failed to load map');
    }
  }

  setMap(map) {
    this.map = map;
    const key = layerKey(map);
    if (!this.raster || this.raster.key !== key) this.raster = buildRaster(map);
    const nonce = map.metaData && map.metaData.nonce;
    if (this.raster && (!this.fitted || (nonce && nonce !== this.nonce))) this.fit();
    this.nonce = nonce;
    this._draw();
  }

  setMode(mode) {
    this.mode = mode;
    this.draft = null;
    this.gesture = null;
    this._draw();
  }

  setSelection(ids) { this.selected = (Array.isArray(ids) ? ids : ids == null ? [] : [ids]).map(String); this._draw(); }
  getSelection() { return this.selected.slice(); }
  setZones(zones) { this.zones = Array.isArray(zones) ? zones : []; this._draw(); }
  getZones() { return this.zones.slice(); }
  clearZones() { this.zones = []; this.draft = null; this._draw(); }
  clearGoto() { this.gotoPoint = null; this._draw(); }
  setMaxZones(n) { this.opts.maxZones = n; }

  fit() {
    if (!this.raster) return;
    const cw = this.canvas.clientWidth, ch = this.canvas.clientHeight;
    if (!cw || !ch) return;
    const { w, h } = this.raster;
    const scale = Math.min(cw / w, ch / h) * 0.92;
    this.view = { scale, tx: (cw - w * scale) / 2, ty: (ch - h * scale) / 2 };
    this.fitted = true;
    this._draw();
  }

  zoomBy(factor) {
    const cw = this.canvas.clientWidth, ch = this.canvas.clientHeight;
    this._zoomAt(cw / 2, ch / 2, factor);
    this._draw();
  }

  destroy() {
    const c = this.canvas;
    c.removeEventListener('pointerdown', this._onPointerDown);
    c.removeEventListener('pointermove', this._onPointerMove);
    c.removeEventListener('pointerup', this._onPointerUp);
    c.removeEventListener('pointercancel', this._onPointerUp);
    c.removeEventListener('wheel', this._onWheel);
    document.removeEventListener('visibilitychange', this._onVisibility);
    if (this.mq) this.mq.removeEventListener('change', this._onTheme);
    this.ro.disconnect();
    this.dotnet = null;
  }

  // ---- coordinate helpers -------------------------------------------------------

  _screenToRaster(sx, sy) {
    const { scale, tx, ty } = this.view;
    return { x: (sx - tx) / scale, y: (sy - ty) / scale };
  }

  _rasterToCm(p) {
    const ps = this.map.pixelSize || 5;
    return { x: Math.round((p.x + this.raster.minX) * ps), y: Math.round((p.y + this.raster.minY) * ps) };
  }

  _cmToRaster(x, y) {
    const ps = this.map.pixelSize || 5;
    return { x: x / ps - this.raster.minX, y: y / ps - this.raster.minY };
  }

  _clampCm(p) {
    const ps = this.map.pixelSize || 5;
    const r = this.raster;
    return {
      x: Math.min(Math.max(p.x, r.minX * ps), (r.minX + r.w) * ps),
      y: Math.min(Math.max(p.y, r.minY * ps), (r.minY + r.h) * ps)
    };
  }

  _segmentAt(sx, sy) {
    if (!this.raster) return null;
    const p = this._screenToRaster(sx, sy);
    const x = Math.floor(p.x), y = Math.floor(p.y);
    if (x < 0 || y < 0 || x >= this.raster.w || y >= this.raster.h) return null;
    const i = this.raster.segIdx[y * this.raster.w + x];
    return i >= 0 ? this.raster.segments[i] : null;
  }

  // ---- drawing --------------------------------------------------------------------

  _resize() {
    const cw = this.canvas.clientWidth, ch = this.canvas.clientHeight;
    if (!cw || !ch) return;
    this.dpr = window.devicePixelRatio || 1;
    const bw = Math.round(cw * this.dpr), bh = Math.round(ch * this.dpr);
    if (this.canvas.width !== bw || this.canvas.height !== bh) {
      this.canvas.width = bw;
      this.canvas.height = bh;
    }
    if (this.raster && !this.fitted) this.fit();
    else this._draw();
  }

  _showMessage(text) {
    const ctx = this.ctx;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.clearRect(0, 0, this.canvas.clientWidth, this.canvas.clientHeight);
    ctx.fillStyle = PALETTES[this.theme].label;
    ctx.font = '14px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText(text, this.canvas.clientWidth / 2, this.canvas.clientHeight / 2);
  }

  _draw() {
    const ctx = this.ctx;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    if (!this.raster || !this.map) return;
    const { scale, tx, ty } = this.view;
    ctx.setTransform(this.dpr * scale, 0, 0, this.dpr * scale, this.dpr * tx, this.dpr * ty);
    ctx.imageSmoothingEnabled = false;
    const selection = this.mode === 'select-segments' ? this.selected : [];
    ctx.drawImage(rasterImage(this.raster, this.theme, selection), 0, 0);
    this._drawEntities(ctx);
    this._drawZones(ctx);
    this._drawGoto(ctx);
    this._drawLabels(ctx);
  }

  _drawEntities(ctx) {
    const pal = PALETTES[this.theme];
    const { scale } = this.view;
    const ps = this.map.pixelSize || 5;
    const minX = this.raster.minX, minY = this.raster.minY;
    const toR = v => v / ps;
    const px = k => k / scale; // constant screen size in raster units
    const entities = this.map.entities || [];

    const polygon = pts => {
      ctx.beginPath();
      for (let i = 0; i + 1 < pts.length; i += 2) {
        const x = toR(pts[i]) - minX, y = toR(pts[i + 1]) - minY;
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.closePath();
    };
    const polyline = pts => {
      ctx.beginPath();
      for (let i = 0; i + 1 < pts.length; i += 2) {
        const x = toR(pts[i]) - minX, y = toR(pts[i + 1]) - minY;
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
    };

    // areas first
    for (const e of entities) {
      const pts = e.points || [];
      switch (e.type) {
        case 'carpet': polygon(pts); ctx.fillStyle = COLORS.carpetFill; ctx.fill(); break;
        case 'ramp': polygon(pts); ctx.fillStyle = COLORS.rampFill; ctx.fill(); break;
        case 'no_go_area':
          polygon(pts); ctx.fillStyle = COLORS.noGoFill; ctx.fill();
          ctx.strokeStyle = COLORS.noGoStroke; ctx.lineWidth = px(2); ctx.stroke(); break;
        case 'no_mop_area':
          polygon(pts); ctx.fillStyle = COLORS.noMopFill; ctx.fill();
          ctx.strokeStyle = COLORS.noMopStroke; ctx.lineWidth = px(2); ctx.stroke(); break;
        case 'active_zone':
          polygon(pts); ctx.fillStyle = COLORS.activeFill; ctx.fill();
          ctx.strokeStyle = COLORS.activeStroke; ctx.lineWidth = px(2); ctx.stroke(); break;
        case 'virtual_wall':
          polyline(pts); ctx.strokeStyle = COLORS.virtualWall; ctx.lineWidth = px(4); ctx.lineCap = 'round'; ctx.stroke(); break;
        case 'threshold':
        case 'curtain':
          polyline(pts); ctx.strokeStyle = COLORS.threshold; ctx.lineWidth = px(2); ctx.setLineDash([px(4), px(3)]); ctx.stroke(); ctx.setLineDash([]); break;
      }
    }

    // paths
    ctx.lineJoin = 'round';
    ctx.lineCap = 'round';
    for (const e of entities) {
      if (e.type !== 'path' && e.type !== 'predicted_path') continue;
      const pts = e.points || [];
      if (pts.length < 4) continue;
      polyline(pts);
      ctx.lineWidth = Math.max(0.35, px(1.5));
      if (e.type === 'predicted_path') { ctx.strokeStyle = pal.predicted; ctx.setLineDash([px(3), px(3)]); }
      else { ctx.strokeStyle = pal.path; ctx.setLineDash([]); }
      ctx.stroke();
      ctx.setLineDash([]);
    }

    // points
    for (const e of entities) {
      const pts = e.points || [];
      if (pts.length < 2) continue;
      const x = toR(pts[0]) - minX, y = toR(pts[1]) - minY;
      switch (e.type) {
        case 'charger_location': {
          const r = Math.max(2.5, px(9));
          ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2);
          ctx.fillStyle = COLORS.charger; ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.5)'; ctx.lineWidth = px(1.5); ctx.stroke();
          this._glyph(ctx, '⚡', x, y, r * 1.3);
          break;
        }
        case 'go_to_target': {
          const r = Math.max(2, px(7));
          ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2);
          ctx.fillStyle = COLORS.gotoTarget; ctx.fill();
          ctx.strokeStyle = '#fff'; ctx.lineWidth = px(1.5); ctx.stroke();
          break;
        }
        case 'obstacle': {
          const r = Math.max(1, px(3));
          ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2);
          ctx.fillStyle = COLORS.obstacle; ctx.fill();
          break;
        }
      }
    }
    for (const e of entities) {
      if (e.type !== 'robot_position') continue;
      const pts = e.points || [];
      if (pts.length < 2) continue;
      const x = toR(pts[0]) - minX, y = toR(pts[1]) - minY;
      const angle = ((e.metaData && e.metaData.angle) || 0) + this.opts.angleOffset;
      const r = Math.max(3.5, px(11));
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(angle * Math.PI / 180);
      ctx.beginPath(); ctx.arc(0, 0, r, 0, Math.PI * 2);
      ctx.fillStyle = COLORS.robotFill; ctx.fill();
      ctx.strokeStyle = COLORS.robotStroke; ctx.lineWidth = px(2); ctx.stroke();
      // heading: 0° = north (up)
      ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(0, -r);
      ctx.strokeStyle = COLORS.robotStroke; ctx.lineWidth = px(2.5); ctx.stroke();
      ctx.beginPath(); ctx.arc(0, -r * 0.55, r * 0.22, 0, Math.PI * 2);
      ctx.fillStyle = '#0a84ff'; ctx.fill();
      ctx.restore();
    }
  }

  _glyph(ctx, text, x, y, size) {
    ctx.save();
    ctx.font = `${size}px system-ui, sans-serif`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillStyle = '#fff';
    ctx.fillText(text, x, y + size * 0.05);
    ctx.restore();
  }

  _drawZones(ctx) {
    const { scale } = this.view;
    const px = k => k / scale;
    const draw = (z, dashed) => {
      const a = this._cmToRaster(z.x1, z.y1), b = this._cmToRaster(z.x2, z.y2);
      const x = Math.min(a.x, b.x), y = Math.min(a.y, b.y), w = Math.abs(b.x - a.x), h = Math.abs(b.y - a.y);
      ctx.fillStyle = COLORS.zoneFill; ctx.fillRect(x, y, w, h);
      ctx.strokeStyle = COLORS.zoneStroke; ctx.lineWidth = px(2);
      ctx.setLineDash(dashed ? [px(5), px(4)] : []);
      ctx.strokeRect(x, y, w, h);
      ctx.setLineDash([]);
    };
    for (const z of this.zones) draw(z, false);
    if (this.draft) draw(this.draft, true);
  }

  _drawGoto(ctx) {
    if (!this.gotoPoint) return;
    const { scale } = this.view;
    const px = k => k / scale;
    const p = this._cmToRaster(this.gotoPoint.x, this.gotoPoint.y);
    const r = Math.max(2, px(8));
    ctx.beginPath();
    ctx.moveTo(p.x, p.y);
    ctx.lineTo(p.x - r * 0.8, p.y - r * 1.6);
    ctx.arc(p.x, p.y - r * 1.6, r * 0.8, Math.PI, 0);
    ctx.closePath();
    ctx.fillStyle = COLORS.goto; ctx.fill();
    ctx.strokeStyle = '#fff'; ctx.lineWidth = px(1.5); ctx.stroke();
    ctx.beginPath(); ctx.arc(p.x, p.y - r * 1.6, r * 0.3, 0, Math.PI * 2);
    ctx.fillStyle = '#fff'; ctx.fill();
  }

  _drawLabels(ctx) {
    const pal = PALETTES[this.theme];
    const { scale, tx, ty } = this.view;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.font = '600 12px system-ui, -apple-system, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    for (const s of this.raster.segments) {
      const d = s.layer.dimensions;
      if (!d) continue;
      const rx = ((d.x.avg ?? d.x.mid) - this.raster.minX), ry = ((d.y.avg ?? d.y.mid) - this.raster.minY);
      const sx = rx * scale + tx, sy = ry * scale + ty;
      const widthPx = (d.x.max - d.x.min) * scale;
      if (widthPx < 28) continue;
      const text = s.name || ('Room ' + s.id);
      const selected = this.selected.includes(s.id) && this.mode === 'select-segments';
      const label = selected ? '✓ ' + text : text;
      ctx.lineWidth = 4;
      ctx.strokeStyle = pal.labelHalo;
      ctx.strokeText(label, sx, sy);
      ctx.fillStyle = pal.label;
      ctx.fillText(label, sx, sy);
    }
  }

  // ---- interaction --------------------------------------------------------------------

  _local(e) {
    const r = this.canvas.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  }

  _pointerDown(e) {
    if (!this.raster) return;
    this.canvas.setPointerCapture(e.pointerId);
    const p = this._local(e);
    this.pointers.set(e.pointerId, p);
    if (this.pointers.size === 1) {
      const drawing = this.mode === 'draw-zone';
      this.gesture = { kind: drawing ? 'draw' : 'pan', start: p, startView: { ...this.view }, time: performance.now(), moved: false };
      if (drawing) {
        const cm = this._clampCm(this._rasterToCm(this._screenToRaster(p.x, p.y)));
        this.draft = { x1: cm.x, y1: cm.y, x2: cm.x, y2: cm.y };
      }
    } else if (this.pointers.size === 2) {
      const [a, b] = [...this.pointers.values()];
      const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
      this.gesture = {
        kind: 'pinch', startDist: Math.hypot(a.x - b.x, a.y - b.y), startScale: this.view.scale,
        anchor: this._screenToRaster(mid.x, mid.y), moved: true
      };
      this.draft = null;
    }
    e.preventDefault();
  }

  _pointerMove(e) {
    if (!this.pointers.has(e.pointerId) || !this.gesture) return;
    const p = this._local(e);
    this.pointers.set(e.pointerId, p);
    const g = this.gesture;
    if (g.kind === 'pinch' && this.pointers.size >= 2) {
      const [a, b] = [...this.pointers.values()];
      const dist = Math.hypot(a.x - b.x, a.y - b.y);
      const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
      const scale = Math.min(Math.max(g.startScale * dist / g.startDist, 0.2), 60);
      this.view = { scale, tx: mid.x - g.anchor.x * scale, ty: mid.y - g.anchor.y * scale };
      this._draw();
    } else if (g.kind === 'pan') {
      const dx = p.x - g.start.x, dy = p.y - g.start.y;
      if (Math.hypot(dx, dy) > 6) g.moved = true;
      if (g.moved) {
        this.view = { scale: g.startView.scale, tx: g.startView.tx + dx, ty: g.startView.ty + dy };
        this._draw();
      }
    } else if (g.kind === 'draw' && this.draft) {
      const cm = this._clampCm(this._rasterToCm(this._screenToRaster(p.x, p.y)));
      this.draft.x2 = cm.x; this.draft.y2 = cm.y;
      g.moved = true;
      this._draw();
    }
    e.preventDefault();
  }

  _pointerUp(e) {
    if (!this.pointers.has(e.pointerId)) return;
    const p = this._local(e);
    this.pointers.delete(e.pointerId);
    const g = this.gesture;
    if (!g) return;
    if (this.pointers.size > 0) {
      // Fingers remain: restart a pan from the remaining pointer so nothing jumps.
      const rest = [...this.pointers.values()][0];
      this.gesture = { kind: 'pan', start: rest, startView: { ...this.view }, time: performance.now(), moved: true };
      return;
    }
    this.gesture = null;
    const isTap = !g.moved && performance.now() - g.time < 500 && g.kind !== 'pinch';
    if (g.kind === 'draw') {
      const d = this.draft;
      this.draft = null;
      if (d && Math.abs(d.x2 - d.x1) >= 20 && Math.abs(d.y2 - d.y1) >= 20) {
        const rect = { x1: Math.min(d.x1, d.x2), y1: Math.min(d.y1, d.y2), x2: Math.max(d.x1, d.x2), y2: Math.max(d.y1, d.y2) };
        if (this.zones.length >= this.opts.maxZones) this.zones = this.zones.slice(1 - this.opts.maxZones);
        this.zones = this.opts.maxZones <= 1 ? [rect] : [...this.zones, rect];
        this._notify('OnZoneChanged', this.zones);
      }
      this._draw();
      return;
    }
    if (isTap) this._tap(p);
  }

  _tap(p) {
    if (this.mode === 'select-segments') {
      const seg = this._segmentAt(p.x, p.y);
      if (!seg) return;
      const i = this.selected.indexOf(seg.id);
      if (i >= 0) this.selected.splice(i, 1); else this.selected.push(seg.id);
      this._draw();
      this._notify('OnSelectionChanged', this.selected.slice());
    } else if (this.mode === 'go-to') {
      const cm = this._clampCm(this._rasterToCm(this._screenToRaster(p.x, p.y)));
      this.gotoPoint = cm;
      this._draw();
      this._notify('OnPointPicked', cm);
    }
  }

  _wheel(e) {
    if (!this.raster) return;
    e.preventDefault();
    const p = this._local(e);
    const factor = Math.exp(-e.deltaY * 0.0015);
    this._zoomAt(p.x, p.y, factor);
    this._draw();
  }

  _zoomAt(sx, sy, factor) {
    const anchor = this._screenToRaster(sx, sy);
    const scale = Math.min(Math.max(this.view.scale * factor, 0.2), 60);
    this.view = { scale, tx: sx - anchor.x * scale, ty: sy - anchor.y * scale };
  }

  _notify(method, arg) {
    if (!this.dotnet) return;
    this.dotnet.invokeMethodAsync(method, arg).catch(err => console.warn('interop failed', err));
  }
}
