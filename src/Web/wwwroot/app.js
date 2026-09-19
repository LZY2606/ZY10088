"use strict";

const api = async (path, opts = {}) => {
  const res = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...opts,
    body: opts.body ? JSON.stringify(opts.body) : undefined,
  });
  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) throw Object.assign(new Error("request failed"), { status: res.status, data });
  return data;
};

const el = (id) => document.getElementById(id);
const fmt = (v) => (v === null || v === undefined ? "?" : (typeof v === "number" ? formatNum(v) : v));
function formatNum(n) {
  if (n === 0) return "0";
  if (Math.abs(n) >= 1e6 || (Math.abs(n) < 1e-4 && n !== 0)) return n.toExponential(2);
  return Number(n.toFixed(6)).toString();
}
const interval = (lo, hi) => `[${fmt(lo)}, ${fmt(hi)}]`;
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const dt = (s) => s ? new Date(s).toLocaleString("zh-CN", { hour12: false }) : "-";

let state = null;
let graph = null;

async function refreshAll() {
  state = await api("/api/state");
  el("ruleVersion").textContent = state.ruleVersion;
  el("fingerprint").textContent = state.fingerprint;
  el("lastSeq").textContent = state.lastSeq;
  renderOverview();
  await Promise.all([renderTimeline(), renderGraph(), renderHypotheses(), renderConclusions()]);
}

function renderOverview() {
  el("plates").innerHTML = state.plates.map((p) =>
    `<div class="chip"><b>${esc(p.plateId)}</b> ${p.rows}×${p.cols}
      <span class="pill ${p.frozenAt ? "ok" : "muted"}">${p.frozenAt ? "已冻结 " + dt(p.frozenAt) : "未冻结"}</span></div>`).join("");
  el("reagents").innerHTML = state.reagentBatches.map((r) =>
    `<div class="chip"><b>${esc(r.batch)}</b> ${esc(r.name ?? "")}
      <span class="pill ${r.frozenAt ? "ok" : "muted"}">${r.frozenAt ? "版本 " + esc(r.frozenVersion) : "未冻结"}</span></div>`).join("");
  el("tips").innerHTML = state.tips.map((t) => {
    const bad = t.discarded || t.useSeqs.length > 1;
    return `<div class="chip"><b>${esc(t.tipId)}</b> 使用 ${t.useSeqs.length} 次
      <span class="pill ${bad ? "warn" : "ok"}">${t.discarded ? "已弃用" : (t.useSeqs.length === 0 ? "已装枪头" : "已用")}</span></div>`;
  }).join("");

  const errs = state.errors;
  el("errCount").textContent = errs.length ? `${errs.length} 条` : "";
  el("errors").querySelector("tbody").innerHTML = errs.map((e) =>
    `<tr><td><code>${esc(e.code)}</code></td><td>${esc(e.message)}</td>
     <td>${esc(e.eventId ?? "")}</td><td>${esc(e.wellId ?? "")}</td><td>${esc(e.tipId ?? "")}</td></tr>`).join("")
    || `<tr><td colspan="5" class="hint">当前工作投影无证据错误</td></tr>`;

  const f = el("wellFilter").value.trim().toLowerCase();
  const rows = state.wells.filter((w) => !f || w.wellId.toLowerCase().includes(f) || (w.sampleAlias ?? "").toLowerCase().includes(f));
  el("wells").querySelector("tbody").innerHTML = rows.map((w) => {
    const readings = w.readings.map((r) =>
      `<div>${esc(r.analyte)}: ${formatNum(r.valueBase)} ${esc(r.baseUnit)}
        <span class="kv">(原始 ${formatNum(r.originalValue)} ${esc(r.originalUnit)} · 来源 ${esc(r.source)}${r.thresholdHigh ? " · 阈值 " + r.thresholdHigh : ""})</span>
        ${r.isAbnormal ? '<span class="status-bad">异常</span>' : ""}</div>`).join("") || '<span class="hint">无读数</span>';
    const abnormal = w.abnormal ? '<span class="status-bad">异常孔</span>' : '<span class="status-ok">正常</span>';
    return `<tr><td><code>${esc(w.wellId)}</code></td><td>${esc(w.plateId)}</td><td>${esc(w.well)}</td>
      <td>${esc(w.sampleAlias ?? "")}</td><td>${interval(w.volumeLow, w.volumeHigh)}</td>
      <td>${w.reagentBatches.map(esc).join(", ")}</td><td>${readings}</td><td>${abnormal}</td></tr>`;
  }).join("");
}

