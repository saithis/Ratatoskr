// Everything the dashboard asks the server, in one place.
//
// The base path is derived from where index.html was served, so the dashboard works wherever it
// is mounted rather than assuming "/ratatoskr".

const basePath = location.pathname.replace(/\/(index\.html)?$/, "");

/** A failure the server described in ProblemDetails, carrying its stable code. */
export class ManagementError extends Error {
  constructor(status, code, detail) {
    super(detail);
    this.name = "ManagementError";
    this.status = status;
    this.code = code;
  }
}

let antiforgery = null;

/**
 * Fetches the antiforgery token this session needs.
 *
 * Only cookie-authenticated callers are asked for one, so on a bearer-token deployment this is a
 * harmless no-op: the server simply never checks it.
 */
export async function loadAntiforgeryToken() {
  try {
    const response = await fetch(`${basePath}/api/antiforgery`, { credentials: "same-origin" });
    if (response.ok) {
      antiforgery = await response.json();
    }
  } catch {
    // A dashboard that cannot fetch a token is still fully usable for reading, and a mutation
    // will fail with a clear message rather than being blocked pre-emptively here.
  }
}

async function request(path, { method = "GET", body, operationId } = {}) {
  const headers = { Accept: "application/json" };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  if (antiforgery?.headerName && antiforgery?.requestToken) {
    headers[antiforgery.headerName] = antiforgery.requestToken;
  }
  if (operationId) {
    headers["X-Ratatoskr-Operation-Id"] = operationId;
  } else if (method === "POST" || method === "PUT" || method === "DELETE") {
    headers["X-Ratatoskr-Operation-Id"] = crypto.randomUUID();
  }

  const response = await fetch(`${basePath}${path}`, {
    method,
    headers,
    credentials: "same-origin",
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (response.status === 204) {
    return null;
  }

  const text = await response.text();
  const payload = text.length === 0 ? null : JSON.parse(text);

  if (!response.ok) {
    throw new ManagementError(
      response.status,
      payload?.code ?? "unknown",
      payload?.detail ?? `The request failed with status ${response.status}.`,
    );
  }

  return payload;
}

/** The transports this dashboard watches. */
export const listTransports = () => request("/api/transports");

/** Every service seen on every transport. */
export const listServices = () => request("/api/services");

/** One service in full. */
export const getService = (transport, service) =>
  request(`/api/transports/${encode(transport)}/services/${encode(service)}`);

/** The dashboard's own audit trail. */
export const listAudit = (limit = 100) => request(`/api/audit?limit=${limit}`);

/** The URL the shared management routes live under for one service. */
function serviceRoot(transport, service, context) {
  return (
    `/api/transports/${encode(transport)}/services/${encode(service)}` +
    `/contexts/${encode(context)}`
  );
}

/** One keyset page of messages. */
export const listMessages = (target, area, filter, cursor, limit) =>
  request(
    `${serviceRoot(target.transport, target.service, target.context)}/${area}` +
      `?${filterQuery(filter)}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}&limit=${limit}`,
  );

/** How many rows a filter matches. Backs the destructive-action preview. */
export const countMessages = (target, area, filter) =>
  request(
    `${serviceRoot(target.transport, target.service, target.context)}/${area}/count?${filterQuery(filter)}`,
  );

/** One message with its payload. */
export const getMessage = (target, area, id) =>
  request(`${serviceRoot(target.transport, target.service, target.context)}/${area}/${encode(id)}`);

/** Requeues or deletes an explicit list of ids. */
export const mutateByIds = (target, area, action, ids, operationId) =>
  request(`${serviceRoot(target.transport, target.service, target.context)}/${area}/${action}`, {
    method: "POST",
    body: { ids },
    operationId,
  });

/** Requeues or deletes everything a filter matches, in bounded server-side batches. */
export const mutateMatching = (target, area, action, filter, operationId) =>
  request(
    `${serviceRoot(target.transport, target.service, target.context)}/${area}/${action}-matching`,
    { method: "POST", body: { filter }, operationId },
  );

/** Requeues every poisoned handler of one inbox message. */
export const requeueInboxMessage = (target, messageId, operationId) =>
  request(
    `${serviceRoot(target.transport, target.service, target.context)}` +
      `/inbox/messages/${encode(messageId)}/requeue`,
    { method: "POST", operationId },
  );

/** Requeues dead-lettered messages for a channel. */
export const requeueDlq = (target, channelName, queueName, limit, operationId) =>
  request(
    `/api/transports/${encode(target.transport)}/services/${encode(target.service)}` +
      `/channels/${encode(channelName)}/dlq/requeue`,
    {
      method: "POST",
      body: { queueName, limit },
      operationId,
    },
  );

/** Purges a dead-letter queue for a channel. */
export const purgeDlq = (target, channelName, queueName, operationId) =>
  request(
    `/api/transports/${encode(target.transport)}/services/${encode(target.service)}` +
      `/channels/${encode(channelName)}/dlq/purge`,
    {
      method: "POST",
      body: { queueName },
      operationId,
    },
  );

/** Opens the server-sent event stream of registry changes. */
export const openEventStream = () => new EventSource(`${basePath}/api/events`);

function filterQuery(filter) {
  const parts = [`status=${encodeURIComponent(filter.status)}`];
  if (filter.search) {
    parts.push(`search=${encodeURIComponent(filter.search)}`);
  }
  if (filter.from) {
    parts.push(`from=${encodeURIComponent(filter.from)}`);
  }
  if (filter.to) {
    parts.push(`to=${encodeURIComponent(filter.to)}`);
  }
  return parts.join("&");
}

function encode(segment) {
  return encodeURIComponent(String(segment ?? ""));
}
