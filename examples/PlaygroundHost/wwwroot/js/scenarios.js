import * as api from './api.js';
import { state } from './state.js';
import { pollUntil } from './util.js';
import { refreshServicePanels } from './services.js';
import { restartFlowPoll } from './timeline.js';

let cachedCatalog = [];

function setProgress(labels, idx) {
  const el = document.getElementById('scenario-progress');
  if (!el) return;
  el.innerHTML = '';
  labels.forEach((label, i) => {
    const chip = document.createElement('span');
    chip.className = 'progress-chip' + (i < idx ? ' done' : i === idx ? ' active' : '');
    chip.textContent = label;
    el.appendChild(chip);
  });
}

function showResult(pass, expected, actual, reasons) {
  const banner = document.getElementById('scenario-result-banner');
  const grid = document.getElementById('scenario-result-grid');
  if (banner) {
    banner.hidden = false;
    banner.className = 'result-banner ' + (pass ? 'pass' : 'fail');
    banner.textContent = pass ? 'PASS' : 'FAIL';
  }
  if (grid) {
    grid.innerHTML = `<div><strong>Expected</strong><pre>${JSON.stringify(expected, null, 2)}</pre></div>
      <div><strong>Actual</strong><pre>${JSON.stringify(actual, null, 2)}\n\n${reasons.join('\n')}</pre></div>`;
  }
}

export function fillScenarioSelect(selectEl, catalog) {
  if (!selectEl || !catalog?.length) return;
  selectEl.innerHTML = '';
  const byTopic = {};
  for (const s of catalog) {
    const topic = s.topic || 'Other';
    if (!byTopic[topic]) byTopic[topic] = [];
    byTopic[topic].push(s);
  }
  for (const [topic, list] of Object.entries(byTopic)) {
    const og = document.createElement('optgroup');
    og.label = topic;
    for (const s of list) {
      const o = document.createElement('option');
      o.value = s.slug;
      o.textContent = s.title;
      og.appendChild(o);
    }
    selectEl.appendChild(og);
  }
}

export function bindScenarioDescription(selectEl, descEl) {
  if (!selectEl || !descEl) return;
  selectEl.addEventListener('change', () => {
    const s = cachedCatalog.find((x) => x.slug === selectEl.value);
    descEl.textContent = s?.description ?? '';
  });
}

export async function loadScenarioCatalog() {
  cachedCatalog = await api.fetchScenarioCatalog();
  return cachedCatalog;
}

export async function runScenario(slug) {
  const meta = cachedCatalog.find((x) => x.slug === slug);
  if (!meta) throw new Error('unknown scenario');
  setProgress(['Arrange', 'Act', 'Verify'], 0);
  setProgress(['Arrange', 'Act', 'Verify'], 1);
  const start = await api.startScenarioRun(slug);
  const runId = start.runId;
  state.lastScenarioRunId = runId;
  setProgress(['Arrange', 'Act', 'Verify'], 2);
  const pr = await pollUntil(
    () => api.fetchRunStatus(runId),
    (s) => s.state === 'Passed' || s.state === 'Failed',
    120_000,
  );
  const st = pr.value;
  const pass = st?.state === 'Passed';
  setProgress(['Arrange', 'Act', 'Verify'], 3);
  showResult(pass, { state: 'Passed' }, st, pass ? [] : [st?.detail || st?.state || 'run did not complete']);
  try {
    const acts = await api.mergeActivitiesByScenarioRun(runId);
    const oid = acts.map((e) => e.orderId).find((id) => id);
    if (oid) state.lastOrderId = oid;
  } catch {
    /* ignore */
  }
  await refreshServicePanels();
  restartFlowPoll();
  return { pass };
}
