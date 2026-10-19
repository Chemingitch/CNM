/* CNM 前端：与 C# 通过 WebView2 postMessage 通信，无第三方库 */
const $ = (s) => document.querySelector(s);
const $$ = (s) => Array.from(document.querySelectorAll(s));

let seq = 0;
const pending = new Map();
const S = {
  models: [],
  jobs: [],
  reqs: [],
  settings: null,
  engine: null,
  service: null,
  ips: [],
  scanning: false,
  hfFiles: [],
  hfRepo: null,
  hfResults: [],
  hfGroups: null,
  hfSel: {},
  hfHideMm: false,
  hfQuant: '',
  hfMaxSize: 0,
  hfSearched: false,
  hfLoaded: false,
  hfSeq: 0,
  mtab: 'local',
  upLab: '',
  upSort: 'name',
  utab: 'day',
  upDirty: false,
  upShown: null,
  dirty: false,
  loadingModel: null        // 正在加载的模型 id（防重入：加载中再点「加载」直接拒绝）
  , pings: {}               // 外地模型测速结果：key=上游\0模型，值={ok,firstMs,totalMs,error}
};

/* ---------------- 通信 ---------------- */
function post(o) {
  try { window.chrome.webview.postMessage(o); } catch (e) { console.error('postMessage 失败', e); }
}

function call(cmd, extra = {}) {
  return new Promise((resolve, reject) => {
    const reqId = 'r' + (++seq);
    pending.set(reqId, { resolve, reject });
    post(Object.assign({ cmd, reqId }, extra));
    setTimeout(() => {
      if (pending.has(reqId)) { pending.delete(reqId); reject(new Error('操作超时')); }
    }, 600000);
  });
}

// 防御：不在 WebView2 里打开（例如用浏览器直接看 wwwroot 调试）时，别让整个脚本挂掉
if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', (ev) => {
  const m = ev.data;
  if (!m || !m.type) return;
  switch (m.type) {
    case 'reply': {
      const p = pending.get(m.reqId);
      if (p) { pending.delete(m.reqId); p.resolve(m.data); }
      break;
    }
    case 'error': {
      const p = pending.get(m.reqId);
      if (p) { pending.delete(m.reqId); p.reject(new Error(m.message)); }
      else toast(m.message, 'error');
      break;
    }
    case 'state': onState(m.state); break;
    case 'pings':
      S.pings = m.pings || {};
      if (S.mtab === 'remote') renderRemote();
      break;
    case 'models': S.models = m.models || []; renderModels(); break;
    case 'downloads': S.jobs = m.jobs || []; renderJobs(); break;
    case 'log': addLog(m.source, m.line); break;
    case 'req':
      S.reqs.unshift(m.entry);
      if (S.reqs.length > 300) S.reqs.pop();
      renderReqs();
      break;
    case 'toast': toast(m.msg, m.kind); break;
  }
});

/* ---------------- 工具 ---------------- */
const fmtSize = (b) => {
  if (!b || b <= 0) return '?';
  if (b >= 1073741824) return (b / 1073741824).toFixed(2) + ' GB';
  if (b >= 1048576) return (b / 1048576).toFixed(0) + ' MB';
  return (b / 1024).toFixed(0) + ' KB';
};
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/** 延迟显示：1000ms 以下显示 ms，以上显示秒（1.2s 比 1200ms 好读） */
const fmtMs = (ms) => { const n = +ms || 0; return n < 1000 ? n + ' ms' : (n / 1000).toFixed(1) + ' s'; };

/**
 * 测速结果由后端缓存并推送（key = "上游\0模型" → 首字毫秒，-1 = 测过但失败）。
 * 放后端而不是前端内存：拉完模型列表后端会自动测一遍，刷新/重画都不会丢。
 */
function pingOf(up, model) {
  const v = (S.pings || {})[up + '\u0000' + model];
  if (v == null) return null;
  return v < 0 ? { ok: false, error: '测速失败' } : { ok: true, firstMs: v };
}
const pingMs = (up, model) => { const p = pingOf(up, model); return p && p.ok ? p.firstMs : 999999; };

