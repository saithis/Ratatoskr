// The filter-driven bulk flow: choose, preview, confirm, and be told what is left.
//
// The old dashboard offered a one-click "requeue all poisoned" and told the operator it matched
// "the current filter", which it did not. This flow only ever acts on the filter the operator can
// see, shows the count the server itself would touch before anything changes, and reports
// {processed, remaining, capped} afterwards — because a capped run must not read as a finished one.

import * as api from "./api.js";
import { byId, el, replaceChildren, show } from "./dom.js";
import { describe, renderMessages } from "./messages.js";
import * as state from "./state.js";
import { toast } from "./toast.js";

let pending = null;

/** Opens the confirmation dialog for a filter-driven mutation. */
export async function beginMatching(action) {
  const target = state.target();
  if (target === null) {
    toast("Choose a database context first.", "warning");
    return;
  }

  const area = state.current().tab;
  const filter = state.serverFilter();
  pending = { action, area, target, filter, operationId: crypto.randomUUID() };

  byId("confirm-title").textContent =
    action === "requeue" ? "Requeue everything matching" : "Delete everything matching";
  byId("confirm-summary").textContent =
    action === "requeue"
      ? `Requeue every poisoned ${area} row in ${target.context} that matches this filter.`
      : `Permanently delete every ${area} row in ${target.context} that matches this filter. ` +
        "This cannot be undone.";

  replaceChildren(byId("confirm-filter"), describeFilter(filter, target));
  byId("confirm-count").textContent = "Counting…";
  show(byId("confirm-result"), false);
  byId("btn-confirm-apply").disabled = true;
  show(byId("confirm-modal"), true);

  try {
    const { count } = await api.countMessages(target, area, filter);
    pending.count = count;

    byId("confirm-count").textContent =
      count === 0
        ? "Nothing matches this filter."
        : `${count} row${count === 1 ? "" : "s"} match this filter.`;
    byId("btn-confirm-apply").disabled = count === 0;
    byId("btn-confirm-apply").textContent =
      action === "requeue" ? `Requeue ${count}` : `Delete ${count}`;
  } catch (error) {
    byId("confirm-count").textContent = describe(error);
    byId("btn-confirm-apply").disabled = true;
  }
}

/** Applies the previewed mutation. */
export async function applyMatching() {
  if (pending === null) {
    return;
  }

  byId("btn-confirm-apply").disabled = true;
  byId("btn-confirm-apply").textContent = "Working…";

  try {
    const result = await api.mutateMatching(
      pending.target,
      pending.area,
      pending.action,
      pending.filter,
      pending.operationId,
    );

    const message = result.capped
      ? `${result.processed} applied. ${result.remaining} still match — run it again to continue.`
      : `${result.processed} applied. Nothing is left matching this filter.`;

    const outcome = byId("confirm-result");
    outcome.textContent = message;
    outcome.className = result.capped ? "warning-text" : "muted";
    show(outcome, true);

    byId("btn-confirm-apply").textContent = result.capped ? "Run again" : "Done";
    byId("btn-confirm-apply").disabled = !result.capped;
    byId("confirm-count").textContent = result.capped
      ? `${result.remaining} remaining`
      : "Complete";

    toast(message, result.capped ? "warning" : "success");
    await renderMessages();
  } catch (error) {
    byId("confirm-result").textContent = describe(error);
    byId("confirm-result").className = "warning-text";
    show(byId("confirm-result"), true);
    byId("btn-confirm-apply").disabled = false;
    byId("btn-confirm-apply").textContent = "Retry";
  }
}

/** Closes the confirmation dialog. */
export function closeConfirm() {
  pending = null;
  show(byId("confirm-modal"), false);
}

function describeFilter(filter, target) {
  const rows = [
    ["Transport", target.transport],
    ["Service", target.service],
    ["Context", target.context],
    ["Status", filter.status],
  ];

  if (filter.search) {
    rows.push(["Search", filter.search]);
  }
  if (filter.from) {
    rows.push(["From", new Date(filter.from).toLocaleString()]);
  }
  if (filter.to) {
    rows.push(["To", new Date(filter.to).toLocaleString()]);
  }

  return rows.flatMap(([label, value]) => [
    el("dt", { text: label }),
    el("dd", { text: String(value) }),
  ]);
}
