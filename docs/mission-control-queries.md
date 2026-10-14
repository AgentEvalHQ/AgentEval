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

This library supplies shared contracts, evidence pages, verified suite comparison, result detail and governance services described below. GraphQL exposes the indexed pages and verified suite, result-detail and governance queries. Notification delivery and transport-parity acceptance remain integration work. Existing legacy endpoints are preserved.

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

The service captures each selected run into a bounded owned copy and delegates verification to `IRequirementEvidenceReader` and computation to `IRequirementStatusResolver`. It accepts at most eight selections and 64 MiB of aggregate captured files, caps result-row × requirement evaluations at 100,000, and caps the serialized status report at 2 MiB. Hosts may lower these ceilings through `GovernanceQueryLimits`; invalid or higher limits refuse configuration. Limits refuse the whole report. Response-size checking streams into a counting sink and stops at the cap instead of allocating an oversized serialized report. Selections must share an index instance/generation; changes require reselection. Duplicate source IDs are read once; the existing resolver also deduplicates matching run hashes and rejects contradictory overlay snapshots.

An empty evidence selection produces an Available report with explicit `not_measured` requirements, not implicit discovery of recent evidence. The report retains catalogue/policy digests, exact selected hashes, overlay identities, sealed/effective observations, scoped waiver history, reviews and explanations. A signed waiver only changes status when its signer has the configured requirement-specific permission and its expiry is active. Identity compatibility is not substituted for these governance rules.

The resource caps bound input and computation size, not a wall-clock deadline for synchronous verification. Snapshot capture and resolver checks observe cancellation between operations. Acceptance tests cover exact aggregate-byte and response-byte boundaries, cross-run work limits, changed selection revisions, conflicting overlay snapshots of the same seal, invalid host limits, signed scoped waiver authority and expiry. The host read-access boundary below applies to the GraphQL transport.

## GraphQL indexed evidence pages

The additive `evidenceSources(rootId, pageSize, cursor)` field delegates to `IEvidenceQueryService`. It returns typed `state`, `code` and optional `data` with index identity, generation, items and continuation. Generation and indexed-generation values are decimal strings, preserving Int64 precision in JavaScript. Source items expose indexed provenance and relative paths, never persisted physical binding directories.

The capability defaults to NotConfigured. Enable it with `AgentEval:Queries:Enabled=true` and an absolute `AgentEval:Queries:IndexPath` pointing at an existing derived index. Reads do not initialize, migrate or rebuild it; a missing index returns Unavailable/missing-index. Existing host depth, cost and request-timeout settings remain in effect; each resolver has an explicit cost weight.

`evidenceResults`, `evidenceMetrics`, `evidenceSpans` and `evidenceFields` take an explicit `sourceId` plus optional `pageSize` and `cursor`. Each delegates to one shared bounded query and returns index revision, source kind, verification and projection fingerprint with its items. All Int64 values (trial, report ordinal, coordinates and token values) are decimal strings; unsigned nanosecond timestamps remain decimal strings. Metric ordinals are Int32 and finite scores remain numeric. Exceptional scores use `scoreText`; absent aggregate case IDs and unknown pass states remain null. Observation conflicts retain their state and `sourcesJson` provenance without inventing a consensus value. The provenance is JSON text; clients that parse its embedded integers must preserve their precision.

Pages distinguish Empty, NotFound, InvalidRequest, StaleCursor and ResourceLimit. Refusals return null data, never partial rows. A cursor is bound to the source, query family, page size and index revision. Results and metrics only accept evaluation sources; spans and fields only accept observation sources. These are indexed projections, not fresh seal verification or a telemetry-derived evaluation verdict. Governance is exposed separately over verified evidence, as described below.

## GraphQL verified suite identity

`evidenceSuite(sourceId)` and `evidenceSuiteComparison(baselineSourceId, candidateSourceId)` delegate to `ISuiteQueryService`. They return the same typed availability outcomes as shared queries. Comparison data includes compatibility, reason, description and verified identities on both sides. Identity includes the selected index revision, run seal hash/status, suite reference/version/digests and frozen flag. Definition content and physical storage paths are not schema fields. Index generations use decimal strings.

