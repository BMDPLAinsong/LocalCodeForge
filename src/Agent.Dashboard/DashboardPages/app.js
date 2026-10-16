
"use strict";
// ── ③ 安全 token（10-08）：CLI 打印的 URL 带 ?token=，此处取出并统一附带 ──
// fetch 全局包装：所有请求自动拼 token query（零逐点改动）；EventSource 只能带 query，单独处理。
const TOKEN = new URLSearchParams(location.search).get("token") || "";
{
  const origFetch = window.fetch.bind(window);
  window.fetch = (input, init) => {
    const u = (typeof input === "string") ? input : input.url;
    const nu = TOKEN ? u + (u.includes("?") ? "&" : "?") + "token=" + encodeURIComponent(TOKEN) : u;
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), 12000);   // 12s 超时：杜绝 fetch 永久 pending
    return origFetch(nu, Object.assign({}, init, { signal: ctl.signal })).finally(() => clearTimeout(timer));
  };
}
// ── 网络失败/超时统一转伪响应：既有 `!r.ok` 分支接管错误展示（消除"加载中…"永久卡死）──
async function wrapFetch(url) {
  try { return await fetch(url); }
  catch (err) { return { ok: false, status: (err && err.name === "AbortError") ? "连接超时" : "连接失败" }; }
}
if (!TOKEN) {
  const s = document.getElementById("sub");
  if (s) s.textContent = "未授权：URL 缺少 token（请从 CLI 打印的完整 URL 打开）";
}
const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, c => ({ "&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;" }[c]));
const cut = (s, n) => { s = String(s ?? ""); return s.length > n ? s.slice(0, n) + "…" : s; };
const fmtMs = (ms) => ms >= 1000 ? (ms / 1000).toFixed(1) + "s" : ms + "ms";
// ── C 微可视化（零依赖）：分段条（堆叠占比 + 图例）──
// parts: [{ v, cls, label, fmt?, keep? }]；cls ∈ b-green/b-red/b-yellow/b-blue/b-cyan/b-dim
function segBar(parts, opts) {
  const o = opts || {};
  const total = parts.reduce((s, p) => s + (p.v > 0 ? p.v : 0), 0);
  if (!total) return "";
  let h = `<div class="seg">`;
  for (const p of parts) if (p.v > 0) h += `<i class="${p.cls}" style="width:${(p.v / total * 100).toFixed(2)}%"></i>`;
  h += `</div>`;
  if (o.legend === false) return h;
  h += `<div class="segl">` + parts.filter(p => p.v > 0 || p.keep)
    .map(p => `<span><span class="sw ${p.sw || "d"}"></span>${p.label}${p.fmt != null ? ` <b>${p.fmt}</b>` : ""}</span>`).join("") + `</div>`;
  return h;
}