// ---------- tabs ----------
document.querySelectorAll(".tabs button").forEach((b) => b.addEventListener("click", () => {
  document.querySelectorAll(".tabs button").forEach((x) => x.classList.remove("active"));
  document.querySelectorAll(".tab").forEach((x) => x.classList.remove("active"));
  b.classList.add("active");
  el("tab-" + b.dataset.tab).classList.add("active");
}));
el("refreshBtn").addEventListener("click", refreshAll);
el("wellFilter").addEventListener("input", renderOverview);
el("resetDemo").addEventListener("click", async () => {
  if (!confirm("将清空当前数据目录并重放演示数据，继续？")) return;
  await api("/api/reset-demo", { method: "POST" });
  await refreshAll();
});

// ---------- batch submission editor ----------
const KINDS = [
  ["PlateRegister", "注册板"], ["SampleAlias", "样本别名"], ["ReagentBatchRegister", "注册试剂批次"],
  ["ReagentBatchFreeze", "冻结试剂版本"], ["TipAttach", "装一次性枪头"], ["TipDiscard", "弃枪头"],
  ["LoadSample", "载入样本"], ["AddReagent", "加稀释液/试剂"], ["Transfer", "移液"], ["Mix", "混匀"],
  ["Reading", "读数"], ["PlateFreeze", "冻结板"], ["Correction", "纠正（替代事件）"],
];

function eventRowHtml(i) {
  const opts = KINDS.map(([k, label]) => `<option value="${k}">${label}</option>`).join("");
  return `<div class="event-block" data-i="${i}">
    <div class="row"><select class="f-kind">${opts}</select>
      <label>时间</label><input class="f-at" type="datetime-local" step="1" />
      <button class="ghost danger f-del">删除</button></div>
    <div class="f-fields"></div>
  </div>`;
}

function fieldsForKind(kind) {
  const num = (cls, ph) => `<input class="${cls}" type="number" step="any" placeholder="${ph}" />`;
  switch (kind) {
    case "PlateRegister": return `板ID <input class="f-plate" placeholder="P3"/> 行 ${num("f-rows", "8")} 列 ${num("f-cols", "12")}`;
    case "SampleAlias": return `板 <input class="f-plate"/> 孔 <input class="f-well" placeholder="A1"/> 别名 <input class="f-alias"/>`;
    case "ReagentBatchRegister": return `批次 <input class="f-reagent"/> 名称 <input class="f-rname"/>`;
    case "ReagentBatchFreeze": return `批次 <input class="f-reagent"/> 冻结版本 <input class="f-frozen" placeholder="DIL-A@r3"/>`;
    case "TipAttach": case "TipDiscard": return `枪头ID <input class="f-tip"/>`;
    case "LoadSample": return `板 <input class="f-plate"/> 孔 <input class="f-well"/> 别名 <input class="f-alias"/>
      体积下界 ${num("f-vlo", "uL")} 上界 ${num("f-vhi", "uL")}`;
    case "AddReagent": return `板 <input class="f-plate"/> 孔 <input class="f-well"/> 批次 <input class="f-reagent"/>
      体积下界 ${num("f-vlo", "uL")} 上界 ${num("f-vhi", "uL")}`;
    case "Transfer": return `源板 <input class="f-fp"/> 源孔 <input class="f-fw"/> 目标板 <input class="f-tp"/> 目标孔 <input class="f-tw"/>
      枪头 <input class="f-tip"/> 吸液下界 ${num("f-alo", "uL")} 上界 ${num("f-ahi", "uL")}`;
    case "Mix": return `板 <input class="f-plate"/> 孔 <input class="f-well"/>`;
    case "Reading": return `板 <input class="f-plate"/> 孔 <input class="f-well"/> 分析物 <input class="f-analyte" value="X"/>
      数值 ${num("f-value", "value")} 单位 <input class="f-unit" placeholder="pg/mL"/> 高阈值 ${num("f-thr", "500")} 来源 <input class="f-source" placeholder="reader-2"/>`;
    case "PlateFreeze": return `板ID <input class="f-plate"/>`;
    case "Correction": return `被替代事件ID <input class="f-sup"/> 理由 <input class="f-reason"/>
      <div class="hint">替代字段：与普通事件同名，填写在下方 JSON（可选，留空则只做声明）</div>
      <textarea class="f-repl" rows="3" style="width:100%" placeholder='{"kind":"Transfer","aspirateLow":10,"aspirateHigh":15}'></textarea>`;
    default: return "";
  }
}

