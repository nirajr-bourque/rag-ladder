'use strict';

// Signals for the inline watchdog in index.html. __ragAlive proves this file parsed and started;
// __ragBooted proves boot finished. Between them they distinguish "the script never ran" from
// "the script ran and hung", which are otherwise indistinguishable from the outside.
window.__ragAlive = true;

// RAG Ladder UI. Vanilla JS and fetch, no framework and no build step: F5 and nothing else.
// Everything asserted on a slide must be reachable by clicking something here, so each panel
// exposes the raw evidence — scores, ranks, evidence spans, traversal paths, the assembled prompt.

const state = {
  docId: localStorage.getItem('ragladder.docId') || '',
  stage: 1,
  stages: [],
  golden: null,
  extraction: null,
  graph: null,
  people: [],
  present: new URLSearchParams(location.search).get('present') === '1',
};

// ---------------------------------------------------------------- helpers

const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text !== undefined) n.textContent = text;
  return n;
};
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmt = (n, d = 3) => (n === null || n === undefined ? '—' : Number(n).toFixed(d));
const pct = (n) => (n === null || n === undefined ? '—' : (Number(n) * 100).toFixed(0) + '%');

function toast(message, bad) {
  document.querySelectorAll('.toast').forEach((t) => t.remove());
  const t = el('div', 'toast' + (bad ? ' bad' : ''), message);
  document.body.appendChild(t);
  setTimeout(() => t.remove(), bad ? 9000 : 4500);
}

async function api(path, options) {
  const response = await fetch(path, options);
  const text = await response.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = { raw: text }; }
  if (!response.ok) {
    const message = body?.error || body?.title || `${response.status} ${response.statusText}`;
    throw new Error(message);
  }
  return body;
}
const post = (path, body) => api(path, {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: body === undefined ? '{}' : JSON.stringify(body),
});

function requireDoc() {
  if (!state.docId) { toast('Select or load a document first.', true); return false; }
  return true;
}

// ---------------------------------------------------------------- shell

function initTabs() {
  $('tabs').addEventListener('click', (e) => {
    const button = e.target.closest('button[data-tab]');
    if (!button) return;
    document.querySelectorAll('nav.tabs button').forEach((b) => b.classList.toggle('active', b === button));
    document.querySelectorAll('section.panel').forEach((p) =>
      p.classList.toggle('active', p.id === 'panel-' + button.dataset.tab));
    onTabShown(button.dataset.tab);
  });
}

function onTabShown(tab) {
  if (tab === 'review') loadExtraction();
  if (tab === 'graph') { loadGraph(); loadAggPresets(); }
  if (tab === 'explore') loadPeople();
  if (tab === 'eval') loadGoldenSummary();
  if (tab === 'process') loadDocDetail();
}

async function loadHealth() {
  try {
    const health = await api('/api/health');
    renderHealth(health);
  } catch (ex) {
    $('healthPill').className = 'pill bad';
    $('healthPill').textContent = 'health unreachable';
    $('healthDetail').textContent = ex.message;
  }
}

function renderHealth(health) {
  const cls = { ok: 'ok', degraded: 'warn', paused: 'warn', unhealthy: 'bad' }[health.status] || 'warn';
  $('healthPill').className = 'pill ' + cls;
  $('healthPill').textContent = 'health: ' + health.status;

  const wrap = el('div');
  const table = el('table');
  table.innerHTML = '<thead><tr><th>Provider</th><th>Status</th><th>Detail</th></tr></thead>';
  const body = el('tbody');
  health.providers.forEach((p) => {
    const tr = el('tr');
    const statusClass = p.status === 'ok' ? 'ok' : p.status === 'unreachable' ? 'bad' : 'warn';
    tr.innerHTML = `<td class="nowrap">${esc(p.name)}</td>` +
      `<td><span class="pill ${statusClass}">${esc(p.status)}</span></td>` +
      `<td class="muted">${esc(p.detail)}</td>`;
    body.appendChild(tr);
  });
  table.appendChild(body);
  wrap.appendChild(table);

  const probe = health.embedder;
  const probeLine = el('p', probe.passed ? 'muted' : 'warnings');
  probeLine.innerHTML = `<b>Embedder probe</b> — similar pair <span class="mono">${fmt(probe.similarPair)}</span>` +
    ` (want &gt; 0.7), unrelated pair <span class="mono">${fmt(probe.unrelatedPair)}</span> (want &lt; 0.3): ` +
    `<span class="pill ${probe.passed ? 'ok' : 'warn'}">${probe.passed ? 'pass' : 'below band'}</span><br>` +
    `<span class="faint">${esc(probe.detail)}</span>`;
  wrap.appendChild(probeLine);

  const caches = health.caches;
  wrap.appendChild(el('p', 'faint',
    `Caches: ${caches.embeddings} embeddings, ${caches.extractions} extractions, ` +
    `${caches.chatResponses} chat responses. ${caches.hits} hits / ${caches.misses} misses this session.`));

  $('healthDetail').replaceChildren(wrap);
}

// ---------------------------------------------------------------- documents

async function loadDocuments() {
  const docs = await api('/api/documents');
  const select = $('docSelect');
  select.replaceChildren(el('option', '', '(none loaded)'));
  select.firstChild.value = '';
  docs.forEach((d) => {
    const option = el('option', '', `${d.title} · ${d.status}`);
    option.value = d.id;
    select.appendChild(option);
  });
  if (state.docId && docs.some((d) => d.id === state.docId)) select.value = state.docId;
  else if (docs.length) { select.value = docs[0].id; setDoc(docs[0].id); }

  const list = el('div');
  if (!docs.length) list.appendChild(el('p', 'muted', 'No documents yet. Load the demo corpus above.'));
  docs.forEach((d) => {
    const card = el('div', 'triple');
    card.innerHTML =
      `<div class="head"><b>${esc(d.title)}</b>` +
      `<span class="pill ${d.status === 'committed' ? 'ok' : d.status === 'failed' ? 'bad' : 'warn'}">${esc(d.status)}</span>` +
      (d.graphCommitted ? '<span class="pill graph">graph committed</span>' : '') +
      `<span class="faint mono">${esc(d.id)}</span>` +
      `<span class="faint">${d.pageCount} pages</span></div>` +
      `<div class="faint">${esc(d.fileName)} · uploaded ${new Date(d.uploadedUtc).toLocaleString()}</div>`;
    const row = el('div', 'row hide-in-present');
    row.style.marginTop = '6px';
    const use = el('button', 'btn', 'Use');
    use.onclick = () => { setDoc(d.id); $('docSelect').value = d.id; toast('Selected ' + d.title); };
    const del = el('button', 'btn danger', 'Delete');
    del.onclick = async () => {
      if (!confirm(`Delete ${d.title}? This removes its chunks, vectors and graph nodes.`)) return;
      await api('/api/documents/' + d.id, { method: 'DELETE' });
      if (state.docId === d.id) setDoc('');
      loadDocuments();
    };
    row.append(use, del);
    card.appendChild(row);
    list.appendChild(card);
  });
  $('docList').replaceChildren(list);
}

function setDoc(id) {
  state.docId = id;
  localStorage.setItem('ragladder.docId', id);
  state.extraction = null;
  state.people = [];
  loadDocDetail();
}

async function loadDocDetail() {
  if (!state.docId) { $('procSections').textContent = '—'; return; }
  try {
    const detail = await api('/api/documents/' + state.docId);
    const counts = Object.entries(detail.chunkCounts || {}).map(([k, v]) => `${k}: ${v}`).join(' · ') || 'not chunked yet';
    const table = el('table');
    table.innerHTML = '<thead><tr><th>#</th><th>Heading</th><th>docType</th><th>subject</th><th>year</th><th>page</th><th>summary</th></tr></thead>';
    const body = el('tbody');
    (detail.sections || []).slice(0, 200).forEach((s) => {
      const tr = el('tr');
      tr.innerHTML = `<td class="num">${s.ordinal}</td><td>${esc(s.heading)}</td>` +
        `<td class="mono">${esc(s.frontMatter?.docType ?? '—')}</td>` +
        `<td>${esc(s.frontMatter?.subject ?? '—')}</td>` +
        `<td class="num">${s.frontMatter?.year ?? '—'}</td><td class="num">${s.page}</td>` +
        `<td class="faint">${esc(s.summary ?? '')}</td>`;
      body.appendChild(tr);
    });
    table.appendChild(body);
    const wrap = el('div');
    wrap.appendChild(el('p', 'muted', `Chunks — ${counts}`));
    wrap.appendChild(table);
    $('procSections').replaceChildren(wrap);
  } catch (ex) {
    $('procSections').textContent = ex.message;
  }
}

