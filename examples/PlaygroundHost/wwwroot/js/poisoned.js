import { state } from './state.js';
import { fetchPoisoned } from './api.js';
import { makeArmedButton } from './util.js';

const panelKeys = ['order-outbox', 'order-inbox', 'inventory-inbox'];

/** @type {Record<string, { totalCount: number | null, items: any[], error: string | null, stale: boolean }>} */
const panels = {
  'order-outbox': { totalCount: null, items: [], error: null, stale: false },
  'order-inbox': { totalCount: null, items: [], error: null, stale: false },
  'inventory-inbox': { totalCount: null, items: [], error: null, stale: false },
};

function requeueTargets(key) {
  const s = state.services;
  if (key === 'order-outbox')
    return { urlBase: `${s.orderServiceUrl}/${s.orderManagementPath}/outbox/poisoned` };
  if (key === 'order-inbox')
    return { urlBase: `${s.orderServiceUrl}/${s.orderManagementPath}/inbox/poisoned` };
  return { urlBase: `${s.inventoryServiceUrl}/${s.inventoryManagementPath}/inbox/poisoned` };
}

async function requeuePanelItem(key, id) {
  if (state.requeueingIds.has(id)) return;
  state.requeueingIds.add(id);
  renderPanel(key);
  try {
    const { urlBase } = requeueTargets(key);
    const res = await fetch(`${urlBase}/${id}/requeue`, { method: 'POST', signal: AbortSignal.timeout(8000) });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
  } catch (e) {
    const errorEl = document.getElementById(`error-${key}`);
    if (errorEl) errorEl.textContent = `Requeue failed: ${e.message}`;
  } finally {
    state.requeueingIds.delete(id);
    renderPanel(key);
  }
}

export function renderPanel(key) {
  const s = panels[key];
  const countEl = document.getElementById(`count-${key}`);
  const errorEl = document.getElementById(`error-${key}`);
  const badgeEl = document.getElementById(`badge-${key}`);
  const listEl = document.getElementById(`list-${key}`);
  const panelEl = document.getElementById(`panel-${key}`);
  if (!countEl || !listEl) return;

  countEl.textContent = s.totalCount !== null ? String(s.totalCount) : '–';
  if (errorEl) errorEl.textContent = s.error ?? '';
  if (badgeEl) badgeEl.hidden = !s.stale;
  if (panelEl) panelEl.classList.toggle('panel--stale', s.stale);

  const visible = s.items.filter((m) => !state.requeueingIds.has(m.handlerStatusId ?? m.id));
  listEl.innerHTML = '';

  for (const item of visible) {
    const li = document.createElement('li');
    li.className = 'item';
    const id = item.handlerStatusId ?? item.id;
    const type = item.messageType ?? item.type ?? '(unknown)';
    const errors = item.errorCount ?? 0;
    li.innerHTML = `
      <div class="item-info">
        <div class="item-type" title="${type}">${type}</div>
        <div class="item-meta">errors: ${errors}${item.handlerKey ? ` · key: ${item.handlerKey}` : ''}</div>
      </div>`;

    if (key === 'inventory-inbox' || key === 'order-inbox' || key === 'order-outbox') {
      const btn = document.createElement('button');
      btn.className = 'btn btn-outline';
      btn.style.fontSize = '0.72rem';
      btn.style.padding = '0.2rem 0.45rem';
      btn.textContent = 'Requeue';
      const invThrow = key === 'inventory-inbox' && state.inventoryDemoMode === 'throw';
      btn.disabled = invThrow;
      btn.title = invThrow
        ? 'Set inventory mode away from throw before requeue'
        : '';
      makeArmedButton(btn, () => requeuePanelItem(key, id));
      li.appendChild(btn);
    }
    listEl.appendChild(li);
  }
}

async function fetchPanel(key, url) {
  try {
    const data = await fetchPoisoned(url);
    panels[key] = { totalCount: data.totalCount, items: data.items ?? [], error: null, stale: false };
  } catch (err) {
    panels[key] = { ...panels[key], error: err.message, stale: true };
  }
  renderPanel(key);
}

export function startPoisonedPollers() {
  const s = state.services;
  const { orderServiceUrl, orderManagementPath, inventoryServiceUrl, inventoryManagementPath } = s;
  const urls = {
    'order-outbox': `${orderServiceUrl}/${orderManagementPath}/outbox/poisoned`,
    'order-inbox': `${orderServiceUrl}/${orderManagementPath}/inbox/poisoned`,
    'inventory-inbox': `${inventoryServiceUrl}/${inventoryManagementPath}/inbox/poisoned`,
  };
  for (const key of panelKeys) {
    (async function tick() {
      await fetchPanel(key, urls[key]);
      setTimeout(tick, 3000);
    })();
  }
}
