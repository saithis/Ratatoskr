// Entry point: wires the views to the state and the state to the server.
//
// Every listener is attached here or in the module that owns the element. There are no inline
// handlers anywhere in the markup, which is what lets the Content-Security-Policy stay at
// script-src 'self' with no unsafe-inline escape hatch.

import * as api from "./api.js";
import { renderAudit } from "./audit.js";
import { applyMatching, beginMatching, closeConfirm } from "./bulk.js";
import { byId, replaceChildren, show } from "./dom.js";
import { closeDetail } from "./detail.js";
import { describe, mutateSelected, renderMessages, updateSelectionButtons } from "./messages.js";
import {
  renderChannels,
  renderOverview,
  renderServiceGrid,
  renderSidebar,
} from "./navigation.js";
import * as state from "./state.js";
import { connect } from "./stream.js";
import { toast } from "./toast.js";

let selectedDetail = null;
let showingAudit = false;

async function main() {
  await api.loadAntiforgeryToken();
  wireChrome();
  wireTabs();
  wireFilters();
  wireActions();

  state.subscribe(render);
  connect();

  // The stream delivers a snapshot on connect, but a fetch makes the first paint immediate even
  // if the stream is slow to establish through a proxy.
  try {
    state.setServices(await api.listServices());
  } catch (error) {
    toast(describe(error), "danger");
  }
}

function wireChrome() {
  byId("nav-all").addEventListener("click", () => {
    showingAudit = false;
    state.select(null, null);
  });

  byId("btn-audit").addEventListener("click", async () => {
    showingAudit = true;
    render(state.current());
    await renderAudit();
  });

  byId("btn-refresh").addEventListener("click", () => refresh());
}

function wireTabs() {
  for (const button of document.querySelectorAll(".tab-button")) {
    button.addEventListener("click", () => {
      showingAudit = false;
      state.setTab(button.dataset.tab);
    });
  }

  byId("btn-close-modal").addEventListener("click", closeDetail);
  byId("btn-close-confirm").addEventListener("click", closeConfirm);
  byId("btn-confirm-cancel").addEventListener("click", closeConfirm);
  byId("btn-confirm-apply").addEventListener("click", applyMatching);
}

function wireFilters() {
  byId("context-select").addEventListener("change", (event) =>
    state.setContext(event.target.value),
  );

  byId("btn-apply-filter").addEventListener("click", () =>
    state.setFilter({
      status: byId("status-select").value,
      search: byId("search-input").value.trim(),
      from: byId("from-input").value,
      to: byId("to-input").value,
    }),
  );

  byId("search-input").addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      byId("btn-apply-filter").click();
    }
  });

  byId("btn-next-page").addEventListener("click", () => {
    const cursor = byId("btn-next-page").dataset.cursor;
    if (cursor) {
      state.pushCursor(cursor);
    }
  });

  byId("btn-prev-page").addEventListener("click", () => state.popCursor());
}

function wireActions() {
  byId("select-all").addEventListener("change", (event) => {
    for (const checkbox of document.querySelectorAll("#messages-tbody input[type=checkbox]")) {
      if (checkbox.checked !== event.target.checked) {
        checkbox.checked = event.target.checked;
        checkbox.dispatchEvent(new Event("change"));
      }
    }
  });

  byId("btn-requeue-selected").addEventListener("click", () => mutateSelected("requeue"));
  byId("btn-delete-selected").addEventListener("click", () => mutateSelected("delete"));
  byId("btn-requeue-matching").addEventListener("click", () => beginMatching("requeue"));
  byId("btn-delete-matching").addEventListener("click", () => beginMatching("delete"));
}

function render(snapshot) {
  renderSidebar(snapshot.services);
  updateSelectionButtons();

  const selection = snapshot.selection;
  show(byId("view-audit"), showingAudit);
  show(byId("service-header"), !showingAudit && selection !== null);
  show(byId("view-services"), !showingAudit && selection === null);
  show(byId("view-overview"), !showingAudit && selection !== null && snapshot.tab === "overview");
  show(
    byId("view-messages"),
    !showingAudit && selection !== null && (snapshot.tab === "outbox" || snapshot.tab === "inbox"),
  );
  show(byId("view-channels"), !showingAudit && selection !== null && snapshot.tab === "channels");

  if (showingAudit) {
    return;
  }

  if (selection === null) {
    renderServiceGrid(snapshot.services);
    selectedDetail = null;
    return;
  }

  for (const button of document.querySelectorAll(".tab-button")) {
    button.classList.toggle("active", button.dataset.tab === snapshot.tab);
  }

  void renderSelected(snapshot);
}