// ---------------------------------------------------------------- process

let pollTimer = null;

async function startProcessing() {
  if (!requireDoc()) return;
  const body = {
    mode: $('procMode').value,
    skipReview: $('procSkipReview').checked,
    skipExtraction: $('procSkipExtraction').checked,
    skipSectionSummaries: $('procSkipSummaries').checked,
    chunkCap: Number($('procCap').value) || null,
    spreadSampling: $('procSpread').checked,
  };
  await post(`/api/documents/${state.docId}/process`, body);
  toast('Processing started.');
  pollStatus();
}

async function pollStatus() {
  clearTimeout(pollTimer);
  if (!state.docId) return;
  try {
    const status = await api(`/api/documents/${state.docId}/status`);
    const job = status.job;
    if (job) {
      $('procStatus').innerHTML =
        `<b>${esc(job.stage)}</b> — step ${job.stepIndex + 1}/${job.stepCount} · ${esc(job.message || '')}` +
        (job.awaitingReview ? ' <span class="pill warn">awaiting review</span>' : '') +
        (job.completed ? ' <span class="pill ok">complete</span>' : '') +
        (job.failed ? ' <span class="pill bad">failed</span>' : '');
      $('procBar').style.width = Math.round((job.progress || 0) * 100) + '%';
      const warnings = el('ul', 'warnings');
      (job.warnings || []).forEach((w) => warnings.appendChild(el('li', '', w)));
      $('procWarnings').replaceChildren(...warnings.childNodes);

      if (!job.completed && !job.failed && !job.awaitingReview) {
        pollTimer = setTimeout(pollStatus, 1200);
      } else {
        loadDocuments();
        loadDocDetail();
        if (job.awaitingReview) { loadExtraction(); toast('Processing paused at the review gate.'); }
      }
    } else {
      $('procStatus').textContent = 'idle — status: ' + status.status;
    }
  } catch (ex) {
    $('procStatus').textContent = ex.message;
  }
}

// ---------------------------------------------------------------- review

async function loadExtraction() {
  if (!state.docId) return;
  try {
    state.extraction = await api(`/api/documents/${state.docId}/extraction`);
  } catch (ex) {
    $('revTriples').textContent = ex.message;
    $('revFunnel').replaceChildren();
    return;
  }
  renderReview(state.extraction);
}

function renderReview(data) {
  const f = data.funnel;
  const steps = [
    ['extracted', f.extracted], ['grounded', f.grounded], ['conformant', f.conformant],
    ['non-dangling', f.nonDangling], ['resolved', f.resolved], ['deduplicated', f.deduplicated],
    ['verified', f.verified],
  ];
  $('revFunnel').replaceChildren(...steps.map(([name, value]) => {
    const step = el('div', 'step');
    step.appendChild(el('b', '', String(value ?? 0)));
    step.appendChild(el('span', '', name));
    return step;
  }));
  const drops = Object.entries(f.drops || {}).map(([k, v]) => `${k}: ${v}`).join(' · ');
  $('revFunnelNote').textContent =
    `${f.extracted} proposed → ${data.relations.length} surviving (${f.flipped} direction flips corrected). ` +
    (drops ? 'Drops — ' + drops : '');

  const metrics = el('div');
  (data.metrics ? healthRows(data.metrics) : []).forEach((row) => {
    const line = el('div', 'metric');
    line.appendChild(el('span', 'name', row.name));
    const value = el('span', 'val');
    value.innerHTML = `${row.display} <span class="pill ${row.healthy ? 'ok' : 'warn'}">${esc(row.range)}</span>`;
    line.appendChild(value);
    metrics.appendChild(line);
  });
  metrics.appendChild(el('p', 'faint',
    `${data.metrics.chunksProcessed} chunks · ${data.metrics.chatCalls} live model calls · ` +
    `${data.metrics.cachedChunks} served from the extraction cache · ${data.metrics.skippedChunks} skipped.`));
  $('revMetrics').replaceChildren(metrics);

  const merges = el('div');
  merges.appendChild(el('p', 'muted',
    `Person/Character collisions blocked by the type barrier: ${data.metrics.personCharacterCollisionBlocks}. ` +
    `Names shared across any two node types: ${data.metrics.crossTypeNameCollisions}. ` +
    `A performer, a character and a series sharing one name are three nodes, always — enforced in code, ` +
    `not by a similarity threshold.`));
  if (!data.mergeCandidates.length) {
    merges.appendChild(el('p', 'faint', 'No ambiguous person merges were flagged for a human decision.'));
  }
  data.mergeCandidates.forEach((candidate) => {
    const card = el('div', 'triple');
    card.innerHTML = `<div class="head"><b>${esc(candidate.leftName)}</b> <span class="faint">possible duplicate of</span> ` +
      `<b>${esc(candidate.rightName)}</b> <span class="pill">${esc(candidate.type)}</span></div>` +
      `<div class="ev">${esc(candidate.reason)}</div>`;
    const row = el('div', 'row hide-in-present');
    ['merge', 'keep'].forEach((decision) => {
      const button = el('button', 'btn', decision);
      button.onclick = async () => {
        await post(`/api/documents/${state.docId}/review/merge`,
          { leftKey: candidate.leftKey, rightKey: candidate.rightKey, decision });
        toast(`Recorded: ${decision}.`);
      };
      row.appendChild(button);
    });
    card.appendChild(row);
    merges.appendChild(card);
  });
  $('revMerges').replaceChildren(merges);

  const list = el('div');
  data.relations.slice(0, 400).forEach((r) => {
    const card = el('div', 'triple' + (r.rejected ? ' rejected' : ''));
    const verdictClass = r.verdict === 'SUPPORTED' ? 'ok' : r.verdict === 'PARTIAL' ? 'warn' : r.verdict ? 'bad' : '';
    card.innerHTML =
      `<div class="head"><b>${esc(r.subjectName)}</b> <span class="mono">──${esc(r.predicate)}──&gt;</span> ` +
      `<b>${esc(r.objectName)}</b>` +
      `<span class="pill ${r.belowFloor ? 'warn' : ''}">${fmt(r.confidence, 2)}</span>` +
      `<span class="pill">×${r.mentionCount}</span>` +
      (r.flipped ? '<span class="pill warn">direction flipped</span>' : '') +
      (r.verdict ? `<span class="pill ${verdictClass}">${esc(r.verdict)}</span>` : '') +
      `</div>` +
      `<div class="ev">“${esc(r.evidence)}”</div>` +
      `<div class="faint mono">p.${r.page} · ${esc((r.chunkIds || []).join(', '))}` +
      (r.verdictReason ? ` · ${esc(r.verdictReason)}` : '') + `</div>`;

    const row = el('div', 'row hide-in-present');
    row.style.marginTop = '5px';
    const accept = el('button', 'btn', '✓ accept');
    accept.onclick = () => decide({ accept: [r.tripleHash] });
    const reject = el('button', 'btn danger', '✗ reject');
    reject.onclick = () => decide({ reject: [r.tripleHash] });
    const source = el('button', 'btn', '↗ source');
    source.onclick = () => showSource(r);
    row.append(accept, reject, source);
    card.appendChild(row);
    list.appendChild(card);
  });
  $('revTriples').replaceChildren(list);

  const rejected = data.relations.filter((r) => r.rejected).length;
  $('revCounts').textContent = `${data.relations.length - rejected} accepted / ${rejected} rejected of ${data.relations.length}`;
}

function healthRows(m) {
  const rows = [
    ['Grounding pass rate', m.groundingPassRate, '> 0.70', m.groundingPassRate > 0.7, pct(m.groundingPassRate)],
    ['Conformance rate', m.conformanceRate, '> 0.90', m.conformanceRate > 0.9, pct(m.conformanceRate)],
    ['Direction flip rate', m.directionFlipRate, '< 0.15', m.directionFlipRate < 0.15, pct(m.directionFlipRate)],
    ['Verification pass rate', m.verificationPassRate, '> 0.75', m.verificationPassRate > 0.75, pct(m.verificationPassRate)],
    ['Entity merge ratio', m.entityMergeRatio, '1.3 – 2.8', m.entityMergeRatio >= 1.3 && m.entityMergeRatio <= 2.8, fmt(m.entityMergeRatio, 2)],
    ['RELATED_TO share', m.relatedToShare, '< 0.20', m.relatedToShare < 0.2, pct(m.relatedToShare)],
    ['Orphan entity rate', m.orphanEntityRate, '< 0.25', m.orphanEntityRate < 0.25, pct(m.orphanEntityRate)],
    ['Triples per chunk', m.triplesPerChunk, '1.5 – 4.0', m.triplesPerChunk >= 1.5 && m.triplesPerChunk <= 4, fmt(m.triplesPerChunk, 2)],
    ['Human rejection rate', m.humanRejectionRate, '< 0.15', m.humanRejectionRate < 0.15, pct(m.humanRejectionRate)],
  ];
  return rows.map(([name, , range, healthy, display]) => ({ name, range, healthy, display }));
}

