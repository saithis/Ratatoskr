export function sleep(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

/** @template T @param {() => Promise<T>} fn @param {(v: T) => boolean} pred */
export async function pollUntil(fn, pred, timeoutMs = 30_000, intervalMs = 1000) {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    last = await fn();
    if (pred(last)) return { ok: true, value: last };
    await sleep(intervalMs);
  }
  return { ok: false, value: last };
}

export function makeArmedButton(btn, action) {
  let armed = false;
  let resetTimer;
  const origLabel = btn.textContent;
  btn.addEventListener('click', () => {
    if (!armed) {
      armed = true;
      btn.dataset.origLabel = btn.textContent;
      btn.textContent = 'Click again to confirm';
      btn.classList.add('btn--armed');
      resetTimer = setTimeout(() => {
        armed = false;
        btn.textContent = btn.dataset.origLabel ?? origLabel;
        btn.classList.remove('btn--armed');
      }, 3000);
    } else {
      clearTimeout(resetTimer);
      armed = false;
      btn.textContent = btn.dataset.origLabel ?? origLabel;
      btn.classList.remove('btn--armed');
      action();
    }
  });
}
