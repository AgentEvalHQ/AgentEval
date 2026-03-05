# ADR-014: Dataset Pipeline â€” Two-Model Architecture

**Status:** Accepted  
**Date:** 2026-02-24  
**Decision Makers:** AgentEval Contributors  
**Related Document:** `strategy/AgentEval-dataloader-Implementation-Review-and-Refinement.md` (Conflicts C, D)

---

## Context

AgentEval's dataset-driven evaluation pipeline involves two distinct models that represent "a test case" at different abstraction layers:

| Model | Location | Purpose |
|-------|----------|---------|
| `DatasetTestCase` | `src/AgentEval/DataLoaders/IDatasetLoader.cs` | Persistence model â€” loaded from `.jsonl`, `.json`, `.csv`, `.yaml` files; tolerates format aliases; is format-agnostic |
| `TestCase` | `src/AgentEval/Models/EvaluationModels.cs` | Execution model â€” consumed by `IEvaluationHarness`, `StochasticRunner`, assertions; has strict typed requirements |

### The Problem

Documentation, samples, and user-facing guides had started treating the two models as synonymous. Specifically:

1. `docs/getting-started.md` showed a YAML dataset file using `name:`, `expected_output_contains:`, and `evaluation_criteria:` as field names â€” all of which are `TestCase` properties, not `DatasetTestCase` properties. Loading such a file via `IDatasetLoader` silently drops those fields into `Metadata`.

2. No official bridge existed between `DatasetTestCase` and `TestCase`. Users had to write manual mapping code, which inevitably lost information (most critically: `GroundTruthToolCall` â†’ `string` projection had no guidance).

3. `DatasetTestCase` could not carry `evaluation_criteria` â€” a value authors legitimately want to specify in their dataset files for use by the LLM judge.

### Forces

- **CLEAN Architecture** (enforced in this project): persistence concerns must not bleed into domain models.
- **DRY**: every consumer of dataset files was writing the same mapping boilerplate.
- **Type safety**: `DatasetTestCase.GroundTruth` is `GroundTruthToolCall` (structured, BFCL-style function-call accuracy). `TestCase.GroundTruth` is `string` (free text for LLM-as-judge). These are semantically different despite sharing a name.
- **Documentation trust**: the primary getting-started guide must show fields that actually work.

---

## Decision

**Keep both models separate.** `DatasetTestCase` is the persistence/input layer; `TestCase` is the domain/execution layer. The boundary between them is made explicit and well-supported.

### Specific Changes

#### 1. Extend `DatasetTestCase` with `EvaluationCriteria`

Add the following properties to `DatasetTestCase`, recognized from the corresponding fields in all four loaders (JSONL, JSON, CSV, YAML):
- `IReadOnlyList<string>? EvaluationCriteria` â€” from `evaluation_criteria`
- `IReadOnlyList<string>? Tags` â€” from `tags` (maps to `TestCase.Tags`)
- `int? PassingScore` â€” from `passing_score` (maps to `TestCase.PassingScore` which is `int`, defaults to `EvaluationDefaults.DefaultPassingScore` if null)

**Rationale:** `EvaluationCriteria` is the primary `TestCase` property that makes sense as a file-level specification. Without it, dataset authors cannot specify evaluation criteria without writing a custom `ToTestCase()` override for every project. `Tags` enables test filtering by category directly from dataset files. `PassingScore` allows per-test threshold overrides without code changes.

When adding new recognized fields, `JsonParsingHelper.KnownPropertyNames` must also be updated so these fields are not duplicated into `Metadata` for JSON/JSONL loaders. Additionally, the YAML loaderâ€™s private `YamlTestCase` DTO class requires corresponding properties and mapping in `ConvertToDatasetTestCase()`.

#### 2. Provide `DatasetTestCaseExtensions.ToTestCase()` as the official bridge

```csharp
public static TestCase ToTestCase(
    this DatasetTestCase d,
    Func<GroundTruthToolCall?, string?>? groundTruthProjection = null) => new()
{
    Name = string.IsNullOrEmpty(d.Id) ? d.Input[..Math.Min(50, d.Input.Length)] : d.Id,
    Input = d.Input,
    ExpectedOutputContains = d.ExpectedOutput,
    EvaluationCriteria = d.EvaluationCriteria,
    ExpectedTools = d.ExpectedTools,
    GroundTruth = groundTruthProjection != null
        ? groundTruthProjection(d.GroundTruth)
        : (d.GroundTruth is null ? null : JsonSerializer.Serialize(d.GroundTruth)),
    Tags = d.Tags,
    PassingScore = d.PassingScore ?? EvaluationDefaults.DefaultPassingScore, // int? â†’ int
    // Filter null values: DatasetTestCase.Metadata is Dictionary<string, object?>
    // but TestCase.Metadata is IDictionary<string, object> (non-nullable values).
    Metadata = d.Metadata.Count > 0
        ? d.Metadata
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!)
        : null,
};
```

#### 3. `GroundTruth` projection default: JSON-serialize the structured value

When a `DatasetTestCase` has a `GroundTruthToolCall` and no custom projection is provided, `ToTestCase()` serializes it to a JSON string (e.g., `{"name":"book_flight","arguments":{"city":"Paris"}}`).  

**Rationale:** Using the function name only (`d.GroundTruth?.Name`) silently discards argument data that the LLM judge could use to verify whether the agent called the tool with the correct parameters. JSON serialization preserves complete information and is human-readable by the judge.