async function decide(payload) {
  await post(`/api/documents/${state.docId}/review/decisions`, payload);
  loadExtraction();
}

async function showSource(relation) {
  const id = (relation.chunkIds || [])[0];
  if (!id) { toast('No source chunk recorded for this triple.', true); return; }
  const data = await api(`/api/documents/${state.docId}/chunks?take=500`);
  const chunk = (data.chunks || []).find((c) => c.id === id);
  if (!chunk) { toast('Source chunk not found.', true); return; }
  const highlighted = highlight(chunk.rawText, relation.evidence);
  const card = el('div', 'chunk expanded');
  card.innerHTML = `<div class="meta"><span class="mono">${esc(chunk.id)}</span><span>page ${chunk.page}</span></div>` +
    `<div class="body">${highlighted}</div>`;
  $('revTriples').prepend(card);
  card.scrollIntoView({ behavior: 'smooth', block: 'center' });
}

function highlight(text, span) {
  if (!span) return esc(text);
  const index = text.toLowerCase().indexOf(String(span).toLowerCase());
  if (index < 0) return esc(text);
  return esc(text.slice(0, index)) + '<mark>' + esc(text.slice(index, index + span.length)) + '</mark>' +
    esc(text.slice(index + span.length));
}

// ---------------------------------------------------------------- ask

async function loadStages() {
  state.stages = await api('/api/stages');
  const bar = $('stageBar');
  bar.replaceChildren();
  state.stages.forEach((s) => {
    const button = el('button');
    button.innerHTML = `${s.number}<small>${esc(s.name)}</small>`;
    button.dataset.stage = s.number;
    button.onclick = () => selectStage(s.number);
    bar.appendChild(button);
  });
  [$('cmpLeft'), $('cmpRight')].forEach((select, i) => {
    select.replaceChildren();
    state.stages.forEach((s) => {
      const option = el('option', '', `${s.number} — ${s.name}`);
      option.value = s.number;
      select.appendChild(option);
    });
    select.value = i === 0 ? 1 : 2;
  });
  selectStage(state.stage);
}

function selectStage(n) {
  state.stage = n;
  document.querySelectorAll('#stageBar button').forEach((b) =>
    b.classList.toggle('active', Number(b.dataset.stage) === n));
  const def = state.stages.find((s) => s.number === n);
  if (def) {
    $('stageTeaches').innerHTML =
      `<b>${esc(def.name)}</b> — ${esc(def.teaches)}. <span class="mono">${esc(def.optionSummary)}</span>` +
      (def.trapsFixed?.length ? ` · fixes trap ${def.trapsFixed.join(', ')}` : '');
  }
  markWarmStages();
}

/// Rings the stage buttons whose answer for the question currently in the box is already cached,
/// so before a demo you can see at a glance which rungs will replay instantly and which will not.
async function markWarmStages() {
  const question = ($('askQuestion')?.value || '').trim();
  document.querySelectorAll('#stageBar button').forEach((b) => b.classList.remove('warm'));
  const note = $('askCacheNote');
  if (!state.docId || !question) { if (note) note.textContent = ''; return; }

  try {
    const data = await api(`/api/ask/cache?documentId=${encodeURIComponent(state.docId)}`);
    const warm = new Set((data.answers || [])
      .filter((a) => a.question.trim().toLowerCase() === question.toLowerCase() && a.stage !== null)
      .map((a) => a.stage));
    document.querySelectorAll('#stageBar button').forEach((b) => {
      b.classList.toggle('warm', warm.has(Number(b.dataset.stage)));
    });
    if (note) {
      note.textContent = warm.size
        ? `${warm.size} of ${state.stages.length} rungs cached for this question · ${data.count}/${data.limit} answers held`
        : `not cached yet · ${data.count}/${data.limit} answers held`;
    }
  } catch {
    if (note) note.textContent = '';
  }
}

function readOptions() {
  return {
    collection: $('optCollection').value,
    topK: Number($('optTopK').value),
    candidateK: Number($('optCandidateK').value),
    useMetadataFilter: $('optFilter').checked,
    filter: {},
    useHybrid: $('optHybrid').checked,
    useRerank: $('optRerank').checked,
    useQueryRewrite: $('optRewrite').checked,
    requireCitations: $('optCitations').checked,
    useGraphExpansion: $('optGraph').checked,
    graphMode: $('optGraphMode').value,
    graphHops: {
      next: $('optHopNext').checked, parent: $('optHopParent').checked,
      entity: $('optHopEntity').checked, entityRel: $('optHopRel').checked,
    },
    maxPathHops: Number($('optMaxHops').value),
    minEdgeConfidence: Number($('optMinConf').value),
    includeDerivedEdges: $('optDerived').checked,
    useAgentic: $('optAgentic').checked,
    useRouter: $('optRouter').checked,
    skipRetrieval: $('optSkipRetrieval').checked,
  };
}

// ---------------------------------------------------------------- chat

function chatTurn(role) {
  $('chatEmpty')?.remove();
  const turn = el('div', 'turn ' + role);
  $('chatLog').appendChild(turn);
  $('chatLog').scrollTop = $('chatLog').scrollHeight;
  return turn;
}

function stageLabel(n) {
  const def = state.stages.find((s) => s.number === n);
  return def ? `stage ${n} · ${def.name}` : `stage ${n}`;
}

async function ask(custom) {
  if (!requireDoc()) return;
  const box = $('askQuestion');
  const question = box.value.trim();
  if (!question) { toast('Type a question.', true); return; }

  const stage = state.stage;

  const you = chatTurn('you');
  you.appendChild(Object.assign(el('div', 'byline'), {
    innerHTML: `<span class="pill">${esc(custom ? 'custom flags' : stageLabel(stage))}</span>`,
  }));
  you.appendChild(el('div', 'bubble', question));

  box.value = '';
  box.style.height = 'auto';

  const bot = chatTurn('bot');
  const pending = el('div', 'bubble pending', 'thinking…');
  bot.appendChild(pending);
  $('chatLog').scrollTop = $('chatLog').scrollHeight;

  $('btnAsk').disabled = true;
  try {
    const response = custom
      ? await post('/api/ask', { documentId: state.docId, question, options: readOptions() })
      : await post(`/api/ask/stage/${stage}`, { documentId: state.docId, question });
    renderChatAnswer(bot, pending, response);
  } catch (ex) {
    pending.className = 'bubble refused';
    pending.textContent = ex.message;
    toast(ex.message, true);
  } finally {
    $('btnAsk').disabled = false;
    $('chatLog').scrollTop = $('chatLog').scrollHeight;
    markWarmStages();
  }
}

function renderChatAnswer(turn, pending, r) {
  pending.className = 'bubble' + (r.refused ? ' refused' : '') + (r.unconstrained ? ' unconstrained' : '');
  pending.textContent = r.answer || '(empty)';

  const t = r.timings || {};
  const bits = [`<span class="pill">${esc(stageLabel(r.stage ?? state.stage))}</span>`];
  if (r.unconstrained) bits.push('<span class="pill bad">unconstrained — not from the document</span>');
  if (r.refused) bits.push('<span class="pill warn">refused</span>');
  if (r.fromCache) bits.push('<span class="pill">cached</span>');
  if (r.retrieval) bits.push(`${r.retrieval.chunks.length} chunk(s) · ${esc(r.retrieval.collection)}`);
  if (r.graph?.path) bits.push(`<span class="pill graph">${r.graph.path.hops} hops</span>`);
  if (r.groundedness !== null && r.groundedness !== undefined) bits.push(`groundedness ${pct(r.groundedness)}`);
  bits.push(`${((t.totalMs ?? 0) / 1000).toFixed(1)}s`);

  const byline = el('div', 'byline');
  byline.innerHTML = bits.join(' · ');
  turn.appendChild(byline);

  // Evidence stays one click away rather than gone: the demo's whole claim is that every
  // assertion is inspectable. Hidden by default so the chat reads as a chat.
  if ($('chatShowWork').checked || r.warnings?.length) {
    const work = el('details', 'work');
    if ($('chatShowWork').checked) work.open = true;
    work.appendChild(el('summary', '', 'show the work — what this rung did, retrieval, graph, prompt'));
    work.appendChild(renderStageWork(r));
    work.appendChild(renderAnswer(r, true, true));
    turn.appendChild(work);
  }
}

