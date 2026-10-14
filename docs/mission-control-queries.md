# Shared Mission Control queries

## Implemented UI and desktop scope

The governance views described here are part of the existing React web app,
served by `agenteval mc serve`. They cover evidence browsing, requirement status,
decision history and verification of configured checkpoints. They do not provide
a native desktop application or checkpoint authoring and execution orchestration.

`AgentEval.MissionControl.Core` supplies transport-independent services that a
future desktop client can call in-process. Avalonia Studio, desktop packaging,
native accessibility and desktop acceptance tests are not implemented by this
milestone. Web browser acceptance does not establish desktop readiness.

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

This library supplies shared contracts, evidence pages, verified suite comparison, result detail and governance services described below. GraphQL exposes the indexed pages and verified suite, result-detail, governance and checkpoint queries. Notification delivery and transport-parity acceptance are implemented as described below. Existing legacy endpoints are preserved.

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

`AgentEval:Queries:GovernanceLimits` can lower `MaxSources` (default/ceiling 8), `MaxCaptureBytes` (64 MiB), `MaxRowRulePairs` (100000) and `MaxResponseBytes` (2 MiB). The shared service validates ceilings and enforces them before returning a report. Separate host-wide response/operation limits cover alias-multiplied transport envelopes; see the HTTP budgets and contract acceptance below.

## Contract acceptance

The reviewed GraphQL contract for the eleven shared query fields is pinned in `tests/AgentEval.Tests/MissionControl/EvidenceSchema.txt`. Its HTTP introspection test walks every reachable output/scalar/enum type and records field signatures, argument defaults and nullability. A schema change requires reviewing this artifact, including whether new reachable fields expose storage details or alter numeric precision. Setting `AGENTEVAL_SCHEMA_CAPTURE` to a local output path captures the current contract for review; it does not update the expected snapshot or bypass comparison. Complete payloads for the original nine query fields are compared between direct service calls and HTTP over actual indexed/sealed/OTLP fixtures. Only documented index-identity naming and exact integer-to-string adaptations are applied. Cases retain suite compatibility explanations, sealed/effective result detail, observation conflicts and signed waiver authority/refusal provenance. Typed unavailable, empty, missing, invalid, stale and resource-limit outcomes are also compared for indexed pages. HTTP envelope tests include real Kestrel response-cap refusal without partial data. Streaming acceptance stalls writes and flushes to verify deadline cancellation and subscriber-slot recovery.

## Shared change notifications

`IEvidenceChangeFeed` is the transport-free subscription boundary; `IEvidenceChangePublisher` is the host-owned producer. The `EvidenceChangeFeed` implementation defaults to 32 subscribers with 16 queued notifications each, with fixed configurable ceilings of 256 subscribers and 1024 queued items per subscriber. A subscription attempt returns null when all slots are occupied. Consumers must dispose subscriptions; cancellation and completion of the single-reader enumeration release the slot automatically.

Every subscription starts with a Connected resync hint. Changed and Reconcile hints tell clients to repeat authoritative queries. When a queue fills, its pending items are replaced by an explicit Overflow hint with the latest sequence. Publishing does not wait for slow consumers. There is no replay log: reconnecting always requires resynchronization. Epoch and sequence are process-local notification ordering, never an index generation or evidence snapshot identity. Notifications carry no evidence content, paths or authorization grants. Shutdown ends subscriptions and discards their pending hints.

Enable host delivery with `AgentEval:Queries:Notifications:Enabled=true` alongside enabled queries and an explicit index path. `GET /api/v1/evidence/events` streams `changed` or `resync` SSE events; disabled delivery returns 503 and exhausted subscriber slots return 429 with Retry-After. Every connection starts with resync, including requests with Last-Event-ID: IDs are not replay cursors. JSON payloads contain only epoch, decimal-string sequence and reason. Clients must refresh authoritative queries on either event type.

