(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const params = new URLSearchParams(location.search);
  const storedToken = localStorage.getItem('controlhub.token') || '';
  let token = params.get('t') || storedToken;

  // 按钮/模块间距：每页可单独设行距、列距，缺省 9px
  const clampGap = (v) => Math.max(0, Math.min(60, v == null || v === '' ? 9 : +v));

  if (params.get('t')) localStorage.setItem('controlhub.token', params.get('t'));

  const state = {
    cfg: null,
    pages: [],
    current: '',
    auto: true,
    activeApp: '',
    connected: false,
    helloOnce: false,
    stats: null
  };

  let ws = null;
  let retry = 0;
  let toastTimer = 0;
  let bgLum = -1;          // 当前页面背景图的平均亮度，-1 表示未知
  let bgUrl = '';

  // ---------------- 工具 ----------------

  function api(path, opts) {
    const sep = path.includes('?') ? '&' : '?';
    const url = token ? `${path}${sep}t=${encodeURIComponent(token)}` : path;
    const o = Object.assign({ headers: {} }, opts || {});
    if (token) o.headers['X-Token'] = token;
    return fetch(url, o);
  }

  function hexToRgb(hex) {
    if (!hex) return null;
    let h = String(hex).trim().replace('#', '');
    if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
    if (!/^[0-9a-fA-F]{6}$/.test(h)) return null;
    return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
  }

  function relLum(rgb) {
    const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
    return 0.2126 * f(rgb[0]) + 0.7152 * f(rgb[1]) + 0.0722 * f(rgb[2]);
  }

  const BLACK = '#0b0f14';
  const WHITE = '#ffffff';

  // 文字颜色：按按钮“下面”的底色在黑/白之间二选一
  function textColorOn(hex, fallbackWhite) {
    const rgb = hexToRgb(hex);
    if (!rgb) return fallbackWhite ? WHITE : BLACK;
    return relLum(rgb) > 0.42 ? BLACK : WHITE;
  }

  function toast(msg, level) {
    const el = $('toast');
    el.textContent = msg;
    el.className = 'toast show ' + (level || '');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.className = 'toast ' + (level || ''); }, 2200);
  }

  function vibrate(ms) {
    try { navigator.vibrate && navigator.vibrate(ms || 10); } catch { }
  }

  // ---------------- 背景 ----------------

  // 这一页的毛玻璃设置：每页没写就跟随全局
  // alpha  = 颜色的透明度（0=实心，100=完全透明，只剩模糊）
  // blur   = 模糊程度（0=完全不模糊，能看清背景图；100=最糊）
  function glassFor(page) {
    const cfg = state.cfg || {};
    const on = page && page.glass != null ? !!page.glass : !!cfg.glass;

    const pick = (p, g, dflt) => {
      const v = p != null ? p : (g != null ? g : dflt);
      return Math.max(0, Math.min(100, +v || 0));
    };
    // 老配置只有 glassOpacity 一个值：同时当成颜色透明度和模糊程度
    const legacy = (page && page.glassOpacity != null) ? page.glassOpacity
      : (cfg.glassOpacity != null ? cfg.glassOpacity : null);
    const alpha = pick(page && page.glassAlpha, cfg.glassAlpha, legacy != null ? legacy : 60);
    const blur = pick(page && page.glassBlur, cfg.glassBlur, legacy != null ? legacy : 50);

    return { on, alpha, blur };
  }

  function applyBackground() {
    const page = currentPage();
    const url = (page && page.background) || (state.cfg && state.cfg.background) || '';

    const g = glassFor(page);
    state.glass = g;
    document.body.classList.toggle('glass', g.on);
    document.body.classList.toggle('noblur', g.blur === 0);
    // 颜色透明度：100 表示按钮填色全透明，0 表示实心
    document.body.style.setProperty('--galpha', (100 - g.alpha) + '%');
    // 模糊程度：0 → 0px（背景图原样透出），100 → 30px
    document.body.style.setProperty('--gblur', (g.blur * 0.3).toFixed(1) + 'px');
    // 不模糊时就不要额外提高饱和度了，否则背景颜色会被染色
    document.body.style.setProperty('--gsat', g.blur === 0 ? '1' : '1.45');

    if (url === bgUrl) { applyTextColors(); return; }
    bgUrl = url;
    bgLum = -1;
    const bg = $('bg');
    if (!url) {
      bg.style.backgroundImage = 'none';
      document.body.classList.remove('has-bg');
      applyTextColors();
      return;
    }
    bg.style.backgroundImage = `url("${url}")`;
    document.body.classList.add('has-bg');
    measureBackground(url);
  }

  // 采样背景图平均亮度，供毛玻璃按钮判断黑白字
  function measureBackground(url) {
    const img = new Image();
    img.onload = () => {
      try {
        const c = document.createElement('canvas');
        c.width = c.height = 16;
        const ctx = c.getContext('2d');
        ctx.drawImage(img, 0, 0, 16, 16);
        const d = ctx.getImageData(0, 0, 16, 16).data;
        let r = 0, g = 0, b = 0, n = 0;
        for (let i = 0; i < d.length; i += 4) { r += d[i]; g += d[i + 1]; b += d[i + 2]; n++; }
        bgLum = relLum([r / n, g / n, b / n]);
      } catch { bgLum = -1; }
      applyTextColors();
    };
    img.onerror = () => { bgLum = -1; applyTextColors(); };
    img.src = url;
  }

  function applyTextColors() {
    const glass = document.body.classList.contains('glass');
    document.querySelectorAll('#grid .tile:not(.empty)').forEach((el) => {
      const own = el.dataset.color || '#1b2530';
      let fg;
      if (glass) {
        // 毛玻璃：按钮本色被背景稀释，按背景图亮度决定黑白
        if (bgLum >= 0) fg = bgLum > 0.45 ? BLACK : WHITE;
        else fg = textColorOn(own, false);
      } else {
        fg = textColorOn(own, false);
      }
      el.style.setProperty('--fg', fg);
    });

    // 状态模块：面板色固定是深色板，毛玻璃时会被背景稀释，
    // 所以按“深色板以 (100-alpha)% 盖在背景上”的合成亮度来判断黑白，规则和按钮一致
    const g = state.glass || { alpha: 60 };
    const panelLum = relLum([20, 30, 42]);
    document.querySelectorAll('#grid .widget').forEach((el) => {
      let fg = WHITE;
      if (glass && bgLum >= 0) {
        const a = (100 - g.alpha) / 100;          // 面板自身的不透明度
        const l = a * panelLum + (1 - a) * bgLum; // 合成后的亮度
        fg = l > 0.45 ? BLACK : WHITE;
      }
      el.style.setProperty('--wfg', fg);
    });
  }

  // ---------------- 渲染 ----------------

  const pageById = (id) => state.pages.find((p) => p.id === id) || state.pages[0] || null;
  const currentPage = () => pageById(state.current);

  function render() {
    const page = currentPage();
    applyBackground();

    $('grid').style.display = 'grid';
    $('status').classList.add('hidden');
    $('status').style.display = 'none';

    // 顶栏已经去掉，页名只留在页签上；这里仍更新 #pageName（如果哪天放回来）
    const pn = document.getElementById('pageName');
    if (pn) pn.textContent = page ? page.name : '没有页面';
    $('appLine').textContent = state.activeApp
      ? state.activeApp
      : (state.connected ? '未检测到前台' : '正在连接…');

    const grid = $('grid');
    grid.innerHTML = '';

    if (!page) {
      $('appLine').textContent = '请在电脑端管理页面添加按钮';
      grid.style.gridTemplateColumns = '1fr';
      grid.style.gridTemplateRows = '1fr';
      renderTabs();
      return;
    }

    const rows = Math.max(1, page.rows | 0);
    const cols = Math.max(1, page.cols | 0);
    grid.style.gridTemplateColumns = `repeat(${cols}, minmax(0, 1fr))`;
    grid.style.gridTemplateRows = `repeat(${rows}, minmax(0, 1fr))`;
    grid.style.gap = `${clampGap(page.rowGap)}px ${clampGap(page.colGap)}px`;

    const occupied = new Set();
    const mark = (r, c, rs, cs) => {
      for (let i = 0; i < rs; i++)
        for (let k = 0; k < cs; k++) occupied.add(`${r + i},${c + k}`);
    };

    // 1) 按钮
    const buttons = Array.isArray(page.buttons) ? page.buttons : [];
    for (const b of buttons) {
      const r = Math.max(0, Math.min(b.row | 0, rows - 1));
      const c = Math.max(0, Math.min(b.col | 0, cols - 1));
      const rs = Math.max(1, Math.min(b.rowSpan || 1, rows - r));
      const cs = Math.max(1, Math.min(b.colSpan || 1, cols - c));
      mark(r, c, rs, cs);
      grid.appendChild(makeTile(b, r, c, rs, cs));
    }

    // 2) 状态模块（和按钮共用一张网格）
    const widgets = Array.isArray(page.widgets) ? page.widgets : [];
    const stats = state.stats || {};
    const now = new Date();
    for (const w of widgets) {
      const r = Math.max(0, Math.min(w.row | 0, rows - 1));
      const c = Math.max(0, Math.min(w.col | 0, cols - 1));
      const rs = Math.max(1, Math.min(w.rowSpan || 1, rows - r));
      const cs = Math.max(1, Math.min(w.colSpan || 1, cols - c));
      mark(r, c, rs, cs);

      const el = document.createElement('div');
      el.className = 'widget k-' + (w.kind || 'label');
      el.dataset.wid = w.id || '';
      el.style.gridRow = `${r + 1} / span ${rs}`;
      el.style.gridColumn = `${c + 1} / span ${cs}`;
      if (w.color) el.style.setProperty('--wc', w.color);
      el.innerHTML = widgetHtml(w, stats, now);
      grid.appendChild(el);
    }

    // 3) 空位（视觉占位，不接受点击）
    for (let r = 0; r < rows; r++) {
      for (let c = 0; c < cols; c++) {
        if (occupied.has(`${r},${c}`)) continue;
        const d = document.createElement('div');
        d.className = 'tile empty';
        d.style.gridRow = String(r + 1);
        d.style.gridColumn = String(c + 1);
        grid.appendChild(d);
      }
    }

    applyTextColors();
    requestAnimationFrame(sizeWidgets);
    renderTabs();
  }

  // 只更新模块里的数字，不重建 DOM（避免打断正在按的按钮）
  function updateWidgets() {
    const page = currentPage();
    if (!page) return;
    const widgets = Array.isArray(page.widgets) ? page.widgets : [];
    if (!widgets.length) return;
    const grid = $('grid');
    const stats = state.stats || {};
    const now = new Date();
    for (const w of widgets) {
      if (!w.id) continue;
      const el = grid.querySelector(`.widget[data-wid="${w.id}"]`);
      if (!el) continue;
      el.innerHTML = widgetHtml(w, stats, now);
    }
    sizeWidgets();
  }

  function makeTile(b, row, col, rowSpan, colSpan) {
    const el = document.createElement('div');
    el.className = 'tile';
    el.dataset.id = b.id;
    el.dataset.color = b.color || '#1b2530';
    el.style.gridRow = `${row + 1} / span ${rowSpan || 1}`;
    el.style.gridColumn = `${col + 1} / span ${colSpan || 1}`;
    el.style.setProperty('--c', b.color || '#1b2530');

    if (b.iconImage) {
      const box = document.createElement('div');
      box.className = 'ico';
      const img = document.createElement('img');
      img.src = b.iconImage;
      img.alt = '';
      img.draggable = false;
      box.appendChild(img);
      el.appendChild(box);
    } else if (b.icon) {
      const box = document.createElement('div');
      box.className = 'ico';
      box.textContent = b.icon;
      el.appendChild(box);
    }

    const lbl = document.createElement('div');
    lbl.className = 'lbl';
    lbl.textContent = b.label || '按钮';
    el.appendChild(lbl);

    if (b.sub) {
      const s = document.createElement('div');
      s.className = 'sub';
      s.textContent = b.sub;
      el.appendChild(s);
    }

    return el;
  }

  function renderTabs() {
    const tabs = $('tabs');
    tabs.innerHTML = '';
    state.pages.forEach((p, i) => {
      const t = document.createElement('button');
      t.className = 'tab' + (p.id === state.current ? ' active' : '');
      t.dataset.id = p.id;
      t.dataset.index = String(i);
      t.innerHTML = `${escapeHtml(p.name || ('页 ' + (i + 1)))}`;
      tabs.appendChild(t);
    });
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (m) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[m]));
  }

  // ---------------- 状态页 ----------------

  // ---------------- 状态页：按模块网格渲染 ----------------

  const WIDGET_NAMES = {
    clock: '时间', date: '日期', app: '前台应用', cpu: 'CPU', mem: '内存',
    gpu: 'GPU', cputemp: 'CPU 温度', gputemp: 'GPU 温度', fps: '帧率', hz: '刷新率', label: '文字'
  };

  // 用实测尺寸给每个模块定字号（容器查询在某些布局下取值不稳）
  function sizeWidgets() {
    const box = $('grid');
    if (!box) return;
    for (const el of box.children) {
      if (!el.classList || !el.classList.contains('widget')) continue;
      const r = el.getBoundingClientRect();
      if (r.width < 1 || r.height < 1) continue;
      el.style.setProperty('--uw', r.width.toFixed(1) + 'px');
      el.style.setProperty('--uh', r.height.toFixed(1) + 'px');
    }
  }

  window.addEventListener('resize', () => setTimeout(sizeWidgets, 80));

  function widgetName(w) {
    return w.title || WIDGET_NAMES[w.kind] || '';
  }

  function widgetHtml(w, s, now) {
    const hh = String(now.getHours()).padStart(2, '0');
    const mm = String(now.getMinutes()).padStart(2, '0');
    switch (w.kind) {
      case 'clock':
        return `<div class="wv">${hh}:${mm}</div>`;

      case 'date': {
        const d = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
        const wd = '日一二三四五六'[now.getDay()];
        return `<div class="wl">${escapeHtml(widgetName(w))}</div>
                <div class="wv">${d}</div>
                <div class="wn">周${wd}</div>`;
      }

      case 'app': {
        const name = s.app || state.activeApp || '';
        return `<div class="wi">🖥️</div>
                <div class="wab"><b>${escapeHtml(name || '未检测到')}</b>
                <i>${escapeHtml(s.title || '当前前台窗口')}</i></div>`;
      }

      case 'cpu':
        return gaugeHtml(w, s.cpu, '%', '');

      case 'mem':
        return gaugeHtml(w, s.memPct, '%',
          s.memTotalMb ? `${fmt(s.memUsedMb)} / ${fmt(s.memTotalMb)} MB` : '');

      case 'gpu':
        return gaugeHtml(w, s.gpu, '%',
          s.gpuMemUsedMb ? `${fmt(s.gpuMemUsedMb)} MB 显存` : '');

      case 'cputemp':
        return gaugeHtml(w, s.cpuTemp, '°C', s.cpuTemp == null ? '需管理员' : '');

      case 'gputemp':
        return gaugeHtml(w, s.gpuTemp, '°C', '');

      case 'fps':
        return `<div class="wl"><span>${escapeHtml(widgetName(w))}</span><span>${s.hz ? '刷新 ' + s.hz + ' Hz' : ''}</span></div>
                <div class="wv${s.fps == null ? ' none' : ''}">${s.fps == null ? '—' : Math.round(s.fps)}<small>FPS</small></div>`;

      case 'hz':
        return `<div class="wl">${escapeHtml(widgetName(w))}</div>
                <div class="wv${s.hz == null ? ' none' : ''}">${s.hz == null ? '—' : s.hz}<small>Hz</small></div>`;

      default:
        return `<div class="wlbl">${escapeHtml(w.text || w.title || '')}</div>`;
    }
  }

  function gaugeHtml(w, value, unit, note) {
    const val = value == null ? '—' : Math.round(value);
    const p = Math.max(0, Math.min(100, Number(value) || 0));
    return `<div class="wl"><span>${escapeHtml(widgetName(w))}</span><span>${escapeHtml(note || '')}</span></div>
            <div class="wv${value == null ? ' none' : ''}">${val}<small>${unit}</small></div>
            <div class="wbar"><i style="width:${value == null ? 0 : p}%"></i></div>`;
  }

  function fmt(v) {
    if (v == null) return '—';
    return Math.round(v).toLocaleString('en-US');
  }


  // ---------------- 按下 → 松开触发 ----------------

  let press = null;      // { el, id, x, y, moved }
  let holdTimer = 0;
  let drag = null;       // 拖动排序状态
  const dragBusy = () => !!drag;

  // 左右滑动翻页：记录一次手势的起点，松手时判断方向与距离
  let swipeStart = null;

  window.addEventListener('pointerdown', (e) => {
    if (e.isPrimary === false) { swipeStart = null; return; }
    const t = e.target;
    const rec = { x: e.clientX, y: e.clientY, t: Date.now(), lx: e.clientX, ly: e.clientY };
    if (!t || !t.closest) { swipeStart = rec; return; }
    // 页签上按下是“拖动换页序”，弹窗里按下是输入，都不当成翻页手势
    if (t.closest('.tab') || t.closest('#modal')) { swipeStart = null; return; }
    swipeStart = rec;
  }, { passive: true, capture: true });

  function trySwipe(e) {
    const s = swipeStart;
    swipeStart = null;
    if (!s || promptOpen()) return;

    // 触摸被系统收走（pointercancel）时 clientX/Y 可能为 0，用最后一次 move 的坐标
    const ex = s.lx != null ? s.lx : (e ? e.clientX : s.x);
    const ey = s.ly != null ? s.ly : (e ? e.clientY : s.y);
    const dx = ex - s.x, dy = ey - s.y;
    const dt = Date.now() - s.t;
    if (dt > 900) return;                        // 慢慢挪的不算
    if (Math.abs(dx) < 64) return;               // 距离太短
    if (Math.abs(dx) < Math.abs(dy) * 1.5) return;  // 斜着/竖向的不算

    stepPage(dx < 0 ? 1 : -1);
  }

  function stepPage(delta) {
    const ids = (state.pages || []).map((p) => p.id);
    if (ids.length < 2) return;
    let i = ids.indexOf(state.current);
    if (i < 0) i = 0;
    const id = ids[(i + delta + ids.length) % ids.length];
    if (!id || id === state.current) return;

    const m = document.querySelector('main');
    if (m) {
      m.classList.remove('swipe-l', 'swipe-r');
      void m.offsetWidth;
      m.classList.add(delta > 0 ? 'swipe-l' : 'swipe-r');
      setTimeout(() => m.classList.remove('swipe-l', 'swipe-r'), 260);
    }
    gotoPage(id);
  }

  function tileAt(x, y) {
    const el = document.elementFromPoint(x, y);
    return el ? el.closest('.tile') : null;
  }

  $('grid').addEventListener('pointerdown', (e) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    const el = e.target.closest('.tile');
    if (!el || el.classList.contains('empty')) return;

    press = { el, id: el.dataset.id, x: e.clientX, y: e.clientY, moved: false };
    el.classList.add('pressed');

    clearTimeout(holdTimer);
    holdTimer = setTimeout(() => {
      if (press && !press.moved) startDrag(press.el, press.x, press.y);
    }, 520);
  }, { passive: true });

  window.addEventListener('pointermove', (e) => {
    if (swipeStart) { swipeStart.lx = e.clientX; swipeStart.ly = e.clientY; }
    if (!press) return;
    const dx = e.clientX - press.x, dy = e.clientY - press.y;
    if (!press.moved && Math.hypot(dx, dy) > 12) {
      press.moved = true;
      clearTimeout(holdTimer);
      press.el.classList.remove('pressed');
    }
    // 手指滑出按钮就取消“按下”的高亮，滑回来再亮起来——松手时只有还按在原按钮上才算数
    if (!press.moved && !drag) {
      if (tileAt(e.clientX, e.clientY) === press.el) press.el.classList.add('pressed');
      else press.el.classList.remove('pressed');
    }
    if (drag) moveDrag(e.clientX, e.clientY);
  }, { passive: true });

  // canceled=true 表示手势被浏览器/系统收走了（触摸时常见），此时只判翻页，绝不触发按钮
  function onRelease(e, canceled) {
    clearTimeout(holdTimer);

    if (drag) { endDrag(); return; }

    const p = press;
    press = null;
    let fired = false;

    if (p) {
      p.el.classList.remove('pressed');
      if (!canceled && !p.moved) {
        // 松手时必须还在同一个按钮上：位置换了（滑出去）就什么都不做
        const under = tileAt(e.clientX, e.clientY);
        const up = e.target && e.target.closest ? e.target.closest('.tile') : null;
        if (under === p.el && (!up || up === p.el)) {
          fireButton(p.id, p.el);
          fired = true;
        }
      }
    }

    // 没按到按钮 / 滑动到按钮外松手 / 手势被收走 → 看看是不是一次左右滑动翻页
    if (!fired) trySwipe(e);
  }

  window.addEventListener('pointerup', (e) => onRelease(e, false));

  // 手势被系统打断（来电、通知、浏览器接管滚动）时，也别丢掉这次滑动
  window.addEventListener('pointercancel', (e) => onRelease(e, true));

  // 长按不该弹出系统菜单/选中文字
  document.addEventListener('contextmenu', (e) => {
    if (e.target.closest && e.target.closest('.tile, .tab')) e.preventDefault();
  });

  function fireButton(id, el) {
    const page = currentPage();
    if (!page || !Array.isArray(page.buttons)) return;
    const b = page.buttons.find((x) => x.id === id);
    if (!b) return;
    vibrate(12);
    el.classList.add('flash');
    setTimeout(() => el.classList.remove('flash'), 320);

    const type = b.action && b.action.type;
    if (type === 'prompt') {
      openPrompt(b);
      return;
    }
    send({ type: 'press', pageId: page.id, buttonId: id });
  }

  // ---------------- 拖动排序（按钮 / 页签） ----------------

  function startDrag(el, x, y) {
    drag = { kind: 'tile', el, id: el.dataset.id, x, y, target: null };
    el.classList.add('dragging');
    el.classList.remove('pressed');
    vibrate(18);
  }

  function moveDrag(x, y) {
    if (!drag) return;
    if (drag.kind === 'tile') {
      const over = tileAt(x, y);
      if (!over || over === drag.el || over.classList.contains('empty')) return;
      if (!over.dataset.id) return;
      drag.el.classList.remove('drop-target');
      over.classList.add('drop-target');
      drag.target = over.dataset.id;
    }
  }

  function endDrag() {
    const d = drag;
    drag = null;
    if (!d) return;

    if (d.kind === 'tile') {
      d.el.classList.remove('dragging');
      document.querySelectorAll('.tile.drop-target').forEach((t) => t.classList.remove('drop-target'));
      if (d.target && d.target !== d.id) swapButtons(d.id, d.target);
    } else if (d.kind === 'tab') {
      document.querySelectorAll('.tab.dragging, .tab.drop-target').forEach((t) => {
        t.classList.remove('dragging', 'drop-target');
      });
      if (d.target != null) {
        const from = Number(d.index), to = Number(d.target);
        if (!Number.isNaN(from) && !Number.isNaN(to) && from !== to) movePage(from, to);
      }
    }
  }

  function swapButtons(idA, idB) {
    const page = currentPage();
    if (!page || !Array.isArray(page.buttons)) return;
    const a = page.buttons.find((b) => b.id === idA);
    const b = page.buttons.find((x) => x.id === idB);
    if (!a || !b) return;

    // 交换两者的格子坐标（保持网格形状不变）
    const ar = a.row, ac = a.col, br = b.row, bc = b.col;
    a.row = br; a.col = bc;
    b.row = ar; b.col = ac;

    render();
    saveConfig(true);
    vibrate(14);
  }

  function movePage(from, to) {
    const pages = state.pages;
    if (from < 0 || to < 0 || from >= pages.length || to >= pages.length) return;
    const [p] = pages.splice(from, 1);
    pages.splice(to, 0, p);
    render();
    saveConfig(true);
    vibrate(14);
  }

  // 页签：点击切页 / 长按拖动排序
  let tabPress = null, tabHold = 0;

  $('tabs').addEventListener('pointerdown', (e) => {
    const t = e.target.closest('.tab');
    if (!t) return;
    tabPress = { el: t, x: e.clientX, y: e.clientY, index: t.dataset.index, moved: false };

    clearTimeout(tabHold);
    tabHold = setTimeout(() => {
      if (!tabPress || tabPress.moved) return;
      drag = { kind: 'tab', el: t, index: t.dataset.index, target: null };
      t.classList.add('dragging');
      vibrate(18);
    }, 460);
  }, { passive: true });

  window.addEventListener('pointermove', (e) => {
    if (!tabPress) return;
    if (Math.hypot(e.clientX - tabPress.x, e.clientY - tabPress.y) > 10) tabPress.moved = true;

    if (drag && drag.kind === 'tab') {
      const over = document.elementFromPoint(e.clientX, e.clientY);
      const tab = over && over.closest('.tab');
      if (!tab || tab === drag.el) return;
      document.querySelectorAll('.tab.drop-target').forEach((t) => t.classList.remove('drop-target'));
      tab.classList.add('drop-target');
      drag.target = tab.dataset.index;
    }
  }, { passive: true });

  window.addEventListener('pointerup', (e) => {
    if (drag && drag.kind === 'tab') { clearTimeout(tabHold); endDrag(); tabPress = null; return; }
    if (!tabPress) return;
    const p = tabPress;
    tabPress = null;
    clearTimeout(tabHold);
    if (p.moved) return;
    const el = document.elementFromPoint(e.clientX, e.clientY);
    const tab = el && el.closest('.tab');
    if (tab && tab === p.el) gotoPage(p.el.dataset.id);
  });

  function gotoPage(id) {
    if (!id || id === state.current) return;
    state.current = id;
    send({ type: 'goto', pageId: id });
    render();
  }

  // ---------------- 输入弹窗 ----------------

  let promptButton = null;
  let pendingAutoPage = null;
  let promptGuardUntil = 0;   // 刚弹出的一小段时间里忽略点击，避免“弹一下就被关掉”
  const promptOpen = () => !$('modal').classList.contains('hidden');

  function openPrompt(b) {
    promptButton = b;
    $('modalTitle').textContent = (b.label || '输入') + ' · 输入内容';
    $('modalInput').value = '';
    $('modal').classList.remove('hidden');
    // 松手后浏览器还会补发一个 click：此时点击点已被遮罩盖住，
    // 那个 click 会命中遮罩、被当成“点空白关闭”。给一小段静默期挡掉它。
    promptGuardUntil = Date.now() + 450;
    setTimeout(() => $('modalInput').focus(), 60);
  }

  function closePrompt() {
    promptButton = null;
    $('modal').classList.add('hidden');
    // 弹窗期间被压下的自动切页，关掉后再执行
    if (pendingAutoPage) {
      const p = pendingAutoPage;
      pendingAutoPage = null;
      state.current = p;
      render();
    }
  }

  $('modalCancel').addEventListener('click', closePrompt);
  $('modal').addEventListener('click', (e) => {
    if (Date.now() < promptGuardUntil) return;       // 吃掉“开窗那一下”的补发点击
    if (e.target === $('modal')) closePrompt();
  });
  $('modalOk').addEventListener('click', () => {
    if (!promptButton) return;
    const text = $('modalInput').value;
    const page = currentPage();
    send({ type: 'press', pageId: page ? page.id : '', buttonId: promptButton.id, text });
    closePrompt();
    vibrate(12);
  });
  // 回车=发送（输入法组字时不触发），Esc=取消
  $('modalInput').addEventListener('keydown', (e) => {
    if (e.isComposing) return;
    if (e.key === 'Escape') { e.preventDefault(); closePrompt(); }
    else if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); $('modalOk').click(); }
  });

  // ---------------- WebSocket ----------------

  function send(obj) {
    if (!ws || ws.readyState !== 1) { toast('还没连上电脑', 'err'); return false; }
    ws.send(JSON.stringify(obj));
    return true;
  }

  function connect() {
    if (!token && state.cfg && state.cfg.token === '') {
      // 无令牌模式也走 ws，不带 t
    }
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const q = token ? `?t=${encodeURIComponent(token)}` : '';
    ws = new WebSocket(`${proto}://${location.host}/ws${q}`);

    ws.onopen = () => {
      retry = 0;
      state.connected = true;
      $('dot').classList.add('on');
      $('cover').classList.remove('show');
      render();
    };

    ws.onmessage = (ev) => {
      let m;
      try { m = JSON.parse(ev.data); } catch { return; }
      handle(m);
    };

    ws.onclose = (ev) => {
      state.connected = false;
      $('dot').classList.remove('on');
      ws = null;
      if (ev.code === 4001 || ev.code === 1008) { $('cover').classList.add('show'); return; }
      const wait = Math.min(700 * Math.pow(1.6, retry++), 5000);
      setTimeout(connect, wait);
    };

    ws.onerror = () => { };
  }

  function handle(m) {
    switch (m.type) {
      case 'hello': {
        state.pages = (m.pages || []).slice();
        const firstId = (state.pages[0] && state.pages[0].id) || '';
        if (!state.helloOnce) {
          // 新打开页面：总是先显示「排在第一位」的那一页
          state.current = firstId || m.currentPage || '';
          state.helloOnce = true;
        } else if (pageById(state.current)) {
          // 断线重连：保留用户正在看的那一页，并把位置同步回服务端
          if (m.currentPage !== state.current) send({ type: 'goto', pageId: state.current });
        } else {
          state.current = firstId || m.currentPage || '';
        }
        state.auto = m.auto !== false;
        state.activeApp = m.activeApp || '';
        state.cfg = {
          background: m.background || '',
          glass: !!m.glass,
          glassAlpha: m.glassAlpha != null ? m.glassAlpha : 60,
          glassBlur: m.glassBlur != null ? m.glassBlur : 50,
          glassOpacity: m.glassOpacity != null ? m.glassOpacity : null,
          token: token,
          mappings: m.mappings || []
        };
        $('autoBtn').classList.toggle('on', state.auto);
        render();
        loadFullConfig();
        break;
      }

      case 'page':
        // 正在输入/拖动时不要让自动切页把界面掀掉
        if (m.why === 'auto' && (promptOpen() || dragBusy())) {
          pendingAutoPage = m.pageId;
          break;
        }
        state.current = m.pageId;
        if (m.app) state.activeApp = m.app;
        render();
        break;

      case 'app':
        state.activeApp = m.app || '';
        $('appLine').textContent = state.activeApp ? state.activeApp : '未检测到前台';
        break;

      case 'stats':
        state.stats = m.stats || null;
        updateWidgets();
        break;

      case 'config':
        // 电脑端改完配置：整包刷新
        state.cfg = Object.assign({}, state.cfg, {
          background: m.background || '',
          glass: !!m.glass,
          glassAlpha: m.glassAlpha != null ? m.glassAlpha : state.cfg.glassAlpha,
          glassBlur: m.glassBlur != null ? m.glassBlur : state.cfg.glassBlur
        });
        refreshConfig();
        break;

      case 'flash':
        break;

      case 'toast':
        toast(m.text || '', m.level || 'ok');
        break;
    }
  }

  async function refreshConfig() {
    try {
      const r = await api('/api/state');
      if (!r.ok) return;
      const j = await r.json();
      if (Array.isArray(j.pages)) {
        state.pages = j.pages;
        if (!state.pages.some((p) => p.id === state.current)) {
          state.current = j.currentPage || (state.pages[0] && state.pages[0].id) || '';
        }
        state.activeApp = j.activeApp || state.activeApp;
        render();
      }
    } catch { }
  }

  async function loadFullConfig() {
    try {
      const r = await api('/api/config');
      if (!r.ok) return;
      const cfg = await r.json();
      state.full = cfg;
      if (Array.isArray(cfg.pages) && !state.pages.length) state.pages = cfg.pages;
    } catch { }
  }

  async function saveConfig(quiet) {
    try {
      let base = state.full;
      if (!base) {
        const r0 = await api('/api/config');
        if (r0.ok) { base = await r0.json(); state.full = base; }
      }
      if (!base) throw new Error('读不到配置');

      const body = Object.assign({}, base, {
        pages: state.pages,
        background: state.cfg ? state.cfg.background : base.background,
        glass: !!(state.cfg && state.cfg.glass)
      });
      const r = await api('/api/config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      state.full = body;
      if (!quiet) toast('已保存', 'ok');
    } catch (e) {
      toast('保存失败：' + e.message, 'err');
    }
  }

  // ---------------- 自动切页开关 ----------------

  $('autoBtn').addEventListener('click', () => {
    state.auto = !state.auto;
    $('autoBtn').classList.toggle('on', state.auto);
    send({ type: 'auto', on: state.auto });
    vibrate(10);
  });

  $('gear').addEventListener('click', (e) => {
    const href = token ? `/admin.html?t=${encodeURIComponent(token)}` : '/admin.html';
    $('gear').setAttribute('href', href);
    if (!token) return;
    // 让 target=_blank 正常打开
    e.preventDefault();
    window.open(href, '_blank');
  });

  // 时钟每秒走；状态页顺带兜底拉一次 stats
  setInterval(() => {
    const p = currentPage();
    if (p) updateWidgets();
  }, 1000);

  setInterval(async () => {
    const p = currentPage();
    if (!p || p.kind !== 'status' || !state.connected) return;
    try {
      const r = await api('/api/stats');
      if (r.ok) { state.stats = await r.json(); }
    } catch { }
  }, 3000);

  // 页面回到前台时补一次配置（防止管理端改完没收到广播）
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      refreshConfig();
      if (!ws) connect();
    }
  });

  // ---------------- 启动 ----------------

  if (!token) {
    // 服务端 token 可能已被取消：先探一下 /api/state
    api('/api/state').then((r) => {
      if (r.ok) { $('cover').classList.remove('show'); connect(); }
      else $('cover').classList.add('show');
    }).catch(() => $('cover').classList.add('show'));
  } else {
    connect();
  }

  // 首次渲染占位
  render();
})();
