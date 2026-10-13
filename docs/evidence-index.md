# Derived SQLite evidence index

`AgentEval.Indexing` supplies the versioned SQLite schema for the shared evidence
index. It targets .NET 8, 9 and 10 and keeps SQLite dependencies outside the
BCL-only `AgentEval.Results` project. Source files remain authoritative; this
database is derived state, not an alternative evidence seal or acceptance store.
Transactional source projection, AEF snapshot imports and verified observation
imports are implemented. Legacy adapters, rebuild orchestration and query
pagination are subsequent slices.

The provider is pinned to
[Microsoft.Data.Sqlite 10.0.12](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12),
whose package targets .NET Standard 2.0. The provider's bundled SQLite native
library is exercised by tests on Linux and Windows. Other deployment platforms
have not been tested by this change.

## Schema initialization and compatibility

```csharp
IEvidenceIndexSchema schema = new SqliteEvidenceIndexSchema();
var info = schema.EnsureCurrent(new EvidenceIndexOptions
{
    DatabasePath = absoluteDedicatedDatabasePath
});
```

The path must be absolute and dedicated to derived storage. Existing linked
path components and non-regular files are refused; Unix requires the existing
native file classifier. The filesystem owner must prevent concurrent path
replacement by untrusted local processes. A sidecar ownership lease excludes
concurrent schema operations, and SQLite locks govern the database transaction.

Version one initializes only an empty database with zero application/version
identifiers and no user schema. Initialization writes the schema, migration
script digest, instance ID and generation zero in one transaction. Failure,
cancellation before commit or an exceeded size budget rolls back the migration.
An empty file or sidecar can remain after unsuccessful initialization; no source
evidence is deleted. This uses SQLite's atomic transaction behavior, as described
in the [provider documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions).

Existing databases must match the application ID, supported user version,
compiled schema fingerprint and migration digest. SQLite quick-check and foreign
key checks must pass, and the index metadata must exist. Unknown versions,
unrelated databases, altered tables/indexes and corrupt files are refused without
automatic repair. Version one is the first migration; no older product schema
is silently adopted. Future migrations must be explicit transactional steps.
The schema fingerprint detects drift; it does not authenticate a database against
an attacker who controls its files.

Startup checks the database file/page size against `MaxDatabaseBytes` (default
512 MiB, allowed 64 KiB–64 GiB). This is currently a startup/migration budget;
the writer also enforces it through SQLite's page cap and before commit. It excludes journal
and sidecar bytes. SQLite lock waits default to two seconds, configurable from
one to thirty. Cancellation is checked between synchronous SQLite operations;
individual database integrity scans are not preemptible.

## Queryable evidence layout

| Table | Purpose |
| --- | --- |
| `index_metadata`, `schema_migrations` | Instance/generation identity and migration provenance |
| `sources` | Root-relative location, evidence kind, content digest and verification state |
| `evaluation_runs` | AEF or legacy run identity, subject, suite, outcome and explicit completeness |
| `evaluation_results`, `metric_values` | Per-case/trial outcomes and metric values with provenance |
| `observation_batches` | Receiver source, capture policy and omission report |
| `observed_spans` | Session/group provenance and independently retained span reports |
| `observed_fields` | Typed normalized values, ambiguity state and reported sources |
| `catalogue_entities` | Catalogue definitions keyed by catalogue digest and entity identity |

Composite foreign keys prevent observation sources from becoming evaluation
runs. Observations and legacy records cannot claim intact AEF verification.
Deleting a source cascades through its derived rows without affecting files.
Duplicate span identities use distinct report ordinals; the schema does not
choose an arrival-order winner. Conflict/invalid fields cannot carry a consensus
value. Integers retain signed 64-bit precision, while unsigned nanosecond times
use twenty-digit decimal text for exact representation and lexical ordering.
Adapters must validate their unsigned range before insertion.

Indexes cover source discovery, subjects, suites, cases, metrics, trace identity
and ordered group members. These are the schema foundations for stable queries;
they do not yet constitute a pagination API or a measured scale guarantee.

## Atomic initial and incremental writes

`IEvidenceIndexWriter` accepts immutable `EvidenceIndexSource` projections from
trusted adapters. Its `Upsert` transaction replaces all rows belonging to one
root-relative source and updates its checkpoint and the global generation
together. Constraints, budget failures and cancellation before commit roll back
the entire replacement, preserving the previous indexed snapshot. A projection
is not a verifier: only an adapter that actually verifies evidence may set AEF
integrity to `Intact`. The writer rejects that state for legacy/observation input.

Source identity hashes an unambiguous root-namespace/relative-path tuple. The
`sources.content_digest` checkpoint is a **projection fingerprint**, combining
the consumed source snapshot's digest, kind, verification and actual projected
rows. It is not the original AEF seal or observation-batch hash. This prevents
changed projections from being skipped when adapter behavior changes but source
bytes do not. Result/metric ordering is canonicalized; observation report order
and source positions remain evidence. Identical projections retain the current
generation; changed projections advance it once. Reopening the writer preserves
idempotence.