function addEventRow(kind = "LoadSample") {
  const wrap = el("eventRows");
  const div = document.createElement("div");
  div.innerHTML = eventRowHtml(wrap.children.length);
  const block = div.firstElementChild;
  wrap.appendChild(block);
  block.querySelector(".f-kind").value = kind;
  block.querySelector(".f-fields").innerHTML = fieldsForKind(kind);
  block.querySelector(".f-kind").addEventListener("change", (e) =>
    (block.querySelector(".f-fields").innerHTML = fieldsForKind(e.target.value)));
  block.querySelector(".f-del").addEventListener("click", () => block.remove());
  const at = block.querySelector(".f-at");
  if (!at.value) {
    const d = new Date(Date.now() - new Date().getTimezoneOffset() * 60000);
    at.value = d.toISOString().slice(0, 19);
  }
}

const val = (block, cls) => {
  const n = block.querySelector("." + cls);
  return n && n.value !== "" ? n.value : null;
};
const numOrNull = (block, cls) => { const v = val(block, cls); return v === null ? null : Number(v); };

function collectEvents() {
  return [...el("eventRows").querySelectorAll(".event-block")].map((block) => {
    const kind = block.querySelector(".f-kind").value;
    const atRaw = val(block, "f-at");
    const d = {
      kind,
      occurredAt: atRaw ? new Date(atRaw).toISOString() : null,
      plateId: val(block, "f-plate"), well: val(block, "f-well"),
      rows: numOrNull(block, "f-rows"), cols: numOrNull(block, "f-cols"),
      alias: val(block, "f-alias"), reagentBatch: val(block, "f-reagent"), reagentName: val(block, "f-rname"),
      frozenVersion: val(block, "f-frozen"), tipId: val(block, "f-tip"),
      volumeLow: numOrNull(block, "f-vlo"), volumeHigh: numOrNull(block, "f-vhi"),
      aspirateLow: numOrNull(block, "f-alo"), aspirateHigh: numOrNull(block, "f-ahi"),
      fromPlate: val(block, "f-fp"), fromWell: val(block, "f-fw"),
      toPlate: val(block, "f-tp"), toWell: val(block, "f-tw"),
      analyte: val(block, "f-analyte"), source: val(block, "f-source"),
      value: numOrNull(block, "f-value"), unit: val(block, "f-unit"),
      thresholdHigh: numOrNull(block, "f-thr"),
      supersedesEventId: val(block, "f-sup"), reason: val(block, "f-reason"),
    };
    const repl = val(block, "f-repl");
    if (repl) {
      const parsed = JSON.parse(repl);
      d.replacement = parsed;
    }
    return d;
  });
}