/// The panel that makes every rung legible, not just the ones with a graph or a rewrite.
///
/// Stages 1 to 5 differ only in flags and retrieval numbers, so without this they all render as
/// "here are five chunks" and the ladder looks like it is doing nothing. This states what changed
/// versus the rung below, which pipeline steps actually ran, and where the time went.
function renderStageWork(r) {
  const card = el('div', 'card');
  const n = r.stage ?? state.stage;
  const def = state.stages.find((s) => s.number === n);
  const previous = state.stages.find((s) => s.number === n - 1);

  card.innerHTML = `<h2>What stage ${n ?? '—'} did</h2>` +
    (def?.teaches ? `<p class="muted">${esc(def.teaches)}</p>` : '');

  // optionSummary is already the delta for the rung, not its cumulative flag set, so it reads
  // directly as "what is new here" without any diffing.
  if (def?.optionSummary) {
    card.appendChild(el('p', 'faint',
      previous
        ? `New at this rung, on top of stage ${previous.number} (${previous.name}): ${def.optionSummary}. Everything below still applies — the ladder is cumulative.`
        : `Flags: ${def.optionSummary}.`));
  }
  if (def?.trapsFixed?.length) {
    card.appendChild(el('p', 'faint', `Traps this rung is meant to fix: ${def.trapsFixed.join(', ')}.`));
  }

  // The pipeline as it actually ran for this answer, with the steps that were skipped shown as
  // skipped rather than omitted — an absent step is the most informative thing about a low rung.
  const o = r.options || {};
  const t = r.timings || {};
  const steps = [
    ['Route the question', !!r.router, r.router ? `classified ${r.router.classification} → ${r.router.route}` : 'no router at this rung'],
    ['Rewrite the query', !!r.rewrite, r.rewrite ? `"${r.rewrite.rewritten}"` : 'the question is searched verbatim', t.rewriteMs],
    ['Filter by metadata', !!r.retrieval?.filterApplied, r.retrieval?.filterApplied ? JSON.stringify(r.retrieval.filter, (k, v) => (v === null ? undefined : v)) : 'every chunk is a candidate'],
    ['Embed and search', !!r.retrieval, r.retrieval ? `${esc(r.retrieval.collection)} collection, ${r.retrieval.candidateCount} candidates` : 'no retrieval — stage 0 answers from memory', (t.embedMs || 0) + (t.searchMs || 0)],
    ['Keyword arm (hybrid)', !!r.retrieval?.hybrid, r.retrieval?.hybrid ? 'BM25 fused with vectors by RRF' : 'vector search only'],
    ['Rerank', !!r.retrieval?.reranked, r.retrieval?.reranked ? `${r.retrieval.candidateCount} candidates rescored down to ${r.retrieval.topK}` : 'search order is kept as-is', t.rerankMs],
    ['Agentic loop', !!(r.trace && r.trace.length), r.trace?.length ? `${r.trace.length} iteration(s)` : 'single-shot retrieval'],
    ['Graph traversal', !!r.graph, r.graph ? `mode ${r.graph.mode}, ${r.graph.edgesTraversed?.length ?? 0} edge(s)` : 'the graph is not consulted', t.graphMs],
    ['Generate', true, `${t.chatCalls ?? 0} model call(s)`, t.generateMs],
    ['Check citations', !!o.requireCitations, o.requireCitations ? `${r.citations?.length ?? 0} citation(s), groundedness ${pct(r.groundedness)}` : 'the answer is not citation-checked'],
  ];

  const table = el('table');
  table.innerHTML = '<thead><tr><th>Step</th><th>Ran</th><th>What happened</th><th>ms</th></tr></thead>';
  const body = el('tbody');
  steps.forEach(([label, ran, detail, ms]) => {
    const tr = el('tr');
    if (!ran) tr.className = 'faint';
    tr.innerHTML = `<td class="nowrap">${esc(label)}</td>` +
      `<td><span class="pill ${ran ? 'ok' : ''}">${ran ? 'yes' : 'skipped'}</span></td>` +
      `<td class="muted">${esc(detail)}</td>` +
      `<td class="num">${ran && ms ? ms : '—'}</td>`;
    body.appendChild(tr);
  });
  table.appendChild(body);
  card.appendChild(table);

  card.appendChild(el('p', 'faint',
    `${t.totalMs ?? 0} ms total` + (r.fromCache ? ' — replayed from the answer cache, no model call was made' : '')));

  return card;
}


function renderAnswer(r, full, omitAnswer) {
  const wrap = el('div');
  if (omitAnswer) return renderEvidence(wrap, r, full);

  const head = el('div', 'card');
  head.appendChild(Object.assign(el('h2'), {
    innerHTML: `Stage ${r.stage ?? '—'} · ${esc(r.stageName)}` +
      (r.fromCache ? ' <span class="pill">cached for these exact flags</span>' : '') +
      (r.unconstrained ? ' <span class="pill bad">unconstrained</span>' : '') +
      (r.refused ? ' <span class="pill warn">refused</span>' : ''),
  }));
  const answer = el('div', 'answer' + (r.refused ? ' refused' : '') + (r.unconstrained ? ' unconstrained' : ''));
  answer.textContent = r.answer || '(empty)';
  head.appendChild(answer);

  if (r.groundedness !== null && r.groundedness !== undefined) {
    head.appendChild(el('p', 'muted', `Groundedness ${pct(r.groundedness)} — the share of factual sentences carrying a citation that the cited chunk visibly supports.`));
  }
  if (r.citations?.length) {
    const table = el('table');
    table.innerHTML = '<thead><tr><th>#</th><th>Chunk</th><th>Page</th><th>Verified</th><th>Supporting span</th></tr></thead>';
    const body = el('tbody');
    r.citations.forEach((c) => {
      const tr = el('tr');
      tr.innerHTML = `<td class="num">${c.index}</td><td class="mono">${esc(c.chunkId)}</td><td class="num">${c.page}</td>` +
        `<td><span class="pill ${c.verified ? 'ok' : 'bad'}">${c.verified ? 'yes' : 'no'}</span></td>` +
        `<td class="faint">${esc(c.quote ?? '—')}</td>`;
      body.appendChild(tr);
    });
    table.appendChild(body);
    head.appendChild(table);
  }

  if (r.warnings?.length) {
    const list = el('ul', 'warnings');
    r.warnings.forEach((w) => list.appendChild(el('li', '', w)));
    head.appendChild(list);
  }

  const t = r.timings || {};
  head.appendChild(el('p', 'faint',
    `${t.totalMs} ms total — embed ${t.embedMs}, search ${t.searchMs}, rerank ${t.rerankMs}, ` +
    `rewrite ${t.rewriteMs}, graph ${t.graphMs}, generate ${t.generateMs} · ${t.chatCalls} model call(s)`));
  wrap.appendChild(head);

  return renderEvidence(wrap, r, full);
}

