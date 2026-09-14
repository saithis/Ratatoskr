// A single transient status line. One element, one timer: a stack of toasts would only ever be
// read as noise during the incident someone opened this dashboard to deal with.

import { byId, show } from "./dom.js";

let timer = null;

/** Shows a message for a few seconds. `kind` is one of success, warning, danger. */
export function toast(message, kind = "success") {
  const element = byId("toast");
  element.textContent = message;
  element.className = `toast toast-${kind}`;
  show(element, true);

  if (timer !== null) {
    clearTimeout(timer);
  }

  timer = setTimeout(() => {
    show(element, false);
    timer = null;
  }, 6000);
}
