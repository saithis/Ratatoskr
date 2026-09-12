# Ratatoskr.Management.Abstractions

Dependency-free, versioned contracts for the Ratatoskr management control plane. This package
references nothing — not the Ratatoskr core, not a storage provider, not a broker client — so an
agent, a dashboard and a transport can be written against it independently.

## What is in here

- **Protocol** — `ManagementRequestEnvelope` / `ManagementResponseEnvelope`, `ProtocolVersion`,
  `ManagementTarget`, `ManagementActor`, `ManagementResult` and the stable `ManagementErrorCodes`.
- **Operations** — `IManagementOperation` plus the request and response shapes for every
  operation named in `ManagementOperationNames`.
- **Discovery** — `ServiceAnnouncement`, `CapabilityDescriptor`, `ChannelTopology`.
- **Transport** — `IManagementTransport`, `IManagementDiscoverySource`,
  `IManagementTransportRegistry`, `ManagementAddress`.

## Guarantees

**Delivery is at least once.** Every request carries a stable `OperationId` that does not change
across retries or redeliveries. Mutating operations persist it alongside their changes, so a
duplicate delivery replays the recorded result instead of mutating twice. A caller that loses a
response retries with the same `OperationId`.

**Failure is a value, not an exception.** Operations return `ManagementResult`, carrying a stable
code from `ManagementErrorCodes`. The code survives the trip across a broker and into
ProblemDetails; the human-readable detail does not, and must never be parsed.

**Targeting.** `ManagementTarget.ServiceName` addresses the logical service, where replicas
compete so exactly one handles the command. Adding `InstanceId` addresses one replica, and fails
fast with `target_unreachable` when that replica is gone. `Resource` names the resource inside the
service — for the EF Core operations, the DbContext short name.

**Versioning.** A receiver serves the same major version and a minor version no greater than its
own. Anything else is refused with `unsupported_protocol_version`. Minor versions are additive.

**Paging.** Lists are keyset-paginated through opaque cursors and carry no total; a total is a
second full scan, so callers that need one ask for it through the matching `*.count` operation.
