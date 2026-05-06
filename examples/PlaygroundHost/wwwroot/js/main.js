import { state } from './state.js';
import * as api from './api.js';
import { refreshServicePanels } from './services.js';
import { restartFlowPoll, bindRawActivityToggle } from './timeline.js';
import { startPoisonedPollers } from './poisoned.js';
import { startRabbitPoller } from './rabbit.js';
import { fillScenarioSelect, bindScenarioDescription, runScenario, loadScenarioCatalog } from './scenarios.js';

async function init() {
  try {
    const cfg = await fetch('/api/config').then((r) => r.json());
    const base = window.location.origin;
    state.services = {
      ...cfg,
      playgroundHostUrl: base,
      orderServiceUrl: base,
      inventoryServiceUrl: base,
      notificationServiceUrl: base,
      orderManagementPath: cfg.publisherManagementPath,
      inventoryManagementPath: cfg.consumerManagementPath,
    };
    document.getElementById('loading').hidden = true;
    document.getElementById('app').hidden = false;

    const sel = document.getElementById('scenario-select');
    const catalog = await loadScenarioCatalog();
    fillScenarioSelect(sel, catalog);
    if (catalog[0]) sel.value = catalog[0].slug;
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
    bindRawActivityToggle();
    startPoisonedPollers();
    startRabbitPoller();
    restartFlowPoll();
  } catch (e) {
    document.getElementById('loading').textContent = `Failed to load config: ${e.message}`;
  }
}

init();