// ── 事件行渲染：单一渲染函数（10-07 UI 层 #2 抽取）──
// SSE 实时流与任务详情回放共用——两种数据源同一视觉（消双路径漂移）。
function renderEventLine(evt) {
  const cls = "ev " + (evt.type === "finalAnswer" ? "final" : evt.type === "error" ? "error" : evt.type);
  const text = evt.type === "toolCall"
    ? evt.name + " " + cut(JSON.stringify(evt.args), 200)
    : evt.type === "toolResultEvent"
    ? evt.name + (evt.success ? " [ok] " : " [fail] ") + cut(evt.output, 300)
    : evt.type === "routing" ? evt.serviceId + " ← " + evt.tier + (evt.intent ? " [" + evt.intent + "]" : "") + " · " + cut(evt.reason, 120)
    : evt.type === "planGenerated" ? "「" + cut(evt.summary, 120) + "」 " + (evt.filePaths || []).length + " files"
    : evt.type === "fileWritten" ? evt.path + " (" + evt.size + " chars)"
    : evt.type === "finalAnswer" ? cut(evt.text, 500)
    : cut(JSON.stringify(evt), 400);
  const tag = { finalAnswer:"完成", error:"错误", toolCall:"工具调用", toolResultEvent:"工具结果",
                routing:"路由", thinking:"思考", planGenerated:"规划", fileWritten:"写文件" }[evt.type] || evt.type;
  return { cls, tag, text };
}
function appendEventLine(box, evt, bad) {
  const { cls, tag, text } = renderEventLine(evt);
  const div = document.createElement("div");
  div.className = cls + (bad ? " bad" : "");
  if (evt.type === "toolResultEvent") {
    const mark = evt.success ? `<span class="okmark">[ok]</span>` : `<span class="failmark">[fail]</span>`;
    div.innerHTML = `<span class="tag">${tag}</span> ${mark} ${esc(cut(evt.output ?? "", 300))}` +
      `<span class="exp" onclick="expandEv(this)">展开</span>`;
    div.dataset.full = esc(evt.output ?? "");
  } else if (evt.type === "finalAnswer") {
    // ② Markdown 渲染（10-10）：完成文本结构化展示；截断 500 字符 + 展开全量
    div.innerHTML = `<span class="tag">${tag}</span> <div class="md">${mdToHtml(cut(evt.text ?? "", 500))}</div>` +
      (String(evt.text ?? "").length > 500 ? `<span class="exp" onclick="expandMd(this)">展开全文</span>` : "");
    div.dataset.full = evt.text ?? "";
  } else {
    div.innerHTML = `<span class="tag">${tag}</span> ${esc(text)}`;
  }
  box.appendChild(div);
  return div;
}
// 展开工具结果全文（点击"展开"→ 显示完整输出；兼容无 .tag 的容器）
function expandEv(el) {
  const d = el.parentElement;
  if (d.dataset.full === undefined) return;
  el.textContent = "已展开";
  const tag = d.querySelector(".tag");
  d.innerHTML = (tag ? `<span class="tag">${tag.textContent}</span> ` : "") + d.dataset.full;
}
// 展开 finalAnswer 全量 Markdown（②）
function expandMd(el) {
  const d = el.parentElement;
  if (d.dataset.full === undefined) return;
  el.remove();
  const tag = d.querySelector(".tag");
  d.innerHTML = (tag ? `<span class="tag">${tag.textContent}</span> ` : "") + `<div class="md">${mdToHtml(d.dataset.full)}</div>`;
}
// ② 轻量 Markdown → 安全 HTML（不引入第三方库；转义先行防 XSS；确定性规则）
function escHtml(s) { return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;"); }
function mdToHtml(src) {
  if (!src) return "";
  const lines = String(src).split(/\r?\n/);
  let html = "", inCode = false, codeBuf = [], codeLang = "", inList = false, listTag = "";
  const flushList = () => { if (inList) { html += "</" + listTag + ">"; inList = false; } };
  const inline = (s) => escHtml(s)
    .replace(/\*\*([^*]+)\*\*/g, "<b>$1</b>")
    .replace(/`([^`]+)`/g, "<code>$1</code>");
  // 10-08 Monaco：``` 后的语言名进 data-lang（点击 .md-pre 升级 Monaco 只读编辑器；无语言标记保持纯文本）
  const flushCode = () => {
    html += `<pre class="md-pre"${codeLang ? ` data-lang="${escHtml(codeLang)}"` : ""}>${escHtml(codeBuf.join("\n"))}</pre>`;
    codeBuf = []; codeLang = ""; inCode = false;
  };
  for (const raw of lines) {
    if (raw.trim().startsWith("```")) {
      if (inCode) { flushCode(); }
      else { flushList(); const lm = raw.trim().match(/^```\s*([\w\-+#.]+)?/); codeLang = lm && lm[1] ? lm[1] : ""; inCode = true; }
      continue;
    }
    if (inCode) { codeBuf.push(raw); continue; }
    const t = raw.trim();
    if (!t) { flushList(); continue; }
    const h = t.match(/^(#{1,4})\s+(.*)$/);
    if (h) { flushList(); html += `<div class="md-h">${inline(h[2])}</div>`; continue; }
    const ul = t.match(/^[-*]\s+(.*)$/);
    if (ul) { if (!inList) { html += "<ul>"; listTag = "ul"; inList = true; } html += `<li>${inline(ul[1])}</li>`; continue; }
    const ol = t.match(/^\d+\.\s+(.*)$/);
    if (ol) { if (!inList) { html += "<ol>"; listTag = "ol"; inList = true; } html += `<li>${inline(ol[1])}</li>`; continue; }
    flushList(); html += `<div class="md-p">${inline(t)}</div>`;
  }
  if (inCode) flushCode();
  flushList();
  return html;
}

// ── 10-08 Monaco（本地 vendor，懒加载）：代码块点击升级只读编辑器；diff 左右对比 ──
// loader.js 注入一次；require.config 指向本地 /vendor（全量 min/vs，构建复制自包含，无 CDN）。
let _monaco = null, _monacoLoading = null;
function loadMonaco() {
  if (_monaco) return Promise.resolve(_monaco);
  if (!_monacoLoading) {
    _monacoLoading = new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = "/vendor/monaco/min/vs/loader.js";
      s.onload = () => {
        try {
          require.config({ paths: { vs: "/vendor/monaco/min/vs" } });
          require(["vs/editor/editor.main"], () => { _monaco = monaco; resolve(monaco); });
        } catch (e) { reject(e); }
      };
      s.onerror = () => reject(new Error("Monaco loader 加载失败（/vendor 不可用）"));
      document.head.appendChild(s);
    });
  }
  return _monacoLoading;
}
const MONACO_LANG = { js:"javascript", py:"python", cs:"csharp", ts:"typescript", sh:"shell",
  yml:"yaml", md:"markdown", html:"html", json:"json", xml:"xml", sql:"sql", ps1:"powershell",
  bat:"bat", cpp:"cpp", h:"cpp", hpp:"cpp", go:"go", java:"java", rb:"ruby", rs:"rust", php:"php",
  css:"css", scss:"scss", dockerfile:"dockerfile", ini:"ini", toml:"ini", cfg:"ini" };
function monacoLang(alias) {
  const a = String(alias || "").toLowerCase();
  return MONACO_LANG[a] || a;
}
async function monacoCode(pre) {
  try {
    const m = await loadMonaco();
    const host = document.createElement("div");
    host.className = "monaco-host";
    pre.replaceWith(host);
    m.editor.create(host, { value: pre.textContent || "", language: monacoLang(pre.dataset.lang),
      readOnly: true, automaticLayout: true, fontSize: 12, minimap: { enabled: false },
      scrollBeyondLastLine: false, wordWrap: "on", tabSize: 4 });
  } catch (e) { /* 加载失败：保持原 pre（仅在 Monaco 就绪后才替换，失败无副作用） */ }
}
// 点击委托：带 data-lang 的代码块升级 Monaco（单例，防重复升级）
document.addEventListener("click", (ev) => {
  const p = ev.target.closest(".md-pre[data-lang]");
  if (p && !p.dataset.monacoUpgraded) { p.dataset.monacoUpgraded = "1"; monacoCode(p); }
});

// ── ① 任务节分组（10-10：平铺 → 卡片内分节）──
// 分组边界确定性切分（不靠模型）：routing 开新卡片；finalAnswer/error 结束卡片并入档；
// 其余事件归属当前卡片。节号 = 节首事件在源数组的 index（稳定）→ 折叠状态跨全量重建保留。
// 折叠状态按 boxKey 隔离（实时流 events / 回放 detail 各自独立，防节号交叉污染）。
let collapsed = new Map();
let collapsedPairs = new Map();   // ③④ 工具块折叠（boxKey -> Set<call 在节内序号>）
function isCollapsed(boxKey, start) { return (collapsed.get(boxKey) || new Set()).has(start); }
function isPairCollapsed(boxKey, idx) { return (collapsedPairs.get(boxKey) || new Set()).has(idx); }
function rerenderBox(boxKey) {
  if (boxKey === "events") { renderEvents(); return; }
  if (boxKey === "detail" && lastDetailList) {
    const box = $("detail");
    box.innerHTML = "";
    renderGrouped(box, lastDetailList, { boxKey: "detail", onDone: (n) => afterDetailRender(box, lastDetailTaskId, lastDetailList.length) });
  }
}
function toggleCard(boxKey, start) {
  const s = collapsed.get(boxKey) || new Set();
  if (s.has(start)) s.delete(start); else s.add(start);
  collapsed.set(boxKey, s);
  rerenderBox(boxKey);
}
function togglePair(boxKey, idx) {
  const s = collapsedPairs.get(boxKey) || new Set();
  if (s.has(idx)) s.delete(idx); else s.add(idx);
  collapsedPairs.set(boxKey, s);
  rerenderBox(boxKey);
}
function groupEvents(events) {
  const groups = []; let cur = null;
  events.forEach((evt, i) => {
    if (evt.type === "routing") {
      if (cur && cur.items.length) { groups.push(cur); cur = null; }
      cur = { start: i, head: evt, items: [], done: false, fail: false };
    } else if (evt.type === "finalAnswer" || evt.type === "error") {
      if (!cur) cur = { start: i, head: null, items: [], done: false, fail: false };
      cur.items.push(evt);
      cur.done = evt.type === "finalAnswer"; cur.fail = evt.type === "error";
      groups.push(cur); cur = null;
    } else {
      if (!cur) cur = { start: i, head: null, items: [], done: false, fail: false };
      cur.items.push(evt);
    }
  });
  if (cur && cur.items.length) groups.push(cur);
  return groups;
}
// ③ 工具调用配对（10-10）：同一节内，toolCall 与其后第一个 toolResultEvent 合成一个渲染单元
// （pair）；无结果就配对的 call / 无 call 的结果 → 独立单行（回放边界兼容）。
function buildUnits(items) {
  const units = []; let pending = null;
  items.forEach((evt, i) => {
    if (evt.type === "toolCall") {
      if (pending) units.push({ kind: "single", evt: pending.call, start: pending.start });
      pending = { call: evt, start: i };
    } else if (evt.type === "toolResultEvent" && pending) {
      units.push({ kind: "pair", call: pending.call, start: pending.start, res: evt });
      pending = null;
    } else if (evt.type === "toolResultEvent") {
      units.push({ kind: "single", evt, start: i });
    } else {
      if (pending) { units.push({ kind: "single", evt: pending.call, start: pending.start }); pending = null; }
      units.push({ kind: "single", evt, start: i });
    }
  });
  if (pending) units.push({ kind: "single", evt: pending.call, start: pending.start });
  return units;
}
// ④ 折叠挂载（工具块）：tp-call 点击折叠/展开结果区；结果"展开"看全量
function renderPair(tb, u, boxKey, vis) {
  const pair = document.createElement("div");
  pair.className = "toolpair";
  const isFold = isPairCollapsed(boxKey, u.start);
  const callLine = renderEventLine(u.call);
  const head = document.createElement("div");
  head.className = "tp-call";
  head.title = "点击折叠/展开工具结果";
  head.onclick = () => togglePair(boxKey, u.start);
  head.setAttribute("role", "button"); head.tabIndex = 0;       // 键盘可达（10-15）
  head.innerHTML = `<span class="chev">${isFold ? "▸" : "▾"}</span><span class="tag">${callLine.tag}</span> ${esc(callLine.text)}`;
  pair.appendChild(head);
  if (vis.showRes) {
    const resBox = document.createElement("div");
    resBox.className = "tp-res" + (isFold ? " fold" : "");
    const mark = u.res.success ? `<span class="okmark">[ok]</span>` : `<span class="failmark">[fail]</span>`;
    resBox.innerHTML = `${mark} ${esc(cut(u.res.output ?? "", 300))}<span class="exp" onclick="expandEv(this)">展开</span>`;
    resBox.dataset.full = esc(u.res.output ?? "");
    pair.appendChild(resBox);
  }
  tb.appendChild(pair);
  return 1;
}
// 统一分组渲染：实时流（eventsCache + 过滤）与回放（任务事件列表）共用——消双路径漂移。
// renderEventLine 核心逻辑不动，只在外层包分组容器。
function renderGrouped(box, events, opts) {
  const boxKey = (opts && opts.boxKey) || "events";
  const filter = (opts && opts.filter) || null;
  const keyword = (opts && opts.keyword) || "";
  const onDone = (opts && opts.onDone) || null;
  let n = 0;
  for (const g of groupEvents(events)) {
    const card = document.createElement("div");
    card.className = "taskcard" + (g.fail ? " fail" : g.done ? " done" : "");
    const isColl = isCollapsed(boxKey, g.start);
    if (isColl) card.classList.add("collapsed");
    const head = document.createElement("div");
    head.className = "tc-head";
    head.title = "点击折叠/展开任务节";
    head.onclick = () => toggleCard(boxKey, g.start);
    head.setAttribute("role", "button"); head.tabIndex = 0;   // 键盘可达（10-15）
    const chev = `<span class="chev">${isColl ? "▸" : "▾"}</span>`;
    if (g.head) {
      const line = renderEventLine(g.head);
      const hay = (line.tag + " " + line.text).toLowerCase();
      const pass = (!filter || filter(g.head)) && (!keyword || hay.includes(keyword));
      head.innerHTML = pass
        ? chev + `<span class="tag">${line.tag}</span> ${esc(line.text)}`
        : chev + `<span class="tag">${g.fail ? "任务失败" : g.done ? "任务完成" : "任务事件"}</span><span class="hint">${g.items.length} 事件</span>`;
    } else {
      head.innerHTML = chev + `<span class="tag">${g.fail ? "任务失败" : g.done ? "任务完成" : "任务事件"}</span>` +
        `<span class="hint">${g.items.length} 事件</span>`;
    }
    card.appendChild(head);
    const tb = document.createElement("div");
    tb.className = "tc-body";
    let firstTail = true;
    const units = buildUnits(g.items);
    for (const u of units) {
      if (u.kind === "pair") {
        const cLine = renderEventLine(u.call);
        const rLine = renderEventLine(u.res);
        const passCall = (!filter || filter(u.call)) && (!keyword || (cLine.tag + " " + cLine.text).toLowerCase().includes(keyword));
        const passRes = (!filter || filter(u.res)) && (!keyword || (rLine.tag + " " + rLine.text).toLowerCase().includes(keyword));
        if (!passCall && !passRes) continue;
        n += renderPair(tb, u, boxKey, { showRes: passRes });
        continue;
      }
      if (filter && !filter(u.evt)) continue;
      const line = renderEventLine(u.evt);
      const hay = (line.tag + " " + line.text).toLowerCase();
      if (keyword && !hay.includes(keyword)) continue;   // 行被隐藏 → 不插分隔线（防残留空线）
      if (u.evt.type === "finalAnswer" || u.evt.type === "error") {
        if (!firstTail && n > 0) { const sep = document.createElement("div"); sep.className = "tc-sep"; tb.appendChild(sep); }
        firstTail = false;
      }
      appendEventLine(tb, u.evt, false); n++;
    }
    card.appendChild(tb);
    box.appendChild(card);
  }
  if (onDone) onDone(n);
  return n;
}

// ── 事件流过滤 + 自动滚动（10-07 UI 层 #5）──
// 过滤只影响展示不丢数据：eventsCache 保留最近 300 事件，切换过滤即时重渲染。
let filterMode = "all";
const filterMap = {
  all:    () => true,
  tools:  (e) => ["toolCall", "toolResultEvent"].includes(e.type),
  errors: (e) => e.type === "error",
  done:   (e) => ["finalAnswer", "error"].includes(e.type),
};
let eventsCache = [];
let evKeyword = "";
function renderEvents() {
  const box = $("events");
  box.innerHTML = "";
  const n = renderGrouped(box, eventsCache, {
    boxKey: "events",
    filter: (e) => filterMap[filterMode](e),
    keyword: evKeyword,
    onDone: () => { if ($("autoScroll").checked) box.scrollTop = box.scrollHeight; },
  });
  if (n === 0) box.innerHTML = eventsCache.length
    ? emptyState("当前过滤下无匹配事件",
        "已缓存 " + eventsCache.length + " 条事件，但没有命中当前过滤条件/关键词。",
        { action: `<button class="btn" onclick="resetEventFilter()">显示全部事件</button>` })
    : emptyState("等待任务事件",
        "Dashboard 不会阻塞 CLI——任务照常在终端执行。在上方命令条提交需求，事件会实时出现在这里。");
}
document.querySelectorAll(".fbtn").forEach(btn => btn.addEventListener("click", () => {
  document.querySelectorAll(".fbtn").forEach(b => b.classList.remove("active"));
  btn.classList.add("active");
  filterMode = btn.dataset.f;
  renderEvents();
}));
$("evFilter").addEventListener("input", (e) => { evKeyword = e.target.value.trim().toLowerCase(); renderEvents(); });
$("evClear").addEventListener("click", () => {
  eventsCache = []; evKeyword = ""; $("evFilter").value = "";
  renderEvents();
});

// ── 工程依赖图谱（10-07 UI 层 #3）──
// 全工程概览：文件数/边数/改动风险 Top5（反向依赖最多）表格；选文件 → 直接/反向依赖表格。
function loadDeps() {
  const dep = $("dep");
  dep.innerHTML = emptyState("正在加载依赖图谱", "读取 /api/deps（工程模式符号索引）…");
  fetch("/api/deps")
    .then(r => { if (!r.ok) throw new Error("未启用工程模式或索引失败（CLI 先 use <路径>）"); return r.json(); })
    .then(data => {
      const sel = $("depFile");
      sel.innerHTML = `<option value="">（全工程概览）</option>` + data.files.map(f => `<option>${esc(f)}</option>`).join("");
      let html = `<div class="kv"><span class="tag">文件</span> ${data.files.length} 个 &nbsp; <span class="tag">依赖边</span> ${data.edges.length} 条</div>`;
      if (data.topReverse.length) {
        html += `<table class="tbl"><tr><th>文件（改动影响面最大 Top5）</th><th>反向依赖数</th></tr>`;
        for (const t of data.topReverse) html += `<tr><td>${esc(t.file)}</td><td>${t.count}</td></tr>`;
        html += `</table><div class="dim" style="margin-top:6px">改动这些文件会波及最多其他文件——多文件修改前先查影响面。</div>`;
      } else {
        html += emptyState("暂无类型级依赖边", "符号索引为空，或工程内没有可索引的代码定义。CLI 执行 use <路径> 后点「加载」重建索引。");
      }
      dep.innerHTML = html;
      if (sel.value) loadFileDeps(sel.value);
    })
    .catch(err => { dep.innerHTML = emptyState("依赖图谱加载失败", err.message, { err: true, action: `<button class="btn" onclick="loadDeps()">重试</button>` }); });
}
// ── SVG 依赖子图（10-07：选中文件 1 度依赖图；纯函数布局 + 原生 SVG，无第三方库）──
// 布局规则：中心 selected 节点 + direct（出边，绿）左列 + reverse（入边，红）右列。
// 节点 > 25 → 返回降级标记（表格视图保留为兜底）；节点名截断 14 字符 + title 全名。
const DEP_NODE_MAX = 25;
function layoutOneDegree(center, direct, reverse) {
  const nodeW = 150, nodeH = 28, gap = 14, pad = 18;
  if (1 + direct.length + reverse.length > DEP_NODE_MAX) return { tooMany: true };
  const rows = Math.max(direct.length, reverse.length, 1);
  const H = rows * (nodeH + gap) + pad * 2;
  const W = nodeW * 2 + 240;
  const cx = W / 2, cy = H / 2 - nodeH / 2;
  const yFor = (i) => pad + i * (nodeH + gap) + (H - rows * (nodeH + gap)) / 2;
  const mk = (f, x, y) => ({ f, x, y });
  return {
    W, H,
    center: mk(center, cx - nodeW / 2, cy),
    left: direct.map((f, i) => mk(f, pad, yFor(i))),
    right: reverse.map((f, i) => mk(f, W - pad - nodeW, yFor(i)))
  };
}
function renderDepSvg(file, direct, reverse) {
  const lay = layoutOneDegree(file, direct, reverse);
  if (lay.tooMany) return `<div class="empty">节点 ${1 + direct.length + reverse.length} 个超过上限 ${DEP_NODE_MAX}，降级表格视图。</div>`;
  const node = (n, cls) => {
    const label = cut(n.f, 14);
    return `<g transform="translate(${n.x},${n.y})" class="depNode" data-file="${esc(n.f).replace(/'/g, "\\'")}" onclick="selectDepFile(this.dataset.file)">
      <rect width="150" height="28" rx="6" class="depRect ${cls}" /><text x="75" y="19" text-anchor="middle" class="depText">${esc(label)}</text>
      <title>${esc(n.f)}</title></g>`;
  };
  const edge = (x1, y1, x2, y2, color) => {
    const mx = (x1 + x2) / 2;
    return `<path d="M${x1},${y1 + 14} C${mx},${y1 + 14} ${mx},${y2 + 14} ${x2},${y2 + 14}" class="depEdge" stroke="${color}" marker-end="url(#depArrow${color === "#4caf78" ? "G" : "R"})" />`;
  };
  let h = `<svg viewBox="0 0 ${lay.W} ${lay.H}" width="100%" style="display:block;max-width:920px;margin:0 auto">
    <defs>
      <marker id="depArrowG" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 z" fill="#4caf78"/></marker>
      <marker id="depArrowR" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 z" fill="#e06c5a"/></marker>
    </defs>`;
  const c = lay.center, cy = c.y + 14;
  for (const n of lay.left)  h += edge(c.x, cy, n.x + 150, n.y, "#4caf78");   // 中心 → direct（绿）
  for (const n of lay.right) h += edge(n.x + 150, n.y, c.x, cy, "#e06c5a");   // reverse → 中心（红）
  h += node(c, "depC");
  for (const n of lay.left)  h += node(n, "depD");
  for (const n of lay.right) h += node(n, "depR");
  h += `</svg><div class="dim" style="margin-top:6px">绿 = 直接依赖（本文件引用）· 红 = 反向依赖（谁引用本文件）· 点击节点切换选中文件。</div>`;
  return h;
}
function selectDepFile(file) {
  const sel = $("depFile");
  sel.value = file;
  loadFileDeps(file);
}
function loadFileDeps(file) {
  const dep = $("dep");
  dep.innerHTML = `<div class="empty">查询 ${esc(file)} 的依赖…</div>`;
  fetch("/api/deps?path=" + encodeURIComponent(file))
    .then(r => r.json())
    .then(data => {
      const box = (title, items) => {
        let h = `<h3 style="margin:8px 0 4px">${title}（${items.length}）</h3>`;
        if (!items.length) return h + `<div class="empty" style="padding:4px">无</div>`;
        h += `<table class="tbl"><tr><th>文件</th></tr>` + items.map(x => `<tr><td>${esc(x)}</td></tr>`).join("") + `</table>`;
        return h;
      };
      dep.innerHTML = renderDepSvg(file, data.direct, data.reverse)
        + `<div class="kv"><span class="tag">${esc(file)}</span></div>`
        + box("反向依赖（谁引用本文件——改动影响面）", data.reverse)
        + box("直接依赖（本文件引用的类型所在文件）", data.direct)
        + `<div class="dim" style="margin-top:6px">依赖基于类型符号引用（粗粒度，含注释/字符串误报），仅供参考方向；精确改动前用 find_symbol/analyze_deps 核实。</div>`;
    })
    .catch(err => { dep.innerHTML = `<div class="empty">${esc(err.message)}</div>`; });
}
$("depLoad").addEventListener("click", loadDeps);
$("depFile").addEventListener("change", (e) => { if (e.target.value) loadFileDeps(e.target.value); });

// ── P0-3 浏览器通知：实时流 final/error → Web Notification（任务完成/失败提醒）──
// 纯前端增强：权限在用户主动提交任务时请求（不弹页载权限）；tag 固定 → 新通知替换旧通知。
function notifyTaskDone(evt) {
  if (!("Notification" in window) || Notification.permission !== "granted") return;
  try {
    const isErr = evt.type === "error";
    const body = isErr
      ? cut(evt.error || evt.message || "任务执行失败", 200)
      : cut(evt.text || "", 200);
    new Notification(isErr ? "LocalCodeForge：任务失败" : "LocalCodeForge：任务完成",
      { body, tag: "lcf-task-done" });
  } catch { /* 系统拒绝/不可用时静默（页面内已有事件流可见） */ }
}

// ── SSE 实时流 ──
function openStream() {
  const es = new EventSource("/events" + (TOKEN ? "?token=" + encodeURIComponent(TOKEN) : ""));
  es.onmessage = (e) => {
    let evt; try { evt = JSON.parse(e.data); } catch { return; }
    // 【10-14 P3-6】joined = 建连即来的首帧（非 AgentEvent）：提示"实时流从中途接入"，
    // 完整历史走任务详情回放。不入事件缓存、不渲染成事件行。
    if (evt.type === "joined") {
      $("sub").textContent = "运行目录：实时流为中途接入（此前事件请看任务详情回放）";
      return;
    }
    // P0-3：任务结束事件（finalAnswer/error）触发浏览器通知
    if (evt.type === "finalAnswer" || evt.type === "error") notifyTaskDone(evt);
    eventsCache.push(evt);
    while (eventsCache.length > 300) eventsCache.shift();
    renderEvents();
    const dot = $("liveDot");
    dot.className = "dot " + (evt.type === "error" ? "yellow" : "green");
    $("sub").textContent = "运行目录：" + (evt.type === "toolCall" ? "任务执行中…" : "在线");
  };
  es.onopen = () => { const dot = $("liveDot"); dot.className = "dot green"; $("sub").textContent = "在线（实时流已连接）"; };
  es.onerror = () => { const dot = $("liveDot"); dot.className = "dot yellow"; $("sub").textContent = "实时流连接异常（自动重连中…）"; };
}

// ── 任务详情回放（10-07 UI 层 #2；10-10 ① 分组渲染）──
let lastDetailTaskId = null;
let lastDetailList = null;   // 回放事件缓存：折叠重渲染本地直画，不重复 fetch
function afterDetailRender(box, taskId, totalCount) {
  const err = box.querySelector(".ev.error");
  if (err) {
    err.classList.add("bad");
    err.scrollIntoView({ block: "center" });
    $("sub").textContent = `任务 ${taskId}：${totalCount} 事件，失败定位到 error`;
  } else {
    box.scrollTop = box.scrollHeight;
    $("sub").textContent = `任务 ${taskId}：${totalCount} 事件`;
  }
}
async function showTaskEvents(taskId) {
  lastDetailTaskId = taskId;
  const box = $("detail");
  box.innerHTML = `<div class="empty">加载任务 ${esc(taskId)} 事件…</div>`;
  const r = await fetch(`/api/tasks/${encodeURIComponent(taskId)}/events`);
  if (!r.ok) { box.innerHTML = `<div class="empty">详情不可用（${r.status}）</div>`; return; }
  const list = await r.json();
  if (!list.length) { box.innerHTML = `<div class="empty">任务 ${esc(taskId)} 无事件记录（事件持久化从 10-07 起生效）</div>`; return; }
  lastDetailList = list;
  box.innerHTML = "";
  // A：历史行点击 → 回放卡片自动展开（任务中心内紧随历史，无需再手动点开）
  const dcard = box.closest(".card");
  if (dcard && !dcard.classList.contains("open")) dcard.classList.add("open");
  // 10-10 ① 分组渲染：回放与实时流共用 renderGrouped（消双路径漂移）
  renderGrouped(box, list, {
    boxKey: "detail",
    onDone: () => afterDetailRender(box, taskId, list.length),
  });
}

// ── stats ──
async function fetchStats() {
  const r = await wrapFetch("/api/stats");
  if (!r.ok) { $("stats").innerHTML = emptyState("统计不可用", "请求 /api/stats 返回 " + r.status + "。", { err: true, action: `<button class="btn" onclick="fetchStats()">重试</button>` }); return; }
  const d = await r.json();
  const s = d.summary;
  const rateCls = s.successRate >= 0.8 ? "green" : s.successRate >= 0.5 ? "" : "red";
  let html = `<div class="statrow"><span>成功率</span><span class="big ${rateCls}">${(s.successRate * 100).toFixed(1)}%</span></div>`;
  // C：成功率分段条（成功/失败/可疑占比，图例带计数——总任务行不再重复计数）
  html += segBar([
    { v: s.successCount, cls: "b-green", sw: "g", label: "成功" },
    { v: s.failureCount, cls: "b-red", sw: "r", label: "失败" },
    { v: s.suspiciousCount, cls: "b-yellow", sw: "y", label: "可疑" },
  ]);
  html += `<div class="statrow"><span>总任务</span><span>${s.totalCount}</span></div>`;
  html += `<div class="statrow"><span>平均耗时</span><span>${fmtMs(s.avgTotalMs)}（修复 ${s.avgRetryCount.toFixed(2)} 次）</span></div>`;
  // C：阶段耗时堆叠条（规划/执行/验证占比，图例带绝对值）
  const stageMs = (s.avgPlannerMs || 0) + (s.avgExecutorMs || 0) + (s.avgValidatorMs || 0);
  if (stageMs > 0) {
    html += `<div class="statrow" style="margin-top:2px"><span>阶段耗时</span><span></span></div>`;
    html += segBar([
      { v: s.avgPlannerMs, cls: "b-yellow", sw: "y", label: "规划", fmt: fmtMs(s.avgPlannerMs) },
      { v: s.avgExecutorMs, cls: "b-blue", sw: "b", label: "执行", fmt: fmtMs(s.avgExecutorMs) },
      { v: s.avgValidatorMs, cls: "b-green", sw: "g", label: "验证", fmt: fmtMs(s.avgValidatorMs) },
    ]);
  } else {
    html += `<div class="statrow"><span>阶段耗时</span><span>无样本</span></div>`;
  }
  if (d.epoch) {
    const c = d.epoch.current, l = d.epoch.legacy;
    html += segBar([
      { v: c.count, cls: "b-blue", sw: "b", label: `agent_pro 30B ${(c.successRate * 100).toFixed(0)}%`, fmt: `${c.count} 次` },
      { v: l.count, cls: "b-dim", sw: "d", label: `4B 历史 ${(l.successRate * 100).toFixed(0)}%`, fmt: `${l.count} 次`, keep: true },
    ]);
  }
  if (d.failures && d.failures.length) {
    const max = Math.max(...d.failures.map(f => f.count), 1);
    html += `<div style="margin-top:6px"><span class="tag" style="color:var(--dim)">失败类别</span></div>`;
    for (const f of d.failures)
      html += `<div class="statrow"><span>${esc(f.category)}</span><span>${f.count}</span></div>
               <div class="bar"><i class="b-red" style="width:${(f.count / max * 100).toFixed(0)}%"></i></div>`;
  }
  // ── 10-14 P5-24 全量对齐：可疑明细 / Executor 生成成功率 / 特征类归因 / 路由决策 / 工具拒绝 ──
  if (d.suspicious && d.suspicious.length) {
    html += `<div style="margin-top:8px"><span class="tag" style="color:var(--dim)">可疑成功明细</span></div>`;
    for (const m of d.suspicious)
      html += `<div class="statrow"><span>${esc(m.mode)}</span><span class="warn">${esc(m.input)}</span></div>`;
  }
  if (d.executor) {
    const r = d.executor.totalCount ? `${(d.executor.successCount / d.executor.totalCount * 100).toFixed(0)}%` : "无样本";
    html += `<div class="statrow" style="margin-top:6px"><span>Executor 生成成功率</span><span>${r}（${d.executor.successCount}/${d.executor.totalCount}）</span></div>`;
  }
  if (d.featureBreakdown && d.featureBreakdown.length) {
    html += `<div style="margin-top:8px"><span class="tag" style="color:var(--dim)">特征类归因</span></div>`;
    for (const f of d.featureBreakdown) {
      const p = f.totalCount ? `${(f.successCount / f.totalCount * 100).toFixed(0)}%` : "无样本";
      html += `<div class="statrow"><span>${esc(f.feature)}</span><span>${p}（${f.successCount}/${f.totalCount}）${f.topFailureCause ? " · " + esc(f.topFailureCause) : ""}</span></div>`;
    }
  }
  if (d.routingDecisions && d.routingDecisions.total > 0) {
    const rd = d.routingDecisions;
    const svc = (rd.byService || []).map(x => `${esc(x.item1)}:${x.item2}`).join("  ");
    const feat = (rd.byFeature || []).slice(0, 5).map(x => `${esc(x.item1)}:${x.item2}`).join("  ");
    html += `<div style="margin-top:8px"><span class="tag" style="color:var(--dim)">路由决策</span></div>`;
    html += `<div class="statrow"><span>决策总数</span><span>${rd.total}</span></div>`;
    if (svc) html += `<div class="statrow"><span>按服务</span><span>${svc}</span></div>`;
    if (feat) html += `<div class="statrow"><span>按特征类</span><span>${feat}</span></div>`;
  }
  if (d.toolRejections && d.toolRejections.total > 0) {
    const tr = d.toolRejections;
    const cats = (tr.byCategory || []).map(x => `${esc(x.item1)}:${x.item2}`).join("  ");
    html += `<div style="margin-top:8px"><span class="tag" style="color:var(--dim)">工具拒绝</span></div>`;
    html += `<div class="statrow"><span>拒绝总数</span><span>${tr.total}</span></div>`;
    if (cats) html += `<div class="statrow"><span>按类别</span><span>${cats}</span></div>`;
  }
  $("stats").innerHTML = html;
  badge("badgeStats", `${s.totalCount} · ${(s.successRate * 100).toFixed(0)}%`, s.successRate >= 0.8);
}

// ── 历史任务（P0-4：关键词/状态过滤 + 一键重跑）──
async function fetchTasks() {
  const q = encodeURIComponent(($("taskQ") ? $("taskQ").value : "").trim() || "");
  const st = encodeURIComponent(($("taskStatus") ? $("taskStatus").value : "") || "");
  const r = await wrapFetch(`/api/tasks?take=50&q=${q}&status=${st}`);
  if (!r.ok) { $("tasks").innerHTML = emptyState("历史任务不可用", "请求 /api/tasks 返回 " + r.status + "。", { err: true, action: `<button class="btn" onclick="fetchTasks()">重试</button>` }); return; }
  const list = await r.json();
  if (!list.length) {
    $("tasks").innerHTML = (q || st)
      ? emptyState("无匹配任务", "当前关键词/状态过滤下没有任务。放宽条件或清空筛选即可看到全部。",
                   { action: `<button class="btn" onclick="clearTaskFilter()">清空筛选</button>` })
      : emptyState("暂无任务记录", "还没有执行过任务。在上方命令条提交第一个需求吧。");
    return;
  }
  let html = `<table><thead><tr><th>时间</th><th>模式</th><th>服务</th><th>结果</th><th>耗时</th><th>输入</th><th></th></tr></thead><tbody id="tasksBody">`;
  for (const t of list) {
    const d = new Date(t.startedAt);
    const hm = d.toTimeString ? d.toTimeString().slice(0, 8) : "";
    const res = t.success ? `<span class="ok">OK</span>` : t.isSuspicious ? `<span class="warn">可疑</span>` : `<span class="fail">FAIL</span>`;
    // 重跑按钮：stopPropagation 防触发行回放；复用 POST /api/commands 提交链路（与输入框同一通道）
    const rerun = `<button class="btn" title="用同一输入重跑该任务" onclick="event.stopPropagation();rerunTask(this.dataset.input)" data-input="${esc(t.userInput).replace(/"/g, "&quot;")}">重跑</button>`;
    html += `<tr title="点击回放任务事件" onclick="showTaskEvents('${esc(t.taskId).replace(/'/g, "\\'")}')"><td>${hm}</td><td>${esc(t.mode)}</td><td>${esc(t.serviceId)}</td><td>${res}</td>
             <td>${fmtMs(t.totalMs)}</td><td class="inp" title="${esc(t.userInput)}">${esc(cut(t.userInput, 60))}</td><td>${rerun}</td></tr>`;
  }
  $("tasks").innerHTML = html + "</tbody></table>";
  badge("badgeTasks", `${list.length} 条`);
  if (lastDetailTaskId) {
    document.querySelectorAll("#tasksBody tr.sel").forEach(r => r.classList.remove("sel"));
    const row = document.querySelector(`#tasksBody tr[onclick*="${lastDetailTaskId}"]`);
    if (row) row.classList.add("sel");
  }
}
// 清空任务筛选（10-15：空态"清空筛选"按钮）
function clearTaskFilter() {
  const q = $("taskQ"), s = $("taskStatus");
  if (q) q.value = ""; if (s) s.value = "";
  fetchTasks();
}

// ── P0-4 一键重跑：同一输入重新提交（复用 /api/commands 提交链路）──
async function rerunTask(input) {
  try {
    const r = await fetch("/api/commands", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ input })
    });
    if (r.status === 202) {
      const d = await r.json().catch(() => ({}));
      toast(d.pending > 1 ? `已重跑，队列第 ${d.pending} 位` : "已重跑提交执行", "ok");
    } else {
      const d = await r.json().catch(() => ({}));
      toast("重跑提交失败：" + (d.error || r.status), "err");
    }
  } catch (e) { toast("重跑提交失败：" + e.message, "err"); }
}

