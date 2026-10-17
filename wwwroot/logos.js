/* CNM —— 「谁家的」标（本地内置，不联网、不引三方库）
 *
 * 在线浏览 / 模型库 / 外地模型 的每一行、每一张卡左边挂一个厂家色块 ——
 * 一眼分得清 Qwen、Llama、DeepSeek、Gemma、Mistral…，不用去读那串仓库名。
 * 认不出来的用「作者名首字母 + 稳定色相」兜底，至少每家的颜色不一样。
 *
 * 想加一家：往 LABS 里加一条就行（name 显示名 / mark 方块里的字 / bg 底色 / who 作者小写 / re 名字里的特征）。
 */
(function () {
  const LABS = [
    { name: '阿里 · 千问', cn: '千问', mark: 'Qw', bg: 'linear-gradient(135deg,#8A6BFF,#4B3BE0)', who: ['qwen'], re: /qwen|qwq/i, icon: 'qwen', hf: 'Qwen' },
    { name: 'Meta · Llama', cn: 'Llama', mark: 'L', bg: 'linear-gradient(135deg,#2E8CFF,#0866FF)', who: ['meta-llama'], re: /(^|[^a-z])llama/i, icon: 'meta', hf: 'meta-llama' },
    { name: 'DeepSeek · 深度求索', cn: '深度求索', mark: 'DS', bg: 'linear-gradient(135deg,#5B7CFF,#1B36C9)', who: ['deepseek-ai'], re: /deepseek/i, icon: 'deepseek', hf: 'deepseek-ai' },
    { name: '谷歌 · Gemma', cn: 'Gemma', mark: 'G', bg: 'linear-gradient(135deg,#4285F4,#34A853 45%,#FBBC05 72%,#EA4335)', who: ['google'], re: /gemma/i, icon: 'google', hf: 'google' },
    { name: '谷歌 · Gemini', cn: 'Gemini', mark: '✦', bg: 'linear-gradient(135deg,#4285F4,#9B72CB 55%,#D96570)', re: /gemini/i, icon: 'googlegemini', hf: 'google' },
    { name: 'Mistral', cn: 'Mistral', mark: 'M', bg: 'linear-gradient(135deg,#FFD800,#FF8205 55%,#FA500F)', who: ['mistralai'], re: /mistral|mixtral|magistral|devstral|ministral|voxtral/i, icon: 'mistralai', hf: 'mistralai' },
    { name: '微软 · Phi', cn: 'Phi', mark: 'Φ', bg: 'linear-gradient(135deg,#2B9CF0,#0078D4)', who: ['microsoft'], re: /(^|[^a-z])phi[-_ ]?[1-4]/i, icon: 'microsoft', hf: 'microsoft' },
    { name: '智谱 · GLM', cn: '智谱', mark: 'GLM', bg: 'linear-gradient(135deg,#4C6FFF,#1C4ED8)', who: ['thudm', 'zai-org', 'zhipuai'], re: /glm|chatglm|codegeex/i, hf: 'THUDM' },
    { name: '月之暗面 · Kimi', cn: '月之暗面', mark: 'K', bg: 'linear-gradient(135deg,#3A4152,#14161C)', who: ['moonshotai'], re: /kimi|moonshot/i, icon: 'kimi', hf: 'moonshotai' },
    { name: '零一万物 · Yi', cn: '零一万物', mark: '01', bg: 'linear-gradient(135deg,#3B4B66,#0F1B2E)', who: ['01-ai'], re: /(^|[^a-z0-9])yi[-_ ]?(1\.5|34b|9b|6b|vl|chat)/i, hf: '01-ai' },
    { name: 'MiniMax', cn: 'MiniMax', mark: 'MM', bg: 'linear-gradient(135deg,#FF6B81,#E4384C)', who: ['minimaxai'], re: /minimax|abab/i, icon: 'minimax', hf: 'MiniMaxAI' },
    { name: '面壁 · MiniCPM', cn: '面壁', mark: 'CPM', bg: 'linear-gradient(135deg,#FF9F45,#F0561D)', who: ['openbmb'], re: /minicpm/i, hf: 'openbmb' },
    { name: '书生 · InternLM', cn: '书生', mark: 'IL', bg: 'linear-gradient(135deg,#38C7C0,#0E8F9E)', who: ['internlm', 'opengvlab'], re: /internlm|internvl/i, hf: 'internlm' },
    { name: '百川', cn: '百川', mark: '百', bg: 'linear-gradient(135deg,#5C7CFA,#2F4BC7)', who: ['baichuan-inc'], re: /baichuan/i, hf: 'baichuan-inc' },
    { name: '阶跃 · Step', cn: '阶跃', mark: 'Step', bg: 'linear-gradient(135deg,#7B61FF,#3B2FBF)', re: /(^|[^a-z])step[-_ ]?[1-9]/i },
    { name: 'Anthropic · Claude', cn: 'Claude', mark: 'C', bg: 'linear-gradient(135deg,#E08B6A,#C25A32)', re: /claude|(^|[^a-z])(opus|sonnet|haiku)/i, icon: 'claude' },
    { name: 'OpenAI', cn: 'OpenAI', mark: 'AI', bg: 'linear-gradient(135deg,#19C39C,#0E8F76)', who: ['openai'], re: /gpt|chatgpt|(^|[^a-z0-9])o[1-4]([^a-z0-9]|$)/i, icon: 'openai' },
    { name: 'xAI · Grok', cn: 'Grok', mark: 'X', bg: 'linear-gradient(135deg,#5A5A5A,#0B0B0B)', re: /grok/i, hf: 'xai-org' },
    { name: 'Nvidia', cn: '英伟达', mark: 'NV', bg: 'linear-gradient(135deg,#7CC243,#3F8F16)', who: ['nvidia'], re: /nemotron/i, icon: 'nvidia', hf: 'nvidia' },
    { name: 'IBM · Granite', cn: 'IBM', mark: 'Gr', bg: 'linear-gradient(135deg,#5A8DEE,#0F62FE)', re: /granite/i, icon: 'ibm', hf: 'ibm-granite' },
    { name: 'Moondream', cn: 'Moondream', mark: 'md', bg: 'linear-gradient(135deg,#9B7BFF,#6A3DE0)', who: ['vikhyatk'], re: /moondream/i, hf: 'vikhyatk' },
    { name: '嵌入 / 向量', cn: '向量', mark: 'vec', bg: 'linear-gradient(135deg,#4FD1C5,#0F8D86)', re: /(^|[-_/])(bge|gte|e5|nomic|qwen3-embedding|embed)/i },
    { name: 'Stability · 图像', cn: '图像', mark: 'SD', bg: 'linear-gradient(135deg,#C77DFF,#7B2CBF)', re: /stable-?diffusion|sdxl|flux/i, hf: 'stabilityai' }
  ];
  const CACHE = new Map();
  const LOGOS = {};     // key -> data:image/…（从后端那儿要来的真 logo / 真头像）
  const TRIED = {};     // key -> 1（问过一次就别反复联网）
  const KIND = {};      // key -> 'icon'（单色品牌标，界面里染白）| 'avatar'（真头像，原色）

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function hashHue(s) {
    let h = 0;
    for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
    return h % 360;
  }

  /** 认出这是谁家的：先看作者（Org），再看名字里的特征；都不认识就用作者名首字母兜底 */
  function labOf(repo) {
    const id = String(repo || '').trim();
    if (CACHE.has(id)) return CACHE.get(id);
    const slash = id.indexOf('/');
    const author = (slash > 0 ? id.slice(0, slash) : id).toLowerCase();

    let lab = LABS.find((l) => l.who && l.who.indexOf(author) >= 0);
    if (!lab) for (const l of LABS) { if (l.re && l.re.test(id)) { lab = l; break; } }
    if (!lab) {
      const seed = author || id || '?';
      lab = {
        name: slash > 0 ? author : '其他',
        mark: (seed[0] || '?').toUpperCase(),
        bg: 'linear-gradient(135deg,hsl(' + hashHue(seed) + ' 52% 52%),hsl(' + hashHue(seed) + ' 52% 36%))',
        unknown: true
      };
    }
    CACHE.set(id, lab);
    return lab;
  }

  /** 这个名字该问哪张图：有现成品牌标的问图标，没有就问它 HF 的头像；都没有 → null（用首字母） */
  function logoKeyOf(text) {
    const id = String(text || '').trim();
    const lab = labOf(id);
    if (lab.icon) return { key: 'icon:' + lab.icon, kind: 'icon', name: lab.icon };
    if (lab.hf) return { key: 'avatar:' + lab.hf.toLowerCase(), kind: 'avatar', name: lab.hf };
    const slash = id.indexOf('/');
    if (slash > 0) {
      const author = id.slice(0, slash);
      if (/^[A-Za-z0-9._-]{1,60}$/.test(author)) return { key: 'avatar:' + author.toLowerCase(), kind: 'avatar', name: author };
    }
    return null;
  }

  /** 色块 / 真 logo + 下面的中文名。
   *  认得出谁家 → logo + 中文（真 logo 画真 logo，没取到就画首字母）；
   *  认不出 → 只有色块 + 作者名（也不是没信息）。 */
  function logoTile(text, cls) {
    const lab = labOf(text);
    const k = logoKeyOf(text);
    const url = k ? LOGOS[k.key] : null;
    const cn = lab.cn || (lab.unknown ? lab.name : '');
    let tile;
    if (url) {
      const isIcon = KIND[k.key] === 'icon';
      tile = '<span class="lg lg-' + (isIcon ? 'icon' : 'img') + (cls ? ' lg-' + cls : '') + '" style="background:' +
             (isIcon ? lab.bg : '#eef2f8') + '"><img src="' + url + '" alt=""></span>';
    } else {
      tile = '<span class="lg' + (cls ? ' lg-' + cls : '') + '" style="background:' + lab.bg + '">' + esc(lab.mark) + '</span>';
    }
    return '<span class="lgb" title="' + esc(lab.name) + '">' + tile +
           (cn ? '<span class="lgb-cn">' + esc(cn) + '</span>' : '') + '</span>';
  }

  /** 这一屏出现了哪些「谁家的」还没取过 → 问后端要（后端取完会写盘缓存，下次开机不再联网） */
  function ensureLogos(repos) {
    if (typeof call !== 'function') return;
    const items = [];
    (repos || []).forEach((r) => {
      const k = logoKeyOf(r);
      if (!k || LOGOS[k.key] || TRIED[k.key]) return;
      TRIED[k.key] = 1; KIND[k.key] = k.kind;
      items.push({ key: k.key, kind: k.kind, name: k.name });
    });
    if (!items.length) return;
    call('logos', { items: items.slice(0, 40) }).then((r) => {
      const got = (r && r.logos) || {};
      let n = 0;
      Object.keys(got).forEach((key) => { LOGOS[key] = got[key]; n++; });
      if (n > 0) repaintLogos();     // 真 logo 到手，重画一遍
    }).catch(() => { });
  }

  /** 重画那些带 logo 的列表（谁在屏幕上就重画谁） */
  function repaintLogos() {
    try {
      if (typeof S !== 'undefined' && S && S.mtab === 'remote') { if (typeof renderRemote === 'function') renderRemote(); }
      else if (typeof renderModels === 'function') renderModels();
      if (typeof S !== 'undefined' && S && S.hfResults && S.hfResults.length && typeof renderHfResults === 'function') renderHfResults();
      if (typeof S !== 'undefined' && S && S.hfRepo && typeof renderHfFiles === 'function') renderHfFiles();
    } catch (e) { /* 重画失败无所谓，下次渲染自然就对 */ }
  }

  /** 厂家名的小胶囊；认不出来就不显示（免得每行都挂个「未知」） */
  function labChip(repo) {
    const lab = labOf(repo);
    if (lab.unknown) return '';
    return '<span class="labchip">' + esc(lab.name) + '</span>';
  }

  window.labOf = labOf;
  window.logoTile = logoTile;
  window.labChip = labChip;
  window.logoKeyOf = logoKeyOf;
  window.ensureLogos = ensureLogos;
  window.CNM_LOGOS = LOGOS;
})();