The host performs one bounded, validated source-page query every `PollSeconds` (default 5, range 1..300). A changed index identity, generation or availability state emits a Changed hint. Every `ReconcileEvery` ticks (default 6, range 1..60) it emits Reconcile even if the index revision is unchanged. This covers overlay changes that are not reflected in the index and transitions between polls; it does not provide an exact event history or immediate filesystem watching. Detection latency includes query duration; synchronous SQLite validation does not have a hard deadline.

The same Notifications section configures MaxSubscribers (default 32, ceiling 256), BufferCapacity (16, ceiling 1024), LifetimeSeconds (600, ceiling 3600) and WriteSeconds (10, ceiling 60). Each must be at least 1. Connections close at their lifetime and clients reconnect/resync. Writes use cancellation deadlines; shutdown cancels streams and completes the feed. Queue limits bound notification buffering. The read-access boundary below also applies to SSE. Tests verify cancellation of stalled writes and flushes, subscriber recovery, connection lifetime and host shutdown.

## Enabled read-access boundary

When `AgentEval:Queries:Enabled=true`, the host protects all `/graphql` routes (including legacy fields and introspection) and `/api/v1/evidence` routes before invoking their handlers. This avoids authorization based on parsing individual GraphQL field names. Unrelated endpoints and the legacy query-disabled host retain their existing behavior.

Without `AgentEval:Queries:ReadApiKey`, access requires an actual loopback peer and a Host of localhost or a loopback IP address. Missing peer information, remote peers and non-loopback Host names are refused. A cross-origin Origin or cross-site/same-site Sec-Fetch-Site is refused; same-origin browser requests and non-browser requests remain supported. These checks prevent a local unauthenticated read service from accepting a DNS-rebinding Host.

Setting ReadApiKey requires an exact Bearer key on every protected request, including loopback. Keys must contain 32..256 printable ASCII characters without spaces; supply a random secret through host configuration rather than a URL. Key hashes are compared in constant time. Remote requests additionally require HTTPS; direct loopback requests may use HTTP. A valid key grants access to all evidence roots configured for this single host: this is not a multi-user or per-root authorization system. SSE clients with a key should use streaming fetch with an Authorization header; native EventSource cannot attach that header.

The host does not trust X-Forwarded-For or X-Forwarded-Proto. Do not expose a keyless host through a proxy that rewrites its peer and Host to loopback. Proxy deployments must require a read key and preserve an authenticated TLS boundary; trusted forwarded-header integration is not added by this feature. Rejected requests return 401 for missing/invalid configured credentials or 403 for origin/transport/locality failures, with no evidence data. Protected responses use no-store.

## GraphQL HTTP envelope budgets

With evidence queries enabled, GraphQL POST operations and GET operations with a query string pass through HTTP budgets after access checks. Configure `AgentEval:Queries:HttpLimits`:

| Setting | Default | Allowed range |
|---|---:|---:|
| RequestBytes | 262144 | 1024..1048576 |
| QueryStringBytes | 65536 | 1024..262144 |
| ResponseBytes | 8388608 | 1024..33554432 |
| Concurrency | 4 | 1..32 |
| RequestSeconds | 30 | 1..120 |

The host reads at most RequestBytes plus one detection byte before GraphQL parsing, including requests without Content-Length. Oversized bodies or UTF-8 query strings return 413. A busy concurrency gate returns 429 without waiting in an unbounded queue. The request deadline covers body reading, execution and response delivery through cooperative cancellation; it cannot forcibly interrupt synchronous SQLite validation.

Responses are buffered under the serialized-byte cap, including aliases and JSON escaping. A response that exceeds the cap is discarded and replaced by a bounded 503 `response-byte-limit` error, without partial evidence JSON. Buffer memory is finite and tied to the concurrency limit; this cap is separate from shared services' logical page/report limits. A deadline before headers are sent returns 408; cancellation after response delivery starts aborts the connection. Slots are released on success, refusal or cancellation.