// ── 路由调权状态（10-07：与终端 stats 小节同语义，阈值来自 AgentOptions.Routing*）──
async function fetchTuning() {
  const r = await wrapFetch("/api/tuning");
  if (!r.ok) { $("tuning").innerHTML = `<div class="empty">tuning 不可用（${r.status}）</div>`; return; }
  const d = await r.json();
  const pct = (v) => (v * 100).toFixed(0) + "%";
  const clamp = (v) => Math.max(0, Math.min(100, v * 100)).toFixed(1);
  // C：阈值仪表——填充 = 当前成功率，竖线 = 阈值（一眼看出"过线没有"）；触发 = 黄色警示，稳态 = 绿色
  const gauge = (rate, threshold) =>
    `<div class="gauge ${rate < threshold ? "warn" : "ok"}"><i style="width:${clamp(rate)}%"></i><u style="left:${clamp(threshold)}%"></u></div>`;
  const pill = (t) => t ? `<span class="pill on">触发</span>` : `<span class="pill off">保持</span>`;
  const w = d.writeDowngrade, rd = d.readUpgrade, cd = d.complexDrop;
  let html = "";
  html += `<div class="tune"><div class="tune-head">L0.5 写降级 small${pill(w.triggered)}</div>`
       + gauge(w.successRate, w.threshold)
       + `<div class="tune-meta">样本 <b>${w.sampleCount}</b> · 成功率 <b>${pct(w.successRate)}</b> · 阈值 <b>${pct(w.threshold)}</b>${w.reason ? " · " + esc(w.reason) : ""}</div></div>`;
  html += `<div class="tune"><div class="tune-head">L0.5b 读升级 pro${pill(rd.triggered)}</div>`
       + `<div class="tune-line"><span class="tl-name">small</span>${gauge(rd.small.successRate, rd.badRateThreshold)}<span class="tl-val">${pct(rd.small.successRate)} / 坏率阈 ${pct(rd.badRateThreshold)}</span></div>`
       + `<div class="tune-line"><span class="tl-name">pro</span>${gauge(rd.pro.successRate, rd.minSuccessRate)}<span class="tl-val">${pct(rd.pro.successRate)} / 阈 ${pct(rd.minSuccessRate)}</span></div>`
       + `<div class="tune-meta">${rd.reason ? esc(rd.reason) : ""}</div></div>`;
  html += `<div class="tune"><div class="tune-head">P2 Complex 降级 large${pill(cd.triggered)}</div>`
       + gauge(cd.successRate, cd.threshold)
       + `<div class="tune-meta">样本 <b>${cd.sampleCount}</b> · 成功率 <b>${pct(cd.successRate)}</b> · 阈值 <b>${pct(cd.threshold)}</b>${cd.reason ? " · " + esc(cd.reason) : ""}</div></div>`;
  html += `<div class="statrow" style="color:var(--dim);margin-top:4px"><span>阈值来源</span><span>AgentOptions.Routing* 配置${d.epoch ? " · 样本仅统计 law-unified 30B 时代" : ""}</span></div>`;
  $("tuning").innerHTML = html;
  const acts = [w.triggered ? "写降级" : null, rd.triggered ? "读升级" : null, cd.triggered ? "复杂降级" : null].filter(Boolean);
  badge("badgeTuning", acts.length ? acts.join("/") : "稳态");
}