Users who want name-only can pass `d => d?.Name`:
```csharp
var testCase = datasetCase.ToTestCase(groundTruthProjection: gt => gt?.Name);
```

#### 4. `RunBatchAsync` accepts `IEnumerable<DatasetTestCase>` directly

The batch evaluation API accepts `DatasetTestCase` and performs the `ToTestCase()` conversion internally, so callers using `RunBatchAsync` never need to manually bridge the models in the common case. Custom `groundTruthProjection` can be provided as an option if needed.

#### 5. Fix documentation

`docs/getting-started.md` YAML example corrected:
- `name:` â†’ `id:`
- `expected_output_contains:` â†’ `expected:`
- `evaluation_criteria:` stays valid (now recognized by all loaders per change 1)

---

## Consequences

### Positive

- **CLEAN boundary maintained**: `DatasetTestCase` remains a persistence concern; `TestCase` remains a domain concern. Code depending on `TestCase` does not need to know about file formats.
- **No boilerplate for users**: `ToTestCase()` covers the common case in one line. `RunBatchAsync` handles it transparently.
- **GroundTruth information preserved** by default (JSON serialization), with escape hatch for customization.
- **Documentation is now correct**: the YAML example uses fields that `IDatasetLoader` actually recognizes.
- **`evaluation_criteria` round-trip**: dataset authors can specify evaluation criteria in YAML/JSON/JSONL/CSV and have it flow through to the harness without any code.
- **`Tags` and `PassingScore` round-trip**: dataset authors can also specify `tags` for test filtering and `passing_score` for per-test threshold overrides.

### Important Note: `RunBatchAsync` Return Type

`RunBatchAsync` returns `TestSummary`, which has `TotalCount`/`PassedCount` properties. Documentation must NOT reference `.TotalTests`/`.PassedTests` â€” those are properties of `EvaluationReport` (the exporter-layer type in `Exporters/EvaluationReport.cs`), which is a different type.

### Negative / Trade-offs

- **Extra type to learn**: new users see two models and must understand the boundary. This is documented clearly in `getting-started.md` and `comparison.md`.
- **`GroundTruthToolCall` â†’ `string` serialization** is a one-way projection; you cannot recover the structured value from `TestCase.GroundTruth` alone. This is acceptable because `TestCase.GroundTruth` is consumed by the LLM judge as text.
- **Small breaking potential**: any existing code that relied on `evaluation_criteria`, `tags`, or `passing_score` silently landing in `Metadata` will now find the values in dedicated `DatasetTestCase` properties instead. This is the correct behavior, but callers accessing `Metadata["evaluation_criteria"]` directly will break.

---

## Alternatives Considered

### A â€” Merge the models (single `TestCase`)

Make `TestCase` file-loadable: add snake_case aliases for all properties, accept `question` / `prompt` as `Input`, etc.

**Rejected because:**
- Persistence concerns (alias mapping, root-key detection) do not belong in the domain model.
- `TestCase.Name` is `required` and has strict domain semantics (used in test output titles). Making it optional with a fallback violates that contract.
- Violates CLEAN architecture principle already established in ADR-006.

### B â€” Discriminated union `TestCaseSource`

Introduce `TestCaseSource { FromFile(DatasetTestCase), Inline(TestCase) }` and have the harness accept this union.

**Rejected because:**
- Adds a third type for what is fundamentally a two-step pipeline concern.
- Does not resolve the `GroundTruth` type mismatch â€” the projection still needs to happen somewhere.
- Unnecessary complexity given that `ToTestCase()` solves the problem without a new type.

### C â€” Keep models separate but require users to bridge manually (status quo)

**Rejected because:**
- Documentation's `getting-started.md` example already proves users get it wrong (it used `TestCase` field names for a `DatasetTestCase` YAML file).
- Every project writing the same `new TestCase { Name = d.Id, ... }` mapping is the exact boilerplate a framework should eliminate.

### D â€” Shared base class or interface `ITestCaseBase`

**Evaluated and rejected because:**
- `GroundTruth` has incompatible types across the two models (`string?` vs `GroundTruthToolCall?`). A shared interface cannot define this property.
- Mutability contracts differ: `TestCase` uses `init` setters (compile-time immutability), `DatasetTestCase` uses `get/set` (required for progressive deserialization from CSV/JSON/YAML).
- Property names intentionally diverge: `Name` (required) vs `Id` (optional), `ExpectedOutputContains` vs `ExpectedOutput`. These are different semantics, not aliases.
- `Metadata` nullability differs: `IDictionary<string, object>?` vs `Dictionary<string, object?>`. Neither contract can be weakened without downstream impact.
- Only 2 properties (`Input`, `ExpectedTools`) actually share both name and type â€” too thin for a useful abstraction.
- Existing benchmark test case types (`ToolAccuracyTestCase`, `TaskCompletionTestCase`, `MultiStepTestCase`) already don't share a base. Introducing inheritance only here would be architecturally inconsistent.
- Extensibility for custom dataset formats is already covered by `IDatasetLoader` (the interface) and `Metadata` (the bag). Subclassing `DatasetTestCase` adds no value `IDatasetLoader` doesn't already provide.
---

## Implementation

The full implementation plan covers specifically:

- **M3** â€” `DatasetTestCase` extended model + `ToTestCase()` adapter
- **M4** â€” `RunBatchAsync` on `MAFEvaluationHarness` accepting `DatasetTestCase`
- **M1** â€” Documentation corrections
