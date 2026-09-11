// Ratatoskr Management Dashboard Client. All untrusted values are assigned with textContent.
(() => {
  const byId = (id) => document.getElementById(id);
  const state = { services: [], selected: null, tab: "overview", outbox: { status: "Poisoned", page: 1 }, inbox: { status: "Poisoned", page: 1 } };
  const basePath = location.pathname.replace(/\/$/, "");
  const api = `${basePath}/api`;
  const element = (tag, text, className) => { const value = document.createElement(tag); if (text !== undefined) value.textContent = text; if (className) value.className = className; return value; };
  const clear = (node, ...children) => node.replaceChildren(...children);
  const format = (value) => value ? new Date(value).toLocaleString() : "—";
  const status = (value) => element("span", value, `card-status ${value}`);
  const button = (text, className, listener) => { const value = element("button", text, className); value.type = "button"; value.addEventListener("click", listener); return value; };
  const target = (kind) => `${api}/services/${encodeURIComponent(state.selected)}/contexts/${encodeURIComponent(byId(`${kind}-context-select`).value)}/${kind}`;

  async function request(url, options) {
    const response = await fetch(url, options);
    if (!response.ok) throw new Error(`Request failed (${response.status})`);
    return response.status === 204 ? null : response.json();
  }

  async function refreshServices() {
    try { state.services = await request(`${api}/services`); renderServices(); if (state.selected) await detail(); }
    catch (error) { console.error("Could not load services", error); }
  }

  function renderServices() {
    byId("total-services-badge").textContent = String(state.services.length);
    const list = byId("sidebar-service-list");
    if (!state.services.length) return clear(list, element("li", "No services discovered yet…"));
    clear(list, ...state.services.map((service) => {
      const item = element("li", undefined, `service-nav-item ${state.selected === service.serviceName ? "active" : ""}`);
      item.append(element("span", service.serviceName), status(service.status));
      item.addEventListener("click", () => selectService(service.serviceName));
      return item;
    }));
    const grid = byId("services-grid");
    clear(grid, ...state.services.map((service) => {
      const card = element("div", undefined, "service-card");
      const header = element("div", undefined, "card-header"); header.append(element("span", service.serviceName, "card-title"), status(service.status)); card.append(header);
      card.append(element("p", `${service.instanceCount} active replica(s) • Contexts: ${(service.dbContextNames || []).join(", ") || "None"}`));
      const metrics = element("div", undefined, "card-metrics");
      [["Poisoned Outbox", service.totalPoisonedOutbox], ["Pending Outbox", service.totalPendingOutbox], ["Poisoned Inbox", service.totalPoisonedInbox], ["Pending Inbox", service.totalPendingInbox]].forEach(([name, value]) => {
        const box = element("div", undefined, "metric-box"); box.append(element("span", name, "metric-label"), element("span", String(value), `metric-val ${value > 0 && name.startsWith("Poisoned") ? "poisoned" : ""}`)); metrics.append(box);
      });
      card.append(metrics); card.addEventListener("click", () => selectService(service.serviceName)); return card;
    }));
  }

  async function selectService(name) {
    state.selected = name; byId("service-header").style.display = "block"; byId("view-all-services").style.display = "none";
    const service = state.services.find((item) => item.serviceName === name); byId("selected-service-title").textContent = name; byId("selected-service-meta").textContent = service ? `${service.instanceCount} replicas` : "";
    clear(byId("selected-service-status"), status(service?.status || "online")); renderServices(); await showTab(state.tab);
  }

  async function detail() {
    if (!state.selected) return; const value = await request(`${api}/services/${encodeURIComponent(state.selected)}`); renderDetail(value); return value;
  }
  function cells(row, values) { values.forEach((value) => row.append(element("td", String(value)))); return row; }
  function renderDetail(value) {
    clear(byId("replicas-tbody"), ...value.instances.map((item) => cells(element("tr"), [item.instanceId, item.machineName, item.environment || "Production", format(item.startedAt), format(item.lastHeartbeat), item.isActive ? "active" : "stale"])));
    clear(byId("contexts-tbody"), ...value.dbContexts.map((item) => cells(element("tr"), [item.dbContextName, item.hasOutbox ? "✓" : "—", item.hasInbox ? "✓" : "—", item.pendingOutboxCount, item.poisonedOutboxCount, item.pendingInboxCount, item.poisonedInboxCount])));
    clear(byId("channels-tbody"), ...value.channels.map((item) => cells(element("tr"), [item.logicalName, item.intent, (item.transportBindings || []).map((binding) => binding.providerKind).join(", "), (item.transportBindings || []).map((binding) => binding.displayName).join(", "), "", (item.messageTypes || []).join(", ")])));
    ["outbox", "inbox"].forEach((kind) => { const select = byId(`${kind}-context-select`); const current = select.value; clear(select, ...value.dbContexts.map((context) => element("option", context.dbContextName))); select.value = current || value.dbContexts[0]?.dbContextName || ""; });
  }

  async function messages(kind) {
    if (!state.selected || !byId(`${kind}-context-select`).value) return;
    const page = state[kind].page; const result = await request(`${target(kind)}?status=${encodeURIComponent(state[kind].status)}&page=${page}&pageSize=20`); renderMessages(kind, result);
  }
  function renderMessages(kind, result) {
    const body = byId(`${kind}-tbody`); const info = byId(`${kind}-page-info`); info.textContent = `Showing ${result.totalCount ? (result.page - 1) * result.pageSize + 1 : 0}-${Math.min(result.page * result.pageSize, result.totalCount)} of ${result.totalCount}`;
    byId(`${kind}-prev-page`).disabled = result.page <= 1; byId(`${kind}-next-page`).disabled = result.page * result.pageSize >= result.totalCount;
    if (!result.items.length) return clear(body, cells(element("tr"), ["No messages found."]));
    clear(body, ...result.items.map((item) => {
      const row = element("tr"); const id = kind === "outbox" ? item.id : item.messageId; const error = kind === "outbox" ? item.error : item.lastError;
      cells(row, [id, kind === "inbox" ? item.handlerKey : item.transportName, kind === "inbox" ? item.transportName : format(item.createdAt), kind === "inbox" ? format(item.createdAt) : item.errorCount, kind === "inbox" ? item.errorCount : (item.isPoisoned ? "Poisoned" : item.processedAt ? "Processed" : "Pending"), kind === "inbox" ? (item.isPoisoned ? "Poisoned" : item.completedAt ? "Completed" : "Pending") : (error || "—")]);
      if (kind === "inbox") row.append(element("td", error || "—"));
      const actions = element("td"); actions.append(button("Inspect", "btn btn-secondary btn-sm", () => inspect(kind, item.id)));
      if (item.isPoisoned) actions.append(button("↺", "btn btn-primary btn-sm", () => mutate(kind, item.id, "requeue")));
      actions.append(button("🗑", "btn btn-danger btn-sm", () => mutate(kind, item.id, "delete"))); row.append(actions); return row;
    }));
  }

  async function mutate(kind, id, action) {
    const deleting = action === "delete"; if (deleting && !confirm("This permanently deletes the selected message. Continue?")) return;
    try { await request(`${target(kind)}/${id}${action === "requeue" ? "/requeue" : ""}`, { method: deleting ? "DELETE" : "POST" }); closeModal(); await messages(kind); }
    catch (error) { console.error("Management mutation failed", error); }
  }
  async function bulk(kind, action) {
    const phrase = action === "delete" ? "permanently delete" : "requeue";
    if (!confirm(`This will ${phrase} all messages matching the current explicit poisoned filter. Continue?`)) return;
    try { await request(`${target(kind)}/bulk-${action}`, { method: action === "delete" ? "DELETE" : "POST" }); await messages(kind); }
    catch (error) { console.error("Bulk management mutation failed", error); }
  }
  async function inspect(kind, id) {
    try { const item = await request(`${target(kind)}/${id}`); byId("modal-title").textContent = `${kind === "outbox" ? "Outbox Message" : "Inbox Handler"}: ${kind === "outbox" ? item.id : item.handlerKey}`;
      const content = byId("modal-body"); const fields = [["Error", kind === "outbox" ? item.error : item.lastError], ["CloudEvents Metadata", JSON.stringify(item.properties, null, 2)], ["Payload", formatJson(item.content)]];
      clear(content, ...fields.filter(([, value]) => value).map(([label, value]) => { const section = element("div"); section.append(element("strong", label), element("pre", value, "code-viewer")); return section; }));
      const footer = byId("modal-footer"); clear(footer, ...(item.isPoisoned ? [button("↺ Requeue", "btn btn-primary", () => mutate(kind, item.id, "requeue"))] : []), button("🗑 Delete", "btn btn-danger", () => mutate(kind, item.id, "delete")), button("Close", "btn btn-secondary", closeModal)); byId("detail-modal").style.display = "flex";
    } catch (error) { console.error("Could not load message detail", error); }
  }
  function formatJson(value) { try { return value ? JSON.stringify(JSON.parse(value), null, 2) : "—"; } catch { return value || "—"; } }
  function closeModal() { byId("detail-modal").style.display = "none"; }
  async function showTab(tab) { state.tab = tab; ["overview", "outbox", "inbox", "channels"].forEach((name) => byId(`view-${name}`).style.display = name === tab ? "block" : "none"); document.querySelectorAll(".tab-button").forEach((item) => item.classList.toggle("active", item.dataset.tab === tab)); if (tab === "outbox" || tab === "inbox") await messages(tab); else await detail(); }
  function setupSse() { const source = new EventSource(`${api}/events`); source.onopen = () => { byId("sse-badge").classList.add("connected"); byId("sse-status-text").textContent = "Live SSE Stream"; }; source.addEventListener("snapshot", (event) => { try { state.services = JSON.parse(event.data); renderServices(); } catch (error) { console.error("Invalid service snapshot", error); } }); source.onerror = () => { byId("sse-badge").classList.remove("connected"); byId("sse-status-text").textContent = "Reconnecting…"; }; }
  function setup() { byId("btn-refresh").addEventListener("click", () => state.selected ? showTab(state.tab) : refreshServices()); byId("btn-close-modal").addEventListener("click", closeModal); document.querySelector('[data-nav="all"]').addEventListener("click", () => { state.selected = null; byId("service-header").style.display = "none"; byId("view-all-services").style.display = "block"; renderServices(); }); document.querySelectorAll(".tab-button").forEach((item) => item.addEventListener("click", () => showTab(item.dataset.tab))); ["outbox", "inbox"].forEach((kind) => { byId(`${kind}-context-select`).addEventListener("change", () => { state[kind].page = 1; messages(kind); }); byId(`${kind}-prev-page`).addEventListener("click", () => { state[kind].page--; messages(kind); }); byId(`${kind}-next-page`).addEventListener("click", () => { state[kind].page++; messages(kind); }); byId(`btn-bulk-requeue-${kind}`).addEventListener("click", () => bulk(kind, "requeue")); byId(`btn-bulk-delete-${kind}`).addEventListener("click", () => bulk(kind, "delete")); document.querySelectorAll(`#${kind}-status-pills [data-status]`).forEach((item) => item.addEventListener("click", () => { state[kind].status = item.dataset.status; state[kind].page = 1; messages(kind); })); }); }
  setup(); setupSse(); refreshServices();
})();
