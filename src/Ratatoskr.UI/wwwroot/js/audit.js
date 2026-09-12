// The audit view: every mutation this dashboard issued, and what came back.

import * as api from "./api.js";
import { byId, el, emptyRow, formatInstant, replaceChildren } from "./dom.js";
import { describe } from "./messages.js";

/** Loads and renders the audit trail. */
export async function renderAudit() {
  const tbody = byId("audit-tbody");

  try {
    const entries = await api.listAudit(200);
    replaceChildren(
      tbody,
      entries.length > 0
        ? entries.map((entry) => renderRow(entry))
        : [emptyRow(8, "No mutation has been issued from this dashboard yet.")],
    );
  } catch (error) {
    replaceChildren(tbody, [emptyRow(8, describe(error))]);
  }
}

function renderRow(entry) {
  return el("tr", {
    children: [
      el("td", { text: formatInstant(entry.startedAt) }),
      el("td", { text: entry.actorDisplayName ?? entry.actor ?? "—" }),
      el("td", { text: entry.transportName }),
      el("td", { text: entry.serviceName }),
      el("td", { text: entry.resource ?? "—" }),
      el("td", { text: entry.operation }),
      el("td", {
        className: "mono error-cell",
        text: entry.requestJson ?? "—",
        attrs: { title: entry.requestJson ?? "" },
      }),
      el("td", {
        children: [
          el("span", {
            className: `badge ${entry.outcome === "Ok" ? "badge-success" : "badge-danger"}`,
            text: entry.errorCode ?? entry.outcome,
          }),
        ],
      }),
    ],
  });
}
