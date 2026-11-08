(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  let token = new URLSearchParams(location.search).get('t')
             || localStorage.getItem('controlhub.token') || '';
  if (token) localStorage.setItem('controlhub.token', token);

  let cfg = null;
  let selPage = 0;
  let selCell = null;   // {row, col}
  let selWidget = null; // 状态页选中的模块
  let wDrag = null;     // 状态页拖选 {r0,c0,r1,c1}
  let lanOrigin = '';    // 局域网访问地址，手机端用

  // 上传一张图，返回可访问的 URL（失败抛错）
  async function uploadImage(file) {
    if (!file) return '';
    const fd = new FormData();
    fd.append('file', file);
    const r = await fetch('/api/upload' + (token ? '?t=' + encodeURIComponent(token) : ''), {
      method: 'POST', headers: token ? { 'X-Token': token } : {}, body: fd
    });
    const j = await r.json().catch(() => ({}));
    if (!r.ok || !j.ok) throw new Error(j.error || ('HTTP ' + r.status));
    return j.url;
  }

  function pickFile(inputId, onPicked) {
    const inp = $(inputId);
    inp.value = '';
    inp.onchange = async () => {
      const f = inp.files && inp.files[0];
      if (!f) return;
      log('正在上传 ' + f.name + ' …');
      try {
        const url = await uploadImage(f);
        onPicked(url);
        log('已上传 ✓');
      } catch (e) {
        log('上传失败：' + e.message, 'err');
      }
    };
    inp.click();
  }

  const api = (path, opts = {}) => fetch(path + (path.includes('?') ? '&' : '?') + 't=' + encodeURIComponent(token), {
    ...opts,
    headers: { 'Content-Type': 'application/json', 'X-Token': token, ...(opts.headers || {}) }
  });

  function log(text, kind) {
    const el = $('log');
    el.textContent = text;
    el.className = 'show ' + (kind || 'ok');
    clearTimeout(log._t);
    log._t = setTimeout(() => { el.className = ''; }, 4000);
  }

  // ---------------- 载入 / 保存 ----------------
  const clampOp = (v, dflt) => {
    const n = +v;
    if (!Number.isFinite(n)) return dflt;
    return Math.max(0, Math.min(100, Math.round(n)));
  };

  async function load() {
    const r = await api('/api/config');
    if (!r.ok) { log('载入失败：' + r.status + '（令牌不对？）', 'err'); return; }
    cfg = await r.json();
    cfg.glass = !!cfg.glass;
    cfg.glassOpacity = clampOp(cfg.glassOpacity, 60);
    if (cfg.background == null) cfg.background = '';
    if (typeof cfg.token !== 'string') cfg.token = '';
    if (!cfg.pages || !cfg.pages.length) {
      cfg.pages = [{ id: 'default', name: '通用', rows: 3, cols: 3, buttons: [] }];
    }
    for (const p of cfg.pages) {
      if (!Array.isArray(p.buttons)) p.buttons = [];
      if (!Array.isArray(p.widgets)) p.widgets = [];
      if (p.rowGap == null) p.rowGap = 9;
      if (p.colGap == null) p.colGap = 9;
      if (p.background == null) p.background = '';
      if (p.glass !== true && p.glass !== false) p.glass = null;
      p.glassOpacity = (p.glassOpacity == null) ? null : clampOp(p.glassOpacity, 60);
    }
    selPage = 0;
    selCell = null;
    selWidget = null;
    renderAll();
    log('已载入 ' + cfg.pages.length + ' 个页面');
  }

  async function save() {
    if (!cfg) return;
    // 清掉界面上临时生成的空 id
    for (const p of cfg.pages) {
      if (!p.id) p.id = 'page' + Math.random().toString(36).slice(2, 8);
      if (!Array.isArray(p.buttons)) p.buttons = [];
      if (!Array.isArray(p.widgets)) p.widgets = [];
      p.rowGap = Math.max(0, Math.min(60, Math.round(+(p.rowGap ?? 9))));
      p.colGap = Math.max(0, Math.min(60, Math.round(+(p.colGap ?? 9))));
      if (p.glassOpacity == null) delete p.glassOpacity;
      else p.glassOpacity = clampOp(p.glassOpacity, 60);
      if (p.glass !== true && p.glass !== false) delete p.glass;
      for (const b of p.buttons) if (!b.id) b.id = 'btn' + Math.random().toString(36).slice(2, 8);
      for (const w of p.widgets) if (!w.id) w.id = 'w' + Math.random().toString(36).slice(2, 8);
    }
    const r = await api('/api/config', { method: 'PUT', body: JSON.stringify(cfg) });
    if (!r.ok) { log('保存失败：' + (await r.text()), 'err'); return; }
    const j = await r.json().catch(() => ({}));
    if (typeof j.token === 'string' && j.token !== token) {
      token = j.token;
      if (token) localStorage.setItem('controlhub.token', token);
      else localStorage.removeItem('controlhub.token');
      cfg.token = token;
      renderAll();
      log(token ? '已保存 ✓ 验证码变成 ' + token + '，手机端要用新地址' : '已保存 ✓ 验证码已取消，手机可直接访问', 'ok');
      return;
    }
    log('已保存 ✓ 手机端会自动刷新');
  }

  // ---------------- 渲染 ----------------
  const curPage = () => cfg.pages[selPage];

  function renderAll() {
    renderPages();
    renderPageHeader();
    renderGrid();
    renderMaps();
    renderProps();
    renderGlobal();
    const info = new URLSearchParams(location.search).get('t') || token;
    $('phoneUrl').textContent = (lanOrigin || location.origin) + (info ? '/?t=' + info : '/');
    $('who').textContent = '配置：' + (cfg.pages.length) + ' 页 / ' +
      cfg.pages.reduce((a, p) => a + p.buttons.length, 0) + ' 个按钮';
  }

  function renderGlobal() {
    $('glass').checked = !!cfg.glass;
    $('glassOpacity').value = cfg.glassOpacity ?? 60;
    $('glassOpacityVal').textContent = ($('glassOpacity').value) + '%';
    $('tokenInput').value = cfg.token || '';
    const bg = cfg.background || '';
    const th = $('bgThumb');
    th.style.backgroundImage = bg ? `url("${bg}")` : 'none';
    th.style.borderStyle = bg ? 'solid' : 'dashed';
    th.title = bg ? '默认背景图：' + bg : '未设置默认背景图';
  }

  let dragPageFrom = -1;

  function renderPages() {
    const box = $('pages');
    box.innerHTML = '';
    cfg.pages.forEach((p, i) => {
      const el = document.createElement('div');
      el.className = 'pitem' + (i === selPage ? ' active' : '');
      el.draggable = true;
      el.ondragstart = (e) => {
        dragPageFrom = i;
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', String(i));
      };
      el.ondragover = (e) => { e.preventDefault(); el.classList.add('dragover'); };
      el.ondragleave = () => el.classList.remove('dragover');
      el.ondrop = (e) => {
        e.preventDefault();
        el.classList.remove('dragover');
        const from = dragPageFrom >= 0 ? dragPageFrom : +(e.dataTransfer.getData('text/plain'));
        if (!Number.isFinite(from) || from === i) return;
        const [p] = cfg.pages.splice(from, 1);
        cfg.pages.splice(i, 0, p);
        selPage = i;
        renderAll();
        log('页面顺序已调整，记得保存');
      };

      const grip = document.createElement('span');
      grip.className = 'grip';
      grip.textContent = '⠿';
      grip.title = '拖动调整顺序';

      const nm = document.createElement('span');
      nm.className = 'nm';
      nm.textContent = p.name || p.id;
      const cnt = document.createElement('span');
      cnt.style.cssText = 'font-size:11px;color:#6b7f99';
      cnt.textContent = (p.buttons || []).length + '/' + (p.widgets || []).length;
      const x = document.createElement('span');
      x.className = 'x';
      x.textContent = '×';
      x.title = '删除页面';
      x.onclick = (e) => {
        e.stopPropagation();
        if (cfg.pages.length <= 1) { log('至少要保留一个页面', 'err'); return; }
        if (!confirm(`删除页面「${p.name || p.id}」及其 ${p.buttons.length} 个按钮？`)) return;
        cfg.pages.splice(i, 1);
        if (selPage >= cfg.pages.length) selPage = cfg.pages.length - 1;
        selCell = null;
        renderAll();
      };
      el.append(grip, nm, cnt, x);
      el.onclick = () => { selPage = i; selCell = null; selWidget = null; renderAll(); };
      box.appendChild(el);
    });
  }

  function renderPageHeader() {
    const p = curPage();
    $('pageName').value = p.name || '';
    $('pageRows').value = p.rows;
    $('pageCols').value = p.cols;
    $('pageRowGap').value = p.rowGap ?? 9;
    $('pageColGap').value = p.colGap ?? 9;
    $('pageRows').title = '这一页的网格行数（每格可以放按钮，也可以放时间/CPU 之类）';
    $('pageCols').title = '这一页的网格列数';

    // 本页毛玻璃：空 = 跟随全局
    const pg = $('pageGlass');
    pg.value = p.glass === true ? '1' : (p.glass === false ? '0' : '');
    const gOp = $('pageGlassOpacity');
    const own = p.glassOpacity != null;
    gOp.value = own ? p.glassOpacity : (cfg.glassOpacity ?? 60);
    gOp.disabled = !own;
    $('pageGlassOpacityVal').textContent = (own ? '' : '继承 ') + gOp.value + '%';

    const th = $('pageBgThumb');
    if (p.background) {
      th.style.backgroundImage = `url("${p.background}")`;
      th.style.borderStyle = 'solid';
      th.title = '本页背景：' + p.background;
    } else {
      th.style.backgroundImage = 'none';
      th.style.borderStyle = 'dashed';
      th.title = '未设置：使用全局背景';
    }
  }

  function btnAt(page, r, c) {
    const list = Array.isArray(page.buttons) ? page.buttons : [];
    return list.find(b => {
      const rs = Math.max(1, b.rowSpan || 1), cs = Math.max(1, b.colSpan || 1);
      return r >= b.row && r < b.row + rs && c >= b.col && c < b.col + cs;
    }) || null;
  }

  // ---------------- 统一网格编辑器：按钮 + 状态模块同页 ----------------

  const WKINDS = [
    ['clock', '时间'], ['date', '日期'], ['app', '前台应用'],
    ['cpu', 'CPU 使用率'], ['mem', '内存'], ['gpu', 'GPU 使用率'],
    ['cputemp', 'CPU 温度'], ['gputemp', 'GPU 温度'],
    ['fps', '帧率'], ['hz', '刷新率'], ['label', '静态文字']
  ];

  const wKindName = (k) => (WKINDS.find(x => x[0] === k) || ['', '文字'])[1];
  const rid = (p) => p + Math.random().toString(36).slice(2, 8);

  const btnSpan = (b) => ({ rs: Math.max(1, b.rowSpan || 1), cs: Math.max(1, b.colSpan || 1) });
  const widgetSpan = (w) => ({ rs: Math.max(1, w.rowSpan || 1), cs: Math.max(1, w.colSpan || 1) });

  // 区域是否空着（按钮和模块都算占用；skipB/skipW 是“自己”）
  function rectFree(p, r0, c0, rs, cs, skipB, skipW) {
    const clash = (it, span, skip) => {
      if (!it || it === skip) return false;
      const s = span(it);
      return r0 < it.row + s.rs && it.row < r0 + rs && c0 < it.col + s.cs && it.col < c0 + cs;
    };
    if ((p.buttons || []).some(b => clash(b, btnSpan, skipB))) return false;
    if ((p.widgets || []).some(w => clash(w, widgetSpan, skipW))) return false;
    return true;
  }

  function clampItems(p) {
    const fix = (it, span) => {
      it.row = Math.max(0, Math.min(it.row || 0, p.rows - 1));
      it.col = Math.max(0, Math.min(it.col || 0, p.cols - 1));
      const s = span(it);
      it.rowSpan = Math.max(1, Math.min(s.rs, p.rows - it.row));
      it.colSpan = Math.max(1, Math.min(s.cs, p.cols - it.col));
    };
    (p.buttons || []).forEach(b => fix(b, btnSpan));
    (p.widgets || []).forEach(w => fix(w, widgetSpan));
    // 越界的一律丢掉
    p.buttons = (p.buttons || []).filter(b => b.row < p.rows && b.col < p.cols);
    p.widgets = (p.widgets || []).filter(w => w.row < p.rows && w.col < p.cols);
  }

  function renderGrid() {
    const p = curPage();
    if (!p) return;
    if (!Array.isArray(p.buttons)) p.buttons = [];
    if (!Array.isArray(p.widgets)) p.widgets = [];
    if (p.rows < 1) p.rows = 6;
    if (p.cols < 1) p.cols = 4;

    $('grid').style.display = 'none';
    $('wAddRow').style.display = 'flex';
    $('gridHint').textContent = '在网格上拖动框选一块区域，松手后选「按钮」或「时间/CPU…」；点已有的块改属性，× 删除。';

    const g = $('wgrid');
    g.className = 'wgrid on';
    g.innerHTML = '';
    g.style.gridTemplateColumns = `repeat(${p.cols}, 1fr)`;
    g.style.gridTemplateRows = `repeat(${p.rows}, 1fr)`;
    g.style.gap = `${p.rowGap ?? 9}px ${p.colGap ?? 9}px`;
    g.style.minHeight = Math.min(620, Math.max(280, p.rows * 40)) + 'px';

    // 空格子：框选用
    for (let r = 0; r < p.rows; r++) {
      for (let c = 0; c < p.cols; c++) {
        const cell = document.createElement('div');
        cell.className = 'wcell';
        cell.dataset.r = r; cell.dataset.c = c;
        cell.style.gridRow = String(r + 1);
        cell.style.gridColumn = String(c + 1);
        cell.onpointerdown = (e) => {
          if (e.button !== undefined && e.button !== 0) return;
          e.preventDefault();
          wDrag = { r0: r, c0: c, r1: r, c1: c, x: e.clientX, y: e.clientY };
          paintSelection();
        };
        cell.onpointerenter = () => {
          if (!wDrag) return;
          wDrag.r1 = r; wDrag.c1 = c;
          paintSelection();
        };
        g.appendChild(cell);
      }
    }

    // 按钮
    for (const b of p.buttons) {
      const s = btnSpan(b);
      const el = document.createElement('div');
      el.className = 'witem wbtn' + (selCell && selCell.row === b.row && selCell.col === b.col && !selWidget ? ' sel' : '');
      el.style.gridRow = `${b.row + 1} / span ${s.rs}`;
      el.style.gridColumn = `${b.col + 1} / span ${s.cs}`;
      if (b.color) el.style.borderColor = b.color;

      const ic = document.createElement('div');
      ic.className = 'wbicon';
      if (b.iconImage) {
        ic.style.backgroundImage = `url("${b.iconImage}")`;
        ic.style.backgroundSize = '26px 26px';
        ic.style.backgroundPosition = 'center';
        ic.style.backgroundRepeat = 'no-repeat';
      } else {
        ic.textContent = b.icon || '▢';
      }
      const tx = document.createElement('div');
      tx.className = 'wv';
      tx.textContent = b.label || '按钮';
      const kd = document.createElement('div');
      kd.className = 'wk';
      kd.textContent = '按钮 · ' + (b.action ? b.action.type : 'none');

      const x = document.createElement('div');
      x.className = 'wx';
      x.textContent = '×';
      x.title = '删除按钮';
      x.onclick = (e) => {
        e.stopPropagation();
        p.buttons = p.buttons.filter(z => z !== b);
        selCell = null;
        renderAll();
      };

      el.append(ic, kd, tx, x);
      el.onclick = () => { selCell = { row: b.row, col: b.col }; selWidget = null; renderAll(); };
      g.appendChild(el);
    }

    // 状态模块
    for (const w of p.widgets) {
      const s = widgetSpan(w);
      const el = document.createElement('div');
      el.className = 'witem' + (w === selWidget ? ' sel' : '');
      el.style.gridRow = `${w.row + 1} / span ${s.rs}`;
      el.style.gridColumn = `${w.col + 1} / span ${s.cs}`;
      if (w.color) el.style.borderColor = w.color;

      const k = document.createElement('div');
      k.className = 'wk';
      k.textContent = wKindName(w.kind);
      const v = document.createElement('div');
      v.className = 'wv';
      if (w.kind === 'clock') v.textContent = sampleClock();
      else if (w.kind === 'label') v.textContent = w.text || w.title || '文字';
      else if (w.title) v.textContent = w.title;
      else v.innerHTML = '##<small>' + unitOf(w.kind) + '</small>';

      const x = document.createElement('div');
      x.className = 'wx';
      x.textContent = '×';
      x.title = '删除模块';
      x.onclick = (e) => {
        e.stopPropagation();
        p.widgets = p.widgets.filter(z => z !== w);
        if (selWidget === w) selWidget = null;
        renderAll();
      };

      el.append(k, v, x);
      el.onclick = () => { selWidget = w; selCell = null; renderAll(); };
      g.appendChild(el);
    }
  }

  function sampleClock() {
    const d = new Date();
    return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
  }

  function unitOf(kind) {
    if (kind === 'cpu' || kind === 'mem' || kind === 'gpu') return '%';
    if (kind === 'cputemp' || kind === 'gputemp') return '°C';
    if (kind === 'fps') return 'FPS';
    if (kind === 'hz') return 'Hz';
    return '';
  }

  function paintSelection() {
    if (!wDrag) return;
    const r0 = Math.min(wDrag.r0, wDrag.r1), r1 = Math.max(wDrag.r0, wDrag.r1);
    const c0 = Math.min(wDrag.c0, wDrag.c1), c1 = Math.max(wDrag.c0, wDrag.c1);
    for (const cell of $('wgrid').querySelectorAll('.wcell')) {
      const r = +cell.dataset.r, c = +cell.dataset.c;
      cell.classList.toggle('hov', r >= r0 && r <= r1 && c >= c0 && c <= c1);
    }
  }

  // 松手：框选区域 → 选择建按钮还是建模块
  window.addEventListener('pointerup', () => {
    if (!wDrag) return;
    const p = curPage();
    const d = wDrag;
    wDrag = null;
    $('wgrid').querySelectorAll('.wcell.hov').forEach(x => x.classList.remove('hov'));
    if (!p) return;

    const r0 = Math.min(d.r0, d.r1), r1 = Math.max(d.r0, d.r1);
    const c0 = Math.min(d.c0, d.c1), c1 = Math.max(d.c0, d.c1);
    const rs = Math.min(r1 - r0 + 1, p.rows - r0);
    const cs = Math.min(c1 - c0 + 1, p.cols - c0);

    if (!rectFree(p, r0, c0, rs, cs)) { log('这块区域已经被占用了，换个位置', 'err'); return; }
    openChooser(d.x, d.y, { r0, c0, rs, cs });
  });

  let chooser = null;

  function closeChooser() {
    if (chooser) { chooser.remove(); chooser = null; }
  }

  function openChooser(x, y, region) {
    closeChooser();
    const box = document.createElement('div');
    box.className = 'wmenu';

    const head = document.createElement('div');
    head.className = 'wmhead';
    head.textContent = `新建 ${region.rs}×${region.cs}`;
    box.appendChild(head);

    const mk = (label, kind) => {
      const b = document.createElement('button');
      b.className = 'wmbtn' + (kind === 'button' ? ' primary' : '');
      b.textContent = label;
      b.onclick = () => { closeChooser(); createItem(kind, region); };
      box.appendChild(b);
    };

    mk('按钮', 'button');
    const sep = document.createElement('div');
    sep.className = 'wmsep';
    box.appendChild(sep);
    for (const [k, n] of WKINDS) mk(n, k);

    document.body.appendChild(box);
    const r = box.getBoundingClientRect();
    box.style.left = Math.min(x, window.innerWidth - r.width - 12) + 'px';
    box.style.top = Math.min(y, window.innerHeight - r.height - 12) + 'px';
    chooser = box;

    setTimeout(() => {
      document.addEventListener('pointerdown', function onDoc(e) {
        if (box.contains(e.target)) return;
        document.removeEventListener('pointerdown', onDoc, true);
        closeChooser();
      }, true);
    }, 0);
  }

  function createItem(kind, region) {
    const p = curPage();
    if (!p) return;
    const { r0, c0, rs, cs } = region;
    if (!rectFree(p, r0, c0, rs, cs)) { log('这块区域已经被占用了', 'err'); return; }

    if (kind === 'button') {
      const b = { id: rid('btn'), row: r0, col: c0, rowSpan: rs, colSpan: cs, label: '', icon: '', action: { type: 'none' } };
      p.buttons.push(b);
      selCell = { row: r0, col: c0 };
      selWidget = null;
    } else {
      const w = { id: rid('w'), kind, row: r0, col: c0, rowSpan: rs, colSpan: cs };
      if (kind === 'label') w.text = '文字';
      p.widgets.push(w);
      selWidget = w;
      selCell = null;
    }
    renderAll();
    log(kind === 'button' ? '已添加按钮，右边设置它的动作' : '已添加模块，右边改类型/标题');
  }

  // 改尺寸：放不下就不改
  function resizeItem(it, isBtn, rs, cs) {
    const p = curPage();
    rs = Math.max(1, Math.min(rs, p.rows - it.row));
    cs = Math.max(1, Math.min(cs, p.cols - it.col));
    const free = isBtn
      ? rectFree(p, it.row, it.col, rs, cs, it, null)
      : rectFree(p, it.row, it.col, rs, cs, null, it);
    if (!free) { log('放不下：会和其他块重叠', 'err'); return false; }
    it.rowSpan = rs; it.colSpan = cs;
    return true;
  }

  function addItem(kind, rs, cs) {
    const p = curPage();
    let spot = null, ur = rs, uc = cs;
    for (const [r2, c2] of [[rs, cs], [rs, 1], [1, cs], [1, 1]]) {
      spot = freeRect(p, r2, c2);
      if (spot) { ur = r2; uc = c2; break; }
    }
    if (!spot) { log('没有空位了，先扩大网格或删掉一些块', 'err'); return; }
    createItem(kind, { r0: spot.r, c0: spot.c, rs: ur, cs: uc });
  }

  function freeRect(p, rs, cs) {
    for (let r = 0; r + rs <= p.rows; r++) {
      for (let c = 0; c + cs <= p.cols; c++) {
        if (rectFree(p, r, c, rs, cs)) return { r, c };
      }
    }
    return null;
  }

  function renderWidgetProps(p, w) {
    const box = $('props');
    $('propTitle').textContent = `模块 (${w.row + 1}, ${w.col + 1})`;
    box.innerHTML = '';

    box.appendChild(field('类型', () => {
      const s = document.createElement('select');
      for (const [v, n] of WKINDS) {
        const o = document.createElement('option');
        o.value = v; o.textContent = n;
        if (w.kind === v) o.selected = true;
        s.appendChild(o);
      }
      s.onchange = () => { w.kind = s.value; renderAll(); };
      return s;
    }));

    box.appendChild(field('标题', () => {
      const i = input(w.title, v => { w.title = v; renderAll(); });
      i.placeholder = '留空用默认名';
      return i;
    }));

    if (w.kind === 'label') {
      box.appendChild(field('文字', () => {
        const i = input(w.text, v => { w.text = v; renderAll(); });
        i.placeholder = '要显示的文字';
        return i;
      }));
    }

    box.appendChild(field('颜色', () => {
      const i = document.createElement('input');
      i.type = 'color';
      i.value = w.color || '#3b82f6';
      i.oninput = () => { w.color = i.value; };
      i.onchange = () => { w.color = i.value; renderAll(); };
      const row = document.createElement('div');
      row.className = 'row';
      row.appendChild(i);
      const clr = document.createElement('button');
      clr.className = 'btn sm';
      clr.textContent = '默认';
      clr.onclick = () => { w.color = ''; renderAll(); };
      row.appendChild(clr);
      return row;
    }));

    const pos = document.createElement('div');
    pos.className = 'row';
    const mk = (labelText, val, apply) => {
      const lb = document.createElement('label');
      lb.textContent = labelText;
      lb.style.flex = '0 0 auto';
      const inp = document.createElement('input');
      inp.type = 'number';
      inp.style.flex = '1 1 0';
      inp.value = val;
      inp.onchange = () => apply(+inp.value);
      pos.append(lb, inp);
    };
    mk('行', w.row + 1, v => { if (moveWidget(p, w, v - 1, w.col, w.rowSpan, w.colSpan)) renderAll(); });
    mk('列', w.col + 1, v => { if (moveWidget(p, w, w.row, v - 1, w.rowSpan, w.colSpan)) renderAll(); });
    box.appendChild(pos);

    const span = document.createElement('div');
    span.className = 'row';
    const mk2 = (labelText, val, apply) => {
      const lb = document.createElement('label');
      lb.textContent = labelText;
      lb.style.flex = '0 0 auto';
      const inp = document.createElement('input');
      inp.type = 'number';
      inp.style.flex = '1 1 0';
      inp.value = val;
      inp.onchange = () => apply(+inp.value);
      span.append(lb, inp);
    };
    mk2('行跨', w.rowSpan || 1, v => { if (moveWidget(p, w, w.row, w.col, v, w.colSpan)) renderAll(); });
    mk2('列跨', w.colSpan || 1, v => { if (moveWidget(p, w, w.row, w.col, w.rowSpan, v)) renderAll(); });
    box.appendChild(span);

    const del = document.createElement('button');
    del.className = 'btn danger';
    del.textContent = '删除这个模块';
    del.onclick = () => {
      p.widgets = p.widgets.filter(z => z !== w);
      selWidget = null;
      renderAll();
    };
    box.appendChild(del);
  }

  function renderMaps() {
    const tb = $('maps');
    tb.innerHTML = '';
    cfg.mappings.forEach((m, i) => {
      const tr = document.createElement('tr');

      const td1 = document.createElement('td');
      const inp = document.createElement('input');
      inp.value = m.process;
      inp.placeholder = '例如 chrome';
      inp.oninput = () => { m.process = inp.value.trim(); };
      td1.appendChild(inp);

      const td2 = document.createElement('td');
      const sel = document.createElement('select');
      cfg.pages.forEach(p => {
        const o = document.createElement('option');
        o.value = p.id;
        o.textContent = p.name || p.id;
        if (p.id === m.pageId) o.selected = true;
        sel.appendChild(o);
      });
      if (!cfg.pages.some(p => p.id === m.pageId)) {
        const o = document.createElement('option');
        o.value = m.pageId;
        o.textContent = '(缺失) ' + m.pageId;
        o.selected = true;
        sel.appendChild(o);
      }
      sel.onchange = () => { m.pageId = sel.value; };
      td2.appendChild(sel);

      const td3 = document.createElement('td');
      const rm = document.createElement('button');
      rm.className = 'btn sm danger';
      rm.textContent = '×';
      rm.onclick = () => { cfg.mappings.splice(i, 1); renderAll(); };
      td3.appendChild(rm);

      tr.append(td1, td2, td3);
      tb.appendChild(tr);
    });
  }

  // ---------------- 属性面板 ----------------
  function renderProps() {
    const box = $('props');
    const p = curPage();

    if (selWidget && (p.widgets || []).includes(selWidget)) {
      renderWidgetProps(p, selWidget);
      return;
    }

    const b = selCell ? btnAt(p, selCell.row, selCell.col) : null;

    if (!b) {
      $('propTitle').textContent = '属性';
      box.innerHTML = '<div class="empty-note">在左边网格上拖出一块区域新建按钮或状态模块，点已有的块可以改它</div>';
      return;
    }

    $('propTitle').textContent = `按钮 (${b.row + 1}, ${b.col + 1})`;
    box.innerHTML = '';

    box.appendChild(field('文字', () => {
      const i = input(b.label, v => { b.label = v; });
      return i;
    }));
    box.appendChild(field('图标', () => {
      const i = input(b.icon, v => { b.icon = v; });
      i.placeholder = 'emoji 或文字，可留空';
      return i;
    }));

    // 自定义图标图片（优先于上面的 emoji）
    const iconRow = document.createElement('div');
    iconRow.className = 'row';
    const iconLb = document.createElement('label');
    iconLb.textContent = '图标图片';
    const iconG = document.createElement('div');
    iconG.className = 'grow';
    const prev = document.createElement('div');
    prev.className = 'icon-prev';
    if (b.iconImage) {
      prev.style.backgroundImage = `url("${b.iconImage}")`;
      prev.title = b.iconImage;
    }
    const bUp = document.createElement('button');
    bUp.className = 'btn sm';
    bUp.textContent = '上传…';
    bUp.onclick = () => pickFile('btnIconFile', (url) => { b.iconImage = url; renderAll(); });
    const bClr = document.createElement('button');
    bClr.className = 'btn sm danger';
    bClr.textContent = '清除';
    bClr.onclick = () => { b.iconImage = ''; renderAll(); };
    iconG.style.cssText = 'display:flex;gap:6px;align-items:center';
    iconG.append(prev, bUp, bClr);
    iconRow.append(iconLb, iconG);
    box.appendChild(iconRow);

    const colorRow = document.createElement('div');
    colorRow.className = 'hint';
    colorRow.style.margin = '-2px 0 8px 84px';
    colorRow.textContent = '按钮文字会自动在黑色/白色中选择更醒目的一种';
    box.appendChild(colorRow);
    box.appendChild(field('副标题', () => {
      const i = input(b.sub, v => { b.sub = v; });
      i.placeholder = '可选，显示在按钮底部';
      return i;
    }));
    box.appendChild(field('颜色', () => {
      const i = document.createElement('input');
      i.type = 'color';
      i.value = b.color || '#2b6cb0';
      i.style.height = '34px';
      i.oninput = () => { b.color = i.value; renderGrid(); };
      return i;
    }));

    const spanRow = document.createElement('div');
    spanRow.className = 'row';
    spanRow.innerHTML = '<label>行跨</label><div class="grow" style="display:flex;gap:6px;align-items:center"></div>';
    const spanG = spanRow.querySelector('.grow');
    const rsInput = document.createElement('input');
    rsInput.type = 'number'; rsInput.min = '1'; rsInput.value = b.rowSpan || 1;
    rsInput.style.width = '68px';
    rsInput.title = '这个按钮占几行';
    rsInput.onchange = () => {
      const v = Math.max(1, Math.min(+rsInput.value || 1, p.rows - b.row));
      if (resizeItem(b, true, v, b.colSpan || 1)) { b.rowSpan = v; renderAll(); }
      else { rsInput.value = b.rowSpan || 1; }
    };
    const csInput = document.createElement('input');
    csInput.type = 'number'; csInput.min = '1'; csInput.value = b.colSpan || 1;
    csInput.style.width = '68px';
    csInput.title = '这个按钮占几列';
    csInput.onchange = () => {
      const v = Math.max(1, Math.min(+csInput.value || 1, p.cols - b.col));
      if (resizeItem(b, true, b.rowSpan || 1, v)) { b.colSpan = v; renderAll(); }
      else { csInput.value = b.colSpan || 1; }
    };
    const rsLb = document.createElement('span');
    rsLb.textContent = '列跨';
    rsLb.style.cssText = 'font-size:12px;color:#8fa6c2;margin-left:6px';
    spanG.append(rsInput, rsLb, csInput, document.createElement('span'));
    box.appendChild(spanRow);
    const t = document.createElement('div');
    t.className = 'row';
    t.innerHTML = '<label>动作</label><div class="grow"><div class="seg" id="types"></div></div>';
    box.appendChild(t);

    const types = ['none', 'app', 'open', 'text', 'hotkey', 'sequence', 'prompt'];
    const names = { none: '无', app: '启动程序', open: '打开文件', text: '输入文本', hotkey: '快捷键', sequence: '组合', prompt: '弹窗输入' };
    const seg = t.querySelector('#types');
    types.forEach(ty => {
      const btn = document.createElement('button');
      btn.textContent = names[ty];
      btn.className = (b.action.type === ty) ? 'on' : '';
      btn.onclick = () => {
        b.action = { type: ty };
        if (ty === 'sequence') b.action.steps = [];
        renderAll();
      };
      seg.appendChild(btn);
    });

    box.appendChild(actionFields(b));
  }

  function field(label, build) {
    const row = document.createElement('div');
    row.className = 'row';
    const lb = document.createElement('label');
    lb.textContent = label;
    const g = document.createElement('div');
    g.className = 'grow';
    g.appendChild(build());
    row.append(lb, g);
    return row;
  }

  function input(value, onChange, placeholder) {
    const i = document.createElement('input');
    i.value = value || '';
    if (placeholder) i.placeholder = placeholder;
    i.oninput = () => onChange(i.value);
    return i;
  }

  function actionFields(b) {
    const box = document.createElement('div');
    const a = b.action;
    const make = (label, el) => {
      const row = document.createElement('div');
      row.className = 'row';
      const lb = document.createElement('label');
      lb.textContent = label;
      const g = document.createElement('div');
      g.className = 'grow';
      g.appendChild(el);
      row.append(lb, g);
      box.appendChild(row);
      return row;
    };

    if (a.type === 'app' || a.type === 'open') {
      make('路径', input(a.path, v => { a.path = v; }, a.type === 'app' ? 'notepad.exe 或 C:\\\\xx\\\\yy.exe' : 'C:\\\\Users\\\\...\\\\文件.txt'));
      if (a.type === 'app') make('参数', input(a.args, v => { a.args = v; }, '可留空'));
    } else if (a.type === 'text') {
      const ta = document.createElement('textarea');
      ta.value = a.text || '';
      ta.oninput = () => { a.text = ta.value; };
      ta.placeholder = '要输入的文字，支持中文';
      make('文本', ta);

      const sel = document.createElement('select');
      [['type', '逐字输入（推荐）'], ['paste', '剪贴板粘贴（兼容性更好）']].forEach(([v, n]) => {
        const o = document.createElement('option');
        o.value = v; o.textContent = n;
        if ((a.method || 'type') === v) o.selected = true;
        sel.appendChild(o);
      });
      sel.onchange = () => { a.method = sel.value; };
      make('方式', sel);
    } else if (a.type === 'prompt') {
      const method = document.createElement('select');
      [['type', '逐字输入（推荐）'], ['paste', '剪贴板粘贴（兼容性更好）']].forEach(([v, n]) => {
        const o = document.createElement('option');
        o.value = v; o.textContent = n;
        if ((a.method || 'type') === v) o.selected = true;
        method.appendChild(o);
      });
      method.onchange = () => { a.method = method.value; };
      make('输入方式', method);

      const hint = document.createElement('div');
      hint.className = 'hint';
      hint.textContent = '手机端按下这个键会弹出输入框，确认后把文字发送到电脑';
      box.appendChild(hint);
    } else if (a.type === 'hotkey') {
      make('按键', input((a.keys || []).join('+'), v => {
        a.keys = v.split('+').map(s => s.trim()).filter(Boolean);
      }, 'ctrl+shift+s'));
      const hint = document.createElement('div');
      hint.className = 'hint';
      hint.textContent = '支持 ctrl / shift / alt / win / F1-F12 / enter / tab / esc / 方向键 / 单字母数字';
      box.appendChild(hint);
    } else if (a.type === 'sequence') {
      renderSeq(a, box);
    }

    const testRow = document.createElement('div');
    testRow.className = 'row';
    testRow.style.marginTop = '12px';
    const testBtn = document.createElement('button');
    testBtn.className = 'btn sm primary';
    testBtn.textContent = '▶ 在这台电脑上试一下';
    testBtn.style.width = '100%';
    testBtn.onclick = async () => {
      const r = await api('/api/action', { method: 'POST', body: JSON.stringify(a) });
      const j = await r.json().catch(() => ({}));
      log(j.ok ? '执行成功：' + j.result : '执行失败：' + (j.error || r.status), j.ok ? 'ok' : 'err');
    };
    testRow.appendChild(testBtn);
    box.appendChild(testRow);

    return box;
  }

  function renderSeq(a, box) {
    a.steps = a.steps || [];
    a.steps.forEach((s, i) => {
      const row = document.createElement('div');
      row.className = 'seqrow';

      const sel = document.createElement('select');
      ['app', 'open', 'text', 'hotkey'].forEach(ty => {
        const o = document.createElement('option');
        o.value = ty;
        o.textContent = { app: '启动', open: '打开', text: '输入', hotkey: '按键' }[ty];
        if (s.type === ty) o.selected = true;
        sel.appendChild(o);
      });
      sel.onchange = () => {
        a.steps[i] = { type: sel.value };
        renderAll();
      };

      const inp = document.createElement('input');
      inp.value = seqValue(s);
      inp.placeholder = { app: '程序路径', open: '文件路径', text: '要输入的文字', hotkey: 'ctrl+s' }[s.type] || '';
      inp.oninput = () => setSeqValue(s, inp.value);

      const rm = document.createElement('button');
      rm.className = 'btn sm danger';
      rm.textContent = '×';
      rm.onclick = () => { a.steps.splice(i, 1); renderAll(); };

      row.append(sel, inp, rm);
      box.appendChild(row);
    });

    const add = document.createElement('button');
    add.className = 'btn sm';
    add.textContent = '+ 添加一步';
    add.style.width = '100%';
    add.onclick = () => {
      a.steps.push({ type: 'hotkey', keys: ['enter'] });
      renderAll();
    };
    box.appendChild(add);
  }

  function seqValue(s) {
    if (s.type === 'text') return s.text || '';
    if (s.type === 'hotkey') return (s.keys || []).join('+');
    return s.path || '';
  }

  function setSeqValue(s, v) {
    if (s.type === 'text') s.text = v;
    else if (s.type === 'hotkey') s.keys = v.split('+').map(x => x.trim()).filter(Boolean);
    else s.path = v;
  }

  // ---------------- 事件 ----------------
  $('pageName').oninput = e => { curPage().name = e.target.value; renderPages(); };
  $('pageRows').onchange = e => {
    curPage().rows = Math.max(1, Math.min(10, +e.target.value || 1));
    curPage().buttons = curPage().buttons.filter(b => b.row < curPage().rows);
    clampItems(curPage());
    selCell = null; renderAll();
  };
  $('pageCols').onchange = e => {
    curPage().cols = Math.max(1, Math.min(10, +e.target.value || 1));
    curPage().buttons = curPage().buttons.filter(b => b.col < curPage().cols);
    clampItems(curPage());
    selCell = null; renderAll();
  };
  $('pageRowGap').onchange = e => {
    curPage().rowGap = Math.max(0, Math.min(60, Math.round(+e.target.value || 0)));
    renderGrid(); renderPageHeader();
  };
  $('pageColGap').onchange = e => {
    curPage().colGap = Math.max(0, Math.min(60, Math.round(+e.target.value || 0)));
    renderGrid(); renderPageHeader();
  };

  $('addPage').onclick = () => {
    if (cfg.pages.length >= 12) { log('页面太多会不好用', 'err'); return; }
    const name = prompt('新页面名称', '新页面');
    if (name === null) return;
    cfg.pages.push({
      id: 'page' + Math.random().toString(36).slice(2, 8),
      name: name || '新页面', rows: 3, cols: 3, buttons: []
    });
    selPage = cfg.pages.length - 1;
    selCell = null;
    renderAll();
  };

  $('addMap').onclick = () => {
    const pid = cfg.pages[selPage].id;
    cfg.mappings.push({ process: '', pageId: pid });
    renderAll();
  };

  $('mapCurrent').onclick = async () => {
    const r = await api('/api/state');
    const j = await r.json();
    if (!j.activeApp) { log('读不到前台程序，先点一下别的窗口再试', 'err'); return; }
    cfg.mappings.push({ process: j.activeApp, pageId: cfg.pages[selPage].id });
    renderAll();
    log('已添加 ' + j.activeApp + ' → ' + (cfg.pages[selPage].name || '当前页'));
  };

  $('save').onclick = save;
  $('reload').onclick = load;
  $('openPhone').onclick = () => window.open((lanOrigin || location.origin) + (token ? '/?t=' + token : '/'), '_blank');

  $('wAdd').onclick = () => addItem('cpu', 2, 2);
  $('wAuto').onclick = () => {
    const p = curPage();
    if ((p.widgets || []).length && !confirm('会用默认的一套九个模块替换当前模块布局，继续？')) return;
    p.rows = 8; p.cols = 4;
    p.widgets = [
      { id: 'w1', kind: 'clock', row: 0, col: 0, rowSpan: 2, colSpan: 2 },
      { id: 'w2', kind: 'date', row: 0, col: 2, rowSpan: 2, colSpan: 2 },
      { id: 'w3', kind: 'cpu', row: 2, col: 0, rowSpan: 2, colSpan: 2 },
      { id: 'w4', kind: 'mem', row: 2, col: 2, rowSpan: 2, colSpan: 2 },
      { id: 'w5', kind: 'gpu', row: 4, col: 0, rowSpan: 2, colSpan: 2 },
      { id: 'w6', kind: 'gputemp', row: 4, col: 2, rowSpan: 2, colSpan: 2 },
      { id: 'w7', kind: 'cputemp', row: 6, col: 0, rowSpan: 1, colSpan: 2 },
      { id: 'w8', kind: 'fps', row: 6, col: 2, rowSpan: 1, colSpan: 2 },
      { id: 'w9', kind: 'app', row: 7, col: 0, rowSpan: 1, colSpan: 4 }
    ];
    selWidget = null;
    clampItems(p);
    renderAll();
    log('已铺默认模块，记得保存');
  };
  $('glass').onchange = (e) => { cfg.glass = e.target.checked; };
  $('tokenInput').oninput = (e) => { cfg.token = e.target.value.trim(); };
  $('tokenRandom').onclick = () => {
    const chars = 'abcdefghjkmnpqrstuvwxyz23456789';
    let s = '';
    for (let i = 0; i < 6; i++) s += chars[Math.floor(Math.random() * chars.length)];
    cfg.token = s;
    $('tokenInput').value = s;
  };
  $('bgUpload').onclick = () => pickFile('bgFile', (url) => { cfg.background = url; renderAll(); });
  $('bgClear').onclick = () => { cfg.background = ''; renderAll(); };
  $('pageBgUpload').onclick = () => pickFile('pageBgFile', (url) => { curPage().background = url; renderAll(); });
  $('pageBgClear').onclick = () => { curPage().background = ''; renderAll(); };

  $('glassOpacity').oninput = (e) => {
    cfg.glassOpacity = clampOp(e.target.value, 60);
    $('glassOpacityVal').textContent = cfg.glassOpacity + '%';
  };
  $('pageGlass').onchange = (e) => {
    const v = e.target.value;
    const p = curPage();
    p.glass = v === '1' ? true : (v === '0' ? false : null);
    // 选了“跟随全局”就把透明度也交回全局；选了开/关则给它一个起点值，滑杆才好用
    if (p.glass === null) p.glassOpacity = null;
    else if (p.glassOpacity == null) p.glassOpacity = cfg.glassOpacity ?? 60;
    renderPageHeader();
  };
  $('pageGlassOpacity').oninput = (e) => {
    const p = curPage();
    p.glassOpacity = clampOp(e.target.value, cfg.glassOpacity ?? 60);
    $('pageGlassOpacityVal').textContent = p.glassOpacity + '%';
  };

  // 手机端地址优先用局域网 IP（127.0.0.1 手机打不开）
  fetch('/api/info?t=' + encodeURIComponent(token))
    .then(r => r.json())
    .then(d => {
      if (Array.isArray(d.urls) && d.urls.length) {
        lanOrigin = d.urls[0].replace(/\/\?t=[^&]*$/, '');
        renderAll();
      }
    })
    .catch(() => {});

  load();
})();