el("addEvent").addEventListener("click", () => addEventRow());
el("submitBatch").addEventListener("click", async () => {
  let events;
  try { events = collectEvents(); }
  catch (e) { el("submitResult").textContent = "替代事件 JSON 解析失败：" + e.message; return; }
  const submission = {
    idempotencyKey: el("idemKey").value.trim() || null,
    note: el("batchNote").value,
    events,
  };
  try {
    const r = await api("/api/batches", { method: "POST", body: submission });
    el("submitResult").textContent = JSON.stringify(r, null, 2);
    if (r.accepted) { el("eventRows").innerHTML = ""; await refreshAll(); }
  } catch (e) {
    el("submitResult").textContent = JSON.stringify(e.data ?? { error: e.message }, null, 2);
  }
});
el("loadExample").addEventListener("click", () => {
  el("eventRows").innerHTML = "";
  const now = new Date(Date.now() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  addEventRow("TipAttach");
  addEventRow("Transfer");
  addEventRow("Transfer");
  const blocks = el("eventRows").querySelectorAll(".event-block");
  blocks[0].querySelector(".f-tip").value = "TX";
  const setBlock = (b, map) => Object.entries(map).forEach(([cls, v]) => { const n = b.querySelector("." + cls); if (n) n.value = v; });
  setBlock(blocks[1], { "f-fp": "P1", "f-fw": "B1", "f-tp": "P2", "f-tw": "A2", "f-tip": "TX", "f-alo": "20", "f-ahi": "20", "f-at": now + ":01" });
  setBlock(blocks[2], { "f-fp": "P2", "f-fw": "A2", "f-tp": "P2", "f-tw": "A3", "f-tip": "TX", "f-alo": "999", "f-ahi": "999", "f-at": now + ":02" });
  el("idemKey").value = "example-reuse-" + Date.now();
});
addEventRow("PlateRegister");

// ---------- graph / paths ----------
async function renderGraph() {
  const min = el("minFrac").value || "0.000001";
  graph = await api(`/graph?minFraction=${encodeURIComponent(min)}`);
  el("edges").querySelector("tbody").innerHTML = graph.edges.map((e) =>
    `<tr><td><code>${esc(e.eventId)}</code>${e.corrected ? '<span class="pill warn">替代</span>' : ""}</td>
     <td>${dt(e.occurredAt)}</td><td>${esc(e.from.id)}</td><td>${esc(e.to.id)}</td>
     <td>${interval(e.fraction.low, e.fraction.high)}</td>
     <td>${interval(e.aspirate.low, e.aspirate.high)}</td>
     <td>${esc(e.tipId ?? "")}</td></tr>`).join("")
    || `<tr><td colspan="7" class="hint">暂无移液边</td></tr>`;

  const byTarget = {};
  graph.paths.forEach((p) => (byTarget[p.target] ??= []).push(p));
  el("paths").innerHTML = Object.entries(byTarget).map(([target, paths]) =>
    `<details><summary>${esc(target)} — ${paths.length} 条符合阈值的路径</summary>${
      paths.map((p) => `<div class="pathbox"><span class="kv">源</span> <b>${esc(p.origin)}</b>
        <span class="kv">累计稀释范围</span> <b>${interval(p.dilutionLow, p.dilutionHigh)}</b>
        <div class="route">${p.edges.map((x) => esc(x.label)).join(" ⟹ ")}</div>
        <div class="kv">事件 ${p.edges.map((x) => esc(x.eventId)).join(" → ")} · 枪头 ${esc(p.edges.map((x) => x.tip).filter(Boolean).join(",")) || "-"}</div>
      </div>`).join("")}</details>`).join("") || '<p class="hint">没有符合阈值的路径</p>';

  const comps = await api("/readings/compare");
  el("comparisons").innerHTML = comps.length === 0
    ? '<p class="hint">没有同一孔/分析物的多来源读数。</p>'
    : comps.map((c) => `<div class="pathbox">
        <b>${esc(c.wellId)}</b> / ${esc(c.analyte)}
        <span class="pill ${c.consistency === "Consistent" ? "ok" : "warn"}">${c.consistency === "Consistent" ? "一致" : "冲突"}</span>
        <div class="kv">${esc(c.detail)}</div>
        <table><thead><tr><th>来源</th><th>时间</th><th>原始值</th><th>显式换算后</th></tr></thead><tbody>
        ${c.readings.map((r) => `<tr><td>${esc(r.source)}</td><td>${dt(r.occurredAt)}</td>
          <td>${formatNum(r.originalValue)} ${esc(r.originalUnit)}</td>
          <td>${formatNum(r.valueBase)} ${esc(r.baseUnit)}</td></tr>`).join("")}</tbody></table></div>`).join("");
}
el("applyFrac").addEventListener("click", renderGraph);

// ---------- hypotheses & jobs ----------
el("saveHypo").addEventListener("click", async () => {
  const body = {
    kind: el("hKind").value,
    description: el("hDesc").value,
    targetEventId: el("hEvent").value || null,
    reagentBatch: el("hReagent").value || null,
    tipId: el("hTip").value || null,
    analyte: el("hAnalyte").value,
    concentrationLow: Number(el("hClo").value), concentrationHigh: Number(el("hChi").value),
    carryFractionLow: Number(el("hFlo").value), carryFractionHigh: Number(el("hFhi").value),
    minPathFraction: Number(el("hMin").value),
  };
  try {
    await api("/hypotheses", { method: "POST", body });
    setTimeout(renderHypotheses, 900);
  } catch (e) { alert((e.data && e.data.error) || e.message); }
});
el("pumpJobs").addEventListener("click", async () => {
  await api("/jobs/pump", { method: "POST" });
  await renderHypotheses();
});

function expBlock(e) {
  if (!e) return "";
  const tag = e.status === "Definite" ? '<span class="tag definite">必然解释</span>'
    : e.status === "Possible" ? '<span class="tag possible">区间相容</span>'
    : '<span class="tag no">无法解释</span>';
  return `<div class="pathbox">${tag}<b>${esc(e.wellId)}</b> / ${esc(e.analyte)}
    <span class="kv">观测 ${fmt(e.observedValue)} ${esc(e.unit)}（阈值 ${fmt(e.threshold)}）</span>
    <span class="kv">预测区间 ${interval(e.predictedRange?.low, e.predictedRange?.high)}</span>
    <details><summary>${e.contributions.length} 条传播路径（区间计算）</summary>
    ${e.contributions.map((c) => `<div class="pathbox"><div class="route">${c.route.map(esc).join(" ⟹ ")}</div>
      <div class="kv">累计稀释 ${interval(c.dilution.low, c.dilution.high)} ·
      预测水平 ${interval(c.predictedLevel.low, c.predictedLevel.high)}</div></div>`).join("")}
    </details></div>`;
}

async function renderHypotheses() {
  const [hyps, jobs] = await Promise.all([api("/hypotheses"), api("/jobs")]);
  const byId = {};
  hyps.forEach((h) => { (byId[h.hypothesisId] ??= []).push(h); });

  el("hypoList").innerHTML = Object.entries(byId).map(([id, versions]) => {
    const latest = versions.sort((a, b) => b.version - a.version)[0];
    const job = jobs.filter((j) => (j.result?.hypothesisId === id && j.result?.hypothesisVersion === latest.version)
      || j.idempotencyKey === `eval:${id}:v${latest.version}`).sort((a, b) =>
      new Date(b.createdAt) - new Date(a.createdAt))[0];
    const r = job?.result;
    return `<details>
      <summary><b>${esc(id)}</b> v${latest.version} · ${latest.kind}
        ${latest.retired ? '<span class="pill warn">已废弃</span>' : ""}
        <span class="pill ${job?.state === "Completed" ? "ok" : "muted"}">${job ? job.state : "未评估"}</span>
        ${r?.explainsAllAnomalies ? '<span class="tag definite">解释全部异常</span>' : ""}</summary>
      <div class="kv">${esc(latest.description)} · 分析物 ${esc(latest.analyte)} ·
        事件 ${esc(latest.targetEventId ?? "-")} · 批次 ${esc(latest.reagentBatch ?? "-")} · 枪头 ${esc(latest.tipId ?? "-")}</div>
      ${r ? `
        <h4>被解释的异常孔（${r.explainedAnomalies.length}）</h4>${r.explainedAnomalies.map(expBlock).join("") || '<span class="hint">无</span>'}
        <h4>无法解释的异常孔（${r.unexplainedAnomalies.length}）</h4>${r.unexplainedAnomalies.map(expBlock).join("") || '<span class="hint">无</span>'}
        <h4>额外预测：读数正常却应为异常（${r.extraPredictedNormal.length}）</h4>${r.extraPredictedNormal.map(expBlock).join("") || '<span class="hint">无</span>'}
        <h4>额外预测：未读孔（${r.extraPredictedUnread.length}）</h4>${r.extraPredictedUnread.map(expBlock).join("") || '<span class="hint">无</span>'}
        <div class="kv">规则 ${esc(r.ruleVersion)} · 投影指纹 ${esc(r.projectionFingerprint)} · 作业 ${esc(r.jobId)}</div>`
        : `<div class="hint">评估${job ? "状态：" + job.state + (job.error ? "（" + esc(job.error) + "）" : "") : "尚未入队"}</div>`}
      <div class="row"><button class="ghost" onclick="retireHypo('${esc(id)}')">废弃此假设</button></div>
    </details>`;
  }).join("") || '<p class="hint">还没有假设。</p>';
}

window.retireHypo = async (id) => {
  const reason = prompt("废弃理由？", "与新证据不符");
  if (!reason) return;
  await fetch(`/api/hypotheses/${encodeURIComponent(id)}/retire`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ reason }),
  });
  renderHypotheses();
};

