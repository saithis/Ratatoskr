import * as api from './api.js';
import { state } from './state.js';
import { sleep, pollUntil } from './util.js';
import { refreshServicePanels } from './services.js';
import { restartFlowPoll } from './timeline.js';

async function resetBaseline() {
  await api.setToggle('order', 'simulate-outbox-transport-failure', { mode: 'succeed' });
  await api.setToggle('order', 'consume-orderfulfilled-inbox', { mode: 'succeed' });
  await api.setToggle('order', 'consume-orderfailed-inbox', { mode: 'succeed' });
  await api.setToggle('inventory', 'consume-processordercommand-inbox', { mode: 'off' });
  await api.setToggle('notification', 'consume-orderplaced-rabbit', { mode: 'succeed' });
  await api.setToggle('notification', 'consume-orderplaced-analytics-rabbit', { mode: 'succeed' });
  await api.setToggle('notification', 'consume-orderfulfilled-rabbit', { mode: 'succeed' });
}

async function poisonedOutboxCount() {
  const s = state.services;
  const d = await api.fetchPoisoned(`${s.orderServiceUrl}/${s.orderManagementPath}/outbox/poisoned`);
  return d.totalCount ?? 0;
}

async function poisonedInventoryInboxCount() {
  const s = state.services;
  const d = await api.fetchPoisoned(`${s.inventoryServiceUrl}/${s.inventoryManagementPath}/inbox/poisoned`);
  return d.totalCount ?? 0;
}

async function notifDlqTotal() {
  const d = await api.fetchRabbitDepths();
  if (!d.configured) return 0;
  const n = d.queues.find((q) => q.key === 'notifications-events');
  return n ? n.dlq : 0;
}

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