// ── 记忆（10-07：agent_memory.db 列表 + 统计）──
async function fetchMemory() {
  const r = await wrapFetch("/api/memory?take=50");
  if (!r.ok) { $("memory").innerHTML = `<div class="empty">memory 不可用（${r.status}）</div>`; return; }
  const d = await r.json();
  if (!d.rows.length) { $("memory").innerHTML = `<div class="empty">暂无记忆（模型使用 remember 工具 / 失败教训自动归档后出现）</div>`; return; }
  const s = d.stats;
  let html = `<div class="statrow"><span>统计</span><span>共 ${s.total} 条 · 总命中 ${s.totalHits}</span></div>`;
  // C：模型/任务构成分段条
  html += segBar([
    { v: s.modelCount, cls: "b-cyan", sw: "c", label: "模型记忆" },
    { v: s.taskCount, cls: "b-blue", sw: "b", label: "任务归档" },
  ]);
  const maxHit = Math.max(...d.rows.map(m => m.hitCount || 0), 1);
  html += `<table><thead><tr><th>#</th><th>kind</th><th>命中</th><th>次数</th><th>主题</th></tr></thead><tbody>`;
  for (const m of d.rows)
    html += `<tr><td>${m.id}</td><td>${esc(m.kind)}</td><td><div class="heat"><i style="width:${((m.hitCount || 0) / maxHit * 100).toFixed(0)}%"></i></div>${m.hitCount}</td><td>${m.count}</td><td class="inp" title="${esc(m.topic)}">${esc(cut(m.topic, 50))}</td></tr>`;
  $("memory").innerHTML = html + "</tbody></table>";
  badge("badgeMemory", `${s.total} 条`);
}

