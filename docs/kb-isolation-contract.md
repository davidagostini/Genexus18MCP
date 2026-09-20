# KB isolation and local authorization contract

This document defines the target contract for issue #146. It separates
configuration registration from the user's explicit choice of a local
Knowledge Base.

## Configuration and transport

The new configuration format uses `ConfigSchemaVersion` so it does not collide
with the MCP response `_meta.schemaVersion` (`mcp-axi/2`). `GatewayMode` is
explicit and is not inferred from `HttpPort` or `McpStdio`.

The supported combinations are:

| GatewayMode | ResolutionPolicy | Meaning |
| --- | --- | --- |
| `stdio-isolated` | `strict` | default isolated local process |
| `stdio-isolated` | `legacy` | explicit compatibility mode without gateway lease or HTTP |
| `http-shared` | `strict` | shared endpoint with explicit context and ownership |
| `http-shared` | `legacy` | compatibility behavior with legacy fallback warnings |

`stdio-isolated` never uses `GatewayProcessLease`, HTTP, master/proxy,
promotion, or port recovery. A hybrid configuration is invalid in strict mode.

`Server.WorkerSharingMode` defaults to `isolated`. The supported value
`shared-host` is valid only with `GatewayMode=stdio-isolated`; it shares the
SDK Worker process through a per-KB local broker while keeping every Gateway
session and authorization context isolated. Agents that need more than one
Worker keep `WorkerSharingMode="isolated"`. It does not turn one Gateway into
the master of another and does not share MCP sessions.

## Local-friendly authorization

The default local-friendly policy treats an explicit absolute path to a valid
local GeneXus KB as sufficient user authorization for `genexus_kb action=open`.
It does not require a separate trust-root command for normal single-user use.

The gateway still validates the final physical path and refuses ambiguous
identity, alias rebind, malformed/relative paths, and cross-owner lease use.
Those checks prevent accidental cross-context operations; they are not a
second confirmation prompt for the same explicit user request.

An optional hardened policy may require pre-provisioned trusted roots,
additional ACL checks, and stricter local authorization. Hardened policy is
not the default and must be visible in diagnostics.

## Identity and selection

Alias is a display label, not physical identity. The target model uses an
opaque stable `kbId` for the physical KB and a separate worker/context
generation. New selectors are typed as `{ "kbId": "..." }` or
`{ "alias": "..." }`; an untyped string remains legacy-only.

The gateway stores lease and selection context internally when possible. The
client should not need to copy a lease token into every ordinary tool call.
Explicit selection prevents ambiguity, while automatic lease renewal and
release preserve the normal local workflow.

## Compatibility

Legacy configuration and untyped selectors remain available only through the
explicit legacy path. The client registration itself must point to the neutral
runtime configuration and must not contain a KB path, alias, default, or
catalog.

## Local-friendly versus hardened deployments

The repository's default is **local-friendly**: an explicit absolute path to a
valid local KB is enough to request `genexus_kb action=open`. The gateway still
checks that the path is a real, unambiguous KB and that the caller is not using
another owner's lease. These checks are authorization boundaries, not an extra
trust-root ceremony for the normal single-user workflow.

**Hardened** is a deployment posture, not a second implicit resolution policy.
Operators who need it should place the runtime behind OS ACLs, approved roots,
and network controls, and require the HTTP token described below. Hardened
controls must be made visible in diagnostics; they must not be inferred from a
KB alias or from `HttpPort=0`.

## ResolutionPolicy and legacy behavior

`ResolutionPolicy: "strict"` is the default for the neutral configuration. The
resolution order is explicit `kb` argument, session selection from
`genexus_kb action=select`/`set_session_default`, then the strict policy. A
persisted `DefaultKb`/`ActiveKb` does not seed a strict session. A single open KB
may be used as `single-open` only when it does not conflict with a configured
default; zero open KBs never auto-open a declared catalog entry.

`ResolutionPolicy: "legacy"` is an explicit compatibility escape hatch. It
preserves the fallback chain `config-default → single-open → declared-first`
and the legacy persistent `set_default` behavior. Legacy behavior must be
called out in diagnostics and is not a reason to put KB identity into a client
registration.

## Open, close, select, and operations without a lease

- `open` starts/registers the worker and creates an owner-scoped KB-use lease.
- `select` and `set_session_default` change only the in-memory selection for the
  current session; they do not write `config.json` and do not create ownership.
- `set_persistent_default` (and legacy `set_default`) changes the startup
  fallback on disk and returns `persistedTo`.
- `close` releases only the caller's lease. It must not release another
  session's lease or redirect that session's selection.
- In strict mode, a stateful KB-bound operation without the caller's active
  lease fails closed with `KB_NOT_OWNED`. Supplying `kb` identifies a target but
  does not transfer ownership. Neutral gateway/diagnostic operations and
  explicitly read-only operations that do not acquire a KB context can run
  without a lease; callers must not use that exception to infer a KB.
- An invalid, stale, expired, or mismatched lease is rejected with
  `KB_LEASE_INVALID` or `KB_LEASE_EXPIRED`; a token belonging to another owner
  is `KB_NOT_OWNED`. A worker-instance collision is surfaced as `KB_LOCKED`
  (the internal worker marker is `WORKER_HANDSHAKE_REJECT_BUSY`); it is not a
  request to retry blindly or proxy through another gateway.

## HTTP token boundary

`GXMCP_HTTP_TOKEN` is an environment secret, never a config/client-registration
field and never part of an MCP response, lease, journal, or log. When set, every
`/mcp` request must present the same value as `Authorization: Bearer <token>`
or `X-GXMCP-Token`; comparison is constant-time. With no token, loopback binds
remain usable for local development. A non-loopback bind without a token
refuses `/mcp` requests, and a missing or wrong token returns HTTP 401. The
token is read when the HTTP server starts; restart the gateway after rotation.