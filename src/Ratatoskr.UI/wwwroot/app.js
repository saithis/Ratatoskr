(function () {
  'use strict';

  let config = {
    title: 'Ratatoskr Dashboard',
    routePrefix: '/ratatoskr',
    pollingIntervalMs: 5000,
    enablePayloadEditing: true,
    defaultBasePath: '/ratatoskr/api/v1',
    remoteServices: []
  };

  let state = {
    activeTab: 'tab-poison',
    currentServiceIndex: 'local', // 'local' or number index
    currentContext: '',
    currentMode: 'outbox', // 'outbox' or 'inbox'
    contexts: [],
    poisonMessages: [],
    selectedIds: new Set(),
    inspectingItem: null
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
    modalMsgError: document.getElementById('modal-msg-error'),
    modalPayloadEditor: document.getElementById('modal-payload-editor'),
    modalJsonError: document.getElementById('modal-json-error'),
    btnModalClose: document.getElementById('btn-modal-close'),
    btnModalCancel: document.getElementById('btn-modal-cancel'),
    btnModalRequeue: document.getElementById('btn-modal-requeue'),

    toastContainer: document.getElementById('toast-container')
  };

  // Helper to determine target management API base URL
  function getManagementApiBaseUrl() {
    if (state.currentServiceIndex === 'local') {
      return config.defaultBasePath;
    }
    const idx = parseInt(state.currentServiceIndex, 10);
    return `${config.routePrefix}/ui-api/proxy/${idx}/ratatoskr/api/v1`;
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
      const res = await fetch('./ui-api/config');
      if (res.ok) {
        config = await res.json();
        if (config.title) el.dashboardTitle.textContent = config.title;

        // Populate service selector
        el.serviceSelector.innerHTML = '<option value="local">Local Service (This Host)</option>';
        if (config.remoteServices && config.remoteServices.length > 0) {
          config.remoteServices.forEach((svc, index) => {
            const opt = document.createElement('option');
            opt.value = index;
            opt.textContent = `${svc.Name} (${svc.ManagementApiUrl})`;
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
      loadData();
    });

    // Refresh Button
    el.btnRefresh.addEventListener('click', loadData);

    // Workbench Mode Toggle
    el.btnViewOutbox.addEventListener('click', () => {
      el.btnViewOutbox.classList.add('active');
      el.btnViewInbox.classList.remove('active');
      state.currentMode = 'outbox';
      loadPoisonMessages();
    });

    el.btnViewInbox.addEventListener('click', () => {
      el.btnViewInbox.classList.add('active');
      el.btnViewOutbox.classList.remove('active');
      state.currentMode = 'inbox';
      loadPoisonMessages();
    });

    // Context Selector
    el.contextSelector.addEventListener('change', (e) => {
      state.currentContext = e.target.value;
      loadPoisonMessages();
    });

    // Filter Input
    el.filterInput.addEventListener('input', renderPoisonTable);

    // Select All Checkbox
    el.selectAllPoison.addEventListener('change', (e) => {
      const isChecked = e.target.checked;
      state.selectedIds.clear();
      if (isChecked) {
        state.poisonMessages.forEach(item => state.selectedIds.add(item.Id));
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
        el.contextSelector.innerHTML = '<option value="">No EF Core contexts found</option>';
        return;
      }
      const data = await res.json();
      state.contexts = data.Contexts || data.contexts || [];

      if (state.contexts.length > 0) {
        if (!state.currentContext || !state.contexts.includes(state.currentContext)) {
          state.currentContext = state.contexts[0];
        }
        el.contextSelector.innerHTML = state.contexts
          .map(c => `<option value="${c}" ${c === state.currentContext ? 'selected' : ''}>${c}</option>`)
          .join('');
      } else {
        el.contextSelector.innerHTML = '<option value="">None registered</option>';
      }
    } catch (e) {
      console.warn('Failed to fetch contexts:', e);
      el.contextSelector.innerHTML = '<option value="">Error loading contexts</option>';
    }
  }

  // Load Poison Messages
  async function loadPoisonMessages() {
    if (!state.currentContext) {
      el.poisonTableBody.innerHTML = '<tr><td colspan="6" class="text-center text-muted">No DbContext selected.</td></tr>';
      return;
    }

    try {
      const baseUrl = getManagementApiBaseUrl();
      const endpoint = `${baseUrl}/efcore/contexts/${state.currentContext}/${state.currentMode}/poisoned`;
      const res = await fetch(endpoint);
      if (!res.ok) {
        el.poisonTableBody.innerHTML = `<tr><td colspan="6" class="text-center text-muted">Error fetching ${state.currentMode} messages (${res.status})</td></tr>`;
        return;
      }

      const data = await res.json();
      state.poisonMessages = data.Items || data.items || [];
      el.badgePoisonCount.textContent = state.poisonMessages.length;
      state.selectedIds.clear();
      renderPoisonTable();
    } catch (e) {
      console.error('Failed to load poison messages:', e);
      el.poisonTableBody.innerHTML = '<tr><td colspan="6" class="text-center text-muted">Failed to connect to management API.</td></tr>';
    }
  }

  // Render Poison Table
  function renderPoisonTable() {
    const filter = el.filterInput.value.toLowerCase();
    const filtered = state.poisonMessages.filter(item => {
      const msgType = (item.MessageType || item.messageType || '').toLowerCase();
      const id = (item.Id || item.id || '').toLowerCase();
      const err = (item.LastError || item.lastError || item.Error || item.error || '').toLowerCase();
      return msgType.includes(filter) || id.includes(filter) || err.includes(filter);
    });

    if (filtered.length === 0) {
      el.poisonTableBody.innerHTML = '<tr><td colspan="6" class="text-center text-muted">No poisoned messages found.</td></tr>';
      updateBulkButtons();
      return;
    }

    el.poisonTableBody.innerHTML = filtered.map(item => {
      const id = item.Id || item.id;
      const type = item.MessageType || item.messageType || 'Unknown';
      const created = item.FailedAt || item.failedAt || item.CreatedAt || item.createdAt || '-';
      const retries = item.ErrorCount ?? item.errorCount ?? 0;
      const error = item.LastError || item.lastError || item.Error || item.error || 'No error details';
      const isChecked = state.selectedIds.has(id);

      return `
        <tr>
          <td><input type="checkbox" class="row-select" data-id="${id}" ${isChecked ? 'checked' : ''}></td>
          <td>
            <strong>${escapeHtml(type)}</strong><br>
            <small class="text-muted">${id}</small>
          </td>
          <td>${new Date(created).toLocaleString()}</td>
          <td><span class="badge badge-danger">${retries}</span></td>
          <td title="${escapeHtml(error)}">${escapeHtml(error.length > 80 ? error.substring(0, 80) + '...' : error)}</td>
          <td>
            <button class="btn btn-secondary btn-sm btn-inspect" data-id="${id}">Inspect</button>
            <button class="btn btn-danger btn-sm btn-delete-single" data-id="${id}">Delete</button>
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
    try {
      const baseUrl = getManagementApiBaseUrl();
      const res = await fetch(`${baseUrl}/efcore/contexts/${state.currentContext}/${state.currentMode}/poisoned/${id}`);
      if (!res.ok) {
        showToast('Failed to fetch message detail', 'danger');
        return;
      }
      const data = await res.json();
      state.inspectingItem = data;

      el.modalTitle.textContent = `Inspect ${state.currentMode === 'outbox' ? 'Outbox' : 'Inbox'} Message`;
      el.modalMsgId.textContent = data.Id || data.id;
      el.modalMsgType.textContent = data.MessageType || data.messageType;
      el.modalMsgContext.textContent = state.currentContext;
      el.modalMsgRetries.textContent = data.ErrorCount ?? data.errorCount ?? 0;
      el.modalMsgError.textContent = data.LastError || data.lastError || data.Error || 'None';

      let rawJson = data.JsonPayload || data.jsonPayload || '{}';
      try {
        rawJson = JSON.stringify(JSON.parse(rawJson), null, 2);
      } catch (e) {}

      el.modalPayloadEditor.value = rawJson;
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
  }

  // Requeue from Modal
  async function handleModalRequeue() {
    if (!state.inspectingItem) return;
    const id = state.inspectingItem.Id || state.inspectingItem.id;
    const payload = el.modalPayloadEditor.value;

    try {
      const baseUrl = getManagementApiBaseUrl();
      const res = await fetch(`${baseUrl}/efcore/contexts/${state.currentContext}/${state.currentMode}/poisoned/${id}/requeue`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ payload: payload })
      });

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
      const baseUrl = getManagementApiBaseUrl();
      const res = await fetch(`${baseUrl}/efcore/contexts/${state.currentContext}/${state.currentMode}/poisoned/${id}`, {
        method: 'DELETE'
      });

      if (res.ok) {
        showToast('Message deleted.', 'success');
        await loadPoisonMessages();
      } else {
        showToast('Delete failed.', 'danger');
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
      const baseUrl = getManagementApiBaseUrl();
      const res = await fetch(`${baseUrl}/efcore/contexts/${state.currentContext}/${state.currentMode}/bulk-requeue`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ids: ids })
      });

      if (res.ok) {
        showToast(`Requeued ${ids.length} messages.`, 'success');
        await loadPoisonMessages();
      } else {
        showToast('Bulk requeue failed.', 'danger');
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
      const baseUrl = getManagementApiBaseUrl();
      const res = await fetch(`${baseUrl}/efcore/contexts/${state.currentContext}/${state.currentMode}/bulk-delete`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ids: ids })
      });

      if (res.ok) {
        showToast(`Deleted ${ids.length} messages.`, 'success');
        await loadPoisonMessages();
      } else {
        showToast('Bulk delete failed.', 'danger');
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
      const channels = data.Channels || data.channels || [];

      if (channels.length === 0) {
        el.topologyContainer.innerHTML = '<div class="card text-muted">No active channels registered.</div>';
        return;
      }

      el.topologyContainer.innerHTML = channels.map(ch => `
        <div class="card">
          <div class="card-title">
            <span>${escapeHtml(ch.ChannelName || ch.channelName)}</span>
            <span class="badge badge-success">${escapeHtml(ch.Intent || ch.intent)}</span>
          </div>
          <div class="card-subtitle">Registered Messages: ${(ch.Messages || ch.messages || []).length}</div>
          <div class="channel-messages">
            ${(ch.Messages || ch.messages || []).map(m => `
              <div class="form-group" style="margin-top:0.75rem;">
                <strong>${escapeHtml(m.MessageTypeName || m.messageTypeName)}</strong><br>
                <small class="text-muted">${escapeHtml(m.MessageType || m.messageType)}</small>
                ${(m.Handlers || m.handlers || []).length > 0 ? `
                  <div style="margin-top:0.25rem;">
                    <small>Handlers:</small>
                    ${(m.Handlers || m.handlers).map(h => `<span class="badge badge-secondary" style="margin-left:0.25rem;">${escapeHtml(h.HandlerType || h.handlerType)}</span>`).join('')}
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

      el.metricInstance.textContent = data.InstanceId || data.instanceId || '-';
      el.metricEnv.textContent = data.EnvironmentName || data.environmentName || '-';
      el.metricUptime.textContent = formatUptime(data.UptimeSeconds ?? data.uptimeSeconds ?? 0);
      el.metricMemory.textContent = formatBytes(data.WorkingSetBytes ?? data.workingSetBytes ?? 0);
      el.metricPublishChannels.textContent = data.PublishChannelCount ?? data.publishChannelCount ?? 0;
      el.metricConsumeChannels.textContent = data.ConsumeChannelCount ?? data.consumeChannelCount ?? 0;
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
      } catch (e) {}

      return `
        <tr>
          <td><strong>${escapeHtml(svc.Name)}</strong></td>
          <td><code>${escapeHtml(svc.ManagementApiUrl)}</code></td>
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
