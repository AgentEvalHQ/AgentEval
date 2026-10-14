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

## Verified result detail

`IResultDetailQueryService.Result(sourceId, resultId, at)` uses the same configured-root selection policy as suite queries. The host supplies its trust policy at construction; requests select an explicit view time but cannot grant trust authority. The service calls `IAefResultDetailReader` to capture a private ordinary-file copy under AEF staging limits, verify the selected seal and compute overlays using the existing AEF rules. Default capture limits are 1,000 files and 64 MiB total/per-file; configured larger evidence can explicitly refuse at those limits.

The response retains reserialized sealed result JSON, including hierarchy references and reported scores, alongside sealed state, effective state and the overriding event ID. It also includes result/run review status, the verified assurance of the events shown, and the count of unsealed events. Sealed JSON is never rewritten to match an override. A well-formed uncovered tail remains visible as unsealed and has no effect; other overlay-chain problems refuse the detail query. Claimed authentication does not become authenticated assurance when reading files.

Run hash, full captured snapshot digest, trust-policy digest and exact view time identify the inputs. The snapshot digest can change when overlays change even though the run seal and index generation remain unchanged. The detail reader caps its serialized result at 2 MiB and refuses oversized responses as a whole; transport-envelope limits remain the host's responsibility. The selected result may contain captured content and must only reach authorized evidence consumers. Blob bytes are not returned by this query, and a reference in sealed JSON is not permission to fetch a withheld blob.

A missing result is NotFound, an invalid selection or view time is InvalidRequest, and capture/response budget refusal is ResourceLimit. Damaged evidence or an invalid overlay chain is Unavailable. These queries expose evidence and effective state; parents, gate decisions and summaries are not recomputed, and score eligibility/statistical comparison remain distinct work.

## Requirement status queries

`IGovernanceQueryService.Status(subject, sourceIds, at)` computes external requirement status for an exact subject reference/version at an explicit view time. The host configures the validated catalogue, trusted keys and scoped waiver authority. Requests select source IDs through the same configured-root policy used by suite and detail queries; they cannot change the catalogue or grant permissions.

The service captures each selected run into a bounded owned copy and delegates verification to `IRequirementEvidenceReader` and computation to `IRequirementStatusResolver`. It accepts at most eight selections and 64 MiB of aggregate captured files, caps result-row × requirement evaluations at 100,000, and caps the serialized status report at 2 MiB. Limits refuse the whole report. Selections must share an index instance/generation; changes require reselection. Duplicate source IDs are read once; the existing resolver also deduplicates matching run hashes and rejects contradictory overlay snapshots.

An empty evidence selection produces an Available report with explicit `not_measured` requirements, not implicit discovery of recent evidence. The report retains catalogue/policy digests, exact selected hashes, overlay identities, sealed/effective observations, scoped waiver history, reviews and explanations. A signed waiver only changes status when its signer has the configured requirement-specific permission and its expiry is active. Identity compatibility is not substituted for these governance rules.

The resource caps bound input and computation size, not a wall-clock deadline for synchronous verification. Snapshot capture and resolver checks observe cancellation between operations. Host transport/authentication integration and additional limit-boundary acceptance remain separate work.