Plain Nitro browser documents and assets retain their existing delivery path. GraphQL WebSocket upgrades are refused while these budgets are enabled; use bounded HTTP queries and the dedicated SSE invalidation feed. Query-disabled legacy deployments retain their previous behavior. The existing GraphQL cost/depth checks still apply in addition to these envelope limits.
## Governance workspace in the web app

Open **Governance** in Mission Control's sidebar (`/governance`) to check the
configured evidence connection and browse source inventory. This view uses
`evidenceSources` from the shared query service. Its index is configured on the
server independently of the legacy workspace initialization screen.

Filter by a logical root ID, or leave it blank for all configured roots. The
page shows up to 25 sources, the index instance and exact generation values.
Use **Next page** to continue; if the index changes, **First page** restarts the
traversal. **Refresh** checks the current selection again. Integrity labels
describe indexed source verification, not evaluation success or compliance.

An empty result is distinct from an unconfigured, unavailable or refused query.
Failed refreshes hide previously cached inventory. The app uses its configured
API endpoint and existing host access boundary; it does not register filesystem
roots or provision remote credentials.

Select a source to browse its indexed result rows. Aggregate case absence,
undeclared trials and unmeasured outcomes remain explicit. AEF result IDs open
a freshly verified detail view under the host's trust policy. The view displays
sealed and effective states separately, review events, established assurance,
unsealed event counts, and run/snapshot/policy digests. The UTC view time starts
at selection time and remains fixed until edited; **Re-verify** reloads that
same view time. If the selected detail uses a different index revision from
the result list, a warning asks the user to refresh the list. Failed verification
hides previously displayed detail. Sealed JSON is displayed as text.

The source view also provides **Metrics**, **Spans**, and **Fields**. Each query
loads at most 25 rows with independent revision-bound pagination. Metrics retain
finite scores, exceptional score text, nullable pass flags and occurrence
provenance; the browser does not derive a pass rate. Observation sources open
directly to spans, preserving exact nanosecond timestamps and grouping/mapping
metadata. Fields preserve conflict states and exact integer strings, with
expandable original source provenance. These observations do not imply an
evaluation verdict. Views inappropriate for a source family show the host's
typed refusal instead of fabricating an empty result.

Open **Requirements status** from the inventory, or **Requirements for this
source** from a source view. Enter the exact subject reference and version,
UTC view time and one evidence source ID per line. Submitting resolves the
host-configured catalogue and trust policy through `governanceStatus`.
Duplicate source IDs are removed; a blank list explicitly selects no evidence.
The browser does not discover a latest run or infer a subject/version.

The report shows base, effective and final statuses, selected observations,
diagnostics and reviews. Catalogue/policy digests and selected run/overlay
identities identify its inputs. Editing the form does not relabel the existing
report: submitted inputs remain visible until another request is submitted.
Loading, refused and failed requests hide the old report. An unavailable
query is not a missing requirement or a successful evaluation.

Expand a requirement's **Waiver history** to inspect every verified waiver
retained in its report, including inactive and unauthorized entries. Each entry
shows its exact run/requirement scope, event, declared identity, established
assurance, expiry, activity at the report time, and scoped authorization.
Inactive does not necessarily mean expired: a waiver may also begin after the
selected view time. A signed event still requires the configured permission
for that requirement. The final status remains the resolver's decision, and
sealed measurements remain unchanged.

Verified result detail includes a **Verified decision history** timeline for
overrides, adjudications and reviews targeting the selected result, plus
run-level reviews. It retains superseded decisions in overlay file order and
shows the original line, declared timestamp, identity and reason alongside
the verifier's established assurance. Timestamps do not reorder events.
Current markers identify the events supplying the effective state or latest
review for each target; they do not imply signature authority or change the
sealed run. Annotations and unrelated result decisions are not part of this
timeline. Unsealed events remain a separate count with no effective history.
The complete result detail, including history, remains subject to the 2 MiB
response limit; oversized detail is refused without a partial timeline.

### Checkpoint verification adapter

