# AgentEval Development Instructions

This file provides instructions for AI agents working with the AgentEval codebase.

## Project Overview

AgentEval is **the comprehensive .NET evaluation toolkit for AI agents**, built first for Microsoft Agent Framework (MAF). What RAGAS and DeepEval do for Python, AgentEval does for .NET:
- Tool usage validation with fluent assertions
- RAG quality metrics (faithfulness, relevance, groundedness)
- stochastic evaluation and statistical model comparison
- Behavioral policies (NeverCallTool, MustConfirmBefore)
- Trace record/replay for deterministic CI testing
- CLI tool for CI/CD integration

## Quick Reference

### Build & Test
```powershell
dotnet build                    # Build all projects
dotnet test                     # Run 1,000+ tests (×3 TFMs)
dotnet run --project samples/AgentEval.Samples
```

### Key Directories
- `src/AgentEval/` - Main library
- `tests/AgentEval.Tests/` - Unit tests (mirrors src structure)
- `samples/AgentEval.Samples/` - 23 runnable samples
- `docs/` - Documentation

### Core Interfaces
- `IMetric` → `IRAGMetric`, `IAgenticMetric`
- `IEvaluableAgent` → `IStreamableAgent`
- `IEvaluationHarness` → `MAFEvaluationHarness`

### Metric Naming
- `llm_*` = LLM-evaluated (API cost)
- `code_*` = Code-computed (free)
- `embed_*` = Embedding-based

### Assertions Entry Points
```csharp
result.ToolUsage!.Should()    // Tool assertions
result.Performance!.Should()   // Performance assertions
result.ActualOutput!.Should()  // Response assertions
```

## Code Conventions

### C# Style
- C# preview features enabled (LangVersion=preview)
- Nullable enabled throughout
- File-scoped namespaces preferred
- `required` properties for models
- XML docs on public APIs

### Test Naming
`MethodName_StateUnderTest_ExpectedBehavior`

### Error Messages
Must include: Expected, Actual, Suggestions, Because parameter

## Architectural Principles

This codebase follows **SOLID, DRY, KISS, CLEAN** principles strictly. See `docs/adr/006-service-based-architecture-di.md` for details.

### Key Points
- **Interface-First**: All core services have interfaces (IStochasticRunner, IModelComparer, etc.)
- **DI/IOC**: Use `services.AddAgentEval()` for registration; inject interfaces, not implementations
- **No Over-Engineering**: Don't add interfaces for builders, config objects, or test-time tools
- **Single Responsibility**: Each service has one focused purpose

### DI Pattern
```csharp
// Register services
services.AddAgentEval(options => options.DefaultModelId = "gpt-4o");

// Inject interfaces
public class MyService(IStochasticRunner runner, IModelComparer comparer) { }
```

## Common Tasks

### Adding a New Metric
1. Create in `Metrics/RAG/` or `Metrics/Agentic/`
2. Implement `IRAGMetric` or `IAgenticMetric`
3. Use appropriate prefix (`llm_`/`code_`/`embed_`)
4. Add tests using `FakeChatClient`

### Adding a New Assertion
1. Add to `ToolUsageAssertions`, `PerformanceAssertions`, or `ResponseAssertions`
2. Use `[StackTraceHidden]` attribute
3. Call `AgentEvalScope.FailWith()` with structured exception

### Adding a New Sample
1. Create `SampleXX_Name.cs` in samples project
2. Register in `Program.cs` menu
3. Follow header template with time estimate
4. Provide mock fallback for offline testing

## Testing Without LLM

Use `FakeChatClient` for unit tests:
```csharp
var fakeClient = new FakeChatClient("""{"score": 95}""");
var metric = new FaithfulnessMetric(fakeClient);
```

Use Trace Replay for integration tests:
```csharp
var trace = TraceSerializer.Load("saved-trace.json");
var replayer = new TraceReplayingAgent(trace);
var response = await replayer.ReplayNextAsync();
```

## Git Commit Conventions

### Skip CI for Intermediate Commits
When making multiple small changes (docs, formatting, etc.), use `[skip ci]` to avoid triggering full CI pipeline:

```powershell
git commit -m "docs: Fix typo in README [skip ci]"
git commit -m "style: Format code [skip ci]"
```

**When to use `[skip ci]`:**
- Documentation-only changes
- Comment/typo fixes
- Formatting changes
- Intermediate commits during a multi-commit refactor

**When NOT to use `[skip ci]`:**
- Any code changes (even small ones)
- Final commit of a feature/fix
- Changes to tests, samples, or configuration

### Commit Message Format
Use conventional commits:
- `feat:` New feature
- `fix:` Bug fix
- `docs:` Documentation only
- `style:` Formatting, no code change
- `refactor:` Code restructuring
- `test:` Adding/updating tests
- `chore:` Maintenance tasks

## Environment Variables

For samples/integration tests:
```
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-api-key
AZURE_OPENAI_DEPLOYMENT=gpt-4o
```
