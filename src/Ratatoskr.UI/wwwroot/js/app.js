// Ratatoskr Management Dashboard Client

(function () {
  const state = {
    services: [],
    selectedService: null,
    selectedTab: "overview",
    outbox: { context: "", status: "Poisoned", page: 1, pageSize: 20, data: null },
    inbox: { context: "", status: "Poisoned", page: 1, pageSize: 20, data: null },
    autoRefreshTimer: null,
  };

  // Base API url derived from current page location
  const basePath = window.location.pathname.replace(/\/$/, "");
  const apiUrl = `${basePath}/api`;

  // Elements
  const sseBadge = document.getElementById("sse-badge");
  const sseStatusText = document.getElementById("sse-status-text");
  const autoRefreshSelect = document.getElementById("auto-refresh-select");
  const btnRefresh = document.getElementById("btn-refresh");
  const totalServicesBadge = document.getElementById("total-services-badge");
  const sidebarServiceList = document.getElementById("sidebar-service-list");

  const serviceHeader = document.getElementById("service-header");
  const selectedServiceTitle = document.getElementById("selected-service-title");
  const selectedServiceMeta = document.getElementById("selected-service-meta");
  const selectedServiceStatus = document.getElementById("selected-service-status");

  const viewAllServices = document.getElementById("view-all-services");
  const servicesGrid = document.getElementById("services-grid");
  const viewOverview = document.getElementById("view-overview");
  const viewOutbox = document.getElementById("view-outbox");
  const viewInbox = document.getElementById("view-inbox");
  const viewChannels = document.getElementById("view-channels");

  const replicasTbody = document.getElementById("replicas-tbody");
  const contextsTbody = document.getElementById("contexts-tbody");
  const channelsTbody = document.getElementById("channels-tbody");

  const outboxContextSelect = document.getElementById("outbox-context-select");
  const outboxTbody = document.getElementById("outbox-tbody");
  const outboxPageInfo = document.getElementById("outbox-page-info");
  const outboxPrevPage = document.getElementById("outbox-prev-page");
  const outboxNextPage = document.getElementById("outbox-next-page");

  const inboxContextSelect = document.getElementById("inbox-context-select");
  const inboxTbody = document.getElementById("inbox-tbody");
  const inboxPageInfo = document.getElementById("inbox-page-info");
  const inboxPrevPage = document.getElementById("inbox-prev-page");
  const inboxNextPage = document.getElementById("inbox-next-page");

  const detailModal = document.getElementById("detail-modal");
  const modalTitle = document.getElementById("modal-title");
  const modalBody = document.getElementById("modal-body");
  const modalFooter = document.getElementById("modal-footer");
  const btnCloseModal = document.getElementById("btn-close-modal");

  // Init
  function init() {
    setupSse();
    setupEventListeners();
    fetchServices();
    setupAutoRefresh();
  }

  // SSE Setup
  function setupSse() {
    const sse = new EventSource(`${apiUrl}/events`);

    sse.onopen = () => {
      sseBadge.classList.add("connected");
      sseStatusText.textContent = "Live SSE Stream";
    };

    sse.addEventListener("snapshot", (e) => {
      try {
        state.services = JSON.parse(e.data);
        renderServicesList();
        if (!state.selectedService) renderServicesGrid();
      } catch (err) {
        console.error("Failed to parse snapshot", err);
      }
    });

    sse.addEventListener("service-heartbeat", (e) => {
      try {
        const hb = JSON.parse(e.data);
        updateServiceFromHeartbeat(hb);
      } catch (err) {
        console.error("Failed to parse heartbeat", err);
      }
    });

    sse.onerror = () => {
      sseBadge.classList.remove("connected");
      sseStatusText.textContent = "Reconnecting...";
    };
  }

  function updateServiceFromHeartbeat(hb) {
    let svc = state.services.find((s) => s.serviceName.toLowerCase() === hb.serviceName.toLowerCase());
    if (!svc) {
      fetchServices();
      return;
    }

    svc.lastHeartbeat = hb.timestamp;
    svc.status = "online";
    svc.dbContextNames = hb.dbContexts.map((d) => d.dbContextName);
    svc.totalPendingOutbox = hb.dbContexts.reduce((acc, d) => acc + d.pendingOutboxCount, 0);
    svc.totalPoisonedOutbox = hb.dbContexts.reduce((acc, d) => acc + d.poisonedOutboxCount, 0);
    svc.totalPendingInbox = hb.dbContexts.reduce((acc, d) => acc + d.pendingInboxCount, 0);
    svc.totalPoisonedInbox = hb.dbContexts.reduce((acc, d) => acc + d.poisonedInboxCount, 0);

    renderServicesList();
    if (!state.selectedService) {
      renderServicesGrid();
    } else if (state.selectedService.toLowerCase() === hb.serviceName.toLowerCase() && state.selectedTab === "overview") {
      fetchServiceDetail(state.selectedService);
    }
  }

  // Event Listeners
  function setupEventListeners() {
    document.querySelector('[data-nav="all"]').addEventListener("click", () => selectService(null));

    document.querySelectorAll(".tab-button").forEach((btn) => {
      btn.addEventListener("click", () => {
        document.querySelectorAll(".tab-button").forEach((b) => b.classList.remove("active"));
        btn.classList.add("active");
        selectTab(btn.dataset.tab);
      });
    });

    btnRefresh.addEventListener("click", () => refreshCurrentView());

    autoRefreshSelect.addEventListener("change", () => setupAutoRefresh());

    // Outbox filter listeners
    document.querySelectorAll("#outbox-status-pills button").forEach((btn) => {
      btn.addEventListener("click", () => {
        document.querySelectorAll("#outbox-status-pills button").forEach((b) => b.classList.remove("active"));
        btn.classList.add("active");
        state.outbox.status = btn.dataset.status;
        state.outbox.page = 1;
        fetchOutbox();
      });
    });

    outboxContextSelect.addEventListener("change", () => {
      state.outbox.context = outboxContextSelect.value;
      state.outbox.page = 1;
      fetchOutbox();
    });

    outboxPrevPage.addEventListener("click", () => {
      if (state.outbox.page > 1) {
        state.outbox.page--;
        fetchOutbox();
      }
    });

    outboxNextPage.addEventListener("click", () => {
      if (state.outbox.data && state.outbox.page * state.outbox.pageSize < state.outbox.data.totalCount) {
        state.outbox.page++;
        fetchOutbox();
      }
    });

    document.getElementById("btn-bulk-requeue-outbox").addEventListener("click", () => bulkRequeueOutbox());
    document.getElementById("btn-bulk-delete-outbox").addEventListener("click", () => bulkDeleteOutbox());

    // Inbox filter listeners
    document.querySelectorAll("#inbox-status-pills button").forEach((btn) => {
      btn.addEventListener("click", () => {
        document.querySelectorAll("#inbox-status-pills button").forEach((b) => b.classList.remove("active"));
        btn.classList.add("active");
        state.inbox.status = btn.dataset.status;
        state.inbox.page = 1;
        fetchInbox();
      });
    });

    inboxContextSelect.addEventListener("change", () => {
      state.inbox.context = inboxContextSelect.value;
      state.inbox.page = 1;
      fetchInbox();
    });

    inboxPrevPage.addEventListener("click", () => {
      if (state.inbox.page > 1) {
        state.inbox.page--;
        fetchInbox();
      }
    });

    inboxNextPage.addEventListener("click", () => {
      if (state.inbox.data && state.inbox.page * state.inbox.pageSize < state.inbox.data.totalCount) {
        state.inbox.page++;
        fetchInbox();
      }
    });

    document.getElementById("btn-bulk-requeue-inbox").addEventListener("click", () => bulkRequeueInbox());
    document.getElementById("btn-bulk-delete-inbox").addEventListener("click", () => bulkDeleteInbox());

    btnCloseModal.addEventListener("click", () => closeModal());
    detailModal.addEventListener("click", (e) => {
      if (e.target === detailModal) closeModal();
    });
  }

  function setupAutoRefresh() {
    if (state.autoRefreshTimer) clearInterval(state.autoRefreshTimer);
    const secs = parseInt(autoRefreshSelect.value, 10);
    if (secs > 0) {
      state.autoRefreshTimer = setInterval(() => refreshCurrentView(), secs * 1000);
    }
  }

  function refreshCurrentView() {
    if (!state.selectedService) {
      fetchServices();
    } else {
      if (state.selectedTab === "overview") fetchServiceDetail(state.selectedService);
      else if (state.selectedTab === "outbox") fetchOutbox();
      else if (state.selectedTab === "inbox") fetchInbox();
      else if (state.selectedTab === "channels") fetchServiceDetail(state.selectedService);
    }
  }

  // Navigation
  function selectService(serviceName) {
    state.selectedService = serviceName;

    document.querySelectorAll(".service-nav-item").forEach((item) => {
      item.classList.toggle("active", item.dataset.nav === (serviceName || "all"));
    });

    if (!serviceName) {
      serviceHeader.style.display = "none";
      showView(viewAllServices);
      renderServicesGrid();
      return;
    }

    const svc = state.services.find((s) => s.serviceName.toLowerCase() === serviceName.toLowerCase());
    selectedServiceTitle.textContent = serviceName;
    selectedServiceMeta.textContent = svc ? `${svc.instanceCount} active replica(s) • Contexts: ${svc.dbContextNames.join(", ")}` : "";
    selectedServiceStatus.innerHTML = svc ? `<span class="card-status ${svc.status}">${svc.status}</span>` : "";

    serviceHeader.style.display = "block";

    // Populate Context dropdowns if service found
    if (svc && svc.dbContextNames.length > 0) {
      outboxContextSelect.innerHTML = svc.dbContextNames.map((c) => `<option value="${c}">${c}</option>`).join("");
      inboxContextSelect.innerHTML = svc.dbContextNames.map((c) => `<option value="${c}">${c}</option>`).join("");
      state.outbox.context = svc.dbContextNames[0];
      state.inbox.context = svc.dbContextNames[0];
    }

    selectTab(state.selectedTab);
  }

  function selectTab(tab) {
    state.selectedTab = tab;
    if (tab === "overview") {
      showView(viewOverview);
      fetchServiceDetail(state.selectedService);
    } else if (tab === "outbox") {
      showView(viewOutbox);
      fetchOutbox();
    } else if (tab === "inbox") {
      showView(viewInbox);
      fetchInbox();
    } else if (tab === "channels") {
      showView(viewChannels);
      fetchServiceDetail(state.selectedService);
    }
  }

  function showView(viewElement) {
    [viewAllServices, viewOverview, viewOutbox, viewInbox, viewChannels].forEach((v) => (v.style.display = "none"));
    viewElement.style.display = "block";
  }

  // Fetch Services API
  async function fetchServices() {
    try {
      const res = await fetch(`${apiUrl}/services`);
      if (res.ok) {
        state.services = await res.json();
        totalServicesBadge.textContent = state.services.length;
        renderServicesList();
        if (!state.selectedService) renderServicesGrid();
      }
    } catch (err) {
      console.error("Error fetching services", err);
    }
  }

  function renderServicesList() {
    totalServicesBadge.textContent = state.services.length;
    if (state.services.length === 0) {
      sidebarServiceList.innerHTML = `<li style="color:var(--text-muted);font-size:0.8rem;padding:0.5rem;">No services discovered yet...</li>`;
      return;
    }

    sidebarServiceList.innerHTML = state.services
      .map(
        (s) => `
      <li class="service-nav-item ${state.selectedService === s.serviceName ? "active" : ""}" data-nav="${s.serviceName}">
        <span>${escapeHtml(s.serviceName)}</span>
        <span class="card-status ${s.status}" style="font-size:0.65rem;">${s.status}</span>
      </li>
    `
      )
      .join("");

    sidebarServiceList.querySelectorAll(".service-nav-item").forEach((item) => {
      item.addEventListener("click", () => selectService(item.dataset.nav));
    });
  }

  function renderServicesGrid() {
    if (state.services.length === 0) {
      servicesGrid.innerHTML = `
        <div style="grid-column:1/-1;background:var(--bg-card);border:1px solid var(--border-color);border-radius:8px;padding:2rem;text-align:center;">
          <p style="font-size:1.1rem;font-weight:600;margin-bottom:0.5rem;">Waiting for Connected Services</p>
          <p style="color:var(--text-secondary);font-size:0.875rem;">
            Services using <code>Ratatoskr.Management</code> will automatically announce their presence over RabbitMQ or in-process.
          </p>
        </div>`;
      return;
    }

    servicesGrid.innerHTML = state.services
      .map(
        (s) => `
      <div class="service-card" data-service="${escapeHtml(s.serviceName)}">
        <div class="card-header">
          <span class="card-title">${escapeHtml(s.serviceName)}</span>
          <span class="card-status ${s.status}">${s.status}</span>
        </div>
        <p style="font-size:0.75rem;color:var(--text-secondary);">
          ${s.instanceCount} active replica(s) • Contexts: ${escapeHtml(s.dbContextNames.join(", ") || "None")}
        </p>
        <div class="card-metrics">
          <div class="metric-box">
            <span class="metric-label">Poisoned Outbox</span>
            <span class="metric-val ${s.totalPoisonedOutbox > 0 ? "poisoned" : ""}">${s.totalPoisonedOutbox}</span>
          </div>
          <div class="metric-box">
            <span class="metric-label">Pending Outbox</span>
            <span class="metric-val">${s.totalPendingOutbox}</span>
          </div>
          <div class="metric-box">
            <span class="metric-label">Poisoned Inbox</span>
            <span class="metric-val ${s.totalPoisonedInbox > 0 ? "poisoned" : ""}">${s.totalPoisonedInbox}</span>
          </div>
          <div class="metric-box">
            <span class="metric-label">Pending Inbox</span>
            <span class="metric-val">${s.totalPendingInbox}</span>
          </div>
        </div>
      </div>
    `
      )
      .join("");

    servicesGrid.querySelectorAll(".service-card").forEach((card) => {
      card.addEventListener("click", () => selectService(card.dataset.service));
    });
  }

  // Fetch Service Detail (Replicas, Contexts, Channels)
  async function fetchServiceDetail(serviceName) {
    try {
      const res = await fetch(`${apiUrl}/services/${encodeURIComponent(serviceName)}`);
      if (res.ok) {
        const detail = await res.json();
        renderServiceDetail(detail);
      }
    } catch (err) {
      console.error("Error fetching service detail", err);
    }
  }

  function renderServiceDetail(detail) {
    // Render Replicas
    replicasTbody.innerHTML = detail.instances
      .map(
        (i) => `
      <tr>
        <td><span class="code-snippet">${escapeHtml(i.instanceId)}</span></td>
        <td>${escapeHtml(i.machineName)}</td>
        <td><span class="badge badge-transport">${escapeHtml(i.environment || "Production")}</span></td>
        <td>${new Date(i.startedAt).toLocaleString()}</td>
        <td>${new Date(i.lastHeartbeat).toLocaleTimeString()}</td>
        <td><span class="card-status ${i.isActive ? "online" : "stale"}">${i.isActive ? "active" : "stale"}</span></td>
      </tr>
    `
      )
      .join("");

    // Render Contexts
    contextsTbody.innerHTML = detail.dbContexts
      .map(
        (d) => `
      <tr>
        <td><strong>${escapeHtml(d.dbContextName)}</strong></td>
        <td>${d.hasOutbox ? "✓" : "—"}</td>
        <td>${d.hasInbox ? "✓" : "—"}</td>
        <td>${d.pendingOutboxCount}</td>
        <td><span class="${d.poisonedOutboxCount > 0 ? "badge badge-poisoned" : ""}">${d.poisonedOutboxCount}</span></td>
        <td>${d.pendingInboxCount}</td>
        <td><span class="${d.poisonedInboxCount > 0 ? "badge badge-poisoned" : ""}">${d.poisonedInboxCount}</span></td>
      </tr>
    `
      )
      .join("");

    // Render Channels
    channelsTbody.innerHTML = detail.channels
      .map(
        (ch) => `
      <tr>
        <td><strong>${escapeHtml(ch.channelName)}</strong></td>
        <td><span class="badge badge-transport">${escapeHtml(ch.channelType)}</span></td>
        <td><span class="badge ${ch.transportName === "RabbitMQ" ? "badge-transport" : "badge-pending"}">${escapeHtml(ch.transportName)}</span></td>
        <td><span class="code-snippet">${escapeHtml(ch.exchangeName || "—")}</span></td>
        <td><span class="code-snippet">${escapeHtml(ch.queueName || "—")}</span></td>
        <td><small>${escapeHtml(ch.messageTypes.join(", ") || "None")}</small></td>
      </tr>
    `
      )
      .join("");
  }

  // Fetch Outbox
  async function fetchOutbox() {
    if (!state.selectedService || !state.outbox.context) return;
    try {
      const { context, status, page, pageSize } = state.outbox;
      const url = `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
        context
      )}/outbox?status=${encodeURIComponent(status)}&page=${page}&pageSize=${pageSize}`;

      const res = await fetch(url);
      if (res.ok) {
        state.outbox.data = await res.json();
        renderOutboxTable(state.outbox.data);
      }
    } catch (err) {
      console.error("Error fetching outbox", err);
    }
  }

  function renderOutboxTable(paged) {
    const { items, totalCount, page, pageSize } = paged;
    outboxPageInfo.textContent = `Showing ${(page - 1) * pageSize + 1}-${Math.min(page * pageSize, totalCount)} of ${totalCount}`;
    outboxPrevPage.disabled = page <= 1;
    outboxNextPage.disabled = page * pageSize >= totalCount;

    if (items.length === 0) {
      outboxTbody.innerHTML = `<tr><td colspan="7" style="text-align:center;color:var(--text-muted);padding:2rem;">No outbox messages found.</td></tr>`;
      return;
    }

    outboxTbody.innerHTML = items
      .map(
        (item) => `
      <tr>
        <td><span class="code-snippet" title="${item.id}">${item.id.substring(0, 8)}…</span></td>
        <td><span class="badge ${item.transportName === "RabbitMQ" ? "badge-transport" : "badge-pending"}">${item.transportName}</span></td>
        <td>${new Date(item.createdAt).toLocaleString()}</td>
        <td>${item.errorCount}</td>
        <td><span class="badge ${item.isPoisoned ? "badge-poisoned" : item.processedAt ? "badge-success" : "badge-pending"}">${item.isPoisoned ? "Poisoned" : item.processedAt ? "Processed" : "Pending"}</span></td>
        <td><small style="color:var(--danger);">${escapeHtml(item.error ? item.error.substring(0, 60) + (item.error.length > 60 ? "…" : "") : "—")}</small></td>
        <td style="text-align:right;">
          <button class="btn btn-secondary btn-sm" onclick="window.viewOutboxDetail('${item.id}')">Inspect</button>
          ${item.isPoisoned ? `<button class="btn btn-primary btn-sm" onclick="window.requeueOutboxItem('${item.id}')">↺</button>` : ""}
          <button class="btn btn-danger btn-sm" onclick="window.deleteOutboxItem('${item.id}')">🗑</button>
        </td>
      </tr>
    `
      )
      .join("");
  }

  // Fetch Inbox
  async function fetchInbox() {
    if (!state.selectedService || !state.inbox.context) return;
    try {
      const { context, status, page, pageSize } = state.inbox;
      const url = `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
        context
      )}/inbox?status=${encodeURIComponent(status)}&page=${page}&pageSize=${pageSize}`;

      const res = await fetch(url);
      if (res.ok) {
        state.inbox.data = await res.json();
        renderInboxTable(state.inbox.data);
      }
    } catch (err) {
      console.error("Error fetching inbox", err);
    }
  }

  function renderInboxTable(paged) {
    const { items, totalCount, page, pageSize } = paged;
    inboxPageInfo.textContent = `Showing ${(page - 1) * pageSize + 1}-${Math.min(page * pageSize, totalCount)} of ${totalCount}`;
    inboxPrevPage.disabled = page <= 1;
    inboxNextPage.disabled = page * pageSize >= totalCount;

    if (items.length === 0) {
      inboxTbody.innerHTML = `<tr><td colspan="8" style="text-align:center;color:var(--text-muted);padding:2rem;">No inbox messages found.</td></tr>`;
      return;
    }

    inboxTbody.innerHTML = items
      .map(
        (item) => `
      <tr>
        <td><span class="code-snippet" title="${item.messageId}">${item.messageId.substring(0, 10)}…</span></td>
        <td><strong>${escapeHtml(item.handlerKey)}</strong></td>
        <td><span class="badge ${item.transportName === "RabbitMQ" ? "badge-transport" : "badge-pending"}">${item.transportName}</span></td>
        <td>${new Date(item.createdAt).toLocaleString()}</td>
        <td>${item.errorCount}</td>
        <td><span class="badge ${item.isPoisoned ? "badge-poisoned" : item.completedAt ? "badge-success" : "badge-pending"}">${item.isPoisoned ? "Poisoned" : item.completedAt ? "Completed" : "Pending"}</span></td>
        <td><small style="color:var(--danger);">${escapeHtml(item.lastError ? item.lastError.substring(0, 60) + (item.lastError.length > 60 ? "…" : "") : "—")}</small></td>
        <td style="text-align:right;">
          <button class="btn btn-secondary btn-sm" onclick="window.viewInboxDetail('${item.id}')">Inspect</button>
          ${item.isPoisoned ? `<button class="btn btn-primary btn-sm" onclick="window.requeueInboxItem('${item.id}')">↺</button>` : ""}
          <button class="btn btn-danger btn-sm" onclick="window.deleteInboxItem('${item.id}')">🗑</button>
        </td>
      </tr>
    `
      )
      .join("");
  }

  // Actions: Requeue & Delete
  window.requeueOutboxItem = async function (id) {
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.outbox.context
        )}/outbox/${id}/requeue`,
        { method: "POST" }
      );
      if (res.ok) {
        closeModal();
        fetchOutbox();
      } else {
        alert("Failed to requeue outbox message");
      }
    } catch (err) {
      console.error(err);
    }
  };

  async function bulkRequeueOutbox() {
    if (!confirm("Are you sure you want to requeue all poisoned outbox messages?")) return;
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.outbox.context
        )}/outbox/bulk-requeue`,
        { method: "POST" }
      );
      if (res.ok) fetchOutbox();
    } catch (err) {
      console.error(err);
    }
  }

  window.deleteOutboxItem = async function (id) {
    if (!confirm("Are you sure you want to delete this outbox message?")) return;
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.outbox.context
        )}/outbox/${id}`,
        { method: "DELETE" }
      );
      if (res.ok) {
        closeModal();
        fetchOutbox();
      }
    } catch (err) {
      console.error(err);
    }
  };

  async function bulkDeleteOutbox() {
    if (!confirm("Are you sure you want to permanently delete all poisoned outbox messages?")) return;
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.outbox.context
        )}/outbox/bulk-delete`,
        { method: "DELETE" }
      );
      if (res.ok) fetchOutbox();
    } catch (err) {
      console.error(err);
    }
  }

  window.requeueInboxItem = async function (id) {
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.inbox.context
        )}/inbox/${id}/requeue`,
        { method: "POST" }
      );
      if (res.ok) {
        closeModal();
        fetchInbox();
      } else {
        alert("Failed to requeue inbox handler");
      }
    } catch (err) {
      console.error(err);
    }
  };

  async function bulkRequeueInbox() {
    if (!confirm("Are you sure you want to requeue all poisoned inbox handlers?")) return;
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.inbox.context
        )}/inbox/bulk-requeue`,
        { method: "POST" }
      );
      if (res.ok) fetchInbox();
    } catch (err) {
      console.error(err);
    }
  }

  window.deleteInboxItem = async function (id) {
    if (!confirm("Are you sure you want to delete this inbox handler status?")) return;
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.inbox.context
        )}/inbox/${id}`,
        { method: "DELETE" }
      );
      if (res.ok) {
        closeModal();
        fetchInbox();
      }
    } catch (err) {
      console.error(err);
    }
  };

  async function bulkDeleteInbox() {
    if (!confirm("Are you sure you want to delete all poisoned inbox handlers?")) return;
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.inbox.context
        )}/inbox/bulk-delete`,
        { method: "DELETE" }
      );
      if (res.ok) fetchInbox();
    } catch (err) {
      console.error(err);
    }
  }

  // Detail Modals
  window.viewOutboxDetail = async function (id) {
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.outbox.context
        )}/outbox/${id}`
      );
      if (res.ok) {
        const item = await res.json();
        modalTitle.textContent = `Outbox Message: ${item.id}`;
        modalBody.innerHTML = `
          ${item.error ? `<div><strong style="color:var(--danger);">Error & Stack Trace:</strong><pre class="code-viewer error">${escapeHtml(item.error)}</pre></div>` : ""}
          <div>
            <strong>CloudEvents Metadata:</strong>
            <pre class="code-viewer">${escapeHtml(JSON.stringify(item.properties, null, 2))}</pre>
          </div>
          <div>
            <strong>Payload:</strong>
            <pre class="code-viewer">${escapeHtml(tryFormatJson(item.content))}</pre>
          </div>
        `;
        modalFooter.innerHTML = `
          ${item.isPoisoned ? `<button class="btn btn-primary" onclick="window.requeueOutboxItem('${item.id}')">↺ Requeue</button>` : ""}
          <button class="btn btn-danger" onclick="window.deleteOutboxItem('${item.id}')">🗑 Delete</button>
          <button class="btn btn-secondary" onclick="document.getElementById('detail-modal').style.display='none'">Close</button>
        `;
        detailModal.style.display = "flex";
      }
    } catch (err) {
      console.error(err);
    }
  };

  window.viewInboxDetail = async function (statusId) {
    try {
      const res = await fetch(
        `${apiUrl}/services/${encodeURIComponent(state.selectedService)}/contexts/${encodeURIComponent(
          state.inbox.context
        )}/inbox/${statusId}`
      );
      if (res.ok) {
        const item = await res.json();
        modalTitle.textContent = `Inbox Handler: ${item.handlerKey} (${item.messageId})`;
        modalBody.innerHTML = `
          ${item.lastError ? `<div><strong style="color:var(--danger);">Error & Stack Trace:</strong><pre class="code-viewer error">${escapeHtml(item.lastError)}</pre></div>` : ""}
          <div>
            <strong>CloudEvents Metadata:</strong>
            <pre class="code-viewer">${escapeHtml(JSON.stringify(item.properties, null, 2))}</pre>
          </div>
          <div>
            <strong>Payload:</strong>
            <pre class="code-viewer">${escapeHtml(tryFormatJson(item.content))}</pre>
          </div>
          ${
            item.otherHandlers && item.otherHandlers.length > 0
              ? `<div>
                  <strong>Other Handlers for this Message:</strong>
                  <ul style="margin-top:0.5rem;padding-left:1.2rem;font-size:0.85rem;">
                    ${item.otherHandlers
                      .map(
                        (h) => `
                      <li>
                        <strong>${escapeHtml(h.handlerKey)}</strong>: 
                        <span class="badge ${h.isPoisoned ? "badge-poisoned" : h.completedAt ? "badge-success" : "badge-pending"}">
                          ${h.isPoisoned ? "Poisoned" : h.completedAt ? "Completed" : "Pending"}
                        </span>
                      </li>`
                      )
                      .join("")}
                  </ul>
                </div>`
              : ""
          }
        `;
        modalFooter.innerHTML = `
          ${item.isPoisoned ? `<button class="btn btn-primary" onclick="window.requeueInboxItem('${item.id}')">↺ Requeue Handler</button>` : ""}
          <button class="btn btn-danger" onclick="window.deleteInboxItem('${item.id}')">🗑 Delete Handler</button>
          <button class="btn btn-secondary" onclick="document.getElementById('detail-modal').style.display='none'">Close</button>
        `;
        detailModal.style.display = "flex";
      }
    } catch (err) {
      console.error(err);
    }
  };

  function closeModal() {
    detailModal.style.display = "none";
  }

  function tryFormatJson(str) {
    if (!str) return "—";
    try {
      const parsed = JSON.parse(str);
      return JSON.stringify(parsed, null, 2);
    } catch {
      return str;
    }
  }

  function escapeHtml(str) {
    if (!str) return "";
    return str
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#039;");
  }

  // Start app
  init();
})();
