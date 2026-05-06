import { state } from './state.js';
import { fetchScenarioCatalog } from './api.js';

/** Loads scenario catalog summary (outcomes are fixed per scenario; no runtime toggles). */
export async function refreshServicePanels() {
  const errEl = document.getElementById('playground-services-error');
  const host = document.getElementById('playground-services');
  if (!host) return;
  if (errEl) errEl.textContent = '';
  host.innerHTML = '';
  const box = document.createElement('div');
  box.className = 'svc-box';
  box.innerHTML = '<h3>PlaygroundHost</h3>';
  const inner = document.createElement('div');
  inner.style.fontSize = '0.78rem';
  inner.style.color = '#64748b';
  try {
    const catalog = await fetchScenarioCatalog();
    inner.textContent = `${catalog.length} scenario(s) registered. Each scenario encodes its own success, retry, poison, or DLQ path; pick one and press Run.`;
  } catch (e) {
    inner.textContent = `Could not load scenario catalog: ${e.message}`;
  }
  box.appendChild(inner);
  host.appendChild(box);
}
