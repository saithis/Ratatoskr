// The message detail dialog: CloudEvents metadata, the payload, and sibling handlers.

import * as api from "./api.js";
import { byId, el, formatInstant, replaceChildren, show } from "./dom.js";
import * as state from "./state.js";

/** Opens the detail dialog for one row. */
export async function openDetail(area, id) {
  byId("modal-title").textContent = area === "inbox" ? "Inbox handler" : "Outbox message";
  replaceChildren(byId("modal-body"), [el("p", { className: "muted", text: "Loading…" })]);
  replaceChildren(byId("modal-footer"), []);
  show(byId("detail-modal"), true);

  try {
    const detail = await api.getMessage(state.target(), area, id);
    replaceChildren(byId("modal-body"), render(area, detail));
  } catch (error) {
    replaceChildren(byId("modal-body"), [
      el("p", {
        className: "warning-text",
        // Payload access is a separate authorization policy, so "you may see that it failed but
        // not what was in it" is a configuration, not a bug — say so rather than showing nothing.
        text:
          error.status === 403
            ? "You are not permitted to view message payloads on this dashboard."
            : (error.message ?? "The message could not be loaded."),
      }),
    ]);
  }
}

/** Closes the detail dialog. */
export function closeDetail() {
  show(byId("detail-modal"), false);
}

function render(area, detail) {
  const facts = [
    ["Type", detail.messageType],
    ["Transport", detail.transportName],
    ["Poisoned", detail.isPoisoned ? "yes" : "no"],
    ["Errors", detail.errorCount],
    ["Requeued", detail.requeuedCount],
    ["Last error", detail.lastError ?? "—"],
  ];

  if (area === "inbox") {
    facts.unshift(
      ["Handler status", detail.handlerStatusId],
      ["Message id", detail.messageId],
      ["Handler", detail.handlerKey],
    );
    facts.push(
      ["Received", formatInstant(detail.receivedAt)],
      ["Completed", formatInstant(detail.completedAt)],
    );
  } else {
    facts.unshift(["Id", detail.id]);
    facts.push(
      ["Created", formatInstant(detail.createdAt)],
      ["Processed", formatInstant(detail.processedAt)],
      ["Failed", formatInstant(detail.failedAt)],
      ["Scheduled", formatInstant(detail.scheduledAt)],
    );
  }

  const nodes = [section("Message", definitionList(facts))];

  const properties = detail.properties ?? {};
  nodes.push(
    section(
      "CloudEvents properties",
      definitionList(
        Object.entries(properties)
          .filter(([, value]) => value !== null && value !== undefined)
          .map(([key, value]) => [key, String(value)]),
      ),
    ),
  );

  nodes.push(
    section(
      "Payload",
      el("pre", {
        className: "payload",
        // textContent, always: this is another system's data, and a payload is exactly the place
        // a stored cross-site script would be waiting.
        text: detail.jsonPayload ?? `(not JSON — base64) ${detail.payloadBase64}`,
      }),
    ),
  );

  if (area === "inbox" && detail.otherHandlers?.length > 0) {
    nodes.push(
      section(
        "Other handlers of this message",
        el("table", {
          className: "data-table",
          children: [
            el("tbody", {
              children: detail.otherHandlers.map((handler) =>
                el("tr", {
                  children: [
                    el("td", { text: handler.handlerKey }),
                    el("td", { text: handlerState(handler) }),
                    el("td", { text: handler.lastError ?? "—" }),
                  ],
                }),
              ),
            }),
          ],
        }),
      ),
    );
  }

  return nodes;
}

function handlerState(handler) {
  if (handler.isPoisoned) {
    return "poisoned";
  }
  return handler.completedAt ? "completed" : "pending";
}

function section(title, body) {
  return el("section", {
    className: "detail-section",
    children: [el("h4", { text: title }), body],
  });
}

function definitionList(pairs) {
  return el("dl", {
    className: "detail-list",
    children: pairs.flatMap(([label, value]) => [
      el("dt", { text: label }),
      el("dd", { text: String(value ?? "—") }),
    ]),
  });
}