async function renderSelected(snapshot) {
  const { transport, service } = snapshot.selection;

  byId("service-title").textContent = service;
  byId("service-meta").textContent = `on control plane “${transport}”`;

  if (
    selectedDetail === null ||
    selectedDetail.transportName !== transport ||
    selectedDetail.serviceName !== service
  ) {
    try {
      selectedDetail = await api.getService(transport, service);
    } catch (error) {
      toast(describe(error), "danger");
      return;
    }
  }

  renderStatus(selectedDetail);
  renderContextOptions(selectedDetail, snapshot);

  if (snapshot.tab === "overview") {
    renderOverview(selectedDetail);
  } else if (snapshot.tab === "channels") {
    renderChannels(selectedDetail, handleRequeueDlq, handlePurgeDlq);
  } else {
    applyCapabilityGating(selectedDetail, snapshot.tab);
    await renderMessages();
  }
}

async function handleRequeueDlq(channel, queue) {
  const answer = prompt(
    `Requeue dead-lettered messages from ${queue.deadLetterQueueName} back to ${queue.queueName}?\n` +
      `Enter count to requeue (or leave blank to requeue up to 500):`,
    "100",
  );
  if (answer === null) {
    return;
  }
  const parsed = parseInt(answer.trim(), 10);
  const limit = Number.isNaN(parsed) || parsed <= 0 ? null : parsed;
  try {
    const target = state.current().selection;
    const result = await api.requeueDlq(target, channel.logicalName, queue.queueName, limit);
    toast(`Requeued ${result.requeuedCount} message(s). ${result.remainingCount} remaining.`, "success");
    refresh();
  } catch (error) {
    toast(describe(error), "danger");
  }
}

async function handlePurgeDlq(channel, queue) {
  const msg =
    `Are you sure you want to PURGE all messages from ${queue.deadLetterQueueName}? ` +
    "This cannot be undone.";
  if (!confirm(msg)) {
    return;
  }
  try {
    const target = state.current().selection;
    const result = await api.purgeDlq(target, channel.logicalName, queue.queueName);
    toast(`Purged ${result.purgedCount} message(s) from ${queue.deadLetterQueueName}.`, "success");
    refresh();
  } catch (error) {
    toast(describe(error), "danger");
  }
}

function renderStatus(detail) {
  const badge = document.createElement("span");
  badge.className = `badge ${detail.liveness === "Online" ? "badge-success" : "badge-warning"}`;
  badge.textContent = detail.liveness === "Online" ? "online" : "stale";
  replaceChildren(byId("service-status"), [badge]);
}

function renderContextOptions(detail, snapshot) {
  const select = byId("context-select");
  const relevant = detail.dbContexts.filter((context) =>
    snapshot.tab === "inbox" ? context.hasInbox : context.hasOutbox,
  );

  const options = relevant.map((context) => {
    const option = document.createElement("option");
    option.value = context.name;
    option.textContent = context.name;
    return option;
  });

  replaceChildren(select, options);

  if (relevant.length === 0) {
    state.current().context = null;
    return;
  }

  const chosen = relevant.some((context) => context.name === snapshot.context)
    ? snapshot.context
    : relevant[0].name;

  select.value = chosen;
  state.current().context = chosen;
}

function applyCapabilityGating(detail, tab) {
  // A service that does not advertise a capability cannot serve it, so the buttons that would
  // call it are disabled rather than offered and then failing.
  const capabilities = new Set((detail.capabilities ?? []).map((capability) => capability.name));
  const supported = capabilities.has(tab);
  const bulk = capabilities.has("bulk");

  for (const id of ["btn-requeue-selected", "btn-delete-selected"]) {
    byId(id).disabled = byId(id).disabled || !supported;
  }

  byId("btn-requeue-matching").disabled = !supported || !bulk;
  byId("btn-delete-matching").disabled = !supported || !bulk;
}

function refresh() {
  selectedDetail = null;
  if (showingAudit) {
    void renderAudit();
  } else {
    render(state.current());
  }
}

void main();
