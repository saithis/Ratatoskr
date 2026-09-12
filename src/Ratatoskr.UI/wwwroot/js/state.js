// What the dashboard is currently looking at.
//
// A selection is (transport, service, context, tab), because a service name alone is ambiguous:
// the same service can be announced on two control planes at once.

const listeners = new Set();

const state = {
  services: [],
  selection: null,
  tab: "overview",
  context: null,
  filter: { status: "Poisoned", search: "", from: "", to: "" },
  cursor: null,
  cursorStack: [],
  selectedIds: new Set(),
};

/** Subscribes to state changes. Returns an unsubscribe function. */
export function subscribe(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function notify() {
  for (const listener of listeners) {
    listener(state);
  }
}

/** The current state. Treat it as read-only; use the mutators below. */
export function current() {
  return state;
}

/** Replaces the known services, usually from the event stream. */
export function setServices(services) {
  state.services = services ?? [];
  notify();
}

/** Selects one service on one transport, or clears the selection. */
export function select(transport, service) {
  state.selection = transport === null ? null : { transport, service };
  state.tab = "overview";
  state.context = null;
  resetPaging();
  notify();
}

/** Switches the visible tab within the selected service. */
export function setTab(tab) {
  state.tab = tab;
  resetPaging();
  notify();
}

/** Chooses the DbContext the message views operate on. */
export function setContext(context) {
  state.context = context;
  resetPaging();
  notify();
}

/** Replaces the message filter and returns to the first page. */
export function setFilter(filter) {
  state.filter = { ...state.filter, ...filter };
  resetPaging();
  notify();
}

/** Moves to the next page, remembering where to come back to. */
export function pushCursor(nextCursor) {
  state.cursorStack.push(state.cursor);
  state.cursor = nextCursor;
  state.selectedIds.clear();
  notify();
}

/** Moves back one page. */
export function popCursor() {
  state.cursor = state.cursorStack.pop() ?? null;
  state.selectedIds.clear();
  notify();
}

/** Toggles a row's selection for an explicit-id mutation. */
export function toggleSelected(id, selected) {
  if (selected) {
    state.selectedIds.add(id);
  } else {
    state.selectedIds.delete(id);
  }
  notify();
}

/** Clears the row selection, after a mutation or a filter change. */
export function clearSelection() {
  state.selectedIds.clear();
  notify();
}

/** The target the management routes need: transport, service and context. */
export function target() {
  if (state.selection === null || state.context === null) {
    return null;
  }
  return {
    transport: state.selection.transport,
    service: state.selection.service,
    context: state.context,
  };
}

/** The filter in the shape the server expects, with empty fields omitted. */
export function serverFilter() {
  const filter = { status: state.filter.status };
  if (state.filter.search) {
    filter.search = state.filter.search;
  }
  if (state.filter.from) {
    filter.from = new Date(state.filter.from).toISOString();
  }
  if (state.filter.to) {
    filter.to = new Date(state.filter.to).toISOString();
  }
  return filter;
}

function resetPaging() {
  state.cursor = null;
  state.cursorStack = [];
  state.selectedIds.clear();
}
