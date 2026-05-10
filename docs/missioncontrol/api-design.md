# Mission Control API Design — REST + GraphQL Hybrid

This is the operational companion to [plan-07 §3 Challenge 1](../../strategy/FutureFeatures/todo/07-AgentEval-MissionControl-Design.md#3-where-we-depart-from-the-master-analysis-seven-challenges) where the hybrid REST + GraphQL split was decided.

---

## TL;DR

| Concern | Endpoint | Why |
|---|---|---|
| **Reads** (dashboard, compliance matrix, recursive `EvalResult` tree, evaluator registry) | `POST /graphql` (single endpoint) | Recursive trees, sparse selection, schema-driven UI |
| **Ingest / writes** | `POST /api/v1/runs` (REST; idempotent) — Mode C (deferred) | Flat records, content-hash audit chain, retry semantics |
| **Binary downloads** | `GET /api/v1/.../report.pdf`, `GET /api/v1/runs/{id}/trace` | Streams |
| **Live updates** | SSE (Mode A/B) / SignalR (Mode C) — **deferred** | Read-only push (Mode A/B) vs bidirectional (Mode C) |

> Phase 1 ships the read surface only: GraphQL at `/graphql`, the REST binary
> + version surface listed below, and the SPA. Ingest (`POST /api/v1/runs`),
> live updates (SSE + SignalR), and auth land with Mode C in a later phase.

This isn't exotic — GitHub does it (REST v3 + GraphQL v4), Stripe does it (REST primary + GraphQL for dashboard), Shopify does it.

---

## Three reasons the read path wants GraphQL

### 1. The recursive `EvalResult` tree is graph-shaped

`EvalResult.SubResults` is `IReadOnlyList<EvalResult>?` — composites contain composites contain atoms. REST has three bad answers:

- **Send the whole tree always** — expensive on large composites (a GDPR full-calibration tree is ~50 KB JSON × dozens of scenarios).
- **Add `?depth=N`** — congratulations, you reinvented GraphQL's field selection, badly.
- **Provide separate "leaf"/"branch" endpoints** — over-engineered.

GraphQL handles this with **one fragment**:

```graphql
fragment EvalResultRecursive on EvalResult {
  metric { key name }
  score { value passed }
  details {
    subResults { ...EvalResultRecursive }   # ← recursive!
  }
}
```

One round-trip, exactly the shape the UI wants.

### 2. The compliance matrix is genuinely 1+N+M

Subjects (N) × controls (M) × evidence per cell. A REST fat-endpoint works at v1 scale (~50×20=1,000 cells, ~200 KB), but at "100 subjects × 50 controls" becomes a 1 MB response where the UI uses 80% of it.

GraphQL's sparse selection means the matrix overview asks for `{status, passRate}` while the detail drill-down asks for `{lastEvidence{...}}` — same backend, different shape per view.

### 3. Schema-driven UI is GraphQL's exact selling point

The `EvaluatorCard` JSON files (one per evaluator) drive the registry view automatically. With GraphQL, the schema *is* the registry; codegen produces typed React hooks; introspection drives the registry view. Without GraphQL, we'd ship a separate `EvaluatorCard` registry endpoint AND bespoke `?fields=` projection AND hand-rolled typed React hooks. We were already paying the cost of formalising metadata; GraphQL gives us the runtime for free.

---

## Three reasons the write path stays REST

### 1. Ingest is not graph-shaped

`POST /api/v1/runs` is a flat record write with `(solutionId, runId)` idempotency, content-hash validation, and audit-chain enforcement. GraphQL mutations work but add zero value here. HTTP idempotency keys, retry semantics, and the standard `409 Conflict` flow are REST-native.

### 2. PDF / trace / large-blob streaming

`GET /api/v1/.../report.pdf` and `GET /api/v1/runs/{id}/trace` are byte streams with content-type negotiation. GraphQL doesn't do streams cleanly. Apollo's file-upload spec exists but is grafted on. REST is the right answer for binary endpoints.

### 3. HTTP caching

CDN / browser / proxy caching of `GET` requests is automatic and free. GraphQL queries are mostly POST + non-cacheable (Apollo persisted queries fix this but add complexity).

---

## Operational costs of GraphQL (honest read)

- **Self-hoster debugging** is harder than `curl /api/v1/recent`. They need GraphiQL or to know the schema. Mitigation: ship Hot Chocolate's embedded Nitro UI at `/graphql` (off in production).
- **Query-depth and -complexity limits** are required to prevent "give me 1000-deep recursion" attacks. Hot Chocolate ships these (`MaxAllowedExecutionDepth`, complexity analyzer); Mission Control configures `MaxAllowedExecutionDepth = 10` (raised from 8 in Wave 8a to accommodate the SPA's 3-level scenario-tree drill-down at depth 9; deeper attack queries are still rejected).
- **N+1 resolver risk** is real. Mitigation: GreenDonut (DataLoader port for .NET, ships with Hot Chocolate) handles batched per-request loading. Deferred to MC1.4.7 — `Query.evaluatorTimeline` is the documented N+1 candidate, currently bounded by its own `maxScan: 200` cap.
- **Persisted queries** become real work if we want HTTP caching of GraphQL responses. Deferred to v1.6+.

---

## REST surface (`/api/v1/*`)

```
# Shipped today — server metadata + binary streams (Mode A)
GET    /api/v1/version
GET    /api/v1/runs/{runId}/trace
GET    /api/v1/runs/{runId}/reports/{format}            # markdown / html / junit / sarif
GET    /api/v1/compliance/{regulation}/{subject}/{ts}/report.pdf
GET    /api/v1/compliance/{regulation}/schema
GET    /api/v1/subjects/{kind}/{name}/history           # application/x-ndjson stream

# Deferred — Mode C ingest (Phase 2)
POST   /api/v1/runs                                      # idempotent on (solutionId, runId)
POST   /api/v1/runs/bulk
POST   /api/v1/runs/{runId}/trace                        # multipart, large
POST   /api/v1/compliance/evidence
POST   /api/v1/red-team/campaigns

# Deferred — Mode C auth (Phase 2)
POST   /api/v1/auth/tokens
GET    /api/v1/auth/tokens
DELETE /api/v1/auth/tokens/{id}
GET    /api/v1/auth/me

# Deferred — Mode C admin (Phase 2)
GET    /api/v1/admin/audit-log
DELETE /api/v1/solutions/{id}/data?subject={name}        # right-to-be-forgotten

# Deferred — live updates (Mode A/B SSE + Mode C SignalR)
GET    /api/v1/events?topics=                            # SSE; not implemented in Phase 1
WS     /hub/notifications                                # SignalR; Mode C only
```

The shipped block above matches `src/AgentEval.MissionControl/Rest/BinaryEndpoints.cs` and `McHost.cs` exactly.

---

## GraphQL surface (`POST /graphql`)

Schema is auto-discovered from C# records (`src/AgentEval.MissionControl/GraphQL/Query.cs`). Top-level resolvers (Phase 1):

```
Query.solution                                 SolutionInfo?
Query.workspace                                WorkspaceState!     ← drives first-run landing
Query.subjects(kind?)                          [SubjectInfo!]!
Query.subject(kind, name)                      SubjectInfo?
Query.recentRuns(count = 50)                   [RunPointer!]!     ← max 500
Query.run(runId)                               RunManifest?
Query.runSummary(runId)                        RunSummary?
Query.runCostBreakdown(runId)                  RunCostBreakdown?  ← per-tier cost
Query.scenarios(runId)                         [ScenarioResult!]!
Query.scenario(runId, scenarioId)              ScenarioResult?
Query.scenarioTree(runId, scenarioId)          EvalResult?        ← recursive!
Query.compliance                               [ComplianceRegulationSummary!]!
Query.complianceMatrix(regulation)             ComplianceMatrix!  ← killer feature
Query.complianceEvidence(reg, kind, name, ts)  ComplianceEvidence?
Query.evaluators(category?, costTier?)         [EvaluatorCard!]!
Query.evaluator(key)                           EvaluatorCard?
Query.evaluatorTimeline(key, count = 30)       [EvaluatorTimelinePoint!]!  ← drift / calibration surface
Query.ping / Query.agentEvalVersion             smoke
```

In Phase 2 the schema extends with `Query.workspaces`, `Query.search`, `Query.crossRegulationOverlap`, `Query.redTeamCampaigns`, etc. — see plan-07 §8.1 for the full forward-looking shape.

---

## Versioning policy

- **REST**: `/api/v1/`. Major bumps (`/api/v2/`) only on breaking schema change. Additive changes don't bump.
- **GraphQL**: no version segment in the URL. Schema evolves via `@deprecated` directives on fields that are moving out; clients keep working until they migrate. Breaking-removal of a field is a coordinated release-note event.

Both surfaces share the data model; you cannot get *different* data shapes from REST vs GraphQL — they're views over the same `IOutputStoreReader` reads.

---

## See also

- [Plan-07 §3 Challenge 1](../../strategy/FutureFeatures/todo/07-AgentEval-MissionControl-Design.md#3-where-we-depart-from-the-master-analysis-seven-challenges) — original REST + GraphQL hybrid decision.
- [Plan-07 §8](../../strategy/FutureFeatures/todo/07-AgentEval-MissionControl-Design.md#8-api-surface-rest--graphql-hybrid) — full route inventory + sample queries.
- [`getting-started.md`](getting-started.md) — sample queries to get going.