In addition to enabling queries and setting the index path, configure `AgentEval:Queries:AefRoots` as an array of `{ "RootId": "stable-id", "Directory": "absolute-existing-directory" }`. At most 256 ordinary AEF roots are accepted; duplicate IDs and unsafe paths refuse at startup. The shared service requires each selected source's persisted binding to match this configuration. Configuration does not create roots, bind index rows or repair historical unbound rows. Without configured roots, suite queries return NotConfigured. Disabling queries also disables suite reads regardless of root configuration.

Suite reads re-verify the selected seal. Tampering or binding changes return no identity. A comparison of completed and aborted runs can be compatible when their frozen definitions match; this does not establish completeness or score eligibility. Changed definitions and unfrozen recipes retain the shared evaluator's explicit incompatibility reasons. Each resolver has an operation-cost weight (250 for identity, 500 for comparison); the host's total cost limit still applies.

## GraphQL verified result detail

`evidenceResultDetail(sourceId, resultId, at)` delegates to `IResultDetailQueryService` under the same index and AEF-root configuration as suite queries. `at` is explicit RFC 3339 UTC text ending in Z, with up to nine fractional digits; it is never rounded through a JavaScript number or DateTime scalar. Invalid times return InvalidRequest when configured. Missing configuration returns NotConfigured.

The response includes sealed JSON and state, effective state, the selected override event, reviews, displayed assurance, unsealed-event count and exact view time. Run seal, full snapshot and policy digests identify the evidence and authority used. Index generation is decimal text. The service owns seal/overlay verification and response limits; the resolver only maps the result. Unsealed tails do not change effective state, and unsigned authenticated claims remain self-attested. Missing results, invalid evidence and resource limits return typed outcomes with null data. No blob bytes or physical directories are returned.

An embedding host may register a `TrustPolicy` in DI before requests; otherwise result-detail queries use the shared service's no-trusted-keys default. Request arguments cannot grant trust. Standalone hosts can configure the bounded JSON trust-policy file described below. The resolver's operation cost is 250, within the existing host-wide cost/depth limits.

## GraphQL governance and host authority

`governanceStatus(subjectRef, subjectVersion, sourceIds, at)` delegates to `IGovernanceQueryService`. Its typed outcome carries the detached `RequirementStatusReport`: catalogue/policy digests, exact view time, selected hashes, overlay identities, observations, sealed/effective statuses, diagnostics, reviews and scoped waiver history. The report records contain no filesystem locations or lazy evidence readers. Empty selection is an Available report with not_measured requirements, not discovery of a recent run. `at` uses the same exact UTC parser as result detail. The resolver has cost 500; the existing operation cost limit applies to aliases as well.

Configure `AgentEval:Queries:CataloguePath` with an absolute path to a JSON requirement catalogue, in addition to enabled queries, index path and AEF roots. Missing catalogue configuration gives NotConfigured. The host reads at most 1 MiB plus one detection byte and validates the catalogue at startup. CLI YAML catalogue support is unchanged; this host boundary accepts JSON. Files are read once, so configuration changes require a host restart.

`AgentEval:Queries:TrustPolicyPath` optionally supplies an absolute JSON trust-policy path under the same 1 MiB bound. The parsed policy is shared by governance and result detail. An embedding host's pre-registered `TrustPolicy` takes precedence; without either configuration, no signing key is trusted. Requests cannot provide a catalogue, policy, directory or waiver permission. Valid signatures and explicitly scoped `requirements:waive:<id>` authority are separate checks, and expired waivers do not change current status.

`AgentEval:Queries:GovernanceLimits` can lower `MaxSources` (default/ceiling 8), `MaxCaptureBytes` (64 MiB), `MaxRowRulePairs` (100000) and `MaxResponseBytes` (2 MiB). The shared service validates ceilings and enforces them before returning a report. Host-wide response/operation limits and transport parity remain part of W08 acceptance; the report cap does not claim to cap alias-multiplied transport envelopes.

## Contract acceptance

The reviewed GraphQL contract for the nine shared query fields is pinned in `tests/AgentEval.Tests/MissionControl/EvidenceSchema.txt`. Its HTTP introspection test walks every reachable output/scalar/enum type and records field signatures, argument defaults and nullability. A schema change requires reviewing this artifact, including whether new reachable fields expose storage details or alter numeric precision. Setting `AGENTEVAL_SCHEMA_CAPTURE` to a local output path captures the current contract for review; it does not update the expected snapshot or bypass comparison. Full governance reports are also compared between direct service calls and HTTP, including signed waiver authority and refusal provenance. Other query-family parity and the broader host-access/limit audit remain W08-06 work.