// ── 备份 + Diff ──
let backups = [];
async function fetchBackups() {
  const r = await wrapFetch("/api/backups");
  if (!r.ok) { $("backups").innerHTML = `<div class="empty">backups 不可用（${r.status}）</div>`; return; }
  backups = await r.json();
  if (!backups.length) { $("backups").innerHTML = `<div class="empty">暂无工程备份（工程模式改动文件后出现）</div>`; return; }
  // P2 空状态清理：刷新备份列表时 diff 区回到引导态（不留旧 diff 残留）
  $("diff").innerHTML = `<div class="empty">点击备份文件查看改动差异（红 = 删除，绿 = 新增）</div>`;
  const byTask = {};
  for (const b of backups) (byTask[b.taskId] = byTask[b.taskId] || []).push(b);
  let html = "";
  for (const [taskId, files] of Object.entries(byTask)) {
    html += `<div class="bk-task" onclick="toggleTask('${taskId}', this)">▸ ${taskId} <span class="bk-meta">${files.length} 个文件</span>`;
    html += ` <button class="bk-roll" onclick="event.stopPropagation();rollbackPreview('${taskId}')">回滚</button></div>`;
    html += `<div class="bk-files" data-task="${taskId}" style="display:none">`;
    for (const f of files)
      html += `<div class="bk-file" onclick="showDiff('${taskId}', '${esc(f.rel).replace(/'/g, "\\'")}')">${esc(f.rel)} <span class="bk-meta">@{stamp}</span></div>`.replace("{stamp}", f.stamp);
    html += `</div>`;
  }
  $("backups").innerHTML = html;
  badge("badgeBackups", `${backups.length} 条`);
}
// ── Web 端回滚（10-10）：两步确认——dry-run 预览（不执行）→ 确认执行（有风险操作）──
async function rollbackPreview(taskId) {
  $("diff").innerHTML = `<div class="empty">加载回滚预览…</div>`;
  const r = await fetch("/api/rollback/dry-run", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ taskId })
  });
  if (!r.ok) { $("diff").innerHTML = `<div class="empty">回滚预览不可用（${r.status}）</div>`; return; }
  const d = await r.json();
  if (!d.restoreTargets.length && !d.removeTargets.length) {
    $("diff").innerHTML = `<div class="empty">该任务无可用备份（${esc(taskId)}）</div>`; return;
  }
  let html = `<div class="d-hunk">回滚预览 · 任务 ${esc(taskId)}（最早备份 = 任务前状态，未执行任何改动）</div>`;
  if (d.restoreTargets.length) {
    html += `<div class="bk-meta">将恢复 ${d.restoreTargets.length} 个文件：</div>`;
    for (const f of d.restoreTargets) html += `<div class="d-del">恢复 ${esc(f)}</div>`;
  }
  if (d.removeTargets.length) {
    html += `<div class="bk-meta">将删除 ${d.removeTargets.length} 个新建文件：</div>`;
    for (const f of d.removeTargets) html += `<div class="d-del">删除 ${esc(f)}</div>`;
  }
  html += `<div style="margin-top:10px"><button class="bk-roll" onclick="rollbackExec('${taskId}')">确认回滚（恢复任务前状态）</button>`;
  html += ` <button onclick="$('diff').innerHTML='<div class=\\"empty\\">已取消，未执行改动</div>'">取消</button></div>`;
  $("diff").innerHTML = html;
}
async function rollbackExec(taskId) {
  $("diff").innerHTML = `<div class="empty">执行回滚…</div>`;
  const r = await fetch("/api/rollback", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ taskId })
  });
  if (!r.ok) { $("diff").innerHTML = `<div class="empty">回滚失败（${r.status}）</div>`; return; }
  const d = await r.json();
  $("diff").innerHTML = `<div class="d-hunk">回滚完成：恢复 ${d.restored} 个文件，删除 ${d.removedNew} 个新建文件。</div>`;
  toast(`回滚完成：恢复 ${d.restored} 个 / 删除 ${d.removedNew} 个`, "ok");
  fetchBackups();
}
function toggleTask(taskId, el) {
  const files = document.querySelector(`.bk-files[data-task="${taskId}"]`);
  const open = files.style.display !== "none";
  files.style.display = open ? "none" : "block";
  el.textContent = (open ? "▸" : "▾") + el.textContent.slice(1);
  el.classList.toggle("open", !open);
}
function renderDiffText(df) {
  if (df.tooLarge) { $("diff").innerHTML = `<div class="empty">${esc(df.tooLargeReason)}</div>`; return; }
  if (!df.hunks || !df.hunks.length) { $("diff").innerHTML = `<div class="empty">无差异</div>`; return; }
  let html = "";
  for (const h of df.hunks) {
    html += `<div class="d-hunk">@@ -${h.oldStart},${h.oldCount} +${h.newStart},${h.newCount} @@</div>`;
    for (const ln of h.lines) {
      const sign = ln.type === "add" ? "+" : ln.type === "remove" ? "-" : " ";
      const num = ln.type === "add" ? ln.newNo : ln.type === "remove" ? ln.oldNo : `${ln.oldNo} ${ln.newNo}`;
      const cls = ln.type === "add" ? "d-add" : ln.type === "remove" ? "d-del" : "d-same";
      html += `<div class="${cls}">${sign} ${num}| ${esc(ln.text)}</div>`;
    }
  }
  $("diff").innerHTML = html;
}
function diffLang(rel) {
  const ext = (rel.split(".").pop() || "").toLowerCase();
  const map = { cs:"csharp", py:"python", js:"javascript", ts:"typescript", json:"json", md:"markdown",
    html:"html", csproj:"xml", slnx:"xml", props:"xml", targets:"xml", yml:"yaml", yaml:"yaml",
    sql:"sql", ps1:"powershell", bat:"bat", sh:"shell", css:"css", xml:"xml", go:"go", java:"java" };
  return map[ext] || "plaintext";
}
async function renderMonacoDiff(original, modified, rel, df) {
  try {
    const m = await loadMonaco();
    $("diff").innerHTML = `<div class="d-bar"><button class="btn" id="diffTextMode">文本视图</button></div>`;
    const host = document.createElement("div");
    host.className = "monaco-diff-host";
    $("diff").appendChild(host);
    const ed = m.editor.createDiffEditor(host, { automaticLayout: true, fontSize: 12,
      minimap: { enabled: false }, renderSideBySide: true, readOnly: true, scrollBeyondLastLine: false });
    const lang = diffLang(rel);
    ed.setModel({
      original: m.editor.createModel(original, lang, m.Uri.parse("file:///original." + lang)),
      modified: m.editor.createModel(modified, lang, m.Uri.parse("file:///modified." + lang))
    });
    $("diffTextMode").addEventListener("click", () => { renderDiffText(df); });
  } catch (e) { renderDiffText(df); }
}
async function showDiff(taskId, rel) {
  $("diff").innerHTML = `<div class="empty">加载 diff…</div>`;
  const r = await fetch(`/api/diff?taskId=${encodeURIComponent(taskId)}&rel=${encodeURIComponent(rel)}`);
  if (!r.ok) { $("diff").innerHTML = `<div class="empty">diff 不可用（${r.status}）</div>`; return; }
  const d = await r.json();
  // 10-08：/api/diff 返回 { original, modified, diff }（diff = DiffResult；全文仅非 tooLarge）
  const df = d.diff || d;
  if (df.tooLarge || (!df.hunks || !df.hunks.length)) { renderDiffText(df); return; }
  if (d.original != null && d.modified != null) { renderMonacoDiff(d.original, d.modified, rel, df); return; }
  renderDiffText(df);
}

