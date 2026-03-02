# ADR-016: Monolith Modularization — Option C (Internal Multi-Project, Single NuGet)

**Status:** Accepted & Implemented  
**Date:** 2026-02-26  
**Decision Makers:** AgentEval Contributors

---

## Context

The AgentEval codebase (~203 `.cs` files, ~35,000 lines) was a single project (`src/AgentEval`). As it grew to include MAF integration, Red Team security scanning, data loading, embeddings, calibration, and tracing, several coupling anomalies emerged:

1. **Core interfaces referencing implementation namespaces** — `IMetric` in `Core/` depended on types defined in `Models/`
2. **Models containing assertion logic** — `ToolUsageReport` had `Should()` methods that coupled models to assertion implementations
3. **Bidirectional dependencies between Comparison and Assertions** — `StochasticRunner` ↔ assertion types
4. **MAF framework pulled into all consumers** — `Microsoft.Agents.AI` was a dependency even for non-MAF users
5. **Security scanning (PdfSharp-MigraDoc) pulled into all consumers** — heavy transitive dependency for optional feature

The monolith structure made it difficult to:
- Understand ownership boundaries
- Enforce dependency direction at compile time
- Identify the impact radius of changes
- Independently evolve MAF / RedTeam without touching core

---

## Decision

We chose **Option C: Internal Multi-Project, Single NuGet Package** — creating 6 internal projects while shipping a single NuGet package (`AgentEval`).

### Projects

| Project | Files | Purpose | Key Dependencies |
|---|---|---|---|
| `AgentEval.Abstractions` | ~48 | Public contracts, interfaces, models | M.E.AI.Abstractions |
| `AgentEval.Core` | ~63 | Implementations (metrics, assertions, tracing, comparison) | M.E.AI, M.E.AI.Eval.Quality, S.N.Tensors |
| `AgentEval.DataLoaders` | ~23 | Data loading, export, formatting | YamlDotNet |
| `AgentEval.MAF` | 7 | Microsoft Agent Framework integration | M.Agents.AI, M.Agents.AI.Workflows |
| `AgentEval.RedTeam` | 61 | Security scanning, compliance reporting | PdfSharp-MigraDoc, M.E.AI |
| `AgentEval` (umbrella) | 1 | Packaging — `AddAgentEvalAll()` convenience | None (references all above) |

### Alternatives Rejected

- **Option A (Multi-NuGet)**: Consumer complexity too high for current maturity. Consumers would need to reference 2-3 packages instead of one. Migration path preserved — Option A is a csproj change, not a code change.
- **Option B (Shared Framework)**: Fragile contracts, premature abstraction. Would require maintaining a stable shared-framework contract before the API surface is finalized.

### Key Design Choices

1. **`RootNamespace=AgentEval`** on all sub-projects — preserves all original namespaces, zero breaking changes
2. **`PrivateAssets="all"`** on umbrella ProjectReferences — embeds DLLs into the NuGet lib/ folder
3. **`TargetsForTfmSpecificBuildOutput`** MSBuild target — includes sub-project DLLs in the package
4. **Explicit NuGet PackageReferences** on umbrella — declares transitive dependencies for consumers since `PrivateAssets="all"` blocks automatic propagation
5. **`IsPackable=false`** on all sub-projects — only the umbrella produces a NuGet package
6. **`InternalsVisibleTo`** on all sub-projects → `AgentEval.Tests`

---

## Consequences

### Benefits

- **Compiler-enforced dependency direction** — Abstractions cannot reference Core; Core cannot reference MAF
- **Clear ownership per project** — contributors know where new files go
- **Dependency isolation** — MAF users don't pull PdfSharp; RedTeam users don't pull M.Agents.AI
- **Future Option A migration** is a csproj change, not a code change
- **Build parallelism** — MSBuild can compile independent projects in parallel

### Risks

- **Build time increase** (~2-5 seconds, minimal impact)
- **CI must build 6 projects** instead of 1 (handled transparently by `dotnet build`)
- **Test project and Samples must reference all 6 sub-projects** (due to `PrivateAssets="all"`)
- **NuGet packaging complexity** — requires MSBuild target to embed sub-project DLLs

### Metrics

- **Zero namespace changes** — all public types remain accessible at the same namespaces
- **Zero API surface changes** — consumers see no difference
- **All tests pass** across 3 TFMs — before and after
- **18 DLLs in NuGet package** (6 per TFM × 3 TFMs)
- **8 external NuGet dependencies** correctly declared per TFM

---

## Implementation

Completed in 5 phases (Phase 0–4):

| Phase | Description | Key Outcome |
|---|---|---|
| 0 | Fix Coupling Anomalies | Resolved 11 cross-cutting violations |
| 1 | Extract Abstractions | 48 files → `AgentEval.Abstractions` |
| 2 | Extract Core + DataLoaders | 63 + 23 files → two projects |
| 3 | Extract MAF + RedTeam | 7 + 61 files → two projects, umbrella finalized |
| 4 | Validate, Document, CI | Full validation, docs updated, ADR recorded |

See `strategy/impl/` for detailed phase plans.

---

## References

- [Strategy Document](../../strategy/AgentEval-Monolith-Modularization-ArchitectureRefactor.md)
- [Phase 0: Fix Coupling Anomalies](../../strategy/impl/Phase-0-Fix-Coupling-Anomalies.md)
- [Phase 1: Extract Abstractions](../../strategy/impl/Phase-1-Extract-Abstractions.md)
- [Phase 2: Extract Core + DataLoaders](../../strategy/impl/Phase-2-Extract-Core-DataLoaders.md)
- [Phase 3: Extract MAF + RedTeam](../../strategy/impl/Phase-3-Extract-MAF-RedTeam.md)
- [Phase 4: Validate, Document, CI](../../strategy/impl/Phase-4-Validate-Document.md)
- [ADR-006: Service-Based Architecture with DI](006-service-based-architecture-di.md)
