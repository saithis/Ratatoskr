// Small DOM helpers, deliberately narrow.
//
// Nothing here ever assigns markup. Everything the dashboard renders — service names, handler
// keys, error text, message payloads — comes from another process's database, and the only
// reliable way to keep it from executing is to set text, never to parse HTML.

/** Returns the element with the given id, or throws so a typo fails loudly instead of silently. */
export function byId(id) {
  const element = document.getElementById(id);
  if (element === null) {
    throw new Error(`The dashboard markup has no element with id '${id}'.`);
  }
  return element;
}

/** Creates an element, setting text and attributes safely. */
export function el(tag, { text, className, attrs, children } = {}) {
  const node = document.createElement(tag);
  if (className !== undefined) {
    node.className = className;
  }
  if (text !== undefined && text !== null) {
    node.textContent = String(text);
  }
  for (const [name, value] of Object.entries(attrs ?? {})) {
    if (value !== undefined && value !== null && value !== false) {
      node.setAttribute(name, value === true ? "" : String(value));
    }
  }
  for (const child of children ?? []) {
    node.append(child);
  }
  return node;
}

/** Replaces an element's children with the given nodes. */
export function replaceChildren(parent, nodes) {
  parent.replaceChildren(...nodes);
}

/** Shows or hides an element without touching its layout rules. */
export function show(element, visible) {
  element.hidden = !visible;
}

/** Renders a "nothing here" row spanning the whole table. */
export function emptyRow(columns, message) {
  return el("tr", {
    children: [el("td", { text: message, className: "muted", attrs: { colspan: columns } })],
  });
}

/** Formats an instant for display, or a dash when absent. */
export function formatInstant(value) {
  if (!value) {
    return "—";
  }
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? "—" : parsed.toLocaleString();
}

/** Formats how long ago an instant was, which is what an operator actually reads. */
export function formatAge(value) {
  if (!value) {
    return "never";
  }
  const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 60) {
    return `${seconds}s ago`;
  }
  if (seconds < 3600) {
    return `${Math.round(seconds / 60)}m ago`;
  }
  if (seconds < 86400) {
    return `${Math.round(seconds / 3600)}h ago`;
  }
  return `${Math.round(seconds / 86400)}d ago`;
}

/** Shortens an identifier for a table cell while keeping the full value in the title. */
export function shortId(value) {
  const text = String(value ?? "");
  return text.length > 12 ? `${text.slice(0, 8)}…` : text;
}
