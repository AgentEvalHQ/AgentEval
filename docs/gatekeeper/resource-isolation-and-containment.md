# Resource isolation and containment operations

Gatekeeper's HTTP Bulkhead separates **local** connection and request pressure for normal and contained sessions.
It is a degraded-execution option: contained or unverifiable work receives a smaller independent pool instead of
sharing the normal pool.

Use hard admission denial when a contained identity must perform no work. Use resource isolation only when the
application intentionally permits bounded degraded work.

## Architecture

`ContainmentHttpClientPool` owns two complete HTTP pipelines:

```text
normal session     -> normal concurrency permits   -> normal primary handler/socket pool
contained/unknown  -> isolated concurrency permits -> isolated primary handler/socket pool
```

The default normal and isolated pipelines use different `SocketsHttpHandler` instances and therefore separate
connection pools. An outer semaphore enforces request concurrency even when custom primary handlers are supplied.

`ContainmentHttpResourceRoutingFunction` decorates a local `AIFunction`. Immediately before invocation it reads the
authoritative root session, resolves one tenant-bound containment target, reads its snapshot, and overlays the
selected `HttpClient` in `AIFunctionArguments.Services`.

The inner function must resolve `HttpClient` from the invocation service provider. A client captured in a closure or
constructed inside the function cannot be replaced by the decorator.

## Configuration

```csharp
using var pool = new ContainmentHttpClientPool(new ContainmentHttpClientPoolOptions
{
    NormalMaxConcurrency = 32,
    IsolatedMaxConcurrency = 2,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    Timeout = TimeSpan.FromSeconds(30),
});

var routed = httpTool.WithContainmentHttpResourceIsolation(
    containmentStore,
    rootSession => ResolveTenantBoundTarget(rootSession),
    pool);

new[] { routed }.ValidateContainmentHttpResourceIsolation();
```

Validation rules include:

- normal and isolated concurrency are between 1 and 4096;
- isolated concurrency cannot exceed normal concurrency;
- timeout is between one second and ten minutes;
- pooled connection lifetime is between one second and one day;
- an optional base address is absolute HTTP(S);
- custom factories return non-null, distinct top-level handler instances;
- the declared resource-consuming function set is non-empty, bounded, and fully decorated.

A custom factory owns the responsibility for returning an independent handler graph. The pool can verify only the
top-level instance.

## Routing decision

The routing read is fail closed toward isolation:

| Condition | Selected resources |
|---|---|
| Snapshot is `NotContained` or `Released` for the exact resolved target | Normal |
| Snapshot is `Active` or `Indeterminate` | Isolated |
| Root run/session is missing | Isolated |
| Target resolver fails or returns an invalid target | Isolated |
| Store fails, returns null, or returns a mismatched target | Isolated |
| Caller cancellation is requested | Cancellation propagates; the tool is not invoked |

Selection linearizes at the snapshot read. A containment mutation after a request starts does not migrate the
in-flight invocation.

The decorator never falls back to the original service provider for `HttpClient`. Other services may fall back.
This prevents an incorrectly configured selected pool from silently reaching an uncontrolled client.

## Interaction with containment gates

Choose one posture per target:

- **Hard containment:** `ContainedIdentityGate` rejects the run before any tool executes.
- **Degraded containment:** omit the hard admission gate for that target and route resource-consuming tools through
  the isolated pool.
- **Tool override:** `ContainmentOverrideGate` blocks a particular effect rather than allowing degraded execution.

Do not claim both hard denial and degraded execution in the same trace. If `ContainedIdentityGate` stops the run,
the routing function never executes.

Tenant-scope graph decisions feed the same signed containment lifecycle through the fixed-tenant bridge. They do not
create a second resource-routing state store.

## Ownership and disposal

The caller owns the pool, store, target resolver, and decorated functions.

- Create one pool for the intended application lifetime.
- Do not dispose it while requests are in flight.
- Disposal is idempotent and disposes both clients and handler graphs.
- The routing decorator borrows its dependencies and does not dispose them.
- Preserve cancellation from the original invocation.

## Metrics and validation

The pool exposes:

- `NormalCurrentRequests` and `IsolatedCurrentRequests`;
- `NormalPeakRequests` and `IsolatedPeakRequests`.

Peak values measure runtime permits actually held; configured limits alone are not proof of isolation. A release
test should saturate the isolated path with delayed fake handlers, assert its measured peak does not exceed the
isolated cap, and prove useful normal work continues independently.

Use fake handlers for deterministic tests. The Bulkhead sample must not contact a network.

## Combining with HTTP egress enforcement

Resource isolation limits local concurrency and socket-pool sharing. `GatekeeperHttpMessageHandler` validates each
destination, redirect, and DNS result. A deployment needing both must build each normal/isolated handler graph with
wire enforcement inside the concurrency-capped pipeline.

Then validate both properties independently:

1. contained pressure cannot consume normal local permits or sockets;
2. neither pool may reach an unapproved or private destination.

## Honest ceiling

This pattern does **not** partition:

- a provider-side quota shared by the same credential;
- a downstream tenant rate limit;
- CPU, memory, database connections, browser workers, or process resources;
- HTTP calls made by an unwrapped function;
- a client already captured or constructed inside the tool.

Add another resource type only after a named deployment has a measurable exhaustion mode and an enforceable
injection/routing seam. Do not generalize the HTTP proof into a claim of universal process isolation.

## Operations checklist

- Inventory every local function that consumes HTTP resources.
- Make service-provider resolution mandatory for those clients.
- Validate the full declared set at construction.
- Resolve targets from the authoritative root session, never model text.
- Treat missing or indeterminate containment as isolated.
- Use separate handler graphs and measured concurrency.
- Pair with wire-level destination controls when egress matters.
- Alert on unexpected isolated routing and sustained saturation.
- Dispose only after bounded drain.
- Document the shared downstream quotas the local Bulkhead cannot isolate.
