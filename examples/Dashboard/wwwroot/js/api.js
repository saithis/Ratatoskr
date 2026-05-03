import { state } from './state.js';

const jsonHeaders = { 'Content-Type': 'application/json' };

function serviceBase(name) {
  const s = state.services;
  if (!s) throw new Error('config not loaded');
  if (name === 'order') return s.orderServiceUrl;
  if (name === 'inventory') return s.inventoryServiceUrl;
  if (name === 'notification') return s.notificationServiceUrl;
  throw new Error(`unknown service ${name}`);
}

/** @param {'order'|'inventory'|'notification'} service @param {string} key @param {{ mode?: string, failureCount?: number }} [opts] */
export async function setToggle(service, key, opts = {}) {
  const body = { key };
  if (opts.mode != null) body.mode = opts.mode;
  if (opts.failureCount != null) body.failureCount = opts.failureCount;
  const res = await fetch(`${serviceBase(service)}/api/playground/toggle`, {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(12_000),
  });
  if (!res.ok) throw new Error(`toggle HTTP ${res.status}`);
  return res.json();
}

export async function fetchControlState(service) {
  const res = await fetch(`${serviceBase(service)}/api/playground/control-state`, {
    signal: AbortSignal.timeout(12_000),
  });
  if (!res.ok) throw new Error(`control-state HTTP ${res.status}`);
  return res.json();
}

export async function mergeActivities(orderId) {
  const s = state.services;
  const [a, b, c] = await Promise.all([
    fetch(`${s.orderServiceUrl}/api/playground/activities?orderId=${orderId}`, { signal: AbortSignal.timeout(12_000) }),
    fetch(`${s.inventoryServiceUrl}/api/playground/activities?orderId=${orderId}`, { signal: AbortSignal.timeout(12_000) }),
    fetch(`${s.notificationServiceUrl}/api/playground/activities?orderId=${orderId}`, { signal: AbortSignal.timeout(12_000) }),
  ]);
  if (!a.ok || !b.ok || !c.ok) throw new Error('activity fetch failed');
  const merge = [
    ...(await a.json()).map((x) => ({ ...x, _svc: 'Order' })),
    ...(await b.json()).map((x) => ({ ...x, _svc: 'Inventory' })),
    ...(await c.json()).map((x) => ({ ...x, _svc: 'Notification' })),
  ];
  merge.sort((x, y) => new Date(x.timestamp).getTime() - new Date(y.timestamp).getTime());
  return merge;
}

export async function getFlow(orderId) {
  const s = state.services;
  const r = await fetch(`${s.orderServiceUrl}/api/orders/${orderId}/flow`, { signal: AbortSignal.timeout(10_000) });
  if (!r.ok) throw new Error(`flow HTTP ${r.status}`);
  return r.json();
}

export async function placeOrderOutbox() {
  const s = state.services;
  const res = await fetch(`${s.orderServiceUrl}/api/orders`, {
    method: 'POST',
    headers: jsonHeaders,
    body: '{}',
    signal: AbortSignal.timeout(10_000),
  });
  if (!res.ok) throw new Error(`place outbox HTTP ${res.status}`);
  return res.json();
}

export async function placeOrderDirect() {
  const s = state.services;
  const res = await fetch(`${s.orderServiceUrl}/api/orders/direct`, {
    method: 'POST',
    signal: AbortSignal.timeout(10_000),
  });
  if (!res.ok) throw new Error(`place direct HTTP ${res.status}`);
  return res.json();
}

export async function placeOrderOversized() {
  const s = state.services;
  const res = await fetch(`${s.orderServiceUrl}/api/orders/oversized`, {
    method: 'POST',
    signal: AbortSignal.timeout(15_000),
  });
  if (!res.ok) throw new Error(`oversized HTTP ${res.status}`);
  return res.json();
}

export async function replayOrder(orderId) {
  const s = state.services;
  const res = await fetch(`${s.orderServiceUrl}/api/orders/${orderId}/replay`, {
    method: 'POST',
    signal: AbortSignal.timeout(10_000),
  });
  if (!res.ok) throw new Error(`replay HTTP ${res.status}`);
  return res.json();
}

export async function fetchRabbitDepths() {
  const res = await fetch('/api/playground/rabbit-depths', { signal: AbortSignal.timeout(10_000) });
  if (!res.ok) throw new Error(`rabbit HTTP ${res.status}`);
  return res.json();
}

export async function fetchPoisoned(url) {
  const res = await fetch(url, { signal: AbortSignal.timeout(10_000) });
  if (!res.ok) throw new Error(`poisoned HTTP ${res.status}`);
  return res.json();
}
