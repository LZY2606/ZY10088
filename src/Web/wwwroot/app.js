const $ = (id) => document.getElementById(id);
const fmtIv = (s) => s;

async function api(path, opts) {
  const res = await fetch(path, opts);
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw Object.assign(new Error(body.error || res.statusText), { body });
  return body;
}

function exampleEvents() {
  const t = (m) => new Date(Date.UTC(2026, 8, 20, 9, m)).toISOString();
  return [
    { type: "plateRegistered", eventId: "e01", timestamp: t(0), plateId: "P1", rows: 8, cols: 12 },
    { type: "plateRegistered", eventId: "e02", timestamp: t(0), plateId: "P2", rows: 8, cols: 12 },
    { type: "reagentBatchRegistered", eventId: "e03", timestamp: t(0), batchId: "DIL-7", kind: "diluent", version: "2026-09-a" },
    { type: "volumeDeclared", eventId: "e04", timestamp: t(1), plateId: "P1", well: "A1", volumeUl: 200 },
    { type: "volumeDeclared", eventId: "e05", timestamp: t(1), plateId: "P1", well: "A2", volumeUl: null },
    { type: "sampleAliased", eventId: "e06", timestamp: t(1), plateId: "P1", well: "A1", alias: "样本-甲" },
    { type: "sampleAliased", eventId: "e07", timestamp: t(1), plateId: "P1", well: "A2", alias: "样本-乙" },
    { type: "transferRecorded", eventId: "e08", timestamp: t(2), source: { plateId: "P1", well: "A1" }, dest: { plateId: "P2", well: "A1" }, volumeUl: 50, tipId: "tip-1" },
    { type: "transferRecorded", eventId: "e09", timestamp: t(3), source: { plateId: "P1", well: "A2" }, dest: { plateId: "P2", well: "A2" }, volumeUl: 50, tipId: "tip-2" },
    { type: "transferRecorded", eventId: "e10", timestamp: t(4), source: { plateId: "P1", well: "A1" }, dest: { plateId: "P2", well: "B1" }, volumeUl: 20, tipId: "tip-3", diluentBatchId: "DIL-7" },
    { type: "mixRecorded", eventId: "e11", timestamp: t(5), plateId: "P2", well: "A1" },
    { type: "unitConversionDeclared", eventId: "e12", timestamp: t(6), fromUnit: "RFU", toUnit: "mRFU", factor: 1000 },
    { type: "readingRecorded", eventId: "e13", timestamp: t(7), plateId: "P2", well: "A1", value: 4200, unit: "RFU", source: "reader-1" },
    { type: "readingRecorded", eventId: "e14", timestamp: t(7), plateId: "P2", well: "A2", value: 0.8, unit: "mRFU", source: "reader-2" },
    { type: "readingRecorded", eventId: "e15", timestamp: t(8), plateId: "P2", well: "B1", value: 2600, unit: "RFU", source: "reader-1" },
    { type: "entityFrozen", eventId: "e16", timestamp: t(9), entityKind: "plate", entityId: "P1" },
    { type: "entityFrozen", eventId: "e17", timestamp: t(9), entityKind: "plate", entityId: "P2" },
    { type: "entityFrozen", eventId: "e18", timestamp: t(9), entityKind: "reagentBatch", entityId: "DIL-7" }
  ];
}

async function refresh() {
  const s = await api("/api/state");
  $("status-bar").textContent =
    `规则版本 ${s.ruleVersion} · 结论状态 ${s.conclusionStatus}` +
    (s.missingFreezes.length ? ` · 未冻结: ${s.missingFreezes.join(", ")}` : " · 引用已全部冻结");

  const el = $("error-list");
  el.innerHTML = "";
  for (const e of s.errors) {
    const li = document.createElement("li");
    li.className = "error";
    li.textContent = `[${e.kind}] ${e.message}`;
    el.appendChild(li);
  }
  if (!s.errors.length) el.innerHTML = '<li class="ok">无证据错误</li>';

  const tb = document.querySelector("#well-table tbody");
  tb.innerHTML = "";
  for (const w of s.wells) {
    const tr = document.createElement("tr");
    const reads = w.readings.map(r => `${r.value} ${r.unit} (${r.source})`).join("; ");
    tr.innerHTML = `<td>${w.well}</td><td>${w.alias ?? ""}</td><td>${fmtIv(w.initialVolume)}</td><td>${fmtIv(w.currentVolume)}</td><td>${reads}</td>`;
    tb.appendChild(tr);
  }

  const c = await api("/api/conclusion");
  $("conclusion-status").textContent = `状态: ${c.status}`;
  $("missing-freezes").innerHTML = s.missingFreezes.length
    ? `<span class="warn">发布前需冻结: ${s.missingFreezes.join(", ")}</span>` : "";

  const ev = await api("/api/events");
  $("evidence-body").textContent = JSON.stringify({
    ruleVersion: s.ruleVersion,
    conclusionStatus: c.status,
    published: c.published,
    batches: ev.batches,
    effectiveEventOrder: ev.effective,
    conversions: s.conversions,
  }, null, 2);

  const hy = await api("/api/hypotheses");
  const hl = $("hyp-list");
  hl.innerHTML = "";
  for (const h of hy.current) {
    const li = document.createElement("li");
    li.innerHTML = `<span class="pill">${h.kind} v${h.version}</span> ${h.eventId ?? h.batchId} ${h.note ?? ""}`;
    hl.appendChild(li);
  }
}