/// The inspectable half: rewrite, router, trace, graph, retrieved chunks, assembled prompt.
function renderEvidence(wrap, r, full) {
  if (r.warnings?.length && !wrap.childNodes.length) {
    const card = el('div', 'card');
    const list = el('ul', 'warnings');
    r.warnings.forEach((w) => list.appendChild(el('li', '', w)));
    card.innerHTML = '<h2>Warnings</h2>';
    card.appendChild(list);
    wrap.appendChild(card);
  }

  if (r.rewrite) {
    const card = el('div', 'card hide-in-present');
    card.innerHTML = `<h2>Query rewrite</h2>` +
      `<div class="muted">original</div><div class="mono">${esc(r.rewrite.original)}</div>` +
      `<div class="muted" style="margin-top:6px">rewritten</div><div class="mono">${esc(r.rewrite.rewritten)}</div>` +
      (r.rewrite.keywords?.length ? `<div class="faint" style="margin-top:6px">keywords: ${esc(r.rewrite.keywords.join(', '))}</div>` : '');
    wrap.appendChild(card);
  }

  if (r.router) {
    const card = el('div', 'card hide-in-present');
    card.innerHTML = `<h2>Router</h2><div>classified as <b>${esc(r.router.classification)}</b> → route ` +
      `<span class="mono">${esc(r.router.route)}</span></div>` +
      `<div class="faint">${esc(r.router.rationale ?? '')}</div>` +
      `<div class="faint mono">${esc((r.router.appliedFlags || []).join(' · '))}</div>`;
    wrap.appendChild(card);
  }

  if (r.trace?.length) {
    const card = el('div', 'card hide-in-present');
    card.innerHTML = '<h2>Agentic trace</h2>';
    const table = el('table');
    table.innerHTML = '<thead><tr><th>#</th><th>Action</th><th>Query</th><th>Hits</th><th>Thought</th></tr></thead>';
    const body = el('tbody');
    r.trace.forEach((s) => {
      const tr = el('tr');
      tr.innerHTML = `<td class="num">${s.iteration}</td><td>${esc(s.action)}</td>` +
        `<td class="mono">${esc(s.query ?? '—')}</td><td class="num">${s.hits}</td>` +
        `<td class="faint">${esc(s.thought ?? '')}</td>`;
      body.appendChild(tr);
    });
    table.appendChild(body);
    card.appendChild(table);
    wrap.appendChild(card);
  }

  if (r.graph) wrap.appendChild(renderGraphBlock(r.graph));

  if (r.retrieval) {
    const card = el('div', 'card');
    const rt = r.retrieval;
    card.innerHTML = `<h2>Retrieved — ${esc(rt.collection)} · top ${rt.topK} of ${rt.candidateCount} candidates` +
      (rt.hybrid ? ' · hybrid' : '') + (rt.reranked ? ' · reranked' : '') +
      (rt.filterApplied ? ' · filtered' : '') + `</h2>`;
    if (rt.filterApplied && rt.filter) {
      card.appendChild(el('p', 'faint mono', 'filter: ' + JSON.stringify(rt.filter, (k, v) => (v === null ? undefined : v))));
    }
    (rt.chunks || []).forEach((c, i) => card.appendChild(renderChunk(c, i + 1)));

    if (full && rt.candidates?.length > (rt.chunks?.length || 0)) {
      const details = el('details');
      details.appendChild(el('summary', '', `all ${rt.candidates.length} candidates with scores and rank deltas`));
      const table = el('table');
      table.innerHTML = '<thead><tr><th>rank before</th><th>rank after</th><th>arm</th><th>score</th>' +
        '<th>vector</th><th>keyword</th><th>rerank</th><th>chunk</th></tr></thead>';
      const body = el('tbody');
      rt.candidates.forEach((c) => {
        const moved = c.rankBefore && c.rankAfter ? c.rankBefore - c.rankAfter : 0;
        const tr = el('tr');
        tr.innerHTML = `<td class="num">${c.rankBefore ?? '—'}</td>` +
          `<td class="num">${c.rankAfter ?? '—'}${moved ? ` <span class="${moved > 0 ? 'pill ok' : 'pill warn'}">${moved > 0 ? '▲' : '▼'}${Math.abs(moved)}</span>` : ''}</td>` +
          `<td><span class="pill ${c.arm === 'keyword' ? 'warn' : c.arm === 'both' ? 'ok' : ''}">${esc(c.arm)}</span></td>` +
          `<td class="num">${fmt(c.score, 4)}</td><td class="num">${fmt(c.vectorScore, 4)}</td>` +
          `<td class="num">${fmt(c.keywordScore, 3)}</td><td class="num">${fmt(c.rerankScore, 3)}</td>` +
          `<td class="mono faint">${esc(c.chunkId)}</td>`;
        body.appendChild(tr);
      });
      table.appendChild(body);
      details.appendChild(table);
      card.appendChild(details);
    }
    wrap.appendChild(card);
  }

  if (full && r.prompt) {
    const card = el('div', 'card hide-in-present');
    const details = el('details');
    details.appendChild(el('summary', '', 'the exact prompt that was sent'));
    details.appendChild(el('pre', 'prompt', r.prompt));
    card.innerHTML = '<h2>Prompt</h2>';
    card.appendChild(details);
    wrap.appendChild(card);
  }

  return wrap;
}

function renderChunk(c, index) {
  const card = el('div', 'chunk');
  const meta = el('div', 'meta');
  meta.innerHTML = `<span class="mono">[${index}] ${esc(c.chunkId)}</span>` +
    `<span>page ${c.page}</span><span>${esc(c.section || '')}</span>` +
    (c.docType ? `<span class="pill">${esc(c.docType)}</span>` : '') +
    (c.year ? `<span class="pill">${c.year}</span>` : '') +
    `<span class="pill ${c.arm === 'keyword' ? 'warn' : c.arm === 'graph' ? 'graph' : c.arm === 'both' ? 'ok' : ''}">${esc(c.arm)}</span>` +
    `<span class="mono">${fmt(c.score, 4)}</span>` +
    (c.fromGraph ? `<span class="pill graph">${esc(c.graphReason || 'via graph')}</span>` : '');
  const body = el('div', 'body', c.text);
  card.append(meta, body);
  card.onclick = () => card.classList.toggle('expanded');
  return card;
}

function renderGraphBlock(g) {
  const card = el('div', 'card');
  card.innerHTML = `<h2>Graph — mode ${esc(g.mode)}</h2>`;
  if (g.note) card.appendChild(el('p', 'warnings', g.note));

  const picture = renderGraphPicture(g);
  if (picture) card.appendChild(picture);

  if (g.path) {
    card.appendChild(el('p', 'muted', `${g.path.hops} hops`));
    card.appendChild(renderChain(g.path));
    const narrative = el('div', 'answer');
    narrative.textContent = g.path.narrative;
    card.appendChild(narrative);
  }

  if (g.aggregationResult) {
    const agg = g.aggregationResult;
    card.appendChild(el('p', 'muted', agg.title));
    card.appendChild(renderAggTable(agg));
    const details = el('details');
    details.appendChild(el('summary', '', 'the Cypher that produced this'));
    details.appendChild(el('pre', 'prompt', agg.cypher));
    card.appendChild(details);
  }

  if (g.edgesTraversed?.length) {
    const details = el('details');
    details.appendChild(el('summary', '', `${g.edgesTraversed.length} edge(s) traversed, ${g.entitiesTouched?.length ?? 0} entities touched`));
    const table = el('table');
    table.innerHTML = '<thead><tr><th>from</th><th>predicate</th><th>to</th><th>conf</th><th>×</th><th>origin</th></tr></thead>';
    const body = el('tbody');
    g.edgesTraversed.forEach((e) => {
      const tr = el('tr');
      tr.innerHTML = `<td>${esc(e.fromName)}</td><td class="mono">${esc(e.predicate)}</td><td>${esc(e.toName)}</td>` +
        `<td class="num">${fmt(e.confidence, 2)}</td><td class="num">${e.mentionCount}</td>` +
        `<td><span class="pill ${e.derived ? 'graph' : ''}">${e.derived ? 'derived' : 'asserted'}</span></td>`;
      body.appendChild(tr);
    });
    table.appendChild(body);
    details.appendChild(table);
    card.appendChild(details);
  }
  return card;
}

function renderAggTable(agg) {
  const table = el('table');
  table.innerHTML = '<thead><tr>' + agg.columns.map((c) => `<th>${esc(c)}</th>`).join('') + '</tr></thead>';
  const body = el('tbody');
  agg.rows.forEach((row) => {
    const tr = el('tr');
    tr.innerHTML = agg.columns.map((c) => {
      const v = row.values[c];
      return typeof v === 'number' ? `<td class="num">${v}</td>` : `<td>${esc(v ?? '')}</td>`;
    }).join('');
    body.appendChild(tr);
  });
  table.appendChild(body);
  return table;
}

// ---------------------------------------------------------------- compare

async function compare() {
  if (!requireDoc()) return;
  const question = $('cmpQuestion').value.trim();
  if (!question) { toast('Type a question.', true); return; }
  const stages = [Number($('cmpLeft').value), Number($('cmpRight').value)];
  $('cmpResult').replaceChildren(el('p', 'muted', 'running both rungs…'));
  try {
    const data = await post('/api/compare', { documentId: state.docId, question, stages });
    $('cmpResult').replaceChildren(...data.results.map((r) => {
      const column = el('div');
      column.appendChild(renderAnswer(r, false));
      return column;
    }));
  } catch (ex) {
    $('cmpResult').replaceChildren(el('p', 'warnings', ex.message));
  }
}

// ---------------------------------------------------------------- eval

async function loadGoldenSummary() {
  if (!state.docId) return;
  try {
    const set = await api(`/api/documents/${state.docId}/golden`);
    state.golden = set;
    const byType = {};
    set.questions.forEach((q) => { byType[q.type] = (byType[q.type] || 0) + 1; });
    $('goldenSummary').innerHTML =
      `<b>${esc(set.name)}</b> — ${set.questions.length} questions · ` +
      Object.entries(byType).map(([k, v]) => `${esc(k)} ${v}`).join(' · ') +
      (set.questions.some((q) => q.generated) ? ' <span class="pill warn">contains generated questions — weaker evidence</span>' : '');

  } catch {
    $('goldenSummary').textContent = 'No golden set loaded for this document.';
  }
}

