import { state } from './state.js';
import * as api from './api.js';
import { refreshServicePanels } from './services.js';
import { restartFlowPoll, bindRawActivityToggle } from './timeline.js';
import { startPoisonedPollers } from './poisoned.js';
import { startRabbitPoller } from './rabbit.js';
import { fillScenarioSelect, bindScenarioDescription, runScenario, SCENARIOS } from './scenarios.js';

function bindOrderButtons() {
  const err = () => document.getElementById('order-actions-error');
  const info = () => document.getElementById('last-order-info');
  const btnReplay = document.getElementById('btn-replay');

  document.getElementById('btn-place-outbox')?.addEventListener('click', async () => {
    try {
      err().textContent = '';
      const data = await api.placeOrderOutbox();
      state.lastOrderId = data.id;
      info().textContent = `Last order: ${data.id} (${data.status}, outbox)`;
      btnReplay.disabled = false;
      btnReplay.title = '';
      restartFlowPoll();
      await refreshServicePanels();
    } catch (e) {
      err().textContent = e.message;
    }
  });

  document.getElementById('btn-place-direct')?.addEventListener('click', async () => {
    try {
      err().textContent = '';
      const data = await api.placeOrderDirect();
      state.lastOrderId = data.id;
      info().textContent = `Last order: ${data.id} (${data.status}, direct publish)`;
      btnReplay.disabled = false;
      btnReplay.title = '';
      restartFlowPoll();
      await refreshServicePanels();
    } catch (e) {
      err().textContent = e.message;
    }
  });

  document.getElementById('btn-place-oversized')?.addEventListener('click', async () => {
    try {
      err().textContent = '';
      const r = await api.placeOrderOversized();
      info().textContent = `Oversized demo: saveFailed=${r.saveFailed} orderRowExists=${r.orderRowExists}`;
    } catch (e) {
      err().textContent = e.message;
    }
  });

  document.getElementById('btn-replay')?.addEventListener('click', async () => {
    if (!state.lastOrderId) return;
    try {
      err().textContent = '';
      await api.replayOrder(state.lastOrderId);
      info().textContent += ' · replayed';
    } catch (e) {
      err().textContent = e.message;
    }
  });
}

async function init() {
  try {
    const cfg = await fetch('/api/config').then((r) => r.json());
    state.services = cfg;
    document.getElementById('loading').hidden = true;
    document.getElementById('app').hidden = false;

    const sel = document.getElementById('scenario-select');
    fillScenarioSelect(sel);
    if (SCENARIOS[0]) sel.value = SCENARIOS[0].id;
    bindScenarioDescription(sel, document.getElementById('scenario-desc'));

    document.getElementById('btn-run-scenario')?.addEventListener('click', async () => {
      const errEl = document.getElementById('scenario-run-error');
      errEl.textContent = '';
      try {
        await runScenario(sel.value);
      } catch (e) {
        errEl.textContent = e.message;
      }
    });

    await refreshServicePanels();
    bindOrderButtons();
    bindRawActivityToggle();
    startPoisonedPollers();
    startRabbitPoller();
    restartFlowPoll();
  } catch (e) {
    document.getElementById('loading').textContent = `Failed to load config: ${e.message}`;
  }
}

init();
