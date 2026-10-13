# Derived SQLite evidence index

`AgentEval.Indexing` supplies the versioned SQLite schema for the shared evidence
index. It targets .NET 8, 9 and 10 and keeps SQLite dependencies outside the
BCL-only `AgentEval.Results` project. Source files remain authoritative; this
database is derived state, not an alternative evidence seal or acceptance store.
Source import, rebuild orchestration and query pagination are subsequent slices.

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
future mutation services must also enforce it on writes. It excludes journal
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