function parseStages(text) {
  const stages = new Set();
  text.split(',').forEach((part) => {
    const range = part.trim().match(/^(\d+)\s*-\s*(\d+)$/);
    if (range) {
      for (let i = Number(range[1]); i <= Number(range[2]); i++) stages.add(i);
    } else if (part.trim()) {
      stages.add(Number(part.trim()));
    }
  });
  return [...stages].filter((n) => Number.isInteger(n) && n >= 0 && n <= 11).sort((a, b) => a - b);
}

async function runEval() {
  if (!requireDoc()) return;
  const stages = parseStages($('evalStages').value);
  const run = await post(`/api/documents/${state.docId}/eval`, { stages });
  toast(`Eval ${run.runId} started across ${stages.length} stage(s).`);
  pollEval(run.runId);
}

async function pollEval(runId) {
  const run = await api('/api/eval/' + runId);
  renderEval(run);
  if (!run.completed) setTimeout(() => pollEval(runId), 1500);
  else toast('Eval complete.');
}

function renderEval(run) {
  const stages = run.stages;
  const table = el('table', 'heat');
  table.innerHTML = '<thead><tr><th>type</th>' + stages.map((s) => `<th>${s}</th>`).join('') + '</tr></thead>';
  const body = el('tbody');

  Object.entries(run.heatmapByType).forEach(([type, byStage]) => {
    const tr = el('tr');
    tr.innerHTML = `<td class="mono">${esc(type)}</td>` + stages.map((s) => {
      const v = byStage[s];
      const shade = v === undefined ? 0 : v;
      const bg = `rgba(94,194,122,${(shade * 0.55).toFixed(2)})`;
      return `<td class="cell" style="background:${bg}">${v === undefined ? '' : Math.round(v * 100)}</td>`;
    }).join('');
    body.appendChild(tr);
  });

  const overall = el('tr');
  overall.innerHTML = `<td><b>overall</b></td>` + stages.map((s) => {
    const v = run.overallByStage[s] ?? 0;
    return `<td class="cell"><b>${Math.round(v * 100)}</b></td>`;
  }).join('');
  body.appendChild(overall);
  table.appendChild(body);

  const wrap = el('div');
  wrap.appendChild(el('p', 'muted', `${run.completed ? 'complete' : 'running… ' + Math.round((run.progress || 0) * 100) + '%'} · ${run.cells.length} cells`));
  wrap.appendChild(table);
  if (run.warnings?.length) {
    const list = el('ul', 'warnings');
    run.warnings.forEach((w) => list.appendChild(el('li', '', w)));
    wrap.appendChild(list);
  }
  $('evalHeat').replaceChildren(wrap);

  const regressions = el('div');
  if (!run.regressions.length) regressions.appendChild(el('p', 'faint', 'None found in this run.'));
  run.regressions.forEach((r) => {
    regressions.appendChild(el('p', 'warnings',
      `${r.questionId} [${r.type}] passed at stage ${r.fromStage} and failed at stage ${r.toStage} — ${r.note}`));
  });
  $('evalRegressions').replaceChildren(regressions);

  const cells = el('table');
  cells.innerHTML = '<thead><tr><th>question</th><th>type</th><th>stage</th><th>pass</th><th>recall</th><th>answer / failure</th></tr></thead>';
  const cellBody = el('tbody');
  run.cells.slice(-400).forEach((c) => {
    const tr = el('tr');
    tr.innerHTML = `<td class="mono">${esc(c.questionId)}</td><td class="mono faint">${esc(c.type)}</td>` +
      `<td class="num">${c.stage}</td>` +
      `<td><span class="pill ${c.pass ? 'ok' : 'bad'}">${c.pass ? 'pass' : 'fail'}</span>${c.refused ? ' <span class="pill warn">refused</span>' : ''}</td>` +
      `<td class="num">${fmt(c.retrievalRecall, 2)}</td>` +
      `<td class="faint">${esc((c.failure || c.answer || '').slice(0, 240))}</td>`;
    cellBody.appendChild(tr);
  });
  cells.appendChild(cellBody);
  $('evalCells').replaceChildren(cells);
}

// ---------------------------------------------------------------- graph tab

const TYPE_COLORS = {
  Person: '#6aa9ff', Character: '#e0a860', Film: '#5ec27a', TVSeries: '#4fc3c3',
  Studio: '#b57bdc', Franchise: '#d96f9a', Award: '#d4c25e', AwardCategory: '#a8a05a',
  Episode: '#7fa6d9', Season: '#7f93a6', Genre: '#8a8f98', Festival: '#c98b5e',
  Location: '#89a06b', Work: '#9a9a9a',
};

async function loadGraph() {
  if (!state.docId) return;
  const conf = Number($('graphConf').value);
  const derived = $('graphDerived').checked;
  const limit = Number($('graphLimit').value);
  try {
    state.graph = await api(`/api/documents/${state.docId}/graph?minConfidence=${conf}&includeDerived=${derived}&limit=${limit}`);
  } catch (ex) { toast(ex.message, true); return; }

  $('graphStats').textContent =
    `${state.graph.nodes.length} nodes · ${state.graph.edges.length} edges` +
    (state.graph.truncated ? ' (truncated)' : '');
  renderLegend();
  drawGraph(state.graph);
}

function renderLegend() {
  const used = [...new Set((state.graph?.nodes || []).map((n) => n.type))];
  $('graphLegend').replaceChildren(...used.map((type) => {
    const span = el('span');
    span.innerHTML = `<i style="background:${TYPE_COLORS[type] || '#888'}"></i>${esc(type)}`;
    return span;
  }), Object.assign(el('span', 'faint'), { textContent: '— dashed edges are derived (COLLABORATED_WITH), computed from the graph rather than asserted by a document' }));
}

/** Small spring layout. A few hundred nodes settle in well under a second. */
function drawGraph(graph) {
  const svg = $('graphSvg');
  layoutGraph(svg, graph, {
    width: svg.clientWidth || 900,
    height: svg.clientHeight || 560,
    empty: 'No graph yet — process a document and commit the review gate.',
    onEdgeClick: showEdge,
  });
}

/// One force-directed renderer, used by the Graph tab and by the graph panel inside a chat answer.
/// Kept parameterised rather than duplicated so the small inline view and the full tab can never
/// disagree about what the traversal touched.
function layoutGraph(svg, graph, opts) {
  const width = opts.width || 900;
  const height = opts.height || 560;
  svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
  svg.replaceChildren();
  if (!graph.nodes.length) {
    const text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    text.setAttribute('x', 16); text.setAttribute('y', 28);
    text.textContent = opts.empty || 'Nothing to draw.';
    svg.appendChild(text);
    return;
  }

  const index = new Map(graph.nodes.map((n, i) => [n.key, i]));
  const nodes = graph.nodes.map((n, i) => ({
    ...n,
    x: width / 2 + Math.cos((i / graph.nodes.length) * Math.PI * 2) * (width / 3),
    y: height / 2 + Math.sin((i / graph.nodes.length) * Math.PI * 2) * (height / 3),
    vx: 0, vy: 0,
  }));
  const links = graph.edges
    .filter((e) => index.has(e.from) && index.has(e.to))
    .map((e) => ({ ...e, s: index.get(e.from), t: index.get(e.to) }));

  for (let iteration = 0; iteration < 220; iteration++) {
    const k = Math.sqrt((width * height) / nodes.length) * 0.62;
    for (let i = 0; i < nodes.length; i++) {
      for (let j = i + 1; j < nodes.length; j++) {
        let dx = nodes[i].x - nodes[j].x, dy = nodes[i].y - nodes[j].y;
        let d2 = dx * dx + dy * dy || 0.01;
        const force = (k * k) / d2;
        const fx = dx * force * 0.02, fy = dy * force * 0.02;
        nodes[i].vx += fx; nodes[i].vy += fy;
        nodes[j].vx -= fx; nodes[j].vy -= fy;
      }
    }
    links.forEach((l) => {
      const a = nodes[l.s], b = nodes[l.t];
      const dx = b.x - a.x, dy = b.y - a.y;
      const d = Math.sqrt(dx * dx + dy * dy) || 0.01;
      const force = (d - k) * 0.035;
      const fx = (dx / d) * force, fy = (dy / d) * force;
      a.vx += fx; a.vy += fy; b.vx -= fx; b.vy -= fy;
    });
    nodes.forEach((n) => {
      n.x = Math.max(18, Math.min(width - 18, n.x + (n.vx *= 0.82)));
      n.y = Math.max(18, Math.min(height - 18, n.y + (n.vy *= 0.82)));
    });
  }

  const ns = 'http://www.w3.org/2000/svg';
  links.forEach((l) => {
    const line = document.createElementNS(ns, 'line');
    line.setAttribute('x1', nodes[l.s].x); line.setAttribute('y1', nodes[l.s].y);
    line.setAttribute('x2', nodes[l.t].x); line.setAttribute('y2', nodes[l.t].y);
    if (l.derived) line.classList.add('derived');
    if (l.onPath) line.classList.add('on-path');
    if (opts.onEdgeClick) {
      line.style.cursor = 'pointer';
      line.addEventListener('click', () => opts.onEdgeClick(l));
    }
    const title = document.createElementNS(ns, 'title');
    title.textContent = `${l.fromName} -${l.predicate}-> ${l.toName} (${l.confidence.toFixed(2)})`;
    line.appendChild(title);
    svg.appendChild(line);
  });

  nodes.forEach((n) => {
    const circle = document.createElementNS(ns, 'circle');
    circle.setAttribute('cx', n.x); circle.setAttribute('cy', n.y);
    circle.setAttribute('r', n.seed ? 9 : Math.min(11, 4 + Math.sqrt(n.mentionCount || 1)));
    circle.setAttribute('fill', TYPE_COLORS[n.type] || '#888');
    if (n.seed) circle.classList.add('seed');
    const title = document.createElementNS(ns, 'title');
    title.textContent = `${n.name} [${n.type}]${n.year ? ' ' + n.year : ''} · ${n.mentionCount} mention(s)`;
    circle.appendChild(title);
    svg.appendChild(circle);

    if (opts.labelAll || (n.mentionCount || 0) >= 3 || nodes.length < 60) {
      const label = document.createElementNS(ns, 'text');
      label.setAttribute('x', n.x + 10); label.setAttribute('y', n.y + 3);
      label.textContent = n.name.length > 22 ? n.name.slice(0, 21) + '…' : n.name;
      svg.appendChild(label);
    }
  });
}

