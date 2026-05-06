import { fetchRabbitDepths } from './api.js';

export async function refreshRabbitDepths() {
  const badge = document.getElementById('badge-rabbit');
  const err = document.getElementById('error-rabbit');
  const uncfg = document.getElementById('rabbit-unconfigured');
  const body = document.getElementById('rabbit-body');
  const rows = document.getElementById('rabbit-rows');
  const hint = document.getElementById('rabbit-retry-hint');
  try {
    const data = await fetchRabbitDepths();
    if (badge) badge.hidden = true;
    if (err) err.textContent = '';
    if (!data.configured) {
      if (uncfg) uncfg.hidden = false;
      if (body) body.hidden = true;
      return;
    }
    if (uncfg) uncfg.hidden = true;
    if (body) body.hidden = false;
    if (rows) {
      rows.innerHTML = '';
      let anyRetry = false;
      let minTtl = 999;
      for (const q of data.queues) {
        if (q.retry > 0) {
          anyRetry = true;
          minTtl = Math.min(minTtl, q.retryDelaySeconds ?? 5);
        }
        const row = document.createElement('div');
        row.className = 'rabbit-row';
        row.innerHTML = `<span title="${q.mainQueue}">${q.slug} · ${q.key}</span>
          <span class="rabbit-n">${q.main}</span>
          <span class="rabbit-n">${q.retry}</span>
          <span class="rabbit-n">${q.dlq}</span>
          <span class="rabbit-n">${q.retryDelaySeconds ?? '–'}s</span>`;
        rows.appendChild(row);
      }
      if (hint) {
        if (anyRetry) {
          const t = Date.now() / 1000;
          const phase = minTtl - (t % minTtl);
          hint.textContent = `Retry queues hold messages waiting up to ~${minTtl}s TTL before redelivery (countdown ≈ ${phase.toFixed(0)}s until next TTL wave, illustrative).`;
        } else hint.textContent = '';
      }
    }
  } catch (e) {
    if (err) err.textContent = e.message;
    if (badge) badge.hidden = false;
  }
}

export function startRabbitPoller() {
  refreshRabbitDepths();
  setInterval(refreshRabbitDepths, 3000);
}