/** @type {Array<{ id: string, title: string, topic: string, description: string, run: () => Promise<{ pass: boolean, expected: object, actual: object, reasons: string[] }> }>} */
export const SCENARIOS = [
  {
    id: 'outbox-success',
    title: 'Outbox happy path',
    topic: 'Outbox',
    description: 'All toggles succeed; place order via outbox; expect Fulfilled.',
    run: async () => {
      setProgress(['Reset', 'Arrange', 'Act', 'Verify'], 0);
      await resetBaseline();
      setProgress(['Reset', 'Arrange', 'Act', 'Verify'], 2);
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      setProgress(['Reset', 'Arrange', 'Act', 'Verify'], 3);
      const pr = await pollUntil(() => api.getFlow(o.id), (f) => f.status === 'Fulfilled', 45_000);
      const pass = pr.ok;
      return {
        pass,
        expected: { orderStatus: 'Fulfilled' },
        actual: { orderStatus: pr.value?.status, pollOk: pr.ok },
        reasons: pass ? [] : ['Order did not reach Fulfilled in time'],
      };
    },
  },
  {
    id: 'outbox-retry-then-success',
    title: 'Outbox relay retries then succeeds',
    topic: 'Outbox',
    description: 'Simulate 2 transport failures on send, then succeed; order still completes.',
    run: async () => {
      await resetBaseline();
      await api.setToggle('order', 'simulate-outbox-transport-failure', { mode: 'succeed-after', failureCount: 2 });
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      const pr = await pollUntil(() => api.getFlow(o.id), (f) => f.status === 'Fulfilled', 60_000);
      await resetBaseline();
      return {
        pass: pr.ok,
        expected: { orderStatus: 'Fulfilled', outboxSendFailuresSimulated: 2 },
        actual: { orderStatus: pr.value?.status },
        reasons: pr.ok ? [] : ['Fulfilled not reached'],
      };
    },
  },
  {
    id: 'outbox-poison',
    title: 'Outbox poisoned rows',
    topic: 'Outbox',
    description: 'Transport always fails; expect poisoned outbox count to increase.',
    run: async () => {
      await resetBaseline();
      const before = await poisonedOutboxCount();
      await api.setToggle('order', 'simulate-outbox-transport-failure', { mode: 'fail' });
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      await sleep(8000);
      const after = await poisonedOutboxCount();
      await api.setToggle('order', 'simulate-outbox-transport-failure', { mode: 'succeed' });
      const pass = after > before;
      return {
        pass,
        expected: { poisonedOutboxIncrease: true, before, after },
        actual: { before, after },
        reasons: pass ? [] : ['Poisoned outbox count did not increase (wait longer or check relay)'],
      };
    },
  },
  {
    id: 'inbox-retry-then-success',
    title: 'Inventory inbox retry then success',
    topic: 'Inbox',
    description: 'ProcessOrderCommand fails twice then fulfills.',
    run: async () => {
      await resetBaseline();
      await api.setToggle('inventory', 'consume-processordercommand-inbox', { mode: 'succeed-after', failureCount: 2 });
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      const pr = await pollUntil(() => api.getFlow(o.id), (f) => f.status === 'Fulfilled', 45_000);
      await resetBaseline();
      return {
        pass: pr.ok,
        expected: { orderStatus: 'Fulfilled' },
        actual: { orderStatus: pr.value?.status },
        reasons: pr.ok ? [] : ['Not fulfilled'],
      };
    },
  },
  {
    id: 'inbox-poison',
    title: 'Inventory inbox poison',
    topic: 'Inbox',
    description: 'Inventory throw mode until poisoned inbox row appears.',
    run: async () => {
      await resetBaseline();
      const before = await poisonedInventoryInboxCount();
      await api.setToggle('inventory', 'consume-processordercommand-inbox', { mode: 'throw' });
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      const pr = await pollUntil(async () => {
        const n = await poisonedInventoryInboxCount();
        return { n };
      }, (x) => x.n > before, 45_000);
      await api.setToggle('inventory', 'consume-processordercommand-inbox', { mode: 'off' });
      return {
        pass: pr.ok,
        expected: { poisonedInventoryInbox: '> before' },
        actual: pr.value,
        reasons: pr.ok ? [] : ['No new poisoned inventory inbox entry'],
      };
    },
  },
  {
    id: 'business-rejection',
    title: 'Business rejection (OrderFailed)',
    topic: 'Inbox',
    description: 'Inventory reject mode stages OrderFailed; OrderService marks Failed.',
    run: async () => {
      await resetBaseline();
      await api.setToggle('inventory', 'consume-processordercommand-inbox', { mode: 'reject' });
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      const pr = await pollUntil(() => api.getFlow(o.id), (f) => f.status === 'Failed', 45_000);
      await resetBaseline();
      return {
        pass: pr.ok,
        expected: { orderStatus: 'Failed' },
        actual: { orderStatus: pr.value?.status },
        reasons: pr.ok ? [] : ['Order not Failed'],
      };
    },
  },
  {
    id: 'direct-consume-success',
    title: 'Direct publish happy path',
    topic: 'Direct consume',
    description: 'Place order via direct publish; pipeline completes.',
    run: async () => {
      await resetBaseline();
      const o = await api.placeOrderDirect();
      state.lastOrderId = o.id;
      restartFlowPoll();
      const pr = await pollUntil(() => api.getFlow(o.id), (f) => f.status === 'Fulfilled', 45_000);
      return {
        pass: pr.ok,
        expected: { orderStatus: 'Fulfilled', publishOrigin: 'direct' },
        actual: { orderStatus: pr.value?.status, publishOrigin: pr.value?.publishOrigin },
        reasons: pr.ok ? [] : ['Not fulfilled'],
      };
    },
  },
  {
    id: 'direct-consume-retry',
    title: 'Notification OrderPlaced succeed-after-2',
    topic: 'Direct consume',
    description: 'Rabbit retries inline handler twice then succeeds; order still Fulfilled.',
    run: async () => {
      await resetBaseline();
      await api.setToggle('notification', 'consume-orderplaced-rabbit', { mode: 'succeed-after', failureCount: 2 });
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      const pr = await pollUntil(() => api.getFlow(o.id), (f) => f.status === 'Fulfilled', 60_000);
      await resetBaseline();
      return {
        pass: pr.ok,
        expected: { orderStatus: 'Fulfilled' },
        actual: { orderStatus: pr.value?.status },
        reasons: pr.ok ? [] : ['Not fulfilled'],
      };
    },
  },
  {
    id: 'direct-consume-dlq',
    title: 'Notification DLQ (no inbox)',
    topic: 'Direct consume',
    description: 'OrderPlaced handler always fails; expect notifications queue DLQ to grow.',
    run: async () => {
      await resetBaseline();
      const d0 = await notifDlqTotal();
      await api.setToggle('notification', 'consume-orderplaced-rabbit', { mode: 'fail' });
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      const pr = await pollUntil(async () => {
        const d = await notifDlqTotal();
        return { dlq: d };
      }, (x) => x.dlq > d0, 60_000);
      await resetBaseline();
      return {
        pass: pr.ok,
        expected: { dlqIncreased: true },
        actual: pr.value,
        reasons: pr.ok ? [] : ['DLQ did not increase (may need longer wait)'],
      };
    },
  },
  {
    id: 'replay-dedup',
    title: 'Replay (dedup vs double delivery)',
    topic: 'Other',
    description: 'After Fulfilled, replay publishes same ids; Notification sees OrderPlaced again; Inventory dedups command.',
    run: async () => {
      await resetBaseline();
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      const f1 = await pollUntil(() => api.getFlow(o.id), (f) => f.status === 'Fulfilled', 45_000);
      if (!f1.ok)
        return { pass: false, expected: {}, actual: f1, reasons: ['Not fulfilled before replay'] };
      const actBefore = await api.mergeActivities(o.id);
      await api.replayOrder(o.id);
      await sleep(4000);
      const actAfter = await api.mergeActivities(o.id);
      const nPlaced = actAfter.filter(
        (e) => e._svc === 'Notification' && String(e.messageType ?? '').includes('OrderPlaced') && e.stage === 'Dispatched',
      ).length;
      const pass = actAfter.length > actBefore.length && nPlaced >= 2;
      return {
        pass,
        expected: { moreActivityAfterReplay: true, notificationOrderPlacedDispatchedGte2: true },
        actual: { rowsBefore: actBefore.length, rowsAfter: actAfter.length, notifDispatched: nPlaced },
        reasons: pass ? [] : ['Activity did not grow as expected after replay'],
      };
    },
  },
  {
    id: 'efcore-internal-command',
    title: 'EF Core internal command',
    topic: 'Other',
    description: 'ReserveStockInternal is staged in the same SaveChanges; look for handler activity on Order lane / EfCore.',
    run: async () => {
      await resetBaseline();
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      await sleep(5000);
      const act = await api.mergeActivities(o.id);
      const hit = act.some(
        (e) =>
          String(e.messageType ?? '').includes('ReserveStock') &&
          (e._svc === 'Order' || String(e.transportName ?? '').toLowerCase().includes('efcore')),
      );
      return {
        pass: hit,
        expected: { sawReserveStockActivity: true },
        actual: { activityCount: act.length },
        reasons: hit ? [] : ['No ReserveStockInternal activity captured yet'],
      };
    },
  },
  {
    id: 'fanout-two-handlers',
    title: 'Fan-out: two OrderPlaced handlers',
    topic: 'Other',
    description: 'Both notify and analytics handlers run per successful delivery.',
    run: async () => {
      await resetBaseline();
      const o = await api.placeOrderOutbox();
      state.lastOrderId = o.id;
      restartFlowPoll();
      await sleep(6000);
      const act = await api.mergeActivities(o.id);
      const ok = act.filter(
        (e) =>
          e._svc === 'Notification' &&
          String(e.messageType ?? '').includes('OrderPlaced') &&
          e.isSuccess === true &&
          e.stage === 'Dispatched',
      );
      const pass = ok.length >= 2;
      return {
        pass,
        expected: { notificationOrderPlacedSuccessGte2: true },
        actual: { matchingRows: ok.length },
        reasons: pass ? [] : ['Expected at least 2 successful Notification OrderPlaced handler rows'],
      };
    },
  },
  {
    id: 'oversized-payload-rolls-back',
    title: 'Oversized outbox payload',
    topic: 'Other',
    description: 'SaveChanges fails; order row must not persist.',
    run: async () => {
      await resetBaseline();
      const r = await api.placeOrderOversized();
      const pass = r.saveFailed === true && r.orderRowExists === false;
      return {
        pass,
        expected: { saveFailed: true, orderRowExists: false },
        actual: r,
        reasons: pass ? [] : ['Oversized demo did not return expected shape'],
      };
    },
  },
];