$("load-example").onclick = () => {
  $("batch-json").value = JSON.stringify(exampleEvents(), null, 2);
};

$("submit-batch").onclick = async () => {
  try {
    const events = JSON.parse($("batch-json").value);
    const text = JSON.stringify(events);
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
    const key = "batch-" + [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, "0")).join("").slice(0, 24);
    const r = await api("/api/batches", {
      method: "POST",
      headers: { "Content-Type": "application/json", "Idempotency-Key": key },
      body: text,
    });
    $("ingest-result").innerHTML = r.applied
      ? `<span class="ok">已提交 ${r.eventCount} 个事件${r.wasReplay ? "（幂等重放，未重复生效）" : ""}</span>`
      : `<span class="error">整批拒绝: ${r.rejectionReasons.join("; ")}</span>`;
    await refresh();
  } catch (e) {
    $("ingest-result").innerHTML = `<span class="error">${e.message}</span>`;
  }
};

$("run-trace").onclick = async () => {
  const q = `threshold=${$("threshold").value}&unit=${encodeURIComponent($("unit").value)}&minFraction=${$("min-fraction").value}`;
  const r = await api(`/api/trace?${q}`);
  const al = $("anomaly-list");
  al.innerHTML = "<h3>异常孔</h3>";
  for (const a of r.anomalies)
    al.innerHTML += `<div class="pill error">${a.well.plateId}!${a.well.well} ${a.alias ?? ""} = ${a.normalizedValue} ${a.normalizedUnit}</div>`;
  if (!r.anomalies.length) al.innerHTML += '<div class="ok">无超过阈值的读数</div>';
  for (const p of r.unitProblems)
    al.innerHTML += `<div class="warn">[单位不可比] ${p.message}</div>`;

  const pl = $("path-list");
  pl.innerHTML = "<h3>符合阈值的路径（含累计稀释范围）</h3>";
  for (const p of r.paths) {
    const steps = p.steps.map(s =>
      `${s.from.plateId}!${s.from.well} —[${s.eventId}, ${s.volume.lo.toFixed(1)}uL]→ ${s.to.plateId}!${s.to.well}`).join("<br>");
    const div = document.createElement("div");
    div.className = "path";
    div.innerHTML = `${steps || "(源头孔)"}<br>
      累计浓度因子: [${p.cumulativeConcentration.lo}, ${p.cumulativeConcentration.hi === null || p.cumulativeConcentration.hi > 1e308 ? "inf" : p.cumulativeConcentration.hi}]
      · 累计稀释范围: [${p.cumulativeDilution.lo}, ${p.cumulativeDilution.hi > 1e308 ? "inf" : p.cumulativeDilution.hi}]`;
    pl.appendChild(div);
  }
};

$("add-hyp").onclick = async () => {
  const kind = $("hyp-kind").value;
  const target = $("hyp-target").value.trim();
  const body = kind === "carryover"
    ? { kind, id: "hyp-" + target, eventId: target, note: $("hyp-note").value }
    : { kind, id: "hyp-" + target, batchId: target, note: $("hyp-note").value };
  await api("/api/hypotheses", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  await refresh();
};

$("run-analysis").onclick = async () => {
  const q = `threshold=${$("threshold").value}&unit=${encodeURIComponent($("unit").value)}`;
  const r = await api(`/api/analysis?${q}`);
  const el = $("analysis-result");
  let html = "<h3>假设评估</h3>";
  for (const ex of r.explanations)
    html += `<div>${ex.well.plateId}!${ex.well.well} ${ex.alias ?? ""}:
      ${ex.explained ? `<span class="ok">可解释 ← ${ex.explainedByHypothesisIds.join(", ")}</span>` : '<span class="warn">未被任何假设解释</span>'}</div>`;
  html += `<div>额外预测为异常的正常孔: ${r.extraPredictedNormalWells.length
    ? r.extraPredictedNormalWells.map(w => `<span class="pill warn">${w.plateId}!${w.well}</span>`).join("")
    : '<span class="ok">无</span>'}</div>`;
  el.innerHTML = html;
};

$("publish").onclick = async () => {
  try {
    const q = `threshold=${$("threshold").value}&unit=${encodeURIComponent($("unit").value)}`;
    const r = await api(`/api/publish?${q}`, { method: "POST" });
    $("conclusion-status").innerHTML = `<span class="ok">已发布 @ ${r.publishedAt}</span>`;
  } catch (e) {
    $("conclusion-status").innerHTML = `<span class="error">${e.message}</span>`;
  }
  await refresh();
};

$("export").onclick = async () => {
  const q = `threshold=${$("threshold").value}&unit=${encodeURIComponent($("unit").value)}`;
  const r = await api(`/api/export?${q}`);
  const blob = new Blob([JSON.stringify(r.document, null, 2)], { type: "application/json" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = "export.json";
  a.click();
};

$("batch-json").value = JSON.stringify(exampleEvents(), null, 2);
refresh();
