import { state } from './state.js';
import { getFlow, mergeActivities } from './api.js';

let flowTimer = null;
let activityTimer = null;

function renderFlow(data) {
  const origin = data.publishOrigin ?? 'outbox';
  const line = document.getElementById('flow-status-line');
  if (line)
    line.textContent = `Status: ${data.status} · publish: ${origin} · created ${data.createdAt} · last change ${data.statusChangedAt}`;
  const ids = data.messageIds;
  const idsEl = document.getElementById('flow-ids');
  if (idsEl && ids)
    idsEl.textContent = `${ids.orderPlaced} | ${ids.processOrderCommand} | ${ids.reserveStockInternal ?? ''} | ${ids.orderFulfilled} | ${ids.orderFailed}`;
  const steps = data.steps ?? [];
  const host = document.getElementById('flow-steps');
  if (host) {
    host.innerHTML = '';
    for (const step of steps) {
      const el = document.createElement('span');
      el.className = 'flow-step ' + (step.done ? 'done' : 'pending');
      el.textContent = step.label;
      host.appendChild(el);
    }
  }
}

function renderSwimlanes(activities) {
  const host = document.getElementById('timeline-swimlanes');
  if (!host) return;
  const lanes = { Order: [], Inventory: [], Notification: [], EfCore: [] };
  for (const e of activities) {
    const svc = e._svc;
    let lane = svc;
    const transport = (e.transportName ?? '').toLowerCase();
    const type = (e.messageType ?? '').toLowerCase();
    if (svc === 'Order' && (type.includes('reserve') || transport.includes('efcore')))
      lane = 'EfCore';
    if (lanes[lane]) lanes[lane].push(e);
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
  if (!state.lastOrderId) return;
  const oid = state.lastOrderId;
  const errEl = document.getElementById('error-activities');
  const raw = document.getElementById('raw-activity-wrap');
  try {
    const merge = await mergeActivities(oid);
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
  if (!state.lastOrderId) {
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
      const data = await getFlow(state.lastOrderId);
      renderFlow(data);
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
