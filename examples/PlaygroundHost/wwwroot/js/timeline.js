import { state } from './state.js';
import { mergeActivities, mergeActivitiesByScenarioRun } from './api.js';

let flowTimer = null;
let activityTimer = null;

function renderFlowPlaceholder() {
  const line = document.getElementById('flow-status-line');
  if (line)
    line.textContent =
      'Activity is driven by scenario runs. Message ids appear on staged rows in the raw table when available.';
  const idsEl = document.getElementById('flow-ids');
  if (idsEl) idsEl.textContent = '';
  const host = document.getElementById('flow-steps');
  if (host) host.innerHTML = '';
}

function renderSwimlanes(activities) {
  const host = document.getElementById('timeline-swimlanes');
  if (!host) return;
  /** @type {Record<string, any[]>} */
  const lanes = { Publisher: [], Consumer: [], Notifications: [], EfCore: [] };
  for (const e of activities) {
    const transport = (e.transportName ?? '').toLowerCase();
    const type = (e.messageType ?? '').toLowerCase();
    let lane = 'Publisher';
    if (type.includes('reserve') || transport.includes('efcore')) lane = 'EfCore';
    else if (type.includes('process-order') || type.includes('inventory.process') || type.includes('processorder'))
      lane = 'Consumer';
    else if (type.includes('order-placed') || type.includes('order.placed') || type.includes('placed'))
      lane = 'Notifications';
    (lanes[lane] ??= []).push(e);
  }
  host.innerHTML = '';
  for (const [name, items] of Object.entries(lanes)) {
    if (!items.length) continue;
    const box = document.createElement('div');
    box.className = 'svc-box';
    box.innerHTML = `<h3>${name}</h3>`;
    const ul = document.createElement('ul');
    ul.className = 'item-list';
    ul.style.maxHeight = '140px';
    for (const e of items.slice(-40)) {
      const li = document.createElement('li');
      li.className = 'item';
      const ok = e.isSuccess === true ? 'yes' : e.isSuccess === false ? 'no' : '';
      const det = (e.error ?? e.dispatchResult ?? '').toString().slice(0, 80);
      li.innerHTML = `<div class="item-info"><div class="item-type">${e.stage} · ${e.messageType ?? ''}</div>
        <div class="item-meta">${e.timestamp} · ok:${ok} ${det}</div></div>`;
      ul.appendChild(li);
    }
    box.appendChild(ul);
    host.appendChild(box);
  }
}

async function refreshActivitiesTable() {
  const errEl = document.getElementById('error-activities');
  const raw = document.getElementById('raw-activity-wrap');
  try {
    let merge = [];
    if (state.lastScenarioRunId)
      merge = await mergeActivitiesByScenarioRun(state.lastScenarioRunId);
    else if (state.lastOrderId) merge = await mergeActivities(state.lastOrderId);
    else return;

    renderSwimlanes(merge);
    const tb = document.getElementById('activity-rows');
    if (tb && raw && !raw.hidden) {
      tb.innerHTML = '';
      for (const e of merge) {
        const tr = document.createElement('tr');
        const ok = e.isSuccess === true ? 'yes' : e.isSuccess === false ? 'no' : '';
        const detail = (e.error ?? e.dispatchResult ?? '').toString().slice(0, 120);
        tr.innerHTML = `<td>${e.timestamp}</td><td>${e._svc}</td><td>${e.stage}</td><td>${e.messageType ?? ''}</td><td>${ok}</td><td>${detail}</td>`;
        tb.appendChild(tr);
      }
    }
    if (errEl) errEl.textContent = '';
  } catch (e) {
    if (errEl) errEl.textContent = e.message;
  }
}

export function restartFlowPoll() {
  if (flowTimer) {
    clearInterval(flowTimer);
    flowTimer = null;
  }
  if (activityTimer) {
    clearInterval(activityTimer);
    activityTimer = null;
  }
  if (!state.lastOrderId && !state.lastScenarioRunId) {
    const fb = document.getElementById('flow-body');
    const fe = document.getElementById('flow-empty');
    if (fb) fb.hidden = true;
    if (fe) fe.hidden = false;
    const bf = document.getElementById('badge-flow');
    if (bf) bf.hidden = true;
    return;
  }
  const fe = document.getElementById('flow-empty');
  const fb = document.getElementById('flow-body');
  if (fe) fe.hidden = true;
  if (fb) fb.hidden = false;

  const tick = async () => {
    try {
      renderFlowPlaceholder();
      const errf = document.getElementById('error-flow');
      if (errf) errf.textContent = '';
      const b = document.getElementById('badge-flow');
      if (b) b.hidden = true;
    } catch (e) {
      const errf = document.getElementById('error-flow');
      if (errf) errf.textContent = e.message;
      const b = document.getElementById('badge-flow');
      if (b) b.hidden = false;
    }
  };
  tick();
  flowTimer = setInterval(tick, 2000);
  refreshActivitiesTable();
  activityTimer = setInterval(refreshActivitiesTable, 2000);
}

export function bindRawActivityToggle() {
  const cb = document.getElementById('toggle-raw-activities');
  const wrap = document.getElementById('raw-activity-wrap');
  if (cb && wrap) {
    cb.addEventListener('change', () => {
      wrap.hidden = !cb.checked;
      refreshActivitiesTable();
    });
  }
}
