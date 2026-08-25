(function () {
  'use strict';

  // Injected by index.html so every request is rooted at the path the UI is mounted at.
  // Falls back to the default mount point if the placeholder was not substituted.
  const injectedBase = window.RATATOSKR_BASE_PATH;
  const basePath = (
    injectedBase && injectedBase !== '__RATATOSKR_BASE__' ? injectedBase : '/ratatoskr'
  ).replace(/\/+$/, '');

  let config = {
    title: 'Ratatoskr Dashboard',
    routePrefix: basePath,
    pollingIntervalMs: 5000,
    enablePayloadEditing: true,
    defaultBasePath: `${basePath}/api/v1`,
    remoteServices: []
  };

  // Outbox and inbox poison lists are keyed differently by the management API: outbox rows
  // are identified by the message id, inbox rows by the per-handler status id.
  const modes = {
    outbox: {
      label: 'Outbox',
      idField: 'id',
      timestampField: 'createdAt',
      supportsPayloadEdit: true
    },
    inbox: {
      label: 'Inbox',
      idField: 'handlerStatusId',
      timestampField: 'receivedAt',
      supportsPayloadEdit: false
    }
  };

  let state = {
    activeTab: 'tab-poison',
    currentServiceIndex: 'local', // 'local' or number index
    currentContext: '', // DbContext name
    currentMode: 'outbox', // 'outbox' or 'inbox'
    contexts: [], // [{ name, hasOutbox, hasInbox }]
    poisonMessages: [],
    poisonTotalCount: 0,
    selectedIds: new Set(),
    inspectingItem: null,
    inspectingId: null
  };

  // DOM Elements
  const el = {
    dashboardTitle: document.getElementById('dashboard-title'),
    serviceSelector: document.getElementById('service-selector'),
    contextSelector: document.getElementById('context-selector'),
    btnRefresh: document.getElementById('btn-refresh'),
    btnViewOutbox: document.getElementById('btn-view-outbox'),
    btnViewInbox: document.getElementById('btn-view-inbox'),
    filterInput: document.getElementById('filter-input'),
    poisonTableBody: document.getElementById('poison-table-body'),
    selectAllPoison: document.getElementById('select-all-poison'),
    btnBulkRequeue: document.getElementById('btn-bulk-requeue'),
    btnBulkDelete: document.getElementById('btn-bulk-delete'),
    badgePoisonCount: document.getElementById('badge-poison-count'),

    // Topology
    topologyContainer: document.getElementById('topology-container'),

    // Metrics
    metricInstance: document.getElementById('metric-instance'),
    metricEnv: document.getElementById('metric-env'),
    metricUptime: document.getElementById('metric-uptime'),
    metricMemory: document.getElementById('metric-memory'),
    metricPublishChannels: document.getElementById('metric-publish-channels'),
    metricConsumeChannels: document.getElementById('metric-consume-channels'),

    // Multi-service
    multiserviceTableBody: document.getElementById('multiservice-table-body'),

    // Modal
    modalBackdrop: document.getElementById('modal-backdrop'),
    modalTitle: document.getElementById('modal-title'),
    modalMsgId: document.getElementById('modal-msg-id'),
    modalMsgType: document.getElementById('modal-msg-type'),
    modalMsgContext: document.getElementById('modal-msg-context'),
    modalMsgRetries: document.getElementById('modal-msg-retries'),
    modalMsgHandlerRow: document.getElementById('modal-msg-handler-row'),
    modalMsgHandler: document.getElementById('modal-msg-handler'),
    modalMsgError: document.getElementById('modal-msg-error'),
    modalPayloadEditor: document.getElementById('modal-payload-editor'),
    modalPayloadHint: document.getElementById('modal-payload-hint'),
    modalJsonError: document.getElementById('modal-json-error'),
    btnModalClose: document.getElementById('btn-modal-close'),
    btnModalCancel: document.getElementById('btn-modal-cancel'),
    btnModalRequeue: document.getElementById('btn-modal-requeue'),

    toastContainer: document.getElementById('toast-container')
  };

  function activeMode() {
    return modes[state.currentMode];
  }

  // Helper to determine target management API base URL
  function getManagementApiBaseUrl() {
    if (state.currentServiceIndex === 'local') {
      return config.defaultBasePath;
    }
    const idx = parseInt(state.currentServiceIndex, 10);
    return `${config.routePrefix}/ui-api/proxy/${idx}/ratatoskr/api/v1`;
  }

  // Base URL for the poison endpoints of the selected context and mode.
  function getPoisonBaseUrl() {
    return `${getManagementApiBaseUrl()}/efcore/contexts/${encodeURIComponent(state.currentContext)}/${state.currentMode}/poisoned`;
  }

  // Toast Notification
  function showToast(message, type = 'info') {
    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.textContent = message;
    el.toastContainer.appendChild(toast);
    setTimeout(() => toast.remove(), 4000);
  }

  // Initialize App
  async function init() {
    setupEventListeners();
    await loadConfig();
    await loadData();
    setInterval(loadData, config.pollingIntervalMs);
  }

  // Load Config
  async function loadConfig() {
    try {
      const res = await fetch(`${basePath}/ui-api/config`);
      if (res.ok) {
        config = await res.json();
        if (config.title) el.dashboardTitle.textContent = config.title;

        // Populate service selector
        el.serviceSelector.innerHTML = '<option value="local">Local Service (This Host)</option>';
        if (config.remoteServices && config.remoteServices.length > 0) {
          config.remoteServices.forEach((svc, index) => {
            const opt = document.createElement('option');
            opt.value = index;
            opt.textContent = `${svc.name} (${svc.managementApiUrl})`;
            el.serviceSelector.appendChild(opt);
          });
        }
      }
    } catch (e) {
      console.warn('Failed to load UI config, using defaults:', e);
    }
  }

  // Setup Event Listeners
  function setupEventListeners() {
    // Nav Tabs
    document.querySelectorAll('.nav-tab').forEach(tab => {
      tab.addEventListener('click', () => {
        document.querySelectorAll('.nav-tab').forEach(t => t.classList.remove('active'));
        document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));

        tab.classList.add('active');
        const targetId = tab.getAttribute('data-tab');
        state.activeTab = targetId;
        document.getElementById(targetId).classList.add('active');
        loadData();
      });
    });

    // Target Service Switcher
    el.serviceSelector.addEventListener('change', (e) => {
      state.currentServiceIndex = e.target.value;
      state.currentContext = '';
      state.contexts = [];
      loadData();
    });

    // Refresh Button
    el.btnRefresh.addEventListener('click', loadData);

    // Workbench Mode Toggle
    el.btnViewOutbox.addEventListener('click', () => switchMode('outbox'));
    el.btnViewInbox.addEventListener('click', () => switchMode('inbox'));

    // Context Selector
    el.contextSelector.addEventListener('change', (e) => {
      state.currentContext = e.target.value;
      applyContextCapabilities();
      state.selectedIds.clear();
      loadPoisonMessages();
    });

    // Filter Input
    el.filterInput.addEventListener('input', renderPoisonTable);

    // Select All Checkbox
    el.selectAllPoison.addEventListener('change', (e) => {
      const isChecked = e.target.checked;
      state.selectedIds.clear();
      if (isChecked) {
        const idField = activeMode().idField;
        state.poisonMessages.forEach(item => state.selectedIds.add(item[idField]));
      }
      renderPoisonTable();
    });

    // Bulk Actions
    el.btnBulkRequeue.addEventListener('click', handleBulkRequeue);
    el.btnBulkDelete.addEventListener('click', handleBulkDelete);

    // Modal Close
    el.btnModalClose.addEventListener('click', closeModal);
    el.btnModalCancel.addEventListener('click', closeModal);
    el.btnModalRequeue.addEventListener('click', handleModalRequeue);

    // Modal Payload Editor JSON Validation
    el.modalPayloadEditor.addEventListener('input', () => {
      try {
        JSON.parse(el.modalPayloadEditor.value);
        el.modalJsonError.classList.add('hidden');
        el.btnModalRequeue.disabled = false;
      } catch (err) {
        el.modalJsonError.classList.remove('hidden');
        el.btnModalRequeue.disabled = true;
      }
    });
  }

  function switchMode(mode) {
    if (state.currentMode === mode) return;
    state.currentMode = mode;
    el.btnViewOutbox.classList.toggle('active', mode === 'outbox');
    el.btnViewInbox.classList.toggle('active', mode === 'inbox');
    state.selectedIds.clear();
    loadPoisonMessages();
  }

  // Main Data Loading Router
  async function loadData() {
    if (state.activeTab === 'tab-poison') {
      await loadContexts();
      await loadPoisonMessages();
    } else if (state.activeTab === 'tab-topology') {
      await loadTopology();
    } else if (state.activeTab === 'tab-metrics') {
      await loadMetrics();
    } else if (state.activeTab === 'tab-multiservice') {
      await loadMultiServiceMatrix();
    }
  }

  // Load EF Core Contexts
  async function loadContexts() {
    try {
      const baseUrl = getManagementApiBaseUrl();
      const res = await fetch(`${baseUrl}/efcore/contexts`);
      if (!res.ok) {
        state.contexts = [];
        state.currentContext = '';
        el.contextSelector.innerHTML = '<option value="">No EF Core contexts found</option>';
        return;
      }
      const data = await res.json();
      state.contexts = (data.contexts || []).map(c => ({
        name: c.name,
        hasOutbox: c.hasOutbox === true,
        hasInbox: c.hasInbox === true
      }));

      if (state.contexts.length === 0) {
        state.currentContext = '';
        el.contextSelector.innerHTML = '<option value="">None registered</option>';
        return;
      }

      if (!state.contexts.some(c => c.name === state.currentContext)) {
        state.currentContext = state.contexts[0].name;
      }

      // Only rebuild the dropdown when the set of contexts actually changed, otherwise the
      // background poll would reset the control every few seconds while it is in use.
      const rendered = Array.from(el.contextSelector.options).map(o => o.value).join(' ');
      const expected = state.contexts.map(c => c.name).join(' ');
      if (rendered !== expected) {
        el.contextSelector.innerHTML = state.contexts
          .map(c => `<option value="${escapeHtml(c.name)}">${escapeHtml(c.name)}</option>`)
          .join('');
      }
      el.contextSelector.value = state.currentContext;
      applyContextCapabilities();
    } catch (e) {
      console.warn('Failed to fetch contexts:', e);
      state.contexts = [];
      state.currentContext = '';
      el.contextSelector.innerHTML = '<option value="">Error loading contexts</option>';
    }
  }

  // A context can be registered with only an outbox or only an inbox; the management API
  // answers 404 for the missing half, so drive the toggle off the advertised capabilities.
  function applyContextCapabilities() {
    const ctx = state.contexts.find(c => c.name === state.currentContext);
    const hasOutbox = ctx ? ctx.hasOutbox : true;
    const hasInbox = ctx ? ctx.hasInbox : true;

    el.btnViewOutbox.disabled = !hasOutbox;
    el.btnViewInbox.disabled = !hasInbox;

    if (state.currentMode === 'outbox' && !hasOutbox && hasInbox) {
      switchMode('inbox');
    } else if (state.currentMode === 'inbox' && !hasInbox && hasOutbox) {
      switchMode('outbox');
    }
  }

  // Load Poison Messages
  async function loadPoisonMessages() {
    if (!state.currentContext) {
      state.poisonMessages = [];
      state.poisonTotalCount = 0;
      el.badgePoisonCount.textContent = '0';
      el.poisonTableBody.innerHTML = '<tr><td colspan="6" class="text-center text-muted">No DbContext selected.</td></tr>';
      updateBulkButtons();
      return;
    }

    try {
      const res = await fetch(getPoisonBaseUrl());
      if (!res.ok) {
        el.poisonTableBody.innerHTML = `<tr><td colspan="6" class="text-center text-muted">Error fetching ${state.currentMode} messages (${res.status})</td></tr>`;
        return;
      }

      const data = await res.json();
      state.poisonMessages = data.items || [];
      state.poisonTotalCount = data.totalCount ?? state.poisonMessages.length;
      el.badgePoisonCount.textContent = state.poisonTotalCount;

      // Keep the operator's selection across background refreshes, dropping rows that are
      // no longer poisoned.
      const idField = activeMode().idField;
      const present = new Set(state.poisonMessages.map(item => item[idField]));
      state.selectedIds.forEach(id => {
        if (!present.has(id)) state.selectedIds.delete(id);
      });

      renderPoisonTable();
    } catch (e) {
      console.error('Failed to load poison messages:', e);
      el.poisonTableBody.innerHTML = '<tr><td colspan="6" class="text-center text-muted">Failed to connect to management API.</td></tr>';
    }
  }

  // Render Poison Table
  function renderPoisonTable() {
    const mode = activeMode();
    const filter = el.filterInput.value.toLowerCase();
    const filtered = state.poisonMessages.filter(item => {
      const haystack = [
        item.messageType,
        item[mode.idField],
        item.messageId,
        item.handlerKey,
        item.lastError
      ];
      return haystack.some(v => v && String(v).toLowerCase().includes(filter));
    });

    if (filtered.length === 0) {
      el.poisonTableBody.innerHTML = '<tr><td colspan="6" class="text-center text-muted">No poisoned messages found.</td></tr>';
      el.selectAllPoison.checked = false;
      updateBulkButtons();
      return;
    }

    el.poisonTableBody.innerHTML = filtered.map(item => {
      const id = item[mode.idField];
      const type = item.messageType || 'Unknown';
      const timestamp = item[mode.timestampField];
      const retries = item.errorCount ?? 0;
      const error = item.lastError || 'No error details';
      const isChecked = state.selectedIds.has(id);

      return `
        <tr>
          <td><input type="checkbox" class="row-select" data-id="${escapeHtml(id)}" ${isChecked ? 'checked' : ''}></td>
          <td>
            <strong>${escapeHtml(type)}</strong><br>
            <small class="text-muted">${escapeHtml(id)}</small>
            ${item.handlerKey ? `<br><small class="text-muted">Handler: ${escapeHtml(item.handlerKey)}</small>` : ''}
          </td>
          <td>${timestamp ? new Date(timestamp).toLocaleString() : '-'}</td>
          <td><span class="badge badge-danger">${retries}</span></td>
          <td title="${escapeHtml(error)}">${escapeHtml(error.length > 80 ? error.substring(0, 80) + '...' : error)}</td>
          <td>
            <button class="btn btn-secondary btn-sm btn-inspect" data-id="${escapeHtml(id)}">Inspect</button>
            <button class="btn btn-danger btn-sm btn-delete-single" data-id="${escapeHtml(id)}">Delete</button>
          </td>
        </tr>
      `;
    }).join('');

    // Table item event listeners
    document.querySelectorAll('.row-select').forEach(cb => {
      cb.addEventListener('change', (e) => {
        const id = e.target.getAttribute('data-id');
        if (e.target.checked) state.selectedIds.add(id);
        else state.selectedIds.delete(id);
        updateBulkButtons();
      });
    });

    document.querySelectorAll('.btn-inspect').forEach(btn => {
      btn.addEventListener('click', () => openInspectModal(btn.getAttribute('data-id')));
    });

    document.querySelectorAll('.btn-delete-single').forEach(btn => {
      btn.addEventListener('click', () => deleteSingleMessage(btn.getAttribute('data-id')));
    });

    el.selectAllPoison.checked = filtered.every(item => state.selectedIds.has(item[mode.idField]));
    updateBulkButtons();
  }

  function updateBulkButtons() {
    const count = state.selectedIds.size;
    el.btnBulkRequeue.disabled = count === 0;
    el.btnBulkDelete.disabled = count === 0;
    el.btnBulkRequeue.textContent = `Requeue Selected (${count})`;
    el.btnBulkDelete.textContent = `Delete Selected (${count})`;
  }

  // Open Inspect Modal
  async function openInspectModal(id) {
    const mode = activeMode();
    try {
      const res = await fetch(`${getPoisonBaseUrl()}/${encodeURIComponent(id)}`);
      if (!res.ok) {
        showToast('Failed to fetch message detail', 'danger');
        return;
      }
      const data = await res.json();
      state.inspectingItem = data;
      state.inspectingId = id;

      el.modalTitle.textContent = `Inspect ${mode.label} Message`;
      el.modalMsgId.textContent = data.messageId || data.id || id;
      el.modalMsgType.textContent = data.messageType || '-';
      el.modalMsgContext.textContent = state.currentContext;
      el.modalMsgRetries.textContent = data.errorCount ?? 0;
      el.modalMsgError.textContent = data.lastError || 'None';

      if (data.handlerKey) {
        el.modalMsgHandler.textContent = data.handlerKey;
        el.modalMsgHandlerRow.classList.remove('hidden');
      } else {
        el.modalMsgHandlerRow.classList.add('hidden');
      }

      let rawJson = data.jsonPayload || '{}';
      try {
        rawJson = JSON.stringify(JSON.parse(rawJson), null, 2);
      } catch (e) { /* payload is not JSON; show it verbatim */ }

      // Payload edits are only honoured by the outbox requeue endpoint, and only when the
      // host has not switched editing off.
      const editable = mode.supportsPayloadEdit && config.enablePayloadEditing !== false;
      el.modalPayloadEditor.value = rawJson;
      el.modalPayloadEditor.readOnly = !editable;
      el.modalPayloadHint.textContent = editable
        ? ''
        : `Read-only: ${mode.label.toLowerCase()} requeue replays the stored payload.`;
      el.modalJsonError.classList.add('hidden');
      el.btnModalRequeue.disabled = false;
      el.modalBackdrop.classList.remove('hidden');
    } catch (e) {
      console.error('Error loading detail:', e);
      showToast('Error loading message detail', 'danger');
    }
  }

  function closeModal() {
    el.modalBackdrop.classList.add('hidden');
    state.inspectingItem = null;
    state.inspectingId = null;
  }

  // Requeue from Modal
  async function handleModalRequeue() {
    if (!state.inspectingId) return;
    const mode = activeMode();
    const id = state.inspectingId;
    const editable = mode.supportsPayloadEdit && config.enablePayloadEditing !== false;

    try {
      const init = { method: 'POST' };
      if (editable) {
        init.headers = { 'Content-Type': 'application/json' };
        init.body = JSON.stringify({ payload: el.modalPayloadEditor.value });
      }

      const res = await fetch(`${getPoisonBaseUrl()}/${encodeURIComponent(id)}/requeue`, init);

      if (res.ok) {
        showToast(`Message '${id}' requeued successfully.`, 'success');
        closeModal();
        await loadPoisonMessages();
      } else {
        showToast(`Failed to requeue message (${res.status}).`, 'danger');
      }
    } catch (e) {
      console.error('Requeue failed:', e);
      showToast('Requeue request failed.', 'danger');
    }
  }

  // Delete Single Message
  async function deleteSingleMessage(id) {
    if (!confirm(`Are you sure you want to delete poisoned message '${id}'?`)) return;

    try {
      const res = await fetch(`${getPoisonBaseUrl()}/${encodeURIComponent(id)}`, {
        method: 'DELETE'
      });

      if (res.ok) {
        showToast('Message deleted.', 'success');
        state.selectedIds.delete(id);
        await loadPoisonMessages();
      } else {
        showToast(`Delete failed (${res.status}).`, 'danger');
      }
    } catch (e) {
      showToast('Delete request error.', 'danger');
    }
  }

  // Bulk Requeue
  async function handleBulkRequeue() {
    if (state.selectedIds.size === 0) return;
    const ids = Array.from(state.selectedIds);

    try {
      const res = await fetch(`${getPoisonBaseUrl()}/requeue`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ids: ids })
      });

      if (res.ok) {
        const result = await res.json().catch(() => null);
        const failed = result && result.failed ? result.failed.length : 0;
        const succeeded = result && result.succeeded ? result.succeeded.length : ids.length;
        showToast(
          failed > 0
            ? `Requeued ${succeeded} messages, ${failed} failed.`
            : `Requeued ${succeeded} messages.`,
          failed > 0 ? 'warning' : 'success'
        );
        state.selectedIds.clear();
        await loadPoisonMessages();
      } else {
        showToast(`Bulk requeue failed (${res.status}).`, 'danger');
      }
    } catch (e) {
      showToast('Bulk requeue request error.', 'danger');
    }
  }

  // Bulk Delete
  async function handleBulkDelete() {
    if (state.selectedIds.size === 0) return;
    const ids = Array.from(state.selectedIds);
    if (!confirm(`Delete ${ids.length} poisoned messages permanently?`)) return;

    try {
      const res = await fetch(getPoisonBaseUrl(), {
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ids: ids })
      });

      if (res.ok) {
        showToast(`Deleted ${ids.length} messages.`, 'success');
        state.selectedIds.clear();
        await loadPoisonMessages();
      } else {
        showToast(`Bulk delete failed (${res.status}).`, 'danger');
      }
    } catch (e) {
      showToast('Bulk delete request error.', 'danger');
    }
  }

  // Load Topology
  async function loadTopology() {
    try {
      const baseUrl = getManagementApiBaseUrl();
      const res = await fetch(`${baseUrl}/system/topology`);
      if (!res.ok) {
        el.topologyContainer.innerHTML = '<div class="card text-muted">Topology endpoint unavailable.</div>';
        return;
      }
      const data = await res.json();
      const channels = data.channels || [];

      if (channels.length === 0) {
        el.topologyContainer.innerHTML = '<div class="card text-muted">No active channels registered.</div>';
        return;
      }

      el.topologyContainer.innerHTML = channels.map(ch => `
        <div class="card">
          <div class="card-title">
            <span>${escapeHtml(ch.channelName)}</span>
            <span class="badge badge-success">${escapeHtml(ch.intent)}</span>
          </div>
          <div class="card-subtitle">Registered Messages: ${(ch.messages || []).length}</div>
          <div class="channel-messages">
            ${(ch.messages || []).map(m => `
              <div class="form-group" style="margin-top:0.75rem;">
                <strong>${escapeHtml(m.messageTypeName)}</strong><br>
                <small class="text-muted">${escapeHtml(m.messageType)}</small>
                ${(m.handlers || []).length > 0 ? `
                  <div style="margin-top:0.25rem;">
                    <small>Handlers:</small>
                    ${m.handlers.map(h => `<span class="badge badge-secondary" style="margin-left:0.25rem;">${escapeHtml(h.handlerType)}</span>`).join('')}
                  </div>
                ` : ''}
              </div>
            `).join('')}
          </div>
        </div>
      `).join('');
    } catch (e) {
      el.topologyContainer.innerHTML = '<div class="card text-muted">Failed to load topology.</div>';
    }
  }

  // Load Metrics
  async function loadMetrics() {
    try {
      const baseUrl = getManagementApiBaseUrl();
      const res = await fetch(`${baseUrl}/system/metrics`);
      if (!res.ok) return;
      const data = await res.json();

      el.metricInstance.textContent = data.instanceId || '-';
      el.metricEnv.textContent = data.environmentName || '-';
      el.metricUptime.textContent = formatUptime(data.uptimeSeconds ?? 0);
      el.metricMemory.textContent = formatBytes(data.workingSetBytes ?? 0);
      el.metricPublishChannels.textContent = data.publishChannelCount ?? 0;
      el.metricConsumeChannels.textContent = data.consumeChannelCount ?? 0;
    } catch (e) {
      console.warn('Failed to load metrics:', e);
    }
  }

  // Load Multi-Service Matrix
  async function loadMultiServiceMatrix() {
    if (!config.remoteServices || config.remoteServices.length === 0) {
      el.multiserviceTableBody.innerHTML = '<tr><td colspan="4" class="text-center text-muted">Single embedded mode: No remote services registered in options.</td></tr>';
      return;
    }

    const rows = await Promise.all(config.remoteServices.map(async (svc, index) => {
      let statusHtml = '<span class="badge badge-danger">Offline</span>';
      try {
        const proxyUrl = `${config.routePrefix}/ui-api/proxy/${index}/ratatoskr/api/v1/system/metrics`;
        const res = await fetch(proxyUrl);
        if (res.ok) {
          statusHtml = '<span class="badge badge-success">Healthy</span>';
        }
      } catch (e) { /* offline is the default state */ }

      return `
        <tr>
          <td><strong>${escapeHtml(svc.name)}</strong></td>
          <td><code>${escapeHtml(svc.managementApiUrl)}</code></td>
          <td>${statusHtml}</td>
          <td>
            <button class="btn btn-secondary btn-sm btn-switch-service" data-index="${index}">Switch To Service</button>
          </td>
        </tr>
      `;
    }));

    el.multiserviceTableBody.innerHTML = rows.join('');

    document.querySelectorAll('.btn-switch-service').forEach(btn => {
      btn.addEventListener('click', (e) => {
        const idx = e.target.getAttribute('data-index');
        el.serviceSelector.value = idx;
        state.currentServiceIndex = idx;
        state.currentContext = '';
        state.contexts = [];
        state.activeTab = 'tab-poison';
        document.querySelector('[data-tab="tab-poison"]').click();
      });
    });
  }

  // Utility Functions
  function escapeHtml(str) {
    if (!str) return '';
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function formatBytes(bytes) {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  }

  function formatUptime(seconds) {
    const hrs = Math.floor(seconds / 3600);
    const mins = Math.floor((seconds % 3600) / 60);
    const secs = seconds % 60;
    return `${hrs}h ${mins}m ${secs}s`;
  }

  // Start App
  document.addEventListener('DOMContentLoaded', init);
})();