// ── P0-2 健康状态常显（/api/health）：模型端点 / 工程模式 / 运行开关 ──
async function fetchHealth() {
  try {
    const r = await wrapFetch("/api/health");
    if (!r.ok) { $("healthLine").textContent = "状态不可用（" + r.status + "）"; return; }
    const d = await r.json();
    const parts = [];
    parts.push(`${d.models.small.split("@")[0]}`);
    if (d.project) parts.push(`工程：${trunc(d.project, 28)}${d.projectWritable ? "（可写）" : "（只读）"}`);
    else parts.push("沙箱模式（CLI use <路径> 启用工程感知）");
    parts.push(`路由${d.runtime.routing ? "开" : "关"} · 严格验证${d.runtime.strictOutput ? "开" : "关"}`);
    $("healthLine").textContent = parts.join("　");
  } catch (err) { $("healthLine").textContent = "状态不可用（" + ((err && err.name === "AbortError") ? "连接超时" : "连接失败") + "）"; }
}

function refreshAll() {
  fetchStats(); fetchTasks(); fetchBackups(); fetchTuning(); fetchMemory(); fetchHealth();
  // 预加载即视为已加载：展开卡片直接显示，不再重复请求（懒加载分支留给异常路径）
  document.querySelectorAll(".card.collapsible[data-load]").forEach(c => c.dataset.loaded = "1");
}
// 折叠卡片标题预览数字（10-10 P1）：各 fetch 成功后更新
function badge(id, text, hot) {
  const el = $(id);
  if (!el) return;
  el.textContent = text;
  el.classList.toggle("hot", !!hot);
}

