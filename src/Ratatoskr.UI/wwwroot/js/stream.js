// The live feed of registry changes.
//
// The server sends a full snapshot on every wake-up rather than a delta, because it coalesces
// bursts — so a client applying deltas would end up rendering a state that never existed.

import { byId, show } from "./dom.js";
import * as api from "./api.js";
import * as state from "./state.js";

/** Connects to the event stream and keeps the connection badge honest. */
export function connect() {
  setStatus("Connecting…", false);

  const source = api.openEventStream();

  source.addEventListener("services", (event) => {
    setStatus("Live", true);
    state.setServices(JSON.parse(event.data));
  });

  source.addEventListener("open", () => setStatus("Live", true));

  source.addEventListener("error", () => {
    // EventSource reconnects on its own; saying "reconnecting" is more honest than pretending
    // the view is still live while it is not.
    setStatus("Reconnecting…", false);
  });

  return source;
}

function setStatus(text, connected) {
  byId("stream-status").textContent = text;
  byId("stream-badge").className = connected ? "badge-stream connected" : "badge-stream";
  show(byId("stream-badge"), true);
}