/// Draws exactly the neighbourhood this answer walked — not the whole graph. The point a table of
/// edges makes badly and a picture makes instantly is that the traversal reached facts no single
/// chunk contains, so the nodes on the path are highlighted and the seeds are ringed.
function renderGraphPicture(g) {
  const nodes = new Map();
  const add = (key, name, type, year, mentionCount, seed) => {
    if (!key) return;
    const existing = nodes.get(key);
    if (existing) { existing.seed = existing.seed || !!seed; return; }
    nodes.set(key, { key, name: name || key, type: type || 'Unknown', year, mentionCount: mentionCount || 1, seed: !!seed });
  };

  (g.entitiesTouched || []).forEach((e) => add(e.key, e.name, e.type, e.year, e.mentionCount, false));

  const onPath = new Set();
  const pathNodes = g.path?.nodes || [];
  pathNodes.forEach((n, i) => {
    add(n.key, n.name, n.type, n.year, 3, i === 0 || i === pathNodes.length - 1);
    if (i > 0) onPath.add(pathNodes[i - 1].key + '|' + n.key);
  });

  // An expansion can touch four hundred edges. Drawn whole that is a hairball, so the picture
  // shows the strongest slice and the caption says how much was left out — a silent truncation
  // would read as "this is the whole neighbourhood", which is the one thing it must not imply.
  const MAX_EDGES = 40;
  const all = (g.edgesTraversed || []).map((e) => ({
    ...e,
    onPath: onPath.has(e.from + '|' + e.to) || onPath.has(e.to + '|' + e.from),
  }));
  const ranked = [...all].sort((a, b) =>
    (b.onPath - a.onPath) ||
    (a.derived - b.derived) ||
    ((b.mentionCount || 0) - (a.mentionCount || 0)) ||
    ((b.confidence || 0) - (a.confidence || 0)));
  const edges = ranked.slice(0, MAX_EDGES);
  const hidden = all.length - edges.length;

  edges.forEach((e) => {
    add(e.from, e.fromName, e.fromType, null, 1, false);
    add(e.to, e.toName, e.toType, null, 1, false);
  });
  // Entities the traversal touched but whose edges did not make the cut would float unconnected.
  [...nodes.keys()].forEach((key) => {
    if (!edges.some((e) => e.from === key || e.to === key) && !pathNodes.some((n) => n.key === key)) {
      nodes.delete(key);
    }
  });

  // The path endpoints carry no edge rows of their own in path mode, so stitch the chain in.
  for (let i = 1; i < pathNodes.length; i++) {
    const a = pathNodes[i - 1], b = pathNodes[i];
    if (edges.some((e) => (e.from === a.key && e.to === b.key) || (e.from === b.key && e.to === a.key))) continue;
    edges.push({
      from: a.key, to: b.key, fromName: a.name, toName: b.name,
      predicate: (g.path.rels || [])[i - 1] || 'RELATED', confidence: 1, mentionCount: 1,
      derived: false, onPath: true,
    });
  }

  if (nodes.size < 2 || !edges.length) return null;

  const wrap = el('div', 'graph-inline');
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'graph-inline-svg');
  wrap.appendChild(svg);

  const legend = el('div', 'faint');
  legend.innerHTML = `${nodes.size} node(s), ${edges.length} edge(s) drawn` +
    (hidden > 0 ? ` — <b>${hidden} more traversed but not drawn</b>; the table below has all ${all.length}` : '') +
    (pathNodes.length ? ' · <b>bold</b> is the path' : '') +
    ' · ringed nodes are where the traversal started · dashed edges are derived';
  wrap.appendChild(legend);

  // clientWidth is 0 until the element is in the document, so lay out on the next frame.
  requestAnimationFrame(() => layoutGraph(svg, { nodes: [...nodes.values()], edges }, {
    width: svg.clientWidth || 640,
    height: 260,
    labelAll: nodes.size <= 24,
  }));

  return wrap;
}

async function showEdge(edge) {
  const url = `/api/documents/${state.docId}/graph/edge?from=${encodeURIComponent(edge.from)}` +
    `&predicate=${encodeURIComponent(edge.predicate)}&to=${encodeURIComponent(edge.to)}`;
  try {
    const data = await api(url);
    const wrap = el('div', 'triple');
    wrap.innerHTML = `<div class="head"><b>${esc(data.edge.fromName)}</b> ` +
      `<span class="mono">──${esc(data.edge.predicate)}──&gt;</span> <b>${esc(data.edge.toName)}</b>` +
      `<span class="pill">${fmt(data.edge.confidence, 2)}</span><span class="pill">×${data.edge.mentionCount}</span>` +
      `<span class="pill ${data.edge.derived ? 'graph' : ''}">${data.edge.derived ? 'derived' : 'asserted'}</span>` +
      (data.edge.verdict ? `<span class="pill">${esc(data.edge.verdict)}</span>` : '') + `</div>` +
      (data.edge.evidence ? `<div class="ev">“${esc(data.edge.evidence)}”</div>` : '');
    data.chunks.forEach((c) => {
      const chunk = el('div', 'chunk expanded');
      chunk.innerHTML = `<div class="meta"><span class="mono">${esc(c.id)}</span><span>page ${c.page}</span></div>` +
        `<div class="body">${highlight(c.text, data.edge.evidence)}</div>`;
      wrap.appendChild(chunk);
    });
    $('graphEdgeDetail').replaceChildren(wrap);
  } catch (ex) { toast(ex.message, true); }
}

async function loadAggPresets() {
  const presets = await api('/api/graph/presets');
  $('aggButtons').replaceChildren(...presets.map((p) => {
    const button = el('button', 'btn', p.title);
    button.onclick = async () => {
      if (!requireDoc()) return;
      const conf = Number($('graphConf').value);
      const agg = await api(`/api/documents/${state.docId}/graph/aggregate?preset=${p.id}&minConfidence=${conf}`);
      const wrap = el('div');
      wrap.appendChild(el('p', 'muted', `${agg.title} — recomputed at min confidence ${conf.toFixed(2)}`));
      wrap.appendChild(renderAggTable(agg));
      const details = el('details');
      details.appendChild(el('summary', '', 'the Cypher'));
      details.appendChild(el('pre', 'prompt', agg.cypher));
      wrap.appendChild(details);
      $('aggResult').replaceChildren(wrap);
    };
    return button;
  }));
}

// ---------------------------------------------------------------- explore

