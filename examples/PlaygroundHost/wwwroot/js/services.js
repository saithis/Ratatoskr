import { state } from './state.js';
import { setToggle, fetchControlState } from './api.js';

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
  try {
    const data = await fetchControlState('host');
    if (data.toggles?.find((t) => t.key === 'consume-processordercommand-inbox')?.mode)
      state.inventoryDemoMode = String(
        data.toggles.find((t) => t.key === 'consume-processordercommand-inbox').mode,
      ).toLowerCase();

    for (const t of data.toggles ?? []) {
      const row = document.createElement('div');
      row.className = 'toggle-line';

      const lab = document.createElement('span');
      lab.textContent = t.label;
      row.appendChild(lab);

      const modeSpan = document.createElement('span');
      modeSpan.className = 'mode-tag';
      modeSpan.textContent = `${t.mode}${t.failuresRemaining > 0 ? ` (${t.failuresRemaining} left)` : ''}`;
      if (t.mode === 'fail' || t.mode === 'throw' || t.mode === 'reject') modeSpan.classList.add('warn');
      row.appendChild(modeSpan);

      const sel = document.createElement('select');
      const isInventory = t.key === 'consume-processordercommand-inbox';
      const opts = isInventory
        ? `<option value="">(apply mode)</option>
          <option value="off">off (fulfill)</option>
          <option value="throw">throw (inbox retries)</option>
          <option value="succeed-after">succeed-after</option>
          <option value="reject">reject</option>`
        : `<option value="">(apply mode)</option>
          <option value="succeed">succeed</option>
          <option value="fail">fail</option>
          <option value="succeed-after">succeed-after</option>`;
      sel.innerHTML = opts;
      sel.style.fontSize = '0.72rem';
      sel.style.maxWidth = '9rem';

      const num = document.createElement('input');
      num.type = 'number';
      num.min = '1';
      num.max = '20';
      num.value = '2';
      num.title = 'failureCount for succeed-after';
      num.style.width = '3rem';
      num.style.fontSize = '0.72rem';

      const btnApply = document.createElement('button');
      btnApply.className = 'btn btn-outline';
      btnApply.style.fontSize = '0.72rem';
      btnApply.style.padding = '0.2rem 0.45rem';
      btnApply.textContent = 'Apply';
      btnApply.addEventListener('click', async () => {
        try {
          const m = sel.value;
          if (!m) {
            if (errEl) errEl.textContent = 'Pick a mode first';
            return;
          }
          const fc = Number(num.value) || 2;
          const body = m === 'succeed-after' ? { mode: 'succeed-after', failureCount: fc } : { mode: m };
          await setToggle('host', t.key, body);
          await refreshServicePanels();
        } catch (e) {
          if (errEl) errEl.textContent = e.message;
        }
      });

      const btnCycle = document.createElement('button');
      btnCycle.className = 'btn btn-outline';
      btnCycle.style.fontSize = '0.72rem';
      btnCycle.style.padding = '0.2rem 0.45rem';
      btnCycle.textContent = 'Cycle';
      btnCycle.addEventListener('click', async () => {
        try {
          await setToggle('host', t.key, {});
          await refreshServicePanels();
        } catch (e) {
          if (errEl) errEl.textContent = e.message;
        }
      });

      row.appendChild(sel);
      row.appendChild(num);
      row.appendChild(btnApply);
      row.appendChild(btnCycle);
      inner.appendChild(row);
      if (t.hint) {
        const hint = document.createElement('div');
        hint.style.cssText = 'font-size:0.72rem;color:#64748b;margin:0.1rem 0 0.35rem 0;';
        hint.textContent = t.hint;
        inner.appendChild(hint);
      }
    }
  } catch (e) {
    inner.textContent = `Could not load control state: ${e.message}`;
  }
  box.appendChild(inner);
  host.appendChild(box);
}
