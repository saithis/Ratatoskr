import { state } from './state.js';

const jsonHeaders = { 'Content-Type': 'application/json' };

/** All playground and order APIs are served from this host. */
function root() {
  if (state.services?.playgroundHostUrl) return state.services.playgroundHostUrl.replace(/\/$/, '');
  return '';
}

/** @param {string} path absolute path e.g. /api/playground/toggle */
function url(path) {
  return `${root()}${path}`;
}

/** @param {'host'} _service ignored — single host */
/** @param {string} key @param {{ mode?: string, failureCount?: number }} [opts] */
export async function setToggle(_service, key, opts = {}) {
  const body = { key };
  if (opts.mode != null) body.mode = opts.mode;
  if (opts.failureCount != null) body.failureCount = opts.failureCount;
  const res = await fetch(url('/api/playground/toggle'), {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(12_000),
  });
  if (!res.ok) throw new Error(`toggle HTTP ${res.status}`);
  return res.json();
}

export async function fetchControlState(_service) {
  const res = await fetch(url('/api/playground/control-state'), {
    signal: AbortSignal.timeout(12_000),
  });
  if (!res.ok) throw new Error(`control-state HTTP ${res.status}`);
  return res.json();
}

export async function fetchScenarioCatalog() {
  const res = await fetch(url('/api/playground/scenarios'), { signal: AbortSignal.timeout(12_000) });
  if (!res.ok) throw new Error(`scenarios HTTP ${res.status}`);
  return res.json();
}

export async function startScenarioRun(slug) {
  const res = await fetch(url(`/api/playground/scenarios/${encodeURIComponent(slug)}/run`), {
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

export async function getFlow(orderId) {
  const r = await fetch(url(`/api/orders/${orderId}/flow`), { signal: AbortSignal.timeout(10_000) });
  if (!r.ok) throw new Error(`flow HTTP ${r.status}`);
  return r.json();
}

export async function placeOrderOutbox() {
  const res = await fetch(url('/api/orders'), {
    method: 'POST',
    headers: jsonHeaders,
    body: '{}',
    signal: AbortSignal.timeout(10_000),
  });
  if (!res.ok) throw new Error(`place outbox HTTP ${res.status}`);
  return res.json();
}

export async function placeOrderDirect() {
  const res = await fetch(url('/api/orders/direct'), {
    method: 'POST',
    signal: AbortSignal.timeout(10_000),
  });
  if (!res.ok) throw new Error(`place direct HTTP ${res.status}`);
  return res.json();
}

export async function placeOrderOversized() {
  const res = await fetch(url('/api/orders/oversized'), {
    method: 'POST',
    signal: AbortSignal.timeout(15_000),
  });
  if (!res.ok) throw new Error(`oversized HTTP ${res.status}`);
  return res.json();
}

export async function replayOrder(orderId) {
  const res = await fetch(url(`/api/orders/${orderId}/replay`), {
    method: 'POST',
    signal: AbortSignal.timeout(10_000),
  });
  if (!res.ok) throw new Error(`replay HTTP ${res.status}`);
  return res.json();
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