// ---------- conclusions ----------
el("publishC").addEventListener("click", async () => {
  const list = (s) => s.split(",").map((x) => x.trim()).filter(Boolean);
  const body = {
    title: el("cTitle").value,
    referencedPlates: list(el("cPlates").value),
    referencedReagentBatches: list(el("cReagents").value),
    referencedHypothesisIds: list(el("cHyps").value),
  };
  try {
    const r = await api("/conclusions", { method: "POST", body });
    el("publishResult").textContent = JSON.stringify(r, null, 2);
    renderConclusions();
  } catch (e) {
    el("publishResult").textContent = JSON.stringify(e.data ?? { error: e.message }, null, 2);
  }
});

async function renderConclusions() {
  const cs = await api("/conclusions");
  const groups = {};
  cs.forEach((c) => (groups[c.conclusionId] ??= []).push(c));
  el("conclusionList").innerHTML = Object.entries(groups).map(([id, revs]) => {
    const latest = revs.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt))[0];
    return `<details><summary><b>${esc(latest.title)}</b>
      <span class="tag ${latest.state === "Published" ? "pub" : "review"}">${latest.state === "Published" ? "已发布" : "需复核"}</span>
      <code>${esc(id)}</code></summary>
      <div class="kv">规则版本 <b>${esc(latest.ruleVersion)}</b> · 生成 ${dt(latest.createdAt)}</div>
      <div class="kv">板：${latest.referencedPlates.map(esc).join(", ")}<br/>试剂：${latest.referencedReagentBatches.map(esc).join(", ")}<br/>
      假设：${latest.referencedHypothesisRevisions.map(esc).join(", ")}</div>
      <div class="kv">冻结快照：<pre class="result">${esc(JSON.stringify(latest.freezeSnapshot, null, 2))}</pre></div>
      ${latest.reviewReasons.length ? `<div class="kv">复核原因：<ul>${latest.reviewReasons.map((x) => `<li>${esc(x)}</li>`).join("")}</ul>
        触发纠正：${latest.triggeredByCorrectionEventIds.map(esc).join(", ")}</div>` : ""}
      ${latest.snapshot ? `<details><summary>展开支撑快照（输入、规则版本、事件顺序、路径与区间计算）</summary>
        <div class="kv">导出格式 ${esc(latest.snapshot.formatVersion)} · 规则 ${esc(latest.snapshot.ruleVersion)} ·
        ${latest.snapshot.paths.length} 条路径 · ${latest.snapshot.batches.length} 个批次</div>
        <p><a href="/api/export" target="_blank">下载完整导出 JSON（含路径/区间/假设版本）</a></p></details>`
        : '<span class="hint">工作副本待复核，暂不附带快照；已发布的旧版本仍保留其不可变快照。</span>'}
    </details>`;
  }).join("") || '<p class="hint">尚未发布结论。</p>';
}

