import { state } from './state.js';

/** All playground APIs are served from this host. */
function root() {
  if (state.services?.playgroundHostUrl) return state.services.playgroundHostUrl.replace(/\/$/, '');
  return '';
}

function url(path) {
  return `${root()}${path}`;
}

export async function fetchScenarioCatalog() {
  const res = await fetch(url('/api/playground/scenarios'), { signal: AbortSignal.timeout(12_000) });
  if (!res.ok) throw new Error(`scenarios HTTP ${res.status}`);
  return res.json();
}

/** @param {string} slug @param {boolean} [confirmDanger] */
export async function startScenarioRun(slug, confirmDanger = false) {
  const q = confirmDanger ? '?confirmDanger=true' : '';
  const res = await fetch(url(`/api/playground/scenarios/${encodeURIComponent(slug)}/run${q}`), {
    method: 'POST',
    signal: AbortSignal.timeout(15_000),
  });
  if (res.status === 400) {
    const j = await res.json().catch(() => ({}));
    throw new Error(j.error || `run HTTP ${res.status}`);
  }
  if (!res.ok && res.status !== 202) throw new Error(`run HTTP ${res.status}`);
  return res.json();
}

/** @param {string} runId */
export async function cancelScenarioRun(runId) {
  const res = await fetch(url(`/api/playground/runs/${encodeURIComponent(runId)}/cancel`), {
    method: 'POST',
    signal: AbortSignal.timeout(12_000),
  });
  if (!res.ok) throw new Error(`cancel HTTP ${res.status}`);
  return res.json();
}

export async function fetchRunStatus(runId) {
  const res = await fetch(url(`/api/playground/runs/${runId}`), { signal: AbortSignal.timeout(10_000) });
  if (!res.ok) throw new Error(`run status HTTP ${res.status}`);
  return res.json();
}

export async function mergeActivities(orderId) {
  const r = await fetch(url(`/api/playground/activities?orderId=${orderId}`), { signal: AbortSignal.timeout(12_000) });
  if (!r.ok) throw new Error('activity fetch failed');
  const rows = await r.json();
  return rows.map((x) => ({ ...x, _svc: 'Playground' }));
}

export async function mergeActivitiesByScenarioRun(scenarioRunId) {
  const r = await fetch(url(`/api/playground/activities?scenarioRunId=${encodeURIComponent(scenarioRunId)}`), {
    signal: AbortSignal.timeout(12_000),
  });
  if (!r.ok) throw new Error('activity fetch failed');
  const rows = await r.json();
  return rows.map((x) => ({ ...x, _svc: 'Playground' }));
}

export async function fetchRabbitDepths() {
  const res = await fetch(url('/api/playground/rabbit-depths'), { signal: AbortSignal.timeout(10_000) });
  if (!res.ok) throw new Error(`rabbit HTTP ${res.status}`);
  return res.json();
}

export async function fetchPoisoned(pathOrUrl) {
  const u = pathOrUrl.startsWith('http') ? pathOrUrl : url(pathOrUrl);
  const res = await fetch(u, { signal: AbortSignal.timeout(10_000) });
  if (!res.ok) throw new Error(`poisoned HTTP ${res.status}`);
  return res.json();
}