export async function runScenario(id) {
  const sc = SCENARIOS.find((x) => x.id === id);
  if (!sc) throw new Error('unknown scenario');
  setProgress(['Reset', 'Arrange', 'Act', 'Verify'], 0);
  setProgress(['Reset', 'Arrange', 'Act', 'Verify'], 1);
  const { pass, expected, actual, reasons } = await sc.run();
  setProgress(['Reset', 'Arrange', 'Act', 'Verify'], 3);
  showResult(pass, expected, actual, reasons);
  await refreshServicePanels();
  return { pass };
}

export function fillScenarioSelect(selectEl) {
  if (!selectEl) return;
  const byTopic = {};
  for (const s of SCENARIOS) {
    if (!byTopic[s.topic]) byTopic[s.topic] = [];
    byTopic[s.topic].push(s);
  }
  for (const [topic, list] of Object.entries(byTopic)) {
    const og = document.createElement('optgroup');
    og.label = topic;
    for (const s of list) {
      const o = document.createElement('option');
      o.value = s.id;
      o.textContent = s.title;
      og.appendChild(o);
    }
    selectEl.appendChild(og);
  }
}

export function bindScenarioDescription(selectEl, descEl) {
  if (!selectEl || !descEl) return;
  selectEl.addEventListener('change', () => {
    const s = SCENARIOS.find((x) => x.id === selectEl.value);
    descEl.textContent = s?.description ?? '';
  });
}