/** 模型源的主机名，给提示文字用（别再写死 hf-mirror.com —— 用户可以在设置里改） */
function hfHost() {
  const u = (S.settings && S.settings.hfEndpoint) || ($('#setHf') && $('#setHf').value) || 'https://hf-mirror.com';
  try { return new URL(u).host || 'hf-mirror.com'; } catch (e) { return String(u).replace(/^https?:\/\//, '').split('/')[0] || 'hf-mirror.com'; }
}

/** 新建上游时预填的默认值：优先界面上最后一张有地址的卡，其次已保存的配置 */
function lastUpstreamDefaults() {
  let baseUrl = '', proxy = 'auto';
  const rows = $$('#upList .up-card').filter((r) => r.querySelector('.up-url').value.trim());
  if (rows.length) {
    const last = rows[rows.length - 1];
    baseUrl = last.querySelector('.up-url').value.trim();
    proxy = last.querySelector('.up-proxy').value.trim() || 'auto';
    return { baseUrl, proxy };
  }
  const saved = ((S.settings && S.settings.upstreams) || []).filter((u) => (u.baseUrl || '').trim());
  if (saved.length) {
    const u = saved[saved.length - 1];
    baseUrl = u.baseUrl.trim();
    proxy = (u.proxy || '').trim() || 'auto';
  }
  return { baseUrl, proxy };
}
const fmtNum = (n) => {
  n = +n || 0;
  if (n >= 1e6) return (n / 1e6).toFixed(1) + 'M';
  if (n >= 1e3) return (n / 1e3).toFixed(1) + 'k';
  return String(n);
};
const GB = 1073741824;

/** 量化档位：推荐 / 保真 / 省显存 */
function quantTier(q) {
  const s = String(q || '').toUpperCase();
  if (/F32|F16|BF16|Q8/.test(s)) return { key: 'hi', label: '最高保真' };
  if (/Q6|Q5/.test(s)) return { key: 'good', label: '保真优先' };
  if (/Q4|IQ4/.test(s)) return { key: 'rec', label: '' };
  if (/Q3|Q2|IQ3|IQ2/.test(s)) return { key: 'small', label: '更省显存' };
  return { key: '', label: '' };
}

/** 挑量化的偏好顺序（越靠前越推荐：质量与体积平衡） */
const LADDER = ['Q4_K_M', 'Q4_K_S', 'IQ4_XS', 'IQ4_NL', 'Q5_K_M', 'Q5_K_S', 'Q6_K', 'Q4_0', 'Q8_0', 'Q3_K_M', 'Q3_K_S', 'Q2_K', 'F16', 'BF16', 'F32'];

function toast(msg, kind = 'info') {
  const el = document.createElement('div');
  el.className = 'toast ' + (kind || 'info');
  el.textContent = msg;
  $('#toasts').appendChild(el);
  setTimeout(() => { el.style.opacity = '0'; el.style.transform = 'translateY(6px)'; el.style.transition = '.25s'; }, 3200);
  setTimeout(() => el.remove(), 3600);
}

function copyText(text) {
  if (!text) return;
  const done = () => toast('已复制', 'ok');
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(text).then(done).catch(() => fallbackCopy(text, done));
  } else fallbackCopy(text, done);
}
function fallbackCopy(text, done) {
  const ta = document.createElement('textarea');
  ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
  document.body.appendChild(ta); ta.select();
  try { document.execCommand('copy'); done(); } catch (e) { toast('复制失败', 'error'); }
  ta.remove();
}

/* ---------------- 视图切换 ---------------- */
$$('.nav-item').forEach((b) => b.addEventListener('click', () => {
  $$('.nav-item').forEach((x) => x.classList.remove('active'));
  b.classList.add('active');
  $$('.view').forEach((v) => v.classList.add('hidden'));
  $('#view-' + b.dataset.view).classList.remove('hidden');
  if (b.dataset.view === 'browse') ensureBrowseLoaded();
}));

/** 第一次进「在线浏览」就先拉一页（按下载量），省得对着一片空白不知道能点什么 */
function ensureBrowseLoaded() {
  if (S.hfLoaded) return;
  S.hfLoaded = true;
  doSearch();
}

/* ---------------- 状态渲染 ---------------- */
function onState(st) {
  if (!st) return;
  S.engine = st.engine || {};
  S.service = st.service || {};
  S.ips = st.ips || [];
  S.scanning = !!st.scanning;
  if (st.reqLog) { S.reqs = st.reqLog; renderReqs(); if (S.mtab === 'remote') renderRemote(); }
  if (st.usage) { S.usage = st.usage; renderUsage(); }
  if (st.pings) S.pings = st.pings;

  const eng = S.engine, svc = S.service;
  const vram = eng.vramMiB ? ' · 显存 ≈ ' + (eng.vramMiB / 1024).toFixed(1) + ' GB' : '';
  const pe = $('#pillEngine'), ps = $('#pillService');
  if (eng.loading) {
    pe.className = 'pill busy';
    pe.innerHTML = '<span class="dot"></span><span>模型加载中…</span>';
  } else if (eng.running) {
    pe.className = 'pill on';
    pe.innerHTML = '<span class="dot"></span><span>运行中 · ' + esc(eng.loadedId || '') + '</span>';
  } else {
    pe.className = 'pill off';
    pe.innerHTML = '<span class="dot"></span><span>引擎未加载</span>';
  }
  ps.className = 'pill ' + (svc.running ? 'on' : 'off');
  ps.innerHTML = '<span class="dot"></span><span>' + (svc.running ? '服务中 :' + svc.port : '服务停止') + '</span>';

  const miniFull = eng.running ? (eng.loadedId || '已加载') : '未加载模型';
  $('#miniModel').textContent = miniFull + vram;
  (function () {                          // hover 气泡：名字太长被截断时，悬停看全称 + logo
    const tip = $('#miniTip');
    if (!tip) return;
    const lm = eng.running ? (S.models.find((m) => m.loaded) || null) : null;
    const nm = lm ? lm.name : miniFull;
    const sub = lm ? [lm.repo, lm.quant].filter(Boolean).join(' · ') : (eng.running ? '已加载' : '服务未启动');
    let icon = '';
    try { icon = lm && typeof logoTile === 'function' ? logoTile(localKey(lm)) : ''; } catch (e) { }
    tip.innerHTML = icon + '<div style="min-width:0"><div class="tip-name">' + esc(nm) + '</div>' +
      (sub ? '<div class="tip-sub">' + esc(sub) + '</div>' : '') + '</div>';
  })();
  $('#miniService').textContent = svc.running ? '服务中 :' + svc.port + ' · ' + (svc.requests || 0) + ' 次调用' : '服务已停止';
  $('#btnStart').disabled = !!svc.running;
  $('#btnStop').disabled = !svc.running;

  // 当前模型：引擎真的在跑才算“已就绪”——进程崩了界面不能再嘴硬说就绪
  const loaded = eng.running
    ? (S.models.find((m) => m.loaded) || (eng.loadedPath ? { name: eng.loadedId, path: eng.loadedPath } : null))
    : null;
  if (eng.loading) {
    $('#heroTitle').textContent = '正在加载 ' + (eng.pendingId || eng.loadedId || '') + ' …';
    $('#heroSub').textContent = '首次加载要读盘到显存，按模型大小可能要几十秒';
  } else if (loaded) {
    const m = S.models.find((x) => x.loaded);
    $('#heroTitle').textContent = (m ? m.name : eng.loadedId) + ' 已就绪';
    $('#heroSub').textContent = (m
      ? `${m.repo} · ${m.quant || '量化未知'} · ${m.sizeText} · 上下文 ${m.ctx || '?'}${m.vision ? ' · 支持看图' : ''}`
      : eng.loadedPath) + vram + (eng.idleUnloadMinutes ? ` · 空闲 ${eng.idleUnloadMinutes} 分钟自动释放` : '');
  } else {
    $('#heroTitle').textContent = '没有加载模型';
    $('#heroSub').textContent = eng.error || '从下面的模型库里挑一个点“加载”，或者到“在线浏览”下一个新的';
  }
  $('#btnUnload').disabled = !eng.running;

  // 服务页
  const host = (S.ips && S.ips.length && svc.lan) ? S.ips[0] : '127.0.0.1';
  $('#svcBase').textContent = `http://${host}:${svc.port || 1234}/v1`;
  $('#svcKey').textContent = (st.settings && st.settings.apiKey) || '（未设置，任何人都能调）';
  (function () {
    const key = (st.settings && st.settings.apiKey) || '<key>';
    const base = `http://${host}:${svc.port || 1234}`;
    const c = $('#svcCurl');
    if (c) c.textContent = 'curl ' + base + '/v1/models -H "Authorization: Bearer ' + key + '"';
    const okReq = S.reqs.filter((r) => r.status >= 200 && r.status < 300).length;
    const last = S.reqs[0];
    const s2 = $('#svcStats');
    if (s2) {
      const u = (S.usage && S.usage.today) || null;
      const today = u ? ' · 今天 ' + u.req + ' 次（本地 ' + u.local + ' / 外地 ' + u.up + '）' : '';
      s2.textContent = '共 ' + S.reqs.length + ' 条 · 成功 ' + okReq + today +
        (last ? ' · 最近一次 ' + last.time : '（还没人调过）');
    }
  })();
  $('#svcLan').textContent = svc.lan
    ? '局域网开放 · 本机地址 ' + ((S.ips || []).join('、') || '无') + ':' + svc.port
    : '只允许本机 127.0.0.1';
  $('#svcModel').textContent = eng.running ? eng.loadedId + vram : '（没有模型在跑）';
  if (svc.error) $('#svcModel').textContent = svc.error;
  const okReq = S.reqs.filter((r) => r.status >= 200 && r.status < 300).length;
  $('#reqSum').textContent = `${S.reqs.length} 条（成功 ${okReq}）`;

  if (st.settings) {
    S.settings = st.settings;
    if (!S.dirty) fillSettings(st.settings);
    applyTheme(st.settings.theme);
    applyLogo(!!st.settings.customIcon, st.settings.iconVersion);
    renderWatch(st);
  }

  const info = $('#engineInfo');
  if (eng.enginePath) info.textContent = (eng.engineVersion ? eng.engineVersion + ' — ' : '') + eng.enginePath;
  else info.textContent = '未找到 llama-server.exe，点右边“下载并安装引擎”';

  renderModels();
}

function fillSettings(s) {
  $('#setModelsDir').value = s.modelsDir || '';
  $('#setExtraDirs').value = (s.extraModelDirs || []).join(';');
  $('#setPort').value = s.port;
  $('#setLan').checked = !!s.lanExposed;
  $('#setKey').value = s.apiKey || '';
  $('#setAutoStart').checked = !!s.autoStartServer;
  $('#setModelsKey').checked = s.modelsNeedKey !== false;
  $('#setAutoLoad').checked = !!s.autoLoadOnRequest;
  $('#setAutoLast').checked = !!s.autoLoadLastModel;
  {
    const idle = String(s.idleUnloadMinutes == null ? 10 : s.idleUnloadMinutes);
    const sel = $('#setIdle');
    if (!Array.prototype.some.call(sel.options, (o) => o.value === idle)) {
      const o = document.createElement('option');
      o.value = idle; o.textContent = '空闲 ' + idle + ' 分钟';
      sel.appendChild(o);
    }
    sel.value = idle;
  }
  $('#setTray').checked = !!s.closeToTray;
  $('#setCtx').value = s.contextSize;
  $('#setNgl').value = s.gpuLayers;
  $('#setThreads').value = s.threads;
  $('#setParallel').value = s.parallel;
  $('#setFa').checked = !!s.flashAttn;
  $('#setMmprojGpu').checked = !!s.mmprojOnGpu;
  $('#setEnginePath').value = s.enginePath || '';
  $('#setEngineMirror').value = s.engineMirror || '';
  $('#setHf').value = s.hfEndpoint || '';
  $('#setTheme').value = s.theme || 'dark';
  $('#setHkToggle').value = s.hotkeyToggle || '';
  $('#setHkNextModel').value = s.hotkeyNextModel || '';
  $('#setHkUnload').value = s.hotkeyUnload || '';
  renderHotkeyStatus(s.hotkeyStatus);
}

/**
 * 快捷键状态灯：绿=已生效，红=被别的程序占用，灰=没设/不启用。
 * 原因写在 tooltip 里（鼠标停上去看），界面上一律不堆文字。
 */
function renderHotkeyStatus(st) {
  st = st || {};
  const pairs = [
    ['hkToggleState', st.toggle],
    ['hkNextModelState', st.nextModel],
    ['hkUnloadState', st.unload],
  ];
  pairs.forEach(([id, v]) => {
    const el = $('#' + id);
    if (!el) return;
    const s = String(v || '');
    if (!s) {
      el.className = 'hk-led off';
      el.setAttribute('data-tip', '没设这个快捷键');
    } else if (s.startsWith('!')) {
      el.className = 'hk-led bad';
      el.setAttribute('data-tip', '没注册上：' + s.slice(1));
    } else {
      el.className = 'hk-led ok';
      el.setAttribute('data-tip', s + ' · 生效中');
    }
  });
}

/** 把键盘事件转成 "Ctrl+Alt+C" 这种写法（点进输入框直接按组合键） */
function hkFromEvent(e) {
  const mods = [];
  if (e.ctrlKey) mods.push('Ctrl');
  if (e.altKey) mods.push('Alt');
  if (e.shiftKey) mods.push('Shift');
  if (e.metaKey) mods.push('Win');
  const k = e.key;
  // 只按了修饰键：还没按主键，先不填
  if (['Control', 'Alt', 'Shift', 'Meta', 'OS'].includes(k)) return null;
  let main = '';
  if (k === ' ') main = 'Space';
  else if (k === 'Escape') return '';                 // Esc = 清空这个快捷键
  else if (k === 'Backspace') return '';
  else if (k === 'ArrowUp') main = 'Up';
  else if (k === 'ArrowDown') main = 'Down';
  else if (k === 'ArrowLeft') main = 'Left';
  else if (k === 'ArrowRight') main = 'Right';
  else if (k.length === 1) main = k.toUpperCase();
  else if (/^F\d{1,2}$/.test(k)) main = k;
  else main = k;                                       // Enter / Tab / Home / Delete…
  if (!main) return null;
  // 光按字母/数字太容易误触，至少要有修饰键（F1-F24 除外，后端同样规则）
  if (mods.length === 0 && !/^F\d{1,2}$/.test(main)) return null;
  return mods.join('+') + (mods.length ? '+' : '') + main;
}

/** 规范化显示（只做大小写/顺序，不做严格校验——严格校验在后端） */
function normalizeHk(v) {
  const t = String(v || '').trim();
  if (!t) return '';
  return t.split('+').map((x) => {
    const s = x.trim();
    if (!s) return '';
    const low = s.toLowerCase();
    if (low === 'ctrl' || low === 'control') return 'Ctrl';
    if (low === 'alt') return 'Alt';
    if (low === 'shift') return 'Shift';
    if (low === 'win' || low === 'super' || low === 'cmd') return 'Win';
    if (s.length === 1) return s.toUpperCase();
    return s.charAt(0).toUpperCase() + s.slice(1).toLowerCase();
  }).filter(Boolean).join('+');
}

/* ---------------- 守望者：云端上游（API 中转站） ---------------- */

function upInfoText(u) {
  if (u.error) return '连不上：' + u.error + (u.proxyDetail ? '（试过：' + u.proxyDetail + '）' : '');
  const parts = [u.models ? u.models + ' 个模型（去「模型库 → 外地模型」挑）' : '还没连过'];
  if (u.proxyUsed) parts.push('走 ' + u.proxyUsed);
  else {
    const p = String(u.proxy || '').toLowerCase();
    parts.push(p === 'system' ? '跟随系统代理' : (!p || p === 'direct' ? '直连' : '走 ' + u.proxy));
  }
  return parts.join(' · ');
}

/** 「现在默认走」那行：谁在给所有 agent 供模型 */
function renderUpCurrent(def, model) {
  const el = $('#upCurrent');
  if (!el) return;
  el.textContent = model
    ? model + (def ? '（' + def + '）' : '') + ' · 名字对不上时兜底用它'
    : (def ? def + ' —— 还没选模型，去「模型库 → 外地模型」挑一个接入' : '没设 —— 本地库里没有的模型名会报错');
}

/** 一张上游小卡：开关 · 名字 · 地址 · key · 代理 · 测试 · 试一句 · 删 */
function upRow(u) {
  const row = document.createElement('div');
  row.className = 'up-card';
  row.innerHTML =
    '<div class="up-top">' +
      '<label class="switch"><input type="checkbox" class="up-on"' + (u.enabled === false ? '' : ' checked') + '><span></span></label>' +
      '<input class="input up-name" style="max-width:130px" placeholder="名字" value="' + esc(u.name || '') + '">' +
      '<span class="grow"></span>' +
      '<button class="btn tiny up-detect" title="自动试「直连 / 系统代理 / 本机常见端口」，看这个站该不该走代理">自动检测</button>' +
      '<button class="btn tiny up-test">测试</button>' +
      '<button class="btn tiny ghost up-chat">试一句</button>' +
      '<button class="btn tiny ghost up-del">删</button>' +
    '</div>' +
    '<input class="input up-url" placeholder="https://api.xxx.com/v1（中转站的 Base URL）" value="' + esc(u.baseUrl || '') + '">' +
    '<div class="up-mid">' +
      '<input class="input grow up-key" placeholder="API Key（中转站的令牌）" value="' + esc(u.apiKey || '') + '">' +
      '<input class="input up-proxy" list="proxyHints" style="max-width:170px" placeholder="代理：auto / 直连 / socks5://…" value="' + esc(u.proxy || '') + '">' +
    '</div>' +
    '<div class="up-info">' + esc(upInfoText(u)) + '</div>';

  const info = row.querySelector('.up-info');
  const cfgOf = () => ({
    name: row.querySelector('.up-name').value.trim() || '上游',
    baseUrl: row.querySelector('.up-url').value.trim(),
    apiKey: row.querySelector('.up-key').value.trim(),
    proxy: row.querySelector('.up-proxy').value.trim()
  });

  row.querySelectorAll('.input, input[type=checkbox]').forEach((el) => {
    el.addEventListener('input', () => { S.upDirty = true; });
    el.addEventListener('change', () => { S.upDirty = true; });
  });

  row.querySelector('.up-detect').addEventListener('click', async () => {
    const cfg = cfgOf();
    if (!cfg.baseUrl) { toast('先填地址', 'error'); return; }
    info.textContent = '自动检测中：先试直连，不行再试系统代理 / 本机常见端口…';
    const btn = row.querySelector('.up-detect');
    btn.disabled = true;
    try {
      const r = await call('detectUpstreamProxy', { name: cfg.name, baseUrl: cfg.baseUrl, apiKey: cfg.apiKey });
      if (r && r.ok) {
        // 检测通过 = 这条路真的能用：把代理写回、把这条上游打开、并且立刻落盘。
        // 以前只写进输入框、不保存也不启用，于是「界面显示开着，配置里其实是关的」。
        row.querySelector('.up-proxy').value = r.proxy || 'direct';
        const on = row.querySelector('.up-on');
        const wasOn = on.checked;
        on.checked = true;
        info.textContent = '通了：' + r.count + ' 个模型 · 走 ' + (r.proxy || '直连') + '　' + (r.detail || '');
        try {
          const s = await call('saveUpstreams', { upstreams: collectUpstreams() });
          S.upDirty = false;
          S.upShown = null;
          if (s) renderWatch({ settings: s });
          toast('这个站' + (r.proxy ? '要走 ' + r.proxy : '直连就行') + '，已自动启用并保存' + (wasOn ? '' : '（原来没启用）'), 'ok');
        } catch (e2) {
          S.upDirty = true;
          toast('检测通过，但保存失败：' + e2.message + '（请点「保存上游」）', 'error');
        }
      } else {
        info.textContent = '都不通：' + ((r && r.error) || '') + '　' + ((r && r.detail) || '');
        toast('直连 / 系统代理 / 本机常见端口 都没通，检查地址和 Key', 'error');
      }
    } catch (e) { info.textContent = '检测失败：' + e.message; toast(e.message, 'error'); }
    finally { btn.disabled = false; }
  });

  row.querySelector('.up-test').addEventListener('click', async () => {
    const cfg = cfgOf();
    if (!cfg.baseUrl) { toast('先填地址', 'error'); return; }
    info.textContent = '连接中…';
    try {
      const r = await call('testUpstream', cfg);
      if (r && r.ok) {
        info.textContent = r.count + ' 个模型：' + (r.models || []).slice(0, 3).join('、') + (r.count > 3 ? ' …' : '');
        toast('通了：' + r.count + ' 个模型 —— 去「模型库 → 外地模型」挑一个接入', 'ok');
      } else {
        info.textContent = '连不上：' + ((r && r.error) || '未知错误');
        toast('连不上：' + ((r && r.error) || ''), 'error');
      }
    } catch (e) { info.textContent = '连不上：' + e.message; toast(e.message, 'error'); }
  });

  // 真发一条对话过去，看得见完整地址 —— 路径/鉴权/代理哪里不对，一眼就能看出来
  row.querySelector('.up-chat').addEventListener('click', async () => {
    const cfg = cfgOf();
    if (!cfg.baseUrl) { toast('先填地址', 'error'); return; }
    info.textContent = '正在发一条对话…';
    try {
      const r = await call('testUpstreamChat', cfg);
      info.classList.add('mono');
      info.textContent = ((r && r.detail) || '没有回应');
      toast(r && r.ok ? '对话通了' : '对话没通，看卡片上的地址和回应', r && r.ok ? 'ok' : 'error');
    } catch (e) { info.textContent = e.message; toast(e.message, 'error'); }
  });

  row.querySelector('.up-del').addEventListener('click', () => {
    row.remove();
    S.upDirty = true;
  });
  return row;
}

function renderUpstreams(list, def) {
  const box = $('#upList');
  box.innerHTML = '';
  if (!list || list.length === 0) {
    box.innerHTML = '<div class="empty small">还没有上游。点「+ 添加上游」，把中转站的地址和 Key 填进来。</div>';
  } else {
    list.forEach((u) => box.appendChild(upRow(u)));
  }
}

function collectUpstreams() {
  const out = [];
  $$('#upList .up-card').forEach((row) => {
    const name = row.querySelector('.up-name').value.trim();
    if (!name) return;
    out.push({
      name,
      baseUrl: row.querySelector('.up-url').value.trim(),
      apiKey: row.querySelector('.up-key').value.trim(),
      proxy: row.querySelector('.up-proxy').value.trim(),
      enabled: row.querySelector('.up-on').checked
    });
  });
  return out;
}

/** 守望者那块：只在“结构变了”时重画，免得把正在输的字擦掉 */
function renderWatch(st) {
  const s = (st && st.settings) || {};
  const list = s.upstreams || [];
  renderUpCurrent(s.upstreamDefault || '', s.upstreamModel || '');
  // 连上以后模型数量/报错会变 —— 也算进「结构」，这样卡片上的状态行会自动刷新
  const key = JSON.stringify(list.map((u) => [u.name, u.baseUrl, u.apiKey, u.proxy, u.enabled, u.models, u.error, u.proxyUsed, u.proxyDetail])) +
    '|' + (s.upstreamDefault || '') + '|' + (s.upstreamModel || '');
  if (S.upShown === key) { renderRemote(); return; }
  if (S.upDirty && S.upShown != null) { renderRemote(); return; }   // 有没保存的改动 → 不抢用户的输入
  S.upShown = key;
  S.upDirty = false;
  renderUpstreams(list, s.upstreamDefault || '');
  renderRemote();
}

/** 把设置页上填的东西收成一个对象（保存 / 重新连接 都走它） */
function collectSettings() {
  return {
    modelsDir: $('#setModelsDir').value.trim(),
    extraModelDirs: $('#setExtraDirs').value.split(';').map((s) => s.trim()).filter(Boolean),
    port: parseInt($('#setPort').value, 10) || 1234,
    lanExposed: $('#setLan').checked,
    apiKey: $('#setKey').value.trim(),
    autoStartServer: $('#setAutoStart').checked,
    modelsNeedKey: $('#setModelsKey').checked,
    autoLoadOnRequest: $('#setAutoLoad').checked,
    autoLoadLastModel: $('#setAutoLast').checked,
    idleUnloadMinutes: parseInt($('#setIdle').value, 10) || 0,
    closeToTray: $('#setTray').checked,
    contextSize: parseInt($('#setCtx').value, 10) || 8192,
    gpuLayers: parseInt($('#setNgl').value, 10),
    threads: parseInt($('#setThreads').value, 10) || 0,
    parallel: parseInt($('#setParallel').value, 10) || 1,
    flashAttn: $('#setFa').checked,
    mmprojOnGpu: $('#setMmprojGpu').checked,
    enginePath: $('#setEnginePath').value.trim(),
    engineMirror: $('#setEngineMirror').value.trim(),
    hfEndpoint: $('#setHf').value.trim() || 'https://hf-mirror.com',
    theme: $('#setTheme').value,
    hotkeyToggle: normalizeHk($('#setHkToggle').value),
    hotkeyNextModel: normalizeHk($('#setHkNextModel').value),
    hotkeyUnload: normalizeHk($('#setHkUnload').value)
  };
}

function applyTheme(t) {
  document.documentElement.setAttribute('data-theme', t === 'light' ? 'light' : 'dark');
}

/* ---------------- 图标（默认图形 / 用户自定义） ---------------- */
function brandUrl(ver) { return 'https://brand.cnm.local/icon.png?v=' + (ver || 0); }

function applyLogo(has, ver) {
  const box = $('#brandLogo');
  if (box) {
    box.classList.toggle('custom-on', !!has);
    const img = box.querySelector('img.custom');
    if (img && has) img.src = brandUrl(ver);
  }
  renderIconPrev(has, ver);
}

function renderIconPrev(has, ver) {
  const box = $('#iconPrev');
  if (!box) return;
  box.classList.toggle('plain', !!has);
  box.innerHTML = '';
  if (has) {
    const i = document.createElement('img');
    i.src = brandUrl(ver);
    box.appendChild(i);
  } else {
    const m = $('#brandLogo .mark');
    if (m) box.appendChild(m.cloneNode(true));
  }
}

/** 主题：立刻保存并生效（标题栏、底色、界面一起走） */
async function setTheme(t) {
  const next = t === 'light' ? 'light' : 'dark';
  applyTheme(next);
  $('#setTheme').value = next;
  try {
    await call('setTheme', { theme: next });
    if (S.settings) S.settings.theme = next;
    toast(next === 'light' ? '已切到浅色' : '已切到深色', 'ok');
  } catch (e) { toast(e.message, 'error'); }
}

/* ---------------- 模型库 ---------------- */

/** 本地模型卡片左边那个「谁家的」标，喂给 logos.js 的字符串。
 *  只给 作者/仓库/显示名 —— 不带架构名，否则会认错（Bonsai 的 arch 是 qwen35，但它不是千问家的）。 */
function localKey(m) {
  return [m.publisher, m.repo, m.name].filter(Boolean).join('/');
}

function renderModels() {
  const q = ($('#modelFilter').value || '').trim().toLowerCase();

  // 量化下拉：从当前库里现算，保留已选项
  const qSel = $('#modelQuant');
  const cur = qSel.value;
  const quants = Array.from(new Set(S.models.map((m) => m.quant).filter(Boolean))).sort();
  qSel.innerHTML = '<option value="">全部量化</option>' +
    quants.map((x) => `<option value="${esc(x)}">${esc(x)}</option>`).join('');
  if (quants.includes(cur)) qSel.value = cur;
  const quant = qSel.value;
  const sort = $('#modelSort').value;

  const list = S.models.filter((m) => (!q ||
    (m.name + ' ' + m.id + ' ' + m.repo + ' ' + m.quant + ' ' + m.arch).toLowerCase().includes(q)) &&
    (!quant || m.quant === quant));

  const byName = (a, b) => String(a.name).localeCompare(String(b.name), 'zh');
  if (sort === 'size-desc') list.sort((a, b) => (b.size || 0) - (a.size || 0));
  else if (sort === 'size-asc') list.sort((a, b) => (a.size || 0) - (b.size || 0));
  else if (sort === 'quant') list.sort((a, b) => String(a.quant).localeCompare(String(b.quant)) || byName(a, b));
  else list.sort(byName);

  const empty = $('#modelsEmpty');
  empty.style.display = list.length ? 'none' : '';
  if (!list.length) {
    empty.textContent = S.models.length
      ? '没有符合筛选的模型 —— 换个量化，或清掉过滤词。'
      : '这个目录里还没扫到 .gguf。检查一下“设置”里的模型目录。';
  }

  const wrap = $('#modelCards');
  wrap.innerHTML = list.map((m) => `
    <div class="card ${m.loaded ? 'loaded' : ''}">
      <div class="card-top">
        ${logoTile(localKey(m))}
        <div style="min-width:0">
          <div class="card-name">${esc(m.name)}</div>
          <div class="card-repo">${esc(m.repo)}${m.publisher ? ' · ' + esc(m.publisher) : ''}</div>
        </div>
      </div>
      <div class="badges">
        ${m.quant ? `<span class="badge b1">${esc(m.quant)}</span>` : ''}
        <span class="badge">${esc(m.sizeText)}</span>
        ${m.arch ? `<span class="badge b2">${esc(m.arch)}</span>` : ''}
        ${m.ctx ? `<span class="badge">ctx ${m.ctx}</span>` : ''}
        ${m.vision ? '<span class="badge b3">看图</span>' : ''}
        ${m.chat ? '<span class="badge b4">模板</span>' : ''}
        ${m.loaded ? '<span class="badge b3">运行中</span>' : ''}
      </div>
      <div class="card-actions">
        <button class="btn tiny primary" data-load="${esc(m.id)}">${m.loaded ? '重新加载' : '加载'}</button>
        <button class="btn tiny" data-folder="${esc(m.dir)}">目录</button>
        <button class="btn tiny" data-del="${esc(m.id)}">删除</button>
      </div>
    </div>`).join('');
  ensureLogos(list.map(localKey));      // 本地模型也认「谁家的」：QwenPaw → 千问、Llama…，认不出就首字母色块

  wrap.querySelectorAll('[data-load]').forEach((b) => b.addEventListener('click', async () => {
    const id = b.dataset.load;
    if (S.loadingModel) { toast('正在加载 ' + S.loadingModel + '，等它完成（或先点「停止模型」）', 'error'); return; }
    S.loadingModel = id;                 // 防重入：加载中再点别的「加载」直接拒绝（后端 _loadGate 会排队，但 UX 上静默排队让人困惑）
    b.disabled = true;
    toast('开始加载 ' + id + ' …（看引擎日志）');
    try {
      const r = await call('load', { id });
      if (r && r.ok === false) toast(r.message || '加载失败', 'error');
      else toast('已加载 ' + id, 'ok');
    } catch (e) { toast(e.message, 'error'); }
    finally { S.loadingModel = null; b.disabled = false; }
  }));
  wrap.querySelectorAll('[data-folder]').forEach((b) => b.addEventListener('click', () => call('openFolder', { path: b.dataset.folder })));
  wrap.querySelectorAll('[data-del]').forEach((b) => b.addEventListener('click', async () => {
    const id = b.dataset.del;
    if (!confirm('删除模型文件 "' + id + '" ？这会从磁盘上删掉 .gguf（不可恢复）。')) return;
    try { const r = await call('deleteModel', { id }); if (r && r.ok === false) toast(r.message, 'error'); }
    catch (e) { toast(e.message, 'error'); }
  }));
}

/* ---------------- 用量：日 / 月 / 年 ---------------- */

function fmtBytes(n) {
  n = +n || 0;
  if (n < 1024) return n + ' B';
  if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
  if (n < 1073741824) return (n / 1048576).toFixed(1) + ' MB';
  return (n / 1073741824).toFixed(2) + ' GB';
}

function fmtDur(ms) {
  ms = +ms || 0;
  if (!ms) return '—';
  return ms >= 1000 ? (ms / 1000).toFixed(1) + ' 秒' : Math.round(ms) + ' 毫秒';
}

function setUsageTab(t) {
  S.utab = (t === 'month' || t === 'year') ? t : 'day';
  $$('#usageTabs .seg-btn').forEach((b) => b.classList.toggle('on', b.dataset.utab === S.utab));
  renderUsage();
}

function renderUsage() {
  const box = $('#usageBars');
  const totals = $('#usageTotals');
  if (!box || !totals) return;
  const u = S.usage;
  if (!u) {
    totals.textContent = '—';
    box.innerHTML = '';
    renderUsageGlance({}, [], '今天');      // 没数据也要把折叠态刷成「—」，别留着上次的旧数字
    return;
  }

  const tab = S.utab || 'day';
  const pair = { day: ['today', 'days'], month: ['month', 'months'], year: ['year', 'years'] }[tab];
  const t = u[pair[0]] || {};
  const series = u[pair[1]] || [];
  const name = tab === 'day' ? '今天' : tab === 'month' ? '本月' : '今年';

  totals.innerHTML =
    '<span class="ut-big">' + fmtNum(t.req) + '</span><span class="ut-unit">次调用（' + name + '）</span>' +
    '<span class="ut-sep"></span>' +
    '<span class="ut-kv">本地 <b>' + fmtNum(t.local) + '</b></span>' +
    '<span class="ut-kv">外地 <b>' + fmtNum(t.up) + '</b></span>' +
    ((t.fail || 0) > 0 ? '<span class="ut-kv bad">失败 <b>' + fmtNum(t.fail) + '</b></span>' : '') +
    (((t.pt || 0) + (t.ct || 0)) > 0
      ? '<span class="ut-sep"></span><span class="ut-kv">token 输入 <b>' + fmtNum(t.pt) + '</b> / 输出 <b>' + fmtNum(t.ct) + '</b></span>'
      : '') +
    '<span class="ut-sep"></span>' +
    '<span class="ut-kv">平均 <b>' + fmtDur(t.req ? (t.ms || 0) / t.req : 0) + '</b></span>' +
    '<span class="ut-kv">回传 <b>' + fmtBytes(t.bytes) + '</b></span>';

  const max = Math.max(1, ...series.map((d) => +d.req || 0));
  box.innerHTML = series.map((d) => {
    const req = +d.req || 0;
    const h = Math.round((req / max) * 100);
    const hl = req ? Math.round(((+d.local || 0) / req) * 100) : 0;
    const tip = d.k + '：' + req + ' 次（本地 ' + (+d.local || 0) + ' / 外地 ' + (+d.up || 0) + '）' +
      (((+d.pt || 0) + (+d.ct || 0)) > 0 ? ' · token 输入 ' + d.pt + ' / 输出 ' + d.ct : '') +
      ' · 回传 ' + fmtBytes(d.bytes);
    return '<div class="ubar" title="' + esc(tip) + '">' +
      '<div class="ubar-v">' + (req ? req : '') + '</div>' +
      '<div class="ubar-track"><div class="ubar-fill" style="height:' + h + '%">' +
        '<i class="ubar-loc" style="height:' + hl + '%"></i>' +
        '<i class="ubar-up" style="height:' + (req ? 100 - hl : 0) + '%"></i>' +
      '</div></div>' +
      '<div class="ubar-l">' + esc(d.l || d.k) + '</div></div>';
  }).join('');

  const tt = u.total || {};
  $('#usageHint').textContent = '累计 ' + fmtNum(tt.req) + ' 次（本地 ' + fmtNum(tt.local) + ' / 外地 ' + fmtNum(tt.up) + '）' +
    ((((tt.pt || 0) + (tt.ct || 0)) > 0) ? ' · token 输入 ' + fmtNum(tt.pt) + ' / 输出 ' + fmtNum(tt.ct) : '') +
    ' · 回传 ' + fmtBytes(tt.bytes) +
    '　｜　只算 chat/completions 这类真干活的请求（模型列表、探活不算），数字存在 %APPDATA%\\CNM\\usage.json；柱子颜色：下=本地、上=外地。';

  renderUsageGlance(t, series, name);
}

/** 折叠态仪表盘：一行关键数字 + 右侧迷你火花线（点任意处展开） */
function renderUsageGlance(t, series, name) {
  const set = (id, v) => { const e = $('#' + id); if (e) e.textContent = v; };
  set('ugReq', fmtNum(t.req));
  const lab = $('#ugReqLab'); if (lab) lab.textContent = '次调用（' + (name || '今天') + '）';
  set('ugLocal', fmtNum(t.local));
  set('ugUp', fmtNum(t.up));
  const failBox = $('#ugFailBox');
  if (failBox) {
    const f = t.fail || 0;
    failBox.style.display = f > 0 ? '' : 'none';
    if (f > 0) set('ugFail', fmtNum(f));
  }
  const tok = (t.pt || 0) + (t.ct || 0);
  set('ugTok', tok > 0 ? fmtNum(tok) : '—');
  set('ugAvg', t.req ? fmtDur((t.ms || 0) / t.req) : '—');

  // 火花线：近 48 根，按最大值归一；没请求的那天给 2px 基线
  const spark = $('#ugSpark');
  if (spark) {
    const arr = (series || []).slice(-48);
    const mx = Math.max(1, ...arr.map((d) => +d.req || 0));
    spark.innerHTML = arr.map((d) => {
      const req = +d.req || 0;
      const h = req ? Math.max(8, Math.round((req / mx) * 100)) : 0;
      const title = d.k + '：' + req + ' 次（本地 ' + (+d.local || 0) + ' / 外地 ' + (+d.up || 0) + '）';
      return '<div class="ug-b' + (req ? '' : ' zero') + '" style="height:' + (req ? h + '%' : '2px') + '" title="' + esc(title) + '"></div>';
    }).join('');
  }
}

/** 折叠 / 展开用量仪表盘（状态记 localStorage，下次打开保持） */
function setUsageExpanded(on) {
  const p = $('#usagePanel');
  if (!p) return;
  p.classList.toggle('expanded', !!on);
  p.classList.toggle('collapsed', !on);
  const b = $('#btnUsageToggle');
  if (b) b.textContent = on ? '收起 ▴' : '展开 ▾';
  try { localStorage.setItem('cnm.usageExpanded', on ? '1' : '0'); } catch (e) { }
}
function toggleUsage() {
  const p = $('#usagePanel');
  if (p) setUsageExpanded(!p.classList.contains('expanded'));
}

/* ---------------- 模型库 → 外地模型（云端，来自守望者里的上游） ---------------- */

/** 现在接入的是哪个外地模型（所有 agent 都转给它） */
function cloudActive() {
  const s = S.settings || {};
  return { up: s.upstreamDefault || '', model: s.upstreamModel || '' };
}

/** 本地 / 外地 两块来回切（默认本地）。两块各有各的工具条：本地是量化，外地是厂家。 */
function setModelTab(t) {
  S.mtab = t === 'remote' ? 'remote' : 'local';
  const local = $('#paneLocal');
  const remote = $('#paneRemote');
  const toolL = $('#toolLocal');
  const toolR = $('#toolRemote');
  if (local) local.classList.toggle('hidden', S.mtab !== 'local');
  if (remote) remote.classList.toggle('hidden', S.mtab !== 'remote');
  if (toolL) toolL.classList.toggle('hidden', S.mtab !== 'local');
  if (toolR) toolR.classList.toggle('hidden', S.mtab !== 'remote');
  $$('.modelTabs .seg-btn').forEach((b) => b.classList.toggle('on', b.dataset.mtab === S.mtab));
  if (S.mtab === 'remote') renderRemote();
}

/** 这批外地模型里出现了哪些厂家（谁家的），按数量从多到少排 */
function labsPresent(rows) {
  const cnt = {};
  rows.forEach((r) => { const n = labOf(r.m).name || '其他'; cnt[n] = (cnt[n] || 0) + 1; });
  return Object.keys(cnt)
    .map((n) => ({ n, c: cnt[n] }))
    .sort((a, b) => b.c - a.c || a.n.localeCompare(b.n, 'zh'));
}

function renderRemote() {
  const box = $('#remoteCards');
  if (!box) return;
  const ups = (S.settings && S.settings.upstreams) || [];
  const act = cloudActive();
  const qIn = $('#upFilter');
  const q = (qIn ? qIn.value : '').trim().toLowerCase();

  const rows = [];
  ups.forEach((u) => {
    if (u.enabled === false) return;
    (u.modelList || []).forEach((m) => rows.push({ m: String(m), up: u.name, err: u.error || '' }));
  });
  const recent = {};
  (S.reqs || []).forEach((r) => { const m = r.upModel || r.model; if (m) recent[m] = 1; });
  rows.forEach((r) => { r.recent = !!recent[r.m]; });

  // 厂家下拉：从当前这批模型现算（认不出的算「其他」），保留已选
  const labSel = $('#upLab');
  if (labSel) {
    const labs = labsPresent(rows);
    labSel.innerHTML = '<option value="">全部厂家</option>' +
      labs.map((x) => `<option value="${esc(x.n)}">${esc(x.n)}（${x.c}）</option>`).join('');
    if (!labs.some((x) => x.n === S.upLab)) S.upLab = '';
    labSel.value = S.upLab;
  }

  const list = rows.filter((r) => {
    const lab = labOf(r.m).name || '其他';
    if (S.upLab && lab !== S.upLab) return false;
    if (!q) return true;
    return (r.m + ' ' + r.up + ' ' + lab).toLowerCase().includes(q);
  });

  const sort = S.upSort || 'name';
  if (sort === 'lab') list.sort((a, b) => (labOf(a.m).name || '').localeCompare(labOf(b.m).name || '', 'zh') || String(a.m).localeCompare(String(b.m), 'zh'));
  else if (sort === 'recent') list.sort((a, b) => (b.recent ? 1 : 0) - (a.recent ? 1 : 0) || String(a.m).localeCompare(String(b.m), 'zh'));
  else if (sort === 'speed') list.sort((a, b) => pingMs(a.up, a.m) - pingMs(b.up, b.m) || String(a.m).localeCompare(String(b.m), 'zh'));
  else list.sort((a, b) => String(a.m).localeCompare(String(b.m), 'zh'));

  const connected = ups.filter((u) => u.enabled !== false && (u.models || 0) > 0);
  const bad = ups.filter((u) => u.enabled !== false && u.error);

  const sum = $('#remoteSummary');
  if (sum) {
    if (ups.length === 0) sum.textContent = '还没有上游 —— 先在「服务与日志 → 守望者」填地址和 Key';
    else sum.textContent = (act.model ? '已接入 ' + act.model + (act.up ? '（' + act.up + '）' : '') + ' · 名字对不上的请求兜底用它 · ' : '') +
      rows.length + ' 个外地模型' + (labSel && S.upLab ? '（当前显示 ' + list.length + ' 个）' : '') +
      (connected.length ? '' : '（还没连上，点上面「重新连接」）') +
      (bad.length ? ' · ' + bad.length + ' 个连不上' : '');
  }

  const emp = $('#remoteEmpty');
  if (emp) {
    emp.style.display = list.length ? 'none' : '';
    if (!list.length) {
      emp.textContent = ups.length === 0
        ? '还没有外地模型 —— 先到「服务与日志 → 守望者」把中转站的地址和 Key 填上（只用填这两样）。'
        : (bad.length ? '上游连不上：' + bad[0].error
          : ((q || S.upLab) ? '这个条件下没有外地模型，把过滤清掉试试。' : '这个上游没报出模型，点上面「重新连接」再拉一次。'));
    }
  }

  box.innerHTML = list.map((r) => {
    const on = act.up === r.up && act.model === r.m;
    const ping = pingOf(r.up, r.m);
    const badge = ping
      ? (ping.ok
          ? `<span class="badge b5 ${ping.firstMs < 1000 ? '' : ping.firstMs < 3000 ? 'mid' : 'slow'}" title="首字延迟（实测发一次最小请求）">${fmtMs(ping.firstMs)}</span>`
          : `<span class="badge" title="${esc(ping.error || '测速失败')}">测速失败</span>`)
      : '';
    return `
    <div class="card ${on ? 'loaded' : ''}">
      <div class="card-top">
        ${logoTile(r.m)}
        <div style="min-width:0">
          <div class="card-name">${esc(r.m)}</div>
          <div class="card-repo">外地 · ${esc(r.up)}</div>
        </div>
      </div>
      <div class="badges">
        <span class="badge b2">云端</span>
        <span class="badge">${esc(r.up)}</span>
        ${badge}
        ${r.recent ? '<span class="badge b4">用过</span>' : ''}
        ${on ? '<span class="badge b3">已接入</span>' : ''}
      </div>
      <div class="card-actions">
        <button class="btn tiny ${on ? '' : 'primary'}" data-use="${esc(r.up)}|${esc(r.m)}">${on ? '取消接入' : '接入'}</button>
        <button class="btn tiny" data-try="${esc(r.up)}|${esc(r.m)}">试一句</button>
        <button class="btn tiny ghost" data-ping="${esc(r.up)}|${esc(r.m)}">测速</button>
        <button class="btn tiny ghost" data-copyname="${esc(r.m)}">复制名字</button>
      </div>
    </div>`;
  }).join('');
  ensureLogos(list.map((r) => r.m));      // 云端模型也认「谁家的」：gpt-* → OpenAI、claude-* → Claude…

  box.querySelectorAll('[data-use]').forEach((b) => b.addEventListener('click', async () => {
    const p = b.dataset.use.split('|');
    const isOn = cloudActive().up === p[0] && cloudActive().model === p[1];
    try {
      const s = await call('useUpstreamModel', isOn ? { upstream: '', model: '' } : { upstream: p[0], model: p[1] });
      if (s) { S.settings = s; S.upShown = null; renderWatch({ settings: s }); }
      renderRemote(); renderModels();
      toast(isOn ? '已取消接入（名字对不上就报错）' : '已接入 ' + p[1] + '：名字对不上的请求兜底用它（点名别的外地模型照样用它自己）', 'ok');
    } catch (e) { toast(e.message, 'error'); }
  }));

  box.querySelectorAll('[data-ping]').forEach((b) => b.addEventListener('click', async () => {
    const p = b.dataset.ping.split('|');
    const u = ups.find((x) => x.name === p[0]) || {};
    b.disabled = true;
    const old = b.textContent;
    b.textContent = '测速中…';
    try {
      // 界面上正在编辑的地址/Key 优先（还没保存也能测）
      const r = await call('pingModel', { upstream: p[0], model: p[1], baseUrl: u.baseUrl || '', apiKey: u.apiKey || '', proxy: u.proxy || '' });
      setPing(p[0], p[1], r || { ok: false, error: '没结果' });
      if (r && r.ok) toast(p[1] + '：首字 ' + r.firstMs + ' ms', 'ok');
      else toast(p[1] + ' 测速失败：' + ((r && r.error) || '未知'), 'error');
      renderRemote();
    } catch (e) { toast(e.message, 'error'); }
    finally { b.disabled = false; b.textContent = old; }
  }));

  box.querySelectorAll('[data-try]').forEach((b) => b.addEventListener('click', async () => {
    const p = b.dataset.try.split('|');
    const u = ups.find((x) => x.name === p[0]) || {};
    toast('正在让「' + p[1] + '」说一句…');
    try {
      const r = await call('testUpstreamChat', {
        name: p[0], baseUrl: u.baseUrl || '', apiKey: u.apiKey || '', proxy: u.proxy || '', model: p[1]
      });
      const d = String((r && r.detail) || '').replace(/\s+/g, ' ');
      toast(r && r.ok ? '通了：' + d.slice(0, 90) : '没通：' + d.slice(0, 130), r && r.ok ? 'ok' : 'error');
    } catch (e) { toast(e.message, 'error'); }
  }));

  box.querySelectorAll('[data-copyname]').forEach((b) => b.addEventListener('click', () => copyText(b.dataset.copyname)));
}

/* ---------------- 在线浏览 ---------------- */
function renderHfResults(arr) {
  if (arr) S.hfResults = arr;
  const box = $('#hfResults');
  const all = S.hfResults || [];
  const cnt = $('#hfCount');

  if (!all.length) {
    box.innerHTML = '<div class="empty">' + (S.hfSearched ? '没搜到，换个词试试' : '输入关键词搜索，源：' + esc(hfHost()) + '（在「设置 → 模型源」可改）') + '</div>';
    if (cnt) cnt.textContent = '';
    return;
  }

  const q = ($('#hfFilter').value || '').trim().toLowerCase();
  const list = all.filter((r) => !q ||
    String(r.id).toLowerCase().includes(q) ||
    String(labOf(r.id).name || '').toLowerCase().includes(q));   // 也认品牌名：输「阿里」「Meta」都能筛
  if (cnt) cnt.textContent = `${list.length} / ${all.length} 个仓库`;
  if (!list.length) { box.innerHTML = '<div class="empty">这个筛选词下没有仓库</div>'; return; }

  box.innerHTML = list.map((r) => `
    <div class="list-item hpick${S.hfRepo === r.id ? ' on' : ''}" data-repo="${esc(r.id)}">
      ${logoTile(r.id)}
      <div class="li-main grow">
        <div class="li-id">${esc(r.id)}</div>
        <div class="li-sub">↓ ${fmtNum(r.downloads)} · ♥ ${fmtNum(r.likes)}${r.pipeline ? ' · ' + esc(r.pipeline) : ''}</div>
      </div>
    </div>`).join('');
  ensureLogos(list.map((r) => r.id));     // 去要真 logo（Qwen / DeepSeek / unsloth…），拿到会自己重画
  box.querySelectorAll('[data-repo]').forEach((el) => el.addEventListener('click', () => openRepo(el.dataset.repo)));
}

async function openRepo(repo) {
  const mySeq = ++S.hfSeq;          // 手快点了两个仓库时，只认最后点的那次（否则文件列表和仓库会错位）
  S.hfRepo = repo;
  S.hfFiles = []; S.hfGroups = null; S.hfSel = {};
  S.hfHideMm = false; S.hfQuant = ''; S.hfMaxSize = 0;
  $$('#hfResults .hpick').forEach((e) => e.classList.toggle('on', e.dataset.repo === repo));
  $('#hfFiles').innerHTML = '<div class="empty">正在读取 ' + esc(repo) + ' …</div>';
  try {
    const r = await call('hfFiles', { repo });
    if (mySeq !== S.hfSeq) return;                // 已经有更新的点击了，这次结果作废
    if (!r || r.ok === false) { $('#hfFiles').innerHTML = '<div class="empty">' + esc((r && r.message) || '读取失败') + '</div>'; return; }
    S.hfFiles = r.files || [];
    S.hfGroups = groupHfFiles();
    applyRec(false);        // 先按推荐勾好，省得自己翻一堆量化
    renderHfFiles();
  } catch (e) { if (mySeq === S.hfSeq) $('#hfFiles').innerHTML = '<div class="empty">' + esc(e.message) + '</div>'; }
}

/** 把文件按量化分组（mmproj 单独一组），下载按组勾最省事 */
function groupHfFiles() {
  const map = new Map();
  S.hfFiles.forEach((f, i) => {
    const key = f.mmproj ? '__mmproj' : (f.quant || '__other');
    if (!map.has(key)) {
      map.set(key, {
        key,
        label: f.mmproj ? '视觉编码器 mmproj' : (f.quant || '其他'),
        mm: !!f.mmproj,
        items: [],
        size: 0
      });
    }
    const g = map.get(key);
    g.items.push(i);
    g.size += +f.size || 0;
  });
  const arr = Array.from(map.values());
  arr.forEach((g) => { g.tier = g.mm ? { key: 'mm', label: '按需' } : quantTier(g.label); });
  return arr;
}

/** 推荐哪个量化：优先质量/体积平衡的 Q4_K_M 一线；给了体积上限就只挑装得下的 */
function recommendGroup(groups, limitBytes) {
  const main = (groups || []).filter((g) => !g.mm);
  if (!main.length) return null;
  const fits = (g) => !limitBytes || g.size <= limitBytes;
  for (const want of LADDER) {
    const g = main.find((x) => String(x.label).toUpperCase() === want && fits(x));
    if (g) return g;
  }
  const ok = main.filter(fits);
  const pool = ok.length ? ok : main;
  return pool.slice().sort((a, b) => Math.abs(a.size - 8 * GB) - Math.abs(b.size - 8 * GB))[0];
}

function applyRec(notify) {
  // 推荐永远在“当前筛出来的范围”里挑：你限了体积或指定了量化，它就跟着变
  const cand = (S.hfGroups || []).filter((g) => (!S.hfQuant || g.label === S.hfQuant) && (!S.hfHideMm || !g.mm));
  const rec = recommendGroup(cand, S.hfMaxSize * GB);
  S.hfSel = {};
  if (rec) rec.items.forEach((i) => { S.hfSel[i] = true; });
  if (notify) {
    if (rec) toast('已选：' + rec.label + ' · ' + fmtSize(rec.size), 'ok');
    else toast('这个仓库里没有可推荐的量化', 'error');
  }
}

function selectedPaths() {
  return Object.keys(S.hfSel)
    .map((k) => +k)
    .sort((a, b) => a - b)
    .map((i) => S.hfFiles[i] && S.hfFiles[i].path)
    .filter(Boolean);
}

async function dlSelected() {
  const files = selectedPaths();
  if (!files.length) { toast('先勾几个文件（点“推荐选择”最快）', 'error'); return; }
  try {
    const r = await call('download', { repo: S.hfRepo, files });
    if (r && r.ok === false) toast(r.message, 'error');
    else toast(`已加入下载队列：${(r && r.queued) || files.length} 个文件`, 'ok');
  } catch (e) { toast(e.message, 'error'); }
}

function groupHtml(g, rec) {
  const all = g.items.every((i) => S.hfSel[i]);
  const some = g.items.some((i) => S.hfSel[i]);
  const isRec = rec && g.key === rec.key;
  const vram = g.mm ? 0 : g.size * 1.06 / GB + 0.5;
  const t = g.tier || {};
  return `
  <div class="grp${g.mm ? ' mm' : ''}${isRec ? ' rec' : ''}${all ? ' sel' : ''}" data-g="${esc(g.key)}">
    <div class="grp-row">
      <span class="bx${all ? ' on' : some ? ' part' : ''}" data-g="${esc(g.key)}"></span>
      <span class="qname">${esc(g.label)}</span>
      ${isRec ? '<span class="badge rec">推荐</span>' : (t.label ? `<span class="badge t-${esc(t.key)}">${esc(t.label)}</span>` : '')}
      <span class="grow"></span>
      <span class="gmeta">${g.items.length > 1 ? g.items.length + ' 个分片 · ' : ''}${fmtSize(g.size)}${vram ? ' · 显存约 ' + vram.toFixed(1) + ' GB' : ''}</span>
    </div>
    ${g.items.length > 1 ? `<div class="grp-files">${g.items.map((i) => `
      <div class="fitem">
        <span class="bx small${S.hfSel[i] ? ' on' : ''}" data-i="${i}"></span>
        <span class="fpath">${esc(String(S.hfFiles[i].path).split('/').pop())}</span>
        <span class="grow"></span>
        <span class="gmeta">${esc(S.hfFiles[i].sizeText)}</span>
      </div>`).join('')}</div>` : ''}
  </div>`;
}

function renderHfFiles() {
  const box = $('#hfFiles');
  if (!S.hfFiles.length) { box.innerHTML = '<div class="empty">这个仓库里没有 .gguf</div>'; return; }

  const groups = S.hfGroups || (S.hfGroups = groupHfFiles());
  const limitGb = S.hfMaxSize || 0;        // 用户在下拉里选的是 GB
  const limit = limitGb * GB;              // 比较要换成字节，不然整个列表都会被筛空
  const cand = groups.filter((g) => (!S.hfHideMm || !g.mm) && (!S.hfQuant || g.label === S.hfQuant));
  const visible = cand.filter((g) => (!limit || g.size <= limit));
  const rec = recommendGroup(cand, limit);
  const total = groups.reduce((a, g) => a + g.size, 0);
  const sel = Object.keys(S.hfSel);
  const selSize = sel.reduce((a, k) => a + (+S.hfFiles[+k].size || 0), 0);

  const sizeOpts = [[0, '任意大小'], [8, '只看 ≤ 8 GB'], [16, '≤ 16 GB'], [24, '≤ 24 GB'], [32, '≤ 32 GB'], [48, '≤ 48 GB']]
    .map(([gb, label]) => `<option value="${gb}"${gb === limitGb ? ' selected' : ''}>${label}</option>`).join('');

  box.innerHTML = `
  <div class="fhead">
    <div class="frow">
      ${logoTile(S.hfRepo, 'lg')}
      <b class="fname">${esc(S.hfRepo)}</b>
      <span class="gmeta">${groups.filter((g) => !g.mm).length} 个量化 · 全部 ${fmtSize(total)}</span>
      <span class="grow"></span>
      <button class="btn tiny" id="hfRec">推荐选择</button>
      <button class="btn tiny primary" id="hfDl">下载勾选的 (${sel.length})</button>
    </div>
    <div class="frow">
      <select class="input tiny" id="hfQuantSel" style="max-width:150px">
        <option value="">全部量化</option>
        ${groups.filter((g) => !g.mm).map((g) => `<option value="${esc(g.label)}"${g.label === S.hfQuant ? ' selected' : ''}>${esc(g.label)}</option>`).join('')}
      </select>
      <select class="input tiny" id="hfSizeSel" style="max-width:150px">${sizeOpts}</select>
      <label class="chk" id="hfHideLbl"><span class="bx small${S.hfHideMm ? ' on' : ''}"></span>隐藏视觉编码器</label>
      <span class="grow"></span>
      <button class="btn tiny ghost" id="hfAll">全选</button>
      <button class="btn tiny ghost" id="hfNone">清空</button>
    </div>
    <div class="selbar">已选 <b>${sel.length}</b> 个文件 · ${fmtSize(selSize)}${limitGb ? ` · 只看 ≤ ${limitGb} GB 的量化` : ''}</div>
  </div>
  ${visible.length ? visible.map((g) => groupHtml(g, rec)).join('') : '<div class="empty">筛选后没有匹配的量化，放宽容积筛选看看</div>'}`;

  const hit = (sel2, fn) => box.querySelectorAll(sel2).forEach((el) => el.addEventListener('click', fn));
  ensureLogos([S.hfRepo]);              // 这个仓库是谁家的，标题那行挂真 logo
  hit('.bx[data-g]', (ev) => { ev.stopPropagation(); toggleGroup(ev.currentTarget.dataset.g); });
  hit('.bx[data-i]', (ev) => {
    ev.stopPropagation();
    const i = +ev.currentTarget.dataset.i;
    if (S.hfSel[i]) delete S.hfSel[i]; else S.hfSel[i] = true;
    renderHfFiles();
  });
  box.querySelectorAll('.grp-row').forEach((row) => row.addEventListener('click', () => toggleGroup(row.parentElement.dataset.g)));

  const on = (id, fn) => { const e = box.querySelector('#' + id); if (e) e.addEventListener('click', fn); };
  on('hfAll', () => { visible.forEach((g) => g.items.forEach((i) => { S.hfSel[i] = true; })); renderHfFiles(); });
  on('hfNone', () => { S.hfSel = {}; renderHfFiles(); });
  on('hfRec', () => { applyRec(true); renderHfFiles(); });
  on('hfDl', dlSelected);
  on('hfHideLbl', () => { S.hfHideMm = !S.hfHideMm; renderHfFiles(); });

  const qs = box.querySelector('#hfQuantSel');
  if (qs) qs.addEventListener('change', () => { S.hfQuant = qs.value; applyRec(false); renderHfFiles(); });
  const ss = box.querySelector('#hfSizeSel');
  if (ss) ss.addEventListener('change', () => { S.hfMaxSize = +ss.value || 0; applyRec(false); renderHfFiles(); });
}

function toggleGroup(key) {
  const g = (S.hfGroups || []).find((x) => x.key === key);
  if (!g) return;
  const all = g.items.every((i) => S.hfSel[i]);
  g.items.forEach((i) => { if (all) delete S.hfSel[i]; else S.hfSel[i] = true; });
  renderHfFiles();
}

function renderJobs() {
  const box = $('#dlList');
  if (!S.jobs.length) { box.innerHTML = '<div class="empty small">没有下载任务</div>'; return; }
  box.innerHTML = S.jobs.map((j) => `
    <div class="dl">
      <div class="dl-head">
        <div class="dl-name grow">${esc(j.fileName)} <span class="muted">(${esc(j.repo)})</span></div>
        <div class="muted small">${j.status === 'running' ? j.percent + '%' : j.status}</div>
        ${j.status === 'running' || j.status === 'queued' ? `<button class="btn tiny" data-cancel="${esc(j.key)}">取消</button>` : ''}
      </div>
      <div class="bar"><i style="width:${j.percent}%"></i></div>
      <div class="dl-sub">
        <span>${fmtSize(j.done)} / ${fmtSize(j.total)}</span>
        ${j.speed ? '<span>' + j.speed.toFixed(1) + ' MB/s</span>' : ''}
        ${j.error ? '<span style="color:var(--err)">' + esc(j.error) + '</span>' : ''}
        <span class="grow"></span>
        <span>${esc(j.dest)}</span>
      </div>
    </div>`).join('');
  box.querySelectorAll('[data-cancel]').forEach((b) => b.addEventListener('click', () => call('cancelDownload', { key: b.dataset.cancel })));
}

function renderReqs() {
  const tb = $('#reqTable tbody');
  tb.innerHTML = S.reqs.slice(0, 200).map((r) => `
    <tr>
      <td>${esc(r.time)}</td>
      <td>${esc(r.remote)}</td>
      <td>${esc(r.path)}</td>
      <td>${esc(r.model)}${r.upstream ? ' <span class="badge b2">转 ' + esc(r.upstream) + (r.upModel && r.upModel !== r.model ? ' · ' + esc(r.upModel) : '') + '</span>' : ''}</td>
      <td style="color:${r.status >= 200 && r.status < 300 ? 'var(--ok)' : 'var(--err)'}">${r.status}</td>
      <td>${r.ms} ms${r.bytes ? ' · ' + fmtSize(r.bytes) : ''}</td>
    </tr>`).join('');
}

/* ---------------- 日志 ---------------- */
const logs = [];
function addLog(source, line) {
  logs.push({ source, line });
  if (logs.length > 800) logs.shift();
  const box = $('#logBox');
  if (box) {
    box.textContent += `[${source}] ${line}\n`;
    box.scrollTop = box.scrollHeight;
  }
}

/* ---------------- 绑定 ---------------- */
function bind() {
  /* 侧栏折叠：点击按钮切换，状态记 localStorage，下次打开保持 */
  (function initSide() {
    const side = $('#side'), btn = $('#btnSide');
    if (!side || !btn) return;
    if (localStorage.getItem('cnm.sideCollapsed') === '1') side.classList.add('collapsed');
    const sync = () => {
      const on = side.classList.contains('collapsed');
      btn.textContent = on ? '' : '« 收起侧栏';   // 折叠态的 » 由 CSS ::before 画
      btn.title = on ? '展开侧栏' : '折叠侧栏';
      btn.setAttribute('aria-label', btn.title);
    };
    sync();
    btn.addEventListener('click', () => {
      side.classList.toggle('collapsed');
      localStorage.setItem('cnm.sideCollapsed', side.classList.contains('collapsed') ? '1' : '0');
      sync();
    });
  })();

  /* 无系统标题栏：顶栏拖动（原生消息）+ 右上角窗口按钮 */
  const winCall = (cmd) => { try { call(cmd).catch(() => { }); } catch (e) { } };
  $('#btnMin')?.addEventListener('click', () => winCall('windowMinimize'));
  $('#btnMax')?.addEventListener('click', () => winCall('windowMaximize'));
  $('#btnClose')?.addEventListener('click', () => winCall('windowClose'));

  $('#btnStart').addEventListener('click', async () => { const r = await call('startServer'); if (r && r.error) toast(r.error, 'error'); });
  $('#btnStop').addEventListener('click', () => call('stopServer'));
  $('#btnTheme').addEventListener('click', () => {
    const t = document.documentElement.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
    setTheme(t);
  });
  $('#setTheme').addEventListener('change', () => setTheme($('#setTheme').value));

  $('#btnPickIcon').addEventListener('click', () => $('#iconFile').click());
  $('#iconFile').addEventListener('change', async (e) => {
    const f = e.target.files && e.target.files[0];
    e.target.value = '';
    if (!f) return;
    if (f.size > 8 * 1024 * 1024) { toast('图片太大了（超过 8MB）', 'error'); return; }
    let data;
    try {
      data = await new Promise((res, rej) => {
        const r = new FileReader();
        r.onload = () => res(r.result);
        r.onerror = () => rej(new Error('读文件失败'));
        r.readAsDataURL(f);
      });
    } catch (err) { toast(err.message, 'error'); return; }
    try {
      const r = await call('setIcon', { data });
      if (!r || r.ok === false) { toast(r.message || '设置失败', 'error'); return; }
      applyLogo(true, r.iconVersion);
      toast('图标已换（窗口和托盘也变了）', 'ok');
    } catch (err) { toast(err.message, 'error'); }
  });
  $('#btnResetIcon').addEventListener('click', async () => {
    try {
      await call('clearIcon');
      applyLogo(false, 0);
      toast('已恢复默认图标', 'ok');
    } catch (e) { toast(e.message, 'error'); }
  });

  $('#btnRescan').addEventListener('click', async () => {
    try { const m = await call('scan'); S.models = m || []; renderModels(); toast('扫描完成', 'ok'); }
    catch (e) { toast(e.message, 'error'); }
  });
  $('#btnOpenModelsDir').addEventListener('click', () => call('openFolder', { path: (S.settings && S.settings.modelsDir) || '' }));
  $('#modelFilter').addEventListener('input', renderModels);
  $('#modelQuant').addEventListener('change', renderModels);
  $('#modelSort').addEventListener('change', renderModels);
  $('#upFilter').addEventListener('input', renderRemote);
  $('#upLab').addEventListener('change', () => { S.upLab = $('#upLab').value; renderRemote(); });
  $('#upSort').addEventListener('change', () => { S.upSort = $('#upSort').value; renderRemote(); });
  $('#btnUnload').addEventListener('click', async () => { await call('unload'); toast('已停止模型', 'ok'); });

  $('#btnHfSearch').addEventListener('click', doSearch);
  $('#hfQuery').addEventListener('keydown', (e) => { if (e.key === 'Enter') doSearch(); });
  $('#hfFilter').addEventListener('input', () => renderHfResults());
  $('#hfSort').addEventListener('change', () => doSearch());   // 换个排序立刻重拉，不用再点一次搜索
  $('#btnUrlGo').addEventListener('click', async () => {
    const url = $('#urlInput').value.trim();
    if (!url) return;
    try {
      const r = await call('downloadUrl', { url });
      if (!r || r.ok === false) { toast((r && r.message) || '解析失败', 'error'); return; }
      if (r.files) {
        S.hfRepo = r.repo; S.hfFiles = r.files;
        S.hfGroups = groupHfFiles(); S.hfSel = {};
        S.hfHideMm = false; S.hfQuant = ''; S.hfMaxSize = 0;
        applyRec(false);
        renderHfFiles();
        toast('这是个仓库：已按推荐勾好，确认后点“下载勾选的”', 'ok');
      } else toast('已加入下载队列', 'ok');
    } catch (e) { toast(e.message, 'error'); }
  });
  $('#btnClearDl').addEventListener('click', async () => {
    try { await call('clearDownloads'); toast('已清理完成的任务', 'ok'); }
    catch (e) { toast(e.message, 'error'); }
  });
  $('#btnClearLog').addEventListener('click', async () => {
    logs.length = 0;
    $('#logBox').textContent = '';
    // 只清前端是不够的：日志还在往文件里写，重启/刷新就全回来了
    try { await call('clearLog'); toast('日志已清空（界面 + cnm.log / engine.log）', 'ok'); }
    catch (e) { toast('界面清了，但日志文件没清掉：' + e.message, 'error'); }
  });
  $('#btnOpenLogFile').addEventListener('click', async () => {
    try { await call('openLogFile'); } catch (e) { toast(e.message, 'error'); }
  });

  $('#btnNewKey').addEventListener('click', async () => {
    const r = await call('newKey');
    if (r && r.apiKey) {
      // 必须回填：后端已经立即生效了，但输入框还是旧 key ——
      // 用户接着点「保存设置」就会把旧 key 写回去，等于白换一次。
      $('#setKey').value = r.apiKey;
      if (S.settings) S.settings.apiKey = r.apiKey;
      toast('新 Key 已生成并立即生效（已回填，不用再保存）', 'ok');
    }
  });
  $$('[data-copy]').forEach((b) => b.addEventListener('click', () => copyText($('#' + b.dataset.copy).textContent)));

  // 「浏览器打开」：打开的是根路径（一页说明），不是 /v1（那是给程序的）
  $('#btnOpenApi').addEventListener('click', () => {
    const base = String($('#svcBase').textContent || '').replace(/\/v1\/?$/, '');
    if (base.startsWith('http')) call('openUrl', { url: base });
  });

  $('#btnProbeEngine').addEventListener('click', async () => {
    await call('probeEngine');
    toast('已重新检测引擎', 'ok');
    checkEngineUpdate();   // 顺带把「本机装的是哪个后端」也刷新 —— 换过后端这里才准
  });

  // 认显卡：给「该装哪个后端」的建议并自动预选（用户自己改过就不动）
  (async () => {
    try {
      const g = await call('detectGpu');
      const list = (g && g.gpus) || [];
      const names = list.map((x) => x.name + (x.vramMiB ? '（' + (x.vramMiB / 1024).toFixed(0) + ' GB）' : '')).join('、');
      const hint = $('#gpuHint');
      if (hint) hint.textContent = (names ? names + ' → ' : '') + '建议用 ' + (g.recommend || 'vulkan') + '。' + (g.reason || '');
      if (!S.variantTouched && g && g.recommend) { const sel = $('#engineVariant'); if (sel) sel.value = g.recommend; }
    } catch (e) {
      const hint = $('#gpuHint');
      if (hint) hint.textContent = '没认出来（不影响使用，手动挑一个就行）';
    }
  })();
  $('#engineVariant').addEventListener('change', () => { S.variantTouched = true; checkEngineUpdate(); });
  async function checkEngineUpdate() {
    const v = $('#engineVariant').value;
    const out = $('#engineLatest');
    if (!out) return;
    out.textContent = '正在查 llama.cpp 官方最新版本……';
    try {
      const r = await call('engineLatest', { variant: v });
      if (!r || r.ok === false) { out.textContent = (r && r.message) || '查不到（网不通？）'; return; }
      const cur = String(r.current || '').match(/\d{3,}/);
      const lat = String(r.tag || '').match(/\d{3,}/);
      const curN = cur ? Number(cur[0]) : 0, latN = lat ? Number(lat[0]) : 0;
      const inst = String(r.installed || '').trim();          // 本机实际装的后端
      const instName = { vulkan: 'Vulkan', rocm: 'ROCm', cuda: 'CUDA', cpu: 'CPU', generic: '通用', unknown: '' }[inst] || inst;

      const parts = [];
      parts.push('官方最新 ' + r.tag + '（' + v + ' 包约 ' + Math.round(r.sizeMiB) + ' MB）');
      // 「本机」说的是实际装的那个后端，不是下拉里选的 —— 两者可以不一样
      parts.push(instName
        ? '本机装的是 ' + instName + (r.current ? '（' + r.current + '）' : '')
        : (r.current ? '本机 ' + r.current : '本机还没装'));

      if (!curN) parts.push('点「下载并安装引擎」装上');
      else if (latN && curN >= latN) parts.push('已是最新');
      else if (latN) parts.push('有新版，点「下载并安装引擎」会装成 ' + r.tag);
      // 选了 A 但装的是 B：明确提醒，别让人以为已经换了
      if (inst && inst !== 'unknown' && inst !== v) {
        parts.push('⚠ 你选的是 ' + v + '，但本机装的是 ' + instName + ' —— 要换后端请点「下载并安装引擎」');
      }
      out.textContent = parts.join('；');
    } catch (e) { out.textContent = e.message; }
  }
  $('#btnEngineUpdate').addEventListener('click', () => checkEngineUpdate());
  checkEngineUpdate();   // 一进来就顺手查一次，省得用户自己点
  $('#btnInstallEngine').addEventListener('click', async () => {
    const v = $('#engineVariant').value;
    toast('开始下载引擎（慢的话就让它跑，断了再点一次会接着下）…');
    try {
      const r = await call('installEngine', { variant: v });
      if (!r || r.ok === false) toast((r && r.message) || '安装失败', 'error');
      else toast('引擎装好了', 'ok');
    } catch (e) { toast(e.message, 'error'); }
  });
  // 选本地 zip 安装（网慢时最省事：自己下好包，这里直接装）
  $('#btnInstallZip').addEventListener('click', async () => {
    try {
      const r = await call('installEngineZip', {});
      if (r && r.cancelled) return;
      if (!r || r.ok === false) toast((r && r.message) || '安装失败', 'error');
      else toast('已用本地包装好引擎', 'ok');
    } catch (e) { toast(e.message, 'error'); }
  });
  // 常用镜像：点一下填进输入框（还能手改）
  $('#mirrorPreset').addEventListener('change', (e) => {
    if (!e.target.value) return;
    $('#setEngineMirror').value = e.target.value;
    markDirty();
    toast('已填入前缀，记得点保存设置；下载时会先走镜像，不行自动回落直连', 'ok');
  });

  // ---- 守望者（云端上游）----
  $('#btnAddUp').addEventListener('click', () => {
    const n = $$('#upList .up-card').length + 1;
    if ($('#upList').querySelector('.empty')) $('#upList').innerHTML = '';
    // 预填「上次用过的地址 + 代理」：新建一条不用再把中转站地址粘一遍。
    // 优先取界面上最后一张有地址的卡，其次取已保存的配置。
    const last = lastUpstreamDefaults();
    $('#upList').appendChild(upRow({ name: 'up' + n, baseUrl: last.baseUrl, apiKey: '', proxy: last.proxy, enabled: true }));
    S.upDirty = true;
    toast(last.baseUrl
      ? '已带上上次的地址（' + last.baseUrl + '），填 Key 后点「保存上游」'
      : '填完地址和 Key，点「保存上游」（代理那格默认 auto = 自动检测要不要走代理）', 'info');
  });
  $('#btnSaveUp').addEventListener('click', async () => {
    try {
      const s = await call('saveUpstreams', { upstreams: collectUpstreams() });
      if (s) { S.upDirty = false; S.upShown = null; renderWatch({ settings: s }); }
      toast('上游已保存，正在重连…', 'ok');
    } catch (e) { toast(e.message, 'error'); }
  });
  $('#btnRefreshUp').addEventListener('click', async () => {
    if (collectUpstreams().length === 0) { toast('还没有上游', 'error'); return; }
    try {
      // 先存再连：不然连的还是旧地址
      await call('saveUpstreams', { upstreams: collectUpstreams() });
      S.upDirty = false;
      const s = await call('refreshUpstreams');
      if (s) { S.upShown = null; renderWatch({ settings: s }); }
      toast('已重新连接', 'ok');
    } catch (e) { toast(e.message, 'error'); }
  });

  // ---- 模型库：本地 / 外地 两块 ----
  $$('.modelTabs .seg-btn').forEach((b) => b.addEventListener('click', () => setModelTab(b.dataset.mtab)));
  setModelTab(S.mtab);   // 开局把按钮态和工具条对齐（本地条 / 外地条两套）
  $$('#usageTabs .seg-btn').forEach((b) => b.addEventListener('click', () => setUsageTab(b.dataset.utab)));

  /* 用量仪表盘：折叠态默认，点标题/数字区/按钮都能切换 */
  $('#btnUsageToggle').addEventListener('click', (e) => { e.stopPropagation(); toggleUsage(); });
  $('#usageHead').addEventListener('click', (e) => {
    if (e.target.closest('#usageTabs') || e.target.closest('#btnUsageToggle')) return;  // 点分页按钮别折叠
    toggleUsage();
  });
  $('#usageGlance').addEventListener('click', () => toggleUsage());
  $('#btnPingAll').addEventListener('click', async () => {
    const ups = (S.settings && S.settings.upstreams) || [];
    const withModels = ups.filter((u) => u.enabled !== false && (u.models || 0) > 0);
    if (!withModels.length) { toast('还没有连上的上游（点「重新连接」拉一次模型列表）', 'error'); return; }
    const btn = $('#btnPingAll');
    btn.disabled = true;
    const old = btn.textContent;
    btn.textContent = '测速中…';
    let done = 0, total = 0;
    try {
      for (const u of withModels) {
        total += (u.models || 0);
        const r = await call('pingAllUpstreamModels', { upstream: u.name });
        if (r && r.results) {
          r.results.forEach((x) => { setPing(u.name, x.model, x); if (x.ok) done++; });
        }
      }
      renderRemote();
      toast('测速完成：' + done + '/' + total + ' 个模型有结果（下拉选「按速度」排序挑最快的）', done > 0 ? 'ok' : 'error');
    } catch (e) { toast(e.message, 'error'); }
    finally { btn.disabled = false; btn.textContent = old; }
  });
  $('#btnRemoteRefresh').addEventListener('click', async () => {
    if (((S.settings && S.settings.upstreams) || []).length === 0) { toast('还没有上游，先去「服务与日志 → 守望者」填', 'error'); return; }
    toast('正在重新连接上游…');
    try {
      // 守望者那边可能刚改过地址/Key 还没保存 —— 先存再连，别拿旧配置去连
      const dirty = !!S.upDirty;
      if (dirty) { await call('saveUpstreams', { upstreams: collectUpstreams() }); S.upDirty = false; }
      const s = await call('refreshUpstreams');
      if (s) { S.settings = s; S.upShown = null; renderWatch({ settings: s }); }
      renderRemote();
      toast(dirty ? '已保存并重新连接' : '已重新连接', 'ok');
    } catch (e) { toast(e.message, 'error'); }
  });
  $('#btnRemoteToWatch').addEventListener('click', () => {
    const b = $$('.nav-item').find((x) => x.dataset.view === 'service');
    if (b) b.click();
  });

  $$('#view-settings .input, #view-settings input[type=checkbox]').forEach((el) => {
    el.addEventListener('change', markDirty);
    el.addEventListener('input', markDirty);
  });

  // 模型目录：调系统的「选择文件夹」窗口，不用手打路径；选完直接落盘 + 重新扫描
  $('#btnPickModelsDir').addEventListener('click', async () => {
    const dir = await pickDir($('#setModelsDir').value.trim(), '选择模型目录');
    if (!dir) return;
    $('#setModelsDir').value = dir;
    await saveSettings('模型目录已改成 ' + dir + '，正在重新扫描…');
  });
  $('#btnPickExtraDir').addEventListener('click', async () => {
    const dir = await pickDir($('#setModelsDir').value.trim(), '追加一个模型目录');
    if (!dir) return;
    const parts = $('#setExtraDirs').value.split(';').map((s) => s.trim()).filter(Boolean);
    if (!parts.some((p) => p.toLowerCase() === dir.toLowerCase())) parts.push(dir);
    $('#setExtraDirs').value = parts.join(';');
    await saveSettings('附加目录已加上 ' + dir + '，正在重新扫描…');
  });
  $('#btnSave').addEventListener('click', () => saveSettings('设置已保存'));

  /* ---- 快捷键：点进输入框直接按组合键 ---- */
  $$('.hk').forEach((el) => {
    el.addEventListener('keydown', (e) => {
      e.preventDefault();
      const hk = hkFromEvent(e);
      if (hk === null) return;              // 只按了修饰键，等主键
      el.value = hk;
      el.classList.add('dirty');
      S.dirty = true;
    });
    el.addEventListener('blur', () => { el.value = normalizeHk(el.value); });
  });
  $('#btnHkReset').addEventListener('click', () => {
    $('#setHkToggle').value = 'Ctrl+Alt+C';
    $('#setHkNextModel').value = 'Ctrl+Alt+N';
    $('#setHkUnload').value = 'Ctrl+Alt+U';
    S.dirty = true;
    toast('已填回默认组合，点「保存设置」生效', 'info');
  });
  // 试一下：直接调后端动作（跟热键走的是同一条逻辑），验证功能而不是验证按键
  $('#btnHkTestToggle').addEventListener('click', async () => {
    try { await call('toggleWindow'); toast('已执行「呼出/退下」', 'ok'); } catch (e) { toast(e.message, 'error'); }
  });
  $('#btnHkTestNext').addEventListener('click', async () => {
    try {
      const r = await call('cycleModel');
      toast((r && r.message) || '已切换', r && r.ok ? 'ok' : 'error');
    } catch (e) { toast(e.message, 'error'); }
  });
}

/** 有改动没落盘：提醒一句（设置页只有「保存设置」这一键会真正存下来） */
function markDirty() {
  S.dirty = true;
  const el = $('#setDirtyHint');
  if (el) el.textContent = '手改的路径还没保存 —— 记得按最下面的「保存设置」';
}

/** 弹系统「选择文件夹」窗口；用户取消返回 null */
async function pickDir(current, title) {
  try {
    const r = await call('pickFolder', { current: current || '', title: title });
    if (!r || r.ok === false) return null;      // 取消：静静收场，别当错误报
    return r.path || null;
  } catch (e) { toast(e.message, 'error'); return null; }
}

/** 保存设置（「保存设置」和「保存并重新扫描」都走这里） */
async function saveSettings(okText) {
  const settings = collectSettings();
  try {
    const r = await call('saveSettings', { settings });
    S.dirty = false;
    const el = $('#setDirtyHint');
    if (el) el.textContent = '';
    applyTheme(settings.theme);
    toast(okText || '设置已保存', 'ok');
    if (r && r.settings) { S.settings = r.settings; fillSettings(r.settings); }
  } catch (e) { toast(e.message, 'error'); }
}

async function doSearch() {
  const q = $('#hfQuery').value.trim();
  const sort = $('#hfSort').value;
  const mySeq = ++S.hfSeq;          // 后发先到时别用旧结果盖掉新结果
  S.hfSearched = true;
  $('#hfResults').innerHTML = '<div class="empty">搜索中…</div>';
  try {
    const r = await call('hfSearch', { q, sort });
    if (mySeq !== S.hfSeq) return;
    if (!r || r.ok === false) { $('#hfResults').innerHTML = '<div class="empty">' + esc((r && r.message) || '搜索失败') + '</div>'; return; }
    renderHfResults(r.results || []);
  } catch (e) { if (mySeq === S.hfSeq) $('#hfResults').innerHTML = '<div class="empty">' + esc(e.message) + '</div>'; }
}

/* ---------------- 启动 ---------------- */
bind();
// 恢复上次的用量仪表盘展开状态（默认折叠）
try { setUsageExpanded(localStorage.getItem('cnm.usageExpanded') === '1'); } catch (e) { }
// 提示文字里出现的「模型源」跟设置走，别写死
(function () {
  const el = $('#hfHint');
  if (el) el.textContent = '输入关键词搜索，源：' + hfHost() + '（在「设置 → 模型源」可改）';
})();
setModelTab('local');       // 默认打开左边（本地模型）
(async () => {
  try {
    const st = await call('state');
    onState(st);
    const models = await call('models');
    S.models = models || [];
    renderModels();
  } catch (e) { toast('初始化失败：' + e.message, 'error'); }
})();