`Remove` requires the last observed checkpoint fingerprint and refuses a stale
one. It deletes only derived rows. Scan coordinators must establish complete
source coverage before deciding a source disappeared; a partial or failed scan
must never authorize deletion. A retained previous projection means “last indexed
snapshot,” not a guarantee that current source files still match it.

The writer holds the sidecar ownership lease for its lifetime and refuses
concurrent operations rather than queueing. Defaults are 100000 rows per source
(including the source row), 16 MiB aggregate projection text, 100000 retained
sources, and 1000000 total source-owned rows. Configurable maxima are respectively
1000000, 64 MiB, 1000000 and 10000000. The byte limit bounds projected text; it is
not an exact managed-memory ceiling. Text/provenance serialization and synchronous
SQL commands are bounded work, with cancellation checks between operations.

`IOtlpObservationIndexer` takes a captured inventory from an existing
`IOtlpObservationStore`, reads each selected record through its digest verifier,
and commits each projection independently. It uses the stored normalized
provenance rather than silently re-normalizing old observations. Token integers,
conflicting alias candidates, omission reports and separate span reports survive
projection. Prompt/output content is not copied into index rows.

Imports default to 4096 records per call (maximum 100000). The result reports
inventory count, processed/changed/unchanged counts, completeness and path-free
per-record issues. Corrupt/unavailable records leave prior indexed rows intact;
capacity or busy failures stop further work and report incomplete coverage.
Earlier per-source commits survive a later failure or cancellation. No pruning
or source-file deletion is performed by this importer.

## AEF snapshots and scan coordination

`IAefIndexSourceReader` captures a candidate run into a private temporary
directory under finite file/byte limits, then runs the existing AEF and overlay
chain verifiers against that copy. Projection reads the same owned copy, never
the original files after verification. Captured files are copied as regular
files, with exact-size checks; temporary storage is removed after projection or
failure. The source filesystem owner must prevent hostile path replacement.
This is a detached verified byte set, not a filesystem-wide point-in-time snapshot.

The reader uses the existing staging defaults: at most 1000 files, 64 MiB total
and 64 MiB per file, configurable through `AefStagingOptions`. Preflight/copy and
projection observe cancellation between bounded operations. The protocol verifier
is synchronous and must finish its current verification phase before cancellation
can be reported. Scratch bytes are additional to the derived database budget.

Sealed intact runs retain the verified run hash. Valid unsealed runs remain AEF
sources with `Unverified` integrity and no sealed hash. Invalid runs and invalid
overlay chains are refused. Snapshot fingerprints cover every captured file,
including signatures and overlays; intact base integrity does not establish
trusted signer identity or authorized overlay effects.

Result states, case/trial IDs, metric values and annotator provenance are copied
as reported. The scalar index does not invent metric pass flags or a global run
verdict. Producer `running`/`aborted` runs are partial; `completed` remains unknown
for benchmark completeness. Verified suite-definition digests are lookup fields,
not proof of frozen-suite comparability: callers must still apply the suite
compatibility service. Hierarchy, labels, lane summaries and effective overlay
decisions remain in authoritative source documents for detailed queries.

`IAefEvidenceIndexer` combines bounded `IAefRunDiscovery`, snapshot projection and
the transactional writer. The result records changed/unchanged counts and relative
path issues, and reports incomplete coverage if discovery omitted entries or any
candidate failed. Previously indexed snapshots remain available after failures,
but do not claim that the current source is valid or fresh.

Optional `pruneMissing: true` reconciles deleted AEF sources only after a complete,
successful discovery/import pass. It uses checkpoints captured before scanning,
rechecks that each absent source's `run.json` is truly missing, and performs
fingerprint-guarded removal of derived rows. Permission errors are not treated as
absence; reappearing markers and changed checkpoints report issues. Pruning is
disabled by default, never removes other evidence kinds, and never deletes source
files. Root IDs must be stable namespaces for the configured root; reusing one
for an unrelated directory changes the meaning of reconciliation.

### Configured root planning

`IEvidenceIndexRootPlanner` validates a bounded list of absolute, ordinary source directories (default 256, maximum 4096). Root IDs are unique across formats. Same-format nested roots are covered by the outermost configured root; equal-directory aliases select the ordinally smallest root ID. Decisions are returned explicitly in `CoveredRoots`. Path comparison follows Windows case-insensitivity and Unix case-sensitivity, and checks directory boundaries so `runs-other` is not covered by `runs`.

Different evidence formats retain separate scans. The planner does not import or remove rows. Hosts must reconcile previously indexed namespaces when configuration changes, and must keep root IDs bound to stable locations. Source ownership remains trusted: path validation is not protection against a concurrent hostile filesystem owner. The evaluated-run coordinator uses this plan as described below.

### Legacy output-store projection

