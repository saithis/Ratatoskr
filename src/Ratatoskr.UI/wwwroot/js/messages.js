// The outbox and inbox tables, and the per-row actions.

import * as api from "./api.js";
import { byId, el, emptyRow, formatInstant, replaceChildren, shortId } from "./dom.js";
import { openDetail } from "./detail.js";
import * as state from "./state.js";
import { toast } from "./toast.js";

/** Loads and renders one page of the currently selected area. */
export async function renderMessages() {
  const target = state.target();
  const area = state.current().tab;
  const tbody = byId("messages-tbody");

  if (target === null) {
    replaceChildren(tbody, [emptyRow(9, "Choose a database context to inspect.")]);
    return;
  }

  byId("col-id").textContent = area === "inbox" ? "Handler status" : "Message id";
  byId("col-extra").textContent = area === "inbox" ? "Handler" : "Transport";

  try {
    const page = await api.listMessages(
      target,
      area,
      state.serverFilter(),
      state.current().cursor,
      50,
    );

    replaceChildren(
      tbody,
      page.items.length > 0
        ? page.items.map((item) => renderRow(area, item))
        : [emptyRow(9, "Nothing matches this filter.")],
    );

    const count = page.items.length;
    byId("page-info").textContent = `${count} row${count === 1 ? "" : "s"} on this page`;
    byId("btn-next-page").disabled = !page.nextCursor;
    byId("btn-prev-page").disabled = state.current().cursorStack.length === 0;
    byId("btn-next-page").dataset.cursor = page.nextCursor ?? "";
    byId("select-all").checked = false;
    updateSelectionButtons();
  } catch (error) {
    replaceChildren(tbody, [emptyRow(9, describe(error))]);
    byId("page-info").textContent = "";
  }
}

function renderRow(area, item) {
  const id = area === "inbox" ? item.handlerStatusId : item.id;
  const checkbox = el("input", { attrs: { type: "checkbox" } });
  checkbox.checked = state.current().selectedIds.has(id);
  checkbox.addEventListener("change", () => state.toggleSelected(id, checkbox.checked));

  const inspect = el("button", { className: "btn btn-secondary btn-sm", text: "Inspect" });
  inspect.addEventListener("click", () => openDetail(area, id));

  const requeue = el("button", { className: "btn btn-primary btn-sm", text: "Requeue" });
  requeue.disabled = !item.isPoisoned;
  requeue.addEventListener("click", () => mutateOne(area, "requeue", id));

  const remove = el("button", { className: "btn btn-danger btn-sm", text: "Delete" });
  remove.disabled = !item.isPoisoned;
  remove.addEventListener("click", () => mutateOne(area, "delete", id));

  const actions = [inspect, requeue, remove];
  if (area === "inbox") {
    const requeueMessage = el("button", {
      className: "btn btn-secondary btn-sm",
      text: "Requeue message",
      attrs: { title: "Requeue every poisoned handler of this message" },
    });
    requeueMessage.addEventListener("click", () => requeueWholeMessage(item.messageId));
    actions.push(requeueMessage);
  }

  return el("tr", {
    children: [
      el("td", { className: "select-column", children: [checkbox] }),
      el("td", {
        className: "mono",
        text: shortId(id),
        attrs: { title: String(id) },
      }),
      el("td", { text: item.messageType }),
      el("td", { text: area === "inbox" ? item.handlerKey : item.transportName }),
      el("td", { text: formatInstant(area === "inbox" ? item.receivedAt : item.createdAt) }),
      el("td", { text: String(item.errorCount) }),
      el("td", { text: String(item.requeuedCount) }),
      el("td", {
        className: "error-cell",
        text: item.lastError ?? "—",
        attrs: { title: item.lastError ?? "" },
      }),
      el("td", { className: "actions-column", children: actions }),
    ],
  });
}

async function mutateOne(area, action, id) {
  await runMutation(action, () => api.mutateByIds(state.target(), area, action, [id]));
}

async function requeueWholeMessage(messageId) {
  await runMutation("requeue", () => api.requeueInboxMessage(state.target(), messageId));
}

/** Applies an explicit-id mutation to every checked row. */
export async function mutateSelected(action) {
  const ids = [...state.current().selectedIds];
  if (ids.length === 0) {
    return;
  }

  await runMutation(action, () =>
    api.mutateByIds(state.target(), state.current().tab, action, ids),
  );
}

async function runMutation(action, operation) {
  try {
    const result = await operation();
    const failed = result?.failed ?? [];
    const succeeded = result?.succeeded ?? [];

    if (failed.length > 0) {
      // Partial failure is normal for a batch — some rows moved on between listing and acting —
      // so the operator is told exactly how many, not just that "something" went wrong.
      toast(
        `${verb(action)} ${succeeded.length}; ${failed.length} could not be applied.`,
        "warning",
      );
    } else {
      toast(`${verb(action)} ${succeeded.length}.`, "success");
    }

    state.clearSelection();
    await renderMessages();
  } catch (error) {
    toast(describe(error), "danger");
  }
}

function verb(action) {
  return action === "requeue" ? "Requeued" : "Deleted";
}

/** Enables the bulk buttons only when something is actually selected. */
export function updateSelectionButtons() {
  const count = state.current().selectedIds.size;
  const requeue = byId("btn-requeue-selected");
  const remove = byId("btn-delete-selected");

  requeue.disabled = count === 0;
  remove.disabled = count === 0;
  requeue.textContent = count === 0 ? "↺ Requeue selected" : `↺ Requeue ${count} selected`;
  remove.textContent = count === 0 ? "🗑 Delete selected" : `🗑 Delete ${count} selected`;
}

/** Turns any failure into something an operator can act on. */
export function describe(error) {
  if (error instanceof api.ManagementError) {
    return `${error.message} (${error.code})`;
  }
  return error?.message ?? "The request failed.";
}