## Shared change notifications

`IEvidenceChangeFeed` is the transport-free subscription boundary; `IEvidenceChangePublisher` is the host-owned producer. The `EvidenceChangeFeed` implementation defaults to 32 subscribers with 16 queued notifications each, with fixed configurable ceilings of 256 subscribers and 1024 queued items per subscriber. A subscription attempt returns null when all slots are occupied. Consumers must dispose subscriptions; cancellation and completion of the single-reader enumeration release the slot automatically.

Every subscription starts with a Connected resync hint. Changed and Reconcile hints tell clients to repeat authoritative queries. When a queue fills, its pending items are replaced by an explicit Overflow hint with the latest sequence. Publishing does not wait for slow consumers. There is no replay log: reconnecting always requires resynchronization. Epoch and sequence are process-local notification ordering, never an index generation or evidence snapshot identity. Notifications carry no evidence content, paths or authorization grants. Shutdown ends subscriptions and discards their pending hints.

Enable host delivery with `AgentEval:Queries:Notifications:Enabled=true` alongside enabled queries and an explicit index path. `GET /api/v1/evidence/events` streams `changed` or `resync` SSE events; disabled delivery returns 503 and exhausted subscriber slots return 429 with Retry-After. Every connection starts with resync, including requests with Last-Event-ID: IDs are not replay cursors. JSON payloads contain only epoch, decimal-string sequence and reason. Clients must refresh authoritative queries on either event type.

The host performs one bounded, validated source-page query every `PollSeconds` (default 5, range 1..300). A changed index identity, generation or availability state emits a Changed hint. Every `ReconcileEvery` ticks (default 6, range 1..60) it emits Reconcile even if the index revision is unchanged. This covers overlay changes that are not reflected in the index and transitions between polls; it does not provide an exact event history or immediate filesystem watching. Detection latency includes query duration; synchronous SQLite validation does not have a hard deadline.

The same Notifications section configures MaxSubscribers (default 32, ceiling 256), BufferCapacity (16, ceiling 1024), LifetimeSeconds (600, ceiling 3600) and WriteSeconds (10, ceiling 60). Each must be at least 1. Connections close at their lifetime and clients reconnect/resync. Writes use cancellation deadlines; shutdown cancels streams and completes the feed. Queue limits bound notification buffering. The read-access boundary below also applies to SSE; the slow-transport and broader resource-limit audit remains part of W08-06.

## Enabled read-access boundary

When `AgentEval:Queries:Enabled=true`, the host protects all `/graphql` routes (including legacy fields and introspection) and `/api/v1/evidence` routes before invoking their handlers. This avoids authorization based on parsing individual GraphQL field names. Unrelated endpoints and the legacy query-disabled host retain their existing behavior.

Without `AgentEval:Queries:ReadApiKey`, access requires an actual loopback peer and a Host of localhost or a loopback IP address. Missing peer information, remote peers and non-loopback Host names are refused. A cross-origin Origin or cross-site/same-site Sec-Fetch-Site is refused; same-origin browser requests and non-browser requests remain supported. These checks prevent a local unauthenticated read service from accepting a DNS-rebinding Host.

Setting ReadApiKey requires an exact Bearer key on every protected request, including loopback. Keys must contain 32..256 printable ASCII characters without spaces; supply a random secret through host configuration rather than a URL. Key hashes are compared in constant time. Remote requests additionally require HTTPS; direct loopback requests may use HTTP. A valid key grants access to all evidence roots configured for this single host: this is not a multi-user or per-root authorization system. SSE clients with a key should use streaming fetch with an Authorization header; native EventSource cannot attach that header.

The host does not trust X-Forwarded-For or X-Forwarded-Proto. Do not expose a keyless host through a proxy that rewrites its peer and Host to loopback. Proxy deployments must require a read key and preserve an authenticated TLS boundary; trusted forwarded-header integration is not added by this feature. Rejected requests return 401 for missing/invalid configured credentials or 403 for origin/transport/locality failures, with no evidence data. Protected responses use no-store.
