# Installation

## NuGet Package

Install AgentEval from NuGet:

### .NET CLI

```bash
dotnet add package AgentEval --prerelease
```

### Package Manager Console

```powershell
Install-Package AgentEval -Pre
```

### PackageReference

Add to your `.csproj` file:

```xml
<PackageReference Include="AgentEval" Version="*" />
```

> **Note:** Replace `*` with a specific version from NuGet for reproducible builds.

**NuGet Gallery:** https://www.nuget.org/packages/AgentEval

---

## Compatibility

AgentEval is tested and compatible with:

| Dependency | Version | Notes |
|------------|---------|-------|
| **Microsoft Agent Framework (MAF)** | `1.0.0-rc3` | Native integration — adapters, tool tracking, workflows |
| **Microsoft.Extensions.AI** | `10.3.0` | Universal `IChatClient` support |
| **.NET 8.0** | ✅ Supported | LTS |
| **.NET 9.0** | ✅ Supported | STS |
| **.NET 10.0** | ✅ Supported | Preview |

---

## Dependencies

AgentEval ships as a single NuGet package with these key dependencies:

| Package | Version | Purpose |
|---------|---------|--------|
| Microsoft.Agents.AI | 1.0.0-rc3 | Microsoft Agent Framework integration |
| Microsoft.Agents.AI.Workflows | 1.0.0-rc3 | Workflow orchestration support |
| Microsoft.Extensions.AI | 10.3.0 | AI abstractions (IChatClient) |
| Microsoft.Extensions.AI.Evaluation.Quality | 10.3.0 | Quality evaluation metrics |

See [THIRD-PARTY-NOTICES.md](https://github.com/AgentEvalHQ/AgentEval/blob/main/THIRD-PARTY-NOTICES.md) for the complete dependency list with licenses.

---

## Verify Installation

Create a simple test to verify AgentEval is installed and working correctly:

```csharp
using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Extensions.AI;

// 1. Create a evaluation harness
var harness = new MAFEvaluationHarness(verbose: true);

// 2. Create a mock agent for testing
// (In real usage, wrap your actual agent with MAFAgentAdapter)
var mockClient = new FakeChatClient("Hello! How can I help you today?");
var agent = new ChatClientAgent(mockClient, new() { Name = "TestAgent" });
var adapter = new MAFAgentAdapter(agent);

// 3. Define a simple test case
var testCase = new TestCase
{
    Name = "Installation Verification",
    Input = "Hello!",
    ExpectedOutputContains = "Hello"  // Verify response contains greeting
};

// 4. Run the test
var result = await harness.RunEvaluationAsync(adapter, testCase);

// 5. Check results
Console.WriteLine($"✅ AgentEval installed successfully!");
Console.WriteLine($"   Test: {testCase.Name}");
Console.WriteLine($"   Passed: {result.Passed}");
Console.WriteLine($"   Score: {result.Score}/100");
```

If this runs without errors and shows "Passed: True", AgentEval is correctly installed.

---

## CLI Tool

AgentEval also ships a standalone CLI for terminal and CI/CD usage:

```bash
dotnet tool install --global AgentEval.Cli --prerelease
agenteval eval --azure --model gpt-4o --dataset tests.yaml
```

See [CLI Reference](cli.md) for full documentation.

## Next Steps

- [Quick Start](getting-started.md) - Run your first agent evaluation
- [CLI Reference](cli.md) - Evaluate agents from the terminal
- [Cross-Framework Evaluation](cross-framework.md) - Use with any LLM provider
- [Walkthrough](walkthrough.md) - Step-by-step tutorial
- [Architecture](architecture.md) - Understand the framework design