`ILegacyIndexSourceReader` reads the existing schemaVersion `1.0` manifest and summary envelopes plus immediate `scenarios/*.json` files. It checks the fields used by the projection, duplicate JSON property names, duplicate scenario IDs and manifest/summary agreement; this is not full historical schema validation. The production indexing project remains independent of DataLoaders; the test project references DataLoaders to exercise its actual writer lifecycle.

Each consumed file is read into bounded detached bytes. Defaults are 16 MiB per file, 64 MiB total, 4096 scenario-directory entries and 100000 projected result/metric rows. Limits fail the whole source projection. Ordinary file/directory checks reject links and special entries. Missing optional summary/scenarios are distinguished from inaccessible files; no unreadable source is treated as successful absence. Parsing has depth 64 and rejects duplicate properties, malformed types and nonfinite scores.

Legacy rows are always `Legacy` / `Unverified`. The adapter does not verify the historical `contentHash`, assign an AEF run hash or establish suite compatibility. Reported verdicts remain producer claims. Pending runs, missing summary or a scenario-count mismatch are partial; matching counts yield unknown completeness, never proof of planned benchmark coverage.

Scenario rows use `legacy:scenario:<id>`, retain the case ID and reported pass/fail, and carry scalar dictionary metrics with provenance `legacy-scenario-metric`. The envelope score is a separate `legacy:scenario-score` occurrence with the producer's passed flag. Summary metrics use the reserved result ID `legacy:summary`, **null CaseId and Trial**, and provenance `legacy-run-summary`. This aggregate container must not be counted as a test case. Metric dictionary values do not acquire inferred thresholds or passed flags.

The source digest binds a versioned, path-sorted manifest of consumed file hashes. It does not cover unrelated reports/traces or establish an atomic filesystem snapshot. Raw inputs, outputs and embedded evaluation trees stay in source files; nested tree details and stimulus identity need source-detail queries. Bounded discovery and configured-root reconciliation are provided by the coordinators below.

### Legacy imports and overlapping roots

`ILegacyEvidenceIndexer` discovers ordinary `manifest.json` candidates with the same bounded traversal engine used for AEF `run.json` discovery. It stops descending at a candidate; candidate validation belongs to the reader. Unsupported or malformed manifests are reported, never silently counted as successful coverage. Legacy and AEF coordinators share the same per-source transaction, capacity-stop and guarded pruning implementation. Their `ImportedPaths` list records only sources successfully upserted or confirmed unchanged during that pass.

`IEvidenceRootIndexer` imports an explicit bounded AEF/legacy root plan. Different formats may share a physical directory but require distinct root IDs. After a complete owner scan it retires prior rows in a covered namespace only when their replacement was imported **during that pass**, or when pruning is explicitly enabled and the source marker is confirmed absent. Old row fingerprints guard removal. A failed owner scan retains covered rows. A nested run hidden beneath a newly introduced parent marker produces an explicit issue instead of silent loss. Source evidence is never deleted.

Root IDs must remain bound to their configured directory and format. This service does not persist workspace configuration or authorize rebinding IDs; the caller owns that contract. Removing a root from configuration does not silently remove its indexed namespace: rebuild or explicit guarded retirement is required. Imports and covered-root retirement commit per source, not atomically across the workspace; callers must surface incomplete coverage and must not treat retained rows as fresh. Observation stores use their dedicated inventory/import service rather than recursive marker discovery.

### Preparing a rebuild

`IEvidenceIndexRebuildPreparation.Prepare` builds a replacement in a unique ordinary sibling directory. The population callback receives a bounded writer and must return true only after complete coverage (for example, return `IEvidenceRootIndexer.Import(...).Complete`). Cancellation, exceptions, capacity failures or false coverage discard only that invocation's known candidate files. The active index is neither opened nor changed by preparation, so it may remain in use or be corrupt while authoritative sources are read into the replacement.

After population the writer closes, the candidate schema/integrity checks run, remaining SQLite sidecars cause refusal, candidate bytes are flushed and hashed, and a flushed `ready.json` receipt records the candidate identity and intended target. An `owner.json` precedes database creation for later recovery. Receipts are limited to 32 KiB; the candidate inherits database and writer budgets. A per-target preparation lease serializes builds; at most eight retained candidate directories are permitted, with at most 4096 parent entries inspected. Use a dedicated index directory. Ready receipts are not authentication against an owner who can edit local files.

**Preparation does not publish the candidate.** Target ownership, candidate revalidation, guarded promotion, stale-build handling and recovery remain required before the application can adopt it. The receipt does not establish a globally atomic snapshot of the evidence sources. The service flushes files but does not claim portable directory-entry durability across power loss.

The recovery design preserves SQLite sidecars because hot journals may contain data needed for recovery; see [SQLite's corruption guidance](https://www.sqlite.org/howtocorrupt.html). Replacement must also respect platform file-operation constraints; see [File.Replace documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace).