async function loadPeople() {
  if (!state.docId || state.people.length) return;
  try {
    state.people = await api(`/api/documents/${state.docId}/graph/entities?type=Person&limit=500`);
  } catch { return; }
  [$('expFrom'), $('expTo')].forEach((select) => {
    select.replaceChildren();
    state.people.forEach((p) => {
      const option = el('option', '', `${p.name} (${p.mentionCount})`);
      option.value = p.key;
      select.appendChild(option);
    });
  });
  if (state.people.length > 1) $('expTo').selectedIndex = state.people.length - 1;
}

async function connect() {
  if (!requireDoc()) return;
  const from = $('expFrom').value, to = $('expTo').value;
  if (!from || !to || from === to) { toast('Pick two different people.', true); return; }
  $('pathChain').replaceChildren(el('span', 'muted', 'traversing…'));
  $('pathNarrative').style.display = 'none';
  try {
    const data = await api(`/api/documents/${state.docId}/graph/path?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&maxHops=${Number($('expHops').value)}`);
    if (!data.found) {
      $('pathChain').replaceChildren(el('span', 'warnings', data.message));
      return;
    }
    $('pathChain').replaceChildren(...renderChain(data.path).childNodes);
    animateChain();
    const narrative = $('pathNarrative');
    narrative.textContent = data.path.narrative;
    narrative.style.display = '';
  } catch (ex) {
    $('pathChain').replaceChildren(el('span', 'warnings', ex.message));
  }
}

function renderChain(path) {
  const chain = el('div', 'pathchain');
  path.nodes.forEach((n, i) => {
    if (i > 0) {
      const edge = el('div', 'edge', '──' + path.rels[i - 1] + '──▶');
      chain.appendChild(edge);
    }
    const node = el('div', 'node');
    node.innerHTML = `<b>${esc(n.name)}</b><span>${esc(n.type)}${n.year ? ' · ' + n.year : ''}</span>`;
    node.style.borderColor = TYPE_COLORS[n.type] || '#888';
    chain.appendChild(node);
  });
  return chain;
}

function animateChain() {
  const parts = [...$('pathChain').children];
  parts.forEach((part, i) => setTimeout(() => part.classList.add('shown'), i * 380));
}

// ---------------------------------------------------------------- wiring

function wire() {
  $('docSelect').onchange = (e) => setDoc(e.target.value);

  $('btnLoadDemo').onclick = async () => {
    try {
      const doc = await post('/api/documents/load-demo');
      await loadDocuments();
      setDoc(doc.id);
      $('docSelect').value = doc.id;
      toast('Loaded ' + doc.title + '. Process it next.');
    } catch (ex) { toast(ex.message, true); }
  };

  $('btnUpload').onclick = async () => {
    const file = $('fileInput').files[0];
    if (!file) { toast('Choose a PDF first.', true); return; }
    const form = new FormData();
    form.append('file', file);
    try {
      const response = await fetch('/api/documents/upload', { method: 'POST', body: form });
      const doc = await response.json();
      if (!response.ok) throw new Error(doc.error || 'Upload failed.');
      await loadDocuments();
      setDoc(doc.id);
      toast('Uploaded ' + doc.title);
    } catch (ex) { toast(ex.message, true); }
  };

  $('btnProcess').onclick = () => startProcessing().catch((ex) => toast(ex.message, true));
  $('btnAsk').onclick = () => ask(false);
  $('btnAskCustom').onclick = () => ask(true);
  $('btnCompare').onclick = () => compare();

  $('btnClearChat').onclick = () => {
    $('chatLog').replaceChildren(Object.assign(el('div', 'chat-empty'), {
      id: 'chatEmpty',
      textContent: 'Pick a stage, ask a question. The same question at two rungs is the whole demo.',
    }));
  };

  // Enter sends, Shift+Enter is a newline; the box grows with the question.
  const box = $('askQuestion');
  box.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); ask(false); }
  });
  let warmTimer = null;
  box.addEventListener('input', () => {
    box.style.height = 'auto';
    box.style.height = Math.min(box.scrollHeight, 180) + 'px';
    clearTimeout(warmTimer);
    warmTimer = setTimeout(markWarmStages, 250);
  });

  $('btnAcceptAll').onclick = () => decide({ acceptAll: true });
  $('btnRejectBelow').onclick = () => decide({ rejectBelowConfidence: Number($('revFloor').value) });
  $('btnCommit').onclick = async () => {
    try {
      const summary = await post(`/api/documents/${state.docId}/graph/commit`);
      toast(`Committed ${summary.nodes} nodes, ${summary.edges} edges, ${summary.derivedEdges} derived.`);
      loadDocuments();
      state.people = [];
    } catch (ex) { toast(ex.message, true); }
  };

  $('btnLoadGolden').onclick = async () => {
    try {
      const result = await post(`/api/documents/${state.docId}/golden/load`);
      toast(`Loaded ${result.questions} golden questions.`);
      loadGoldenSummary();
    } catch (ex) { toast(ex.message, true); }
  };
  $('btnGenGolden').onclick = async () => {
    try {
      const result = await post(`/api/documents/${state.docId}/golden/generate?perSection=2`);
      toast(`${result.generated} generated. ${result.warning}`);
      loadGoldenSummary();
    } catch (ex) { toast(ex.message, true); }
  };
  $('btnRunEval').onclick = () => runEval().catch((ex) => toast(ex.message, true));

  $('graphConf').oninput = (e) => { $('graphConfVal').textContent = Number(e.target.value).toFixed(2); };
  $('graphConf').onchange = () => loadGraph();
  $('graphDerived').onchange = () => loadGraph();
  $('btnGraphReload').onclick = () => loadGraph();

  $('btnConnect').onclick = () => connect();
  $('btnRandomPair').onclick = () => {
    if (state.people.length < 2) return;
    const a = Math.floor(Math.random() * state.people.length);
    let b = Math.floor(Math.random() * state.people.length);
    if (a === b) b = (b + 1) % state.people.length;
    $('expFrom').value = state.people[a].key;
    $('expTo').value = state.people[b].key;
    connect();
  };

  document.addEventListener('keydown', (e) => {
    if (e.target.matches('input, textarea, select')) {
      if (!(e.key === 'Enter' && (e.metaKey || e.ctrlKey))) return;
    }
    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { ask(false); return; }
    if (/^[0-9]$/.test(e.key) && document.querySelector('#panel-ask.active')) selectStage(Number(e.key));
  });
}

// ---------------------------------------------------------------- boot

/// Boot has to survive a cold API.
///
/// It used to be a straight line of awaits, so if the app was still starting — or was restarted
/// while a tab sat open — the first fetch threw, the rest never ran, and the page was left with an
/// empty stage bar and no document selected. Send then did nothing, because there was nothing to
/// send against, and the only clue was in the console. Each step is now independent and failure is
/// visible and retryable.
async function step(name, fn) {
  try {
    await fn();
    return true;
  } catch (ex) {
    console.error(`boot: ${name} failed`, ex);
    return false;
  }
}

async function boot() {
  if (state.present) document.body.classList.add('present');

  const ok = await step('stages', loadStages)
    && await step('documents', loadDocuments);

  await step('health', loadHealth);
  if (state.docId) { loadGoldenSummary(); pollStatus(); }

  showBootBanner(!ok);
  return ok;
}

/// A dead page must say so. Without this the failure mode is a UI that looks fine and ignores you.
function showBootBanner(failed, message) {
  const existing = document.getElementById('bootBanner');
  if (!failed) { if (existing) existing.remove(); return; }
  if (existing) return;

  const banner = el('div', 'boot-banner');
  banner.id = 'bootBanner';
  banner.appendChild(el('span', '', message
    || 'Could not reach the API — the stage list and document list are empty, so asking will not work. Is the app still starting?'));
  const retry = el('button', 'btn', 'Retry');
  retry.onclick = async () => {
    retry.disabled = true;
    retry.textContent = 'Retrying…';
    if (await boot()) toast('Connected.');
    else { retry.disabled = false; retry.textContent = 'Retry'; }
  };
  banner.appendChild(retry);
  document.body.insertBefore(banner, document.body.firstChild);
}

(async function () {
  // initTabs and wire touch dozens of elements. If either throws, boot never runs and the page is
  // dead with no banner — the exact failure the watchdog exists to name, so they are guarded too.
  try {
    initTabs();
    wire();
  } catch (ex) {
    console.error('boot: wiring failed', ex);
    window.__ragBooted = true;
    showBootBanner(true, 'The UI failed to wire up: ' + ex.message);
    return;
  }

  const ok = await boot();
  window.__ragBooted = true;
  if (ok) return;

  // The usual cause is the page being opened a second or two before the server is listening, so
  // one automatic retry saves a manual reload.
  setTimeout(async () => { if (await boot()) toast('Connected.'); }, 2500);
})();