`IBoundedCheckpointReader` in Indexing wraps the existing checkpoint verifier
for explicit server-resolved run selections. It captures ordinary files into
private snapshots, checks each selected run hash and integrity, and combines
only those snapshots. It does not discover sibling runs. Limits are eight
selections, 64 MiB aggregate capture, 1 MiB each for manifest and envelope,
64 lanes, 100,000 result-row/lane pairs and 2 MiB serialized explanation.

The detached explanation retains exact manifest and policy digests, the
requested time and actual evaluation time, original manifest declarations,
recomputed lane evidence, verification problems, established signers and
anchored run hashes. Decided checkpoints use their recorded evaluation time.
Envelope presence, whether verification was attempted, envelope failures and
per-signature results are separate fields. A passing lane is not checkpoint
approval, and an available explanation is not proof of trust. The host exposes
this adapter through the checkpoint queries below and the browser's
`/governance/checkpoints` view. Choose a configured checkpoint, explicit source
IDs and UTC view time, then submit to inspect its explanation. Draft edits do
not change the submitted report. Refreshes and refusals hide cached reports.

The view separates declared decisions from recomputed lane evidence and
signature trust. Expand each lane for rule parameters and evidence references.
The original manifest remains available as escaped JSON, preserving exact
integers; unsafe JavaScript integers in parameter summaries point to that
original representation instead of displaying rounded values.

`ICheckpointQueryService` provides the shared query boundary over this adapter.
The host supplies at most sixteen `ConfiguredCheckpoint` objects, which copy
validated manifest/envelope bytes and expose path-free descriptors. `List`
returns those declarations; `Explain` accepts a configured ID, up to eight
source IDs and an exact UTC time. It reuses configured evidence-root bindings,
deduplicates sources, refuses mixed index revisions, and returns the selected
IDs and revision beside the explanation. An empty selection has no index
revision and can produce explicit missing-run verification problems. Query
availability does not imply that the checkpoint passed verification.

Browser acceptance: build `src/AgentEval.MissionControl.Spa`, then run
`node scripts/mc-browser-smoke/governance.mjs`. Set `PLAYWRIGHT_CHANNEL=msedge`
to use installed Edge. This checks controlled API responses in a real browser;
`SpaQueriesValidateTests` checks the actual SPA operations against the production
GraphQL schema. `governance-live.mjs` exercises the production host with actual
indexed evidence, signatures, waiver expiry and tamper refusal; see the
[browser acceptance instructions](../scripts/mc-browser-smoke/README.md), including
how to target an installed CLI package.


### Checkpoint GraphQL configuration

Checkpoint configuration is independent of `CataloguePath`. With queries enabled,
an explicit index path and configured AEF roots, add up to sixteen entries under
`AgentEval:Queries:Checkpoints`:

```json
[
  {
    "Id": "release",
    "ManifestPath": "/absolute/path/release.json",
    "EnvelopePath": "/absolute/path/release.dsse.json"
  }
]
```

Paths are host configuration and must be absolute. `EnvelopePath` is optional.
Manifest and envelope files are each limited to 1 MiB and copied at startup;
changing files requires host reconfiguration/restart. The existing
`TrustPolicyPath` supplies verification authority. Missing checkpoint configuration,
disabled queries, or absent AEF roots yields `NOT_CONFIGURED`.

`checkpoints` returns bounded configured declarations. Call
`checkpointExplanation(checkpointId, sourceIds, at)` with the configured ID,
explicit indexed source IDs and UTC time to get the verified explanation.
Index generations are nullable decimal strings; an empty source selection has
no index revision. Signature results are GraphQL enums (`VERIFIED`,
`UNTRUSTED_KEY`, `INVALID`, `UNSUPPORTED_ALGORITHM`), separate from envelope
presence/failure and checkpoint anchors. Complete reports preserve the shared
service data, with only generation-to-string and enum-name transport changes.
Both fields use the existing GraphQL read-access and HTTP resource limits;
explanation has an operation cost of 500 and listing 100.
