# Shared Mission Control queries

`AgentEval.MissionControl.Core` is a transport-independent library for in-process consumers and remote adapters. It references Results and Indexing, with no ASP.NET, GraphQL or MAF dependency. Results retains its BCL-only boundary.

`IEvidenceQueryService` currently exposes one-page `Sources`, `Results`, `Metrics`, `Spans` and `Fields` operations. `EvidenceQueryService` composes injected `IEvidenceSourceReader` and `IEvidenceDetailReader` instances; its parameterless constructor represents an explicitly unconfigured capability. It never discovers storage, creates an index, follows continuations automatically or repairs unavailable evidence.

```csharp
IEvidenceQueryService queries = new EvidenceQueryService(
    new SqliteEvidenceSourceReader(indexOptions),
    new SqliteEvidenceDetailReader(indexOptions));
var outcome = queries.Sources(new(PageSize: 100), cancellationToken);
// Available and Empty retain the page's instance/generation and continuation.
// Other states have no partial page; inspect the stable Code.
```

| State | Meaning |
| --- | --- |
| Available | Complete requested page containing items |
| Empty | Successful empty page, with snapshot metadata retained |
| NotConfigured | No evidence query backend was configured |
| NotFound | Requested source absent from the current index |
| InvalidRequest | Invalid page parameters/cursor, or wrong evidence family |
| StaleCursor | Index generation or instance changed; restart traversal |
| ResourceLimit | Page text or configured index-size budget exceeded |
| Unavailable | Missing, busy, corrupt, incompatible or inaccessible index |

`QueryResult<T>` exposes `State`, `Data` and `Code`. Only successful Available/Empty outcomes contain data; refusals contain an allowlisted code instead of exception messages or filesystem paths. An unavailable capability is not a successful empty workspace. When unconfigured, non-null requests return NotConfigured without attempting backend validation. Null requests remain caller programming errors. Cancellation propagates, including for unconfigured capabilities; unexpected programming exceptions are not swallowed.

The service preserves the [index page contracts](evidence-index.md): finite row/text/cursor budgets, exact Int64 values and unsigned timestamp strings, source provenance, nullable aggregate case IDs and metric pass flags, and conflicting observation fields. These are indexed facts, not freshness, authorization, completeness or evaluation-pass claims. Hosts must enforce access policy before invoking the service. A root filter, source ID or cursor grants no authority.

Each operation performs at most one bounded reader call. Consumers choose a suitable page size instead of automatically walking the index within one request. Full index validation is part of that operation's cost; cancellation does not impose a hard deadline inside synchronous SQLite integrity checks.

This library supplies shared contracts, evidence page semantics and the verified suite queries described below. Fuller evidence detail/comparison and governance services, host registration, GraphQL mapping, notification delivery and transport-parity acceptance remain integration work. Existing legacy endpoints are unchanged by introducing this library.

## Verified suite queries

`ISuiteQueryService.Identity(sourceId)` resolves an exact indexed source through `IEvidenceSourceLookup`, checks its persisted root binding against explicitly configured roots, and re-verifies the sealed suite identity against the indexed run hash. Remote callers supply a source ID, never a directory. Unconfigured root IDs appear absent; missing or changed bindings refuse. Unbound historical index rows cannot be used to infer physical locations. Relative paths and ordinary ancestors are checked again at use time. Trusted local storage ownership remains required; these checks are not protection against a hostile filesystem owner replacing entries concurrently.

`Compare(baselineSourceId, candidateSourceId)` reads both verified identities and requires their selections to share an index instance/generation. If that revision changes, the response is StaleCursor/selection-changed and the caller reselects. The service reuses `ISuiteCompatibilityEvaluator`'s strict frozen-definition policy. Available means the comparison executed, not that the suites are compatible. Legacy or missing definitions remain explicitly incompatible. Identity compatibility alone does not establish completeness, matching subjects, comparable scores or statistical significance.

Responses include source IDs, index selection revisions and verified identities; physical binding directories stay inside the service. The selection revision records which indexed run hash was chosen, not freshness of every source file or overlay. Invalid, altered or unsealed evidence returns no identity. Cancellation is checked around the existing synchronous verifying reader, without claiming a hard wall-clock deadline. Full evidence hierarchy/effective-overlay and statistical comparison queries remain separate work.