// ── 空状态 / 错误态统一渲染（10-15 UI 打磨）──
// 此前是一行灰字：只说"没有"，不说"为什么、下一步能做什么"。
// 结构化四段：标记 + 标题 + 说明 + 可选行动；错误态走 is-err 红色通道并给重试按钮。
function emptyState(title, desc, opts) {
  const o = opts || {};
  return `<div class="emptybox${o.err ? " is-err" : ""}">
      <div class="eb-mark">${o.err ? "&#33;" : (o.mark || "&#9671;")}</div>
      <div class="eb-title">${esc(title)}</div>
      <div class="eb-desc">${esc(desc)}</div>
      ${o.action ? `<div class="eb-act">${o.action}</div>` : ""}
    </div>`;
}
// 过滤复位：空态里的"显示全部事件"按钮（清关键词 + 回到全部过滤）
function resetEventFilter() {
  document.querySelectorAll(".fbtn").forEach(b => b.classList.remove("active"));
  const all = document.querySelector('.fbtn[data-f="all"]');
  if (all) all.classList.add("active");
  filterMode = "all"; evKeyword = "";
  const f = $("evFilter"); if (f) f.value = "";
  renderEvents();
}
// 键盘可达：可点击的 div/span（任务节头、工具块头）此前只有 onclick，键盘完全不可达。
// 统一补 role=button + tabindex，这里做 Enter/Space → click 的全局委托（零逐点改动）。
document.addEventListener("keydown", (e) => {
  if (e.key !== "Enter" && e.key !== " ") return;
  const el = document.activeElement;
  if (el && el.getAttribute && el.getAttribute("role") === "button") { e.preventDefault(); el.click(); }
});

// ── 全局 toast 反馈（10-10 UI 优化）：提交/取消/回滚等操作的统一醒目标识 ──
function toast(msg, kind = "info") {
  const box = $("toast");
  const el = document.createElement("div");
  el.className = "toast " + kind;
  el.textContent = msg;
  box.appendChild(el);
  setTimeout(() => {
    el.style.opacity = "0"; el.style.transition = "opacity .3s";
    setTimeout(() => el.remove(), 350);
  }, 4200);
}