// ---------- timeline ----------
async function renderTimeline() {
  const t = await api("/timeline");
  el("timeline").innerHTML = t.rawLog.map((b) =>
    `<div class="event-block ${b.outcome === "Accepted" ? "accepted" : "rejected"}">
      <b>${esc(b.batchId)}</b> <span class="pill ${b.outcome === "Accepted" ? "ok" : "warn"}">${b.outcome}</span>
      <span class="kv">${dt(b.committedAt)} · 幂等键 ${esc(b.idempotencyKey || "-")} · 规则 ${esc(b.ruleVersion)}</span>
      <div>${esc(b.note ?? "")}</div>
      <details><summary>${b.events.length} 个事件（按提交顺序）</summary>${b.events.map((e) => `
        <div class="event-block ${t.supersededEventIds.includes(e.eventId) ? "rejected" : "accepted"}">
          <code>${e.seq}</code> <b>${e.kind}</b> <code>${esc(e.eventId)}</code>
          ${t.supersededEventIds.includes(e.eventId) ? '<span class="pill warn">已被纠正替代（工作投影）</span>' : ""}
          <div class="kv">时间 ${dt(e.occurredAt)} ${e.plate ? "· 板 " + esc(e.plate) + "/" + esc(e.well ?? "") : ""}
          ${e.from ? "· " + esc(e.from) + " → " + esc(e.to) : ""} ${e.tip ? "· 枪头 " + esc(e.tip) : ""}
          ${e.reagent ? "· 试剂 " + esc(e.reagent) : ""} ${e.analyte ? "· 分析物 " + esc(e.analyte) : ""}
          ${e.volume ? "· 体积 " + interval(e.volume.low, e.volume.high) : ""}
          ${e.aspirate ? "· 吸液 " + interval(e.aspirate.low, e.aspirate.high) : ""}
          ${e.value !== null ? "· 读数 " + e.value + " " + esc(e.unit ?? "") : ""}
          ${e.supersedes ? "· 替代 " + esc(e.supersedes) + "（" + esc(e.reason ?? "") + "）" : ""}</div>
        </div>`).join("")}</details>
      ${b.errors.length ? `<details><summary class="status-bad">${b.errors.length} 条证据错误</summary>
        ${b.errors.map((x) => `<div class="kv"><code>${esc(x.code)}</code> ${esc(x.message)}</div>`).join("")}</details>` : ""}
    </div>`).join("");
}

refreshAll();
setInterval(() => { if (document.getElementById("tab-hypo").classList.contains("active")) renderHypotheses(); }, 2500);
