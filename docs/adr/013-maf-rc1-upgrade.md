# ADR-013: Microsoft Agent Framework RC1 Upgrade

## Status

✅ **Accepted** - February 2026

## Context

AgentEval was built on Microsoft Agent Framework (MAF) `1.0.0-preview.251110.2` and Microsoft.Extensions.AI `10.0.0`. MAF published its first Release Candidate (`1.0.0-rc1`) with several breaking API changes, improved API naming, and stabilized packages. Staying on the preview would mean:

1. **API Drift** — Preview APIs are subject to change without notice; RC APIs are near-final
2. **Dependency Conflicts** — Microsoft.Extensions.AI `10.3.0` (stable) is the paired version for RC1
3. **Community Alignment** — Early adopters and documentation reference RC1 patterns
4. **Package Stability** — `Microsoft.Extensions.AI.OpenAI` moved from preview to stable (`10.3.0`)

### Breaking Changes in RC1

| Area | Preview API | RC1 API |
|------|------------|---------|
| Session Management | `AgentThread` / `GetNewThread()` | `AgentSession` / `CreateSessionAsync()` |
| Streaming | `StreamAsync()` | `RunStreamingAsync()` |
| Event Hierarchy | `AgentRunUpdateEvent` | `AgentResponseUpdateEvent` (base: `ExecutorEvent` → `WorkflowOutputEvent`) |
| Graph Building | `AddFanInEdge()` | `AddFanInBarrierEdge()` |
| Agent Configuration | `ChatClientAgentOptions.Instructions` / `.Tools` | `ChatOptions.Instructions` / `.Tools` (nested in `ChatClientAgentOptions.ChatOptions`) |

## Decision

Upgrade all MAF and Microsoft.Extensions.AI packages to their RC1-aligned versions:

| Package | From | To |
|---------|------|----|
| `Microsoft.Agents.AI` | `1.0.0-preview.251110.2` | `1.0.0-rc1` |
| `Microsoft.Agents.AI.OpenAI` | `1.0.0-preview.251110.2` | `1.0.0-rc1` |
| `Microsoft.Agents.AI.Workflows` | `1.0.0-preview.251110.2` | `1.0.0-rc1` |
| `Microsoft.Extensions.AI` | `10.0.0` | `10.3.0` |
| `Microsoft.Extensions.AI.OpenAI` | `10.0.0-preview.1.25559.3` | `10.3.0` |
| `Microsoft.Extensions.AI.Evaluation.Quality` | `9.5.0` | `10.3.0` |
| `System.Numerics.Tensors` | `10.0.0` | `10.0.3` |

All source code, tests, samples, and documentation updated to use RC1 APIs.

## Consequences

### Positive

- **API Stability** — RC1 APIs are near-final; fewer breaking changes expected before GA
- **Stable Dependencies** — `Microsoft.Extensions.AI.OpenAI` is now a stable release (no preview suffix)
- **Unified Versioning** — All M.E.AI packages align at `10.3.0`
- **Async Consistency** — `CreateSessionAsync()` follows .NET async naming conventions (preview `GetNewThread()` was synchronous)
- **Clearer Naming** — `AgentResponseUpdateEvent` better describes the event's purpose than `AgentRunUpdateEvent`
- **Explicit Configuration** — `ChatOptions.Instructions` makes the options hierarchy clearer

### Negative

- **Migration Effort** — 7 source files, 3 test files, 14 sample files, and 5+ documentation files required updates
- **NuGetConsumer Sample** — Left on old preview packages intentionally (demonstrates NuGet consumption with `ManagePackageVersionsCentrally=false`)
- **Documentation Churn** — All code examples in docs needed `ChatClientAgentOptions.Instructions` → `ChatOptions.Instructions` migration

### Neutral

- **No Behavioral Changes** — All 2,100+ tests pass identically across 3 TFMs (net8.0, net9.0, net10.0)
- **Build Clean** — 0 errors, 0 warnings after upgrade

## Alternatives Considered

### 1. Stay on Preview

- **Rejected** — Preview APIs will diverge further from GA; migration cost only grows over time

### 2. Wait for GA

- **Rejected** — RC1 is functionally complete; waiting adds no value and delays alignment with the ecosystem

### 3. Support Both Preview and RC1

- **Rejected** — Maintaining two API surfaces adds complexity with no user benefit; AgentEval targets the latest MAF version