// ── 命令提交（10-07 webUI 交互）：POST /api/commands → CLI 命令通道（与终端输入统一排队）──
// 10-10：本地命令历史 ↑/↓（上限 10，进程内）
let cmdHistory = [];
let cmdHistIdx = -1;
async function submitCommand() {
  const input = $("cmdInput").value.trim();
  const hint = $("cmdHint");
  if (!input) { hint.textContent = "输入为空"; return; }
  hint.textContent = "提交中…";
  const sendBtn = $("cmdSend");
  sendBtn.classList.add("is-busy");
  try {
    const r = await fetch("/api/commands", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ input })
    });
    if (r.status === 202) {
      const d = await r.json().catch(() => ({}));
      hint.textContent = d.pending > 1
        ? `已排队：第 ${d.pending} 位（队列中 ${d.pending} 个，一次一任务串行）`
        : "已提交，任务开始执行…";
      toast(d.pending > 1 ? `已提交，队列第 ${d.pending} 位` : "任务已提交执行", "ok");
      // P0-3：用户主动提交时请求通知权限（非页载弹窗；授权后任务完成/失败会系统通知）
      if ("Notification" in window && Notification.permission === "default")
        Notification.requestPermission().catch(() => {});
      $("cmdInput").value = "";
      cmdHistory.push(input);
      if (cmdHistory.length > 10) cmdHistory.shift();
      cmdHistIdx = -1;
    } else if (r.status === 429) {
      hint.textContent = "命令通道已满，稍后重试";
      toast("命令通道已满", "err");
    } else {
      const d = await r.json().catch(() => ({}));
      hint.textContent = "提交失败：" + (d.error || r.status);
      toast("提交失败：" + (d.error || r.status), "err");
    }
  } catch (e) {
    hint.textContent = "提交失败：" + e.message;
    toast("提交失败：" + e.message, "err");
  } finally {
    sendBtn.classList.remove("is-busy");
  }
}
async function cancelCurrentTask() {
  const hint = $("cmdHint");
  try {
    const r = await fetch("/api/cancel", { method: "POST" });
    if (r.status === 200) {
      hint.textContent = "取消请求已发送（当前任务将在阶段间停止）";
      toast("取消请求已发送", "info");
    } else if (r.status === 409) {
      const d = await r.json().catch(() => ({}));
      hint.textContent = "无运行中任务：" + (d.error || "");
      toast("无运行中任务", "dim");
    } else {
      hint.textContent = "取消失败：" + r.status;
      toast("取消失败：" + r.status, "err");
    }
  } catch (e) {
    hint.textContent = "取消失败：" + e.message;
    toast("取消失败：" + e.message, "err");
  }
}
$("cmdSend").addEventListener("click", submitCommand);
$("cmdInput").addEventListener("keydown", e => {
  if (e.key === "Enter") { submitCommand(); return; }
  if (e.key === "ArrowUp") {
    e.preventDefault();
    if (!cmdHistory.length) return;
    cmdHistIdx = (cmdHistIdx < 0 ? cmdHistory.length - 1 : Math.max(0, cmdHistIdx - 1));
    $("cmdInput").value = cmdHistory[cmdHistIdx];
  } else if (e.key === "ArrowDown") {
    e.preventDefault();
    if (cmdHistIdx < 0) return;
    cmdHistIdx++;
    if (cmdHistIdx >= cmdHistory.length) { cmdHistIdx = -1; $("cmdInput").value = ""; }
    else $("cmdInput").value = cmdHistory[cmdHistIdx];
  }
});
$("cmdCancel").addEventListener("click", cancelCurrentTask);
// 队列可见性 + 任务归属：每 3 秒轮询 /api/queue + /api/current-task（数据单一来源在后端）
async function refreshQueue() {
  try {
    const [q, c] = await Promise.all([
      fetch("/api/queue").then(r => r.json()),
      fetch("/api/current-task").then(r => r.json()).catch(() => ({ running: false }))
    ]);
    const parts = [];
    if (c.running) parts.push(`▶ 当前任务：${trunc(c.input, 26)}（提交号 ${c.seq}）`);
    if (q.pending > 0) parts.push(`⏳ 队列中 ${q.pending} 个待执行`);
    $("queueStatus").textContent = parts.join("　");
  } catch { /* 服务未就绪时静默 */ }
}
function trunc(s, n) { return (s && s.length > n) ? s.slice(0, n) + "…" : s; }
setInterval(refreshQueue, 3000);
// ── 任务产物浏览（10-08 ①）：文件列表 + 面包屑 + 文本预览（数据源 /api/files）──
let filesDir = "";
async function loadFiles(dir) {
  filesDir = dir || "";
  try {
    const r = await fetch("/api/files?path=" + encodeURIComponent(filesDir));
    const d = await r.json();
    $("filesPath").textContent = "/" + filesDir;
    $("filesPreview").style.display = "none";
    $("filesPreviewClose").style.display = "none";
    if (!r.ok || d.error) { $("files").innerHTML = `<span class="dim">${d.error || r.status}</span>`; return; }
    if (d.isDir) {
      const rows = d.children.map(c => c.isDir
        ? `<span style="cursor:pointer;color:#79c0ff" onclick="loadFiles('${filesDir}/${c.name}')">📁 ${c.name}/</span>`
        : `<span style="cursor:pointer" onclick="previewFile('${filesDir}/${c.name}')">📄 ${c.name}</span> ` +
          `<span class="dim">(${fmtSize(c.size)})</span>`)
        .join("<br>");
      $("files").innerHTML = rows || `<span class="dim">（空目录）</span>`;
    } else if (d.text) {
      $("files").innerHTML = `<span class="dim">📄 ${d.path}（${fmtSize(d.size)}）</span> ` +
        `<a class="btn" style="cursor:pointer;font-size:11px" href="/api/files?path=${encodeURIComponent(filesDir)}&raw=true" download="${encodeURIComponent(d.path.split('/').pop())}">下载</a>`;
      $("filesPreview").style.display = "block";
      $("filesPreviewClose").style.display = "block";
      $("filesPreview").textContent = d.content;
    } else {
      $("files").innerHTML = `<span class="dim">📄 ${d.path}（${fmtSize(d.size)}）二进制文件，请本地打开</span>`;
    }
  } catch (e) { $("files").innerHTML = `<span class="dim">加载失败：${e.message}</span>`; }
}
async function previewFile(rel) {
  try {
    const r = await fetch("/api/files?path=" + encodeURIComponent(rel));
    const d = await r.json();
    if (!r.ok || d.error) { $("filesPreview").style.display = "block"; $("filesPreview").textContent = d.error || r.status; return; }
    if (d.text) {
      $("files").innerHTML = `<span class="dim">📄 ${d.path}（${fmtSize(d.size)}）</span> ` +
        `<a class="btn" style="cursor:pointer;font-size:11px" href="/api/files?path=${encodeURIComponent(rel)}&raw=true" download="${encodeURIComponent(d.path.split('/').pop())}">下载</a>`;
      $("filesPreview").style.display = "block";
      $("filesPreviewClose").style.display = "block";
      $("filesPreview").textContent = d.content;
    } else {
      $("filesPreview").style.display = "block";
      $("filesPreviewClose").style.display = "block";
      $("filesPreview").textContent = `二进制文件（${fmtSize(d.size)}），不支持预览，请本地打开：${d.path}`;
    }
  } catch (e) { $("filesPreview").style.display = "block"; $("filesPreviewClose").style.display = "block"; $("filesPreview").textContent = "加载失败：" + e.message; }
}
function fmtSize(n) {
  if (n == null) return "-";
  if (n < 1024) return n + " B";
  if (n < 1048576) return (n / 1024).toFixed(1) + " KB";
  return (n / 1048576).toFixed(1) + " MB";
}
$("filesRefresh").addEventListener("click", () => loadFiles(filesDir));
// P0-4：历史任务筛选（关键词 + 状态；回车/按钮同行为）
$("taskFilterBtn").addEventListener("click", fetchTasks);
$("taskQ").addEventListener("keydown", e => { if (e.key === "Enter") fetchTasks(); });
$("taskStatus").addEventListener("change", fetchTasks);
$("filesUp").addEventListener("click", () => {
  const idx = filesDir.lastIndexOf("/");
  loadFiles(idx > 0 ? filesDir.slice(0, idx) : "");
});
$("filesPreviewClose").addEventListener("click", () => {
  $("filesPreview").style.display = "none";
  $("filesPreviewClose").style.display = "none";
});
$("diffClear").addEventListener("click", () => {
  $("diff").innerHTML = `<div class="empty">已清空</div>`;
});

// ── 折叠卡片（10-10 UI 收纳）：点击标题展开/收起；首次展开才懒加载对应 API ──
const collapsibleLoads = {
  stats:   () => fetchStats(),
  tuning:  () => fetchTuning(),
  memory:  () => fetchMemory(),
  tasks:   () => fetchTasks(),
  backups: () => fetchBackups(),
};
function toggleCard(h2) {
  const card = h2.closest(".card");
  const open = card.classList.toggle("open");
  if (open) {
    const load = collapsibleLoads[card.dataset.load || ""];
    if (load && !card.dataset.loaded) { card.dataset.loaded = "1"; load(); }
  }
}

openStream();
refreshAll();
