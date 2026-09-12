// The sidebar and the service grid: everything that answers "which service am I looking at".

import { byId, el, emptyRow, formatAge, replaceChildren, show } from "./dom.js";
import * as state from "./state.js";

/** Renders the sidebar, grouped by transport. */
export function renderSidebar(services) {
  byId("service-count").textContent = String(services.length);

  const byTransport = new Map();
  for (const service of services) {
    const group = byTransport.get(service.transportName) ?? [];
    group.push(service);
    byTransport.set(service.transportName, group);
  }

  const sections = [...byTransport.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([transport, group]) => renderTransportGroup(transport, group));

  if (sections.length === 0) {
    sections.push(
      el("p", {
        className: "muted sidebar-empty",
        text: "No control plane has reported a service yet.",
      }),
    );
  }

  replaceChildren(byId("sidebar-transports"), sections);
}

function renderTransportGroup(transport, services) {
  const items = services
    .slice()
    .sort((left, right) => left.serviceName.localeCompare(right.serviceName))
    .map((service) => renderSidebarItem(service));

  return el("div", {
    className: "transport-group",
    children: [
      el("div", { className: "transport-name", text: transport }),
      el("ul", { className: "service-nav-list", children: items }),
    ],
  });
}

function renderSidebarItem(service) {
  const selection = state.current().selection;
  const isSelected =
    selection?.transport === service.transportName && selection?.service === service.serviceName;

  const item = el("li", {
    className: `service-nav-item${isSelected ? " active" : ""}`,
    children: [
      el("span", { text: service.serviceName }),
      el("span", {
        className: `badge ${badgeClass(service)}`,
        text: String(service.poisonedOutbox + service.poisonedInbox),
        attrs: { title: "Poisoned messages across this service's contexts" },
      }),
    ],
  });

  item.addEventListener("click", () => state.select(service.transportName, service.serviceName));
  return item;
}

/** Renders the "all services" grid. */
export function renderServiceGrid(services) {
  show(byId("services-empty"), services.length === 0);

  const cards = services
    .slice()
    .sort(
      (left, right) =>
        left.transportName.localeCompare(right.transportName) ||
        left.serviceName.localeCompare(right.serviceName),
    )
    .map((service) => renderCard(service));

  replaceChildren(byId("services-grid"), cards);
}

function renderCard(service) {
  const card = el("div", {
    className: "service-card",
    children: [
      el("div", {
        className: "card-header",
        children: [
          el("div", {
            children: [
              el("div", { className: "card-title", text: service.serviceName }),
              el("div", { className: "card-subtitle", text: service.transportName }),
            ],
          }),
          el("span", {
            className: `badge ${service.liveness === "Online" ? "badge-success" : "badge-warning"}`,
            text: service.liveness === "Online" ? "online" : "stale",
          }),
        ],
      }),
      el("div", {
        className: "card-stats",
        children: [
          stat("Replicas", `${service.onlineInstanceCount} / ${service.instanceCount}`),
          stat("Last seen", formatAge(service.lastSeenAt)),
          stat("Pending out", service.pendingOutbox),
          stat("Poisoned out", service.poisonedOutbox, service.poisonedOutbox > 0),
          stat("Pending in", service.pendingInbox),
          stat("Poisoned in", service.poisonedInbox, service.poisonedInbox > 0),
        ],
      }),
      el("div", {
        className: "card-contexts",
        text:
          service.contextNames.length > 0
            ? service.contextNames.join(", ")
            : "no contexts reported",
      }),
    ],
  });

  card.addEventListener("click", () => state.select(service.transportName, service.serviceName));
  return card;
}

function stat(label, value, alarming = false) {
  return el("div", {
    className: "card-stat",
    children: [
      el("div", { className: "card-stat-label", text: label }),
      el("div", {
        className: `card-stat-value${alarming ? " alarming" : ""}`,
        text: String(value),
      }),
    ],
  });
}

function badgeClass(service) {
  if (service.poisonedOutbox + service.poisonedInbox > 0) {
    return "badge-danger";
  }
  return service.liveness === "Online" ? "badge-success" : "badge-warning";
}

/** Renders the replica and context tables for one service. */
export function renderOverview(detail) {
  const replicas = detail.instances.map((instance) =>
    el("tr", {
      children: [
        cell(instance.instanceId),
        cell(instance.machineName),
        cell(instance.environment ?? "—"),
        cell(formatAge(instance.startedAt)),
        cell(formatAge(instance.lastSeenAt)),
        el("td", {
          children: [
            el("span", {
              className: `badge ${instance.liveness === "Online" ? "badge-success" : "badge-warning"}`,
              text: instance.liveness === "Online" ? "online" : "stale",
            }),
          ],
        }),
      ],
    }),
  );

  replaceChildren(
    byId("replicas-tbody"),
    replicas.length > 0 ? replicas : [emptyRow(6, "No replica has announced itself.")],
  );

  const contexts = detail.dbContexts.map((context) =>
    el("tr", {
      children: [
        cell(context.name),
        cell(context.hasOutbox ? "yes" : "—"),
        cell(context.hasInbox ? "yes" : "—"),
        cell(context.pendingOutbox),
        cell(context.poisonedOutbox),
        cell(context.pendingInbox),
        cell(context.poisonedInbox),
      ],
    }),
  );

  replaceChildren(
    byId("contexts-tbody"),
    contexts.length > 0 ? contexts : [emptyRow(7, "This service reports no database contexts.")],
  );
}

/** Renders the channel topology for one service. */
export function renderChannels(detail) {
  const rows = detail.channels.map((channel) =>
    el("tr", {
      children: [
        cell(channel.logicalName),
        cell(channel.intent),
        cell(channel.transportBindings.map((binding) => binding.displayName).join(", ") || "—"),
        cell(channel.messageTypes.join(", ") || "—"),
      ],
    }),
  );

  replaceChildren(
    byId("channels-tbody"),
    rows.length > 0 ? rows : [emptyRow(4, "This service reports no channels.")],
  );
}

function cell(value) {
  return el("td", { text: String(value ?? "—") });
}
