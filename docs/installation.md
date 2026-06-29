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
| **Microsoft Agent Framework (MAF)** | Centrally pinned (`Directory.Packages.props`) | Native integration — adapters, tool tracking, workflows |
| **Microsoft.Extensions.AI** | Centrally pinned (`Directory.Packages.props`) | Universal `IChatClient` support |
| **.NET 8.0** | ✅ Supported | LTS |
| **.NET 9.0** | ✅ Supported | STS |
| **.NET 10.0** | ✅ Supported | Preview |

---

## Dependencies

AgentEval ships as a single NuGet package with these key dependencies:

| Package | Purpose |
|---------|--------|
| Microsoft.Agents.AI | Microsoft Agent Framework integration |
| Microsoft.Agents.AI.Workflows | Workflow orchestration support |
| Microsoft.Extensions.AI | AI abstractions (IChatClient) |
| Microsoft.Extensions.AI.Evaluation.Quality | Quality evaluation metrics |

> Exact pinned versions live in [`Directory.Packages.props`](https://github.com/AgentEvalHQ/AgentEval/blob/main/Directory.Packages.props); each release records its versions in the [CHANGELOG](https://github.com/AgentEvalHQ/AgentEval/blob/main/CHANGELOG.md).

See [THIRD-PARTY-NOTICES.md](https://github.com/AgentEvalHQ/AgentEval/blob/main/THIRD-PARTY-NOTICES.md) for the complete dependency list with licenses.

---

## Verify Installation

Create a simple test to verify AgentEval is installed and working correctly:

```csharp
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Testing;        // FakeChatClient
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;       // ChatClientAgent

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

AgentEval ships a standalone CLI for terminal and CI/CD usage, published as a
[`dotnet tool`](https://learn.microsoft.com/dotnet/core/tools/global-tools) on NuGet.

```bash
# Install (one-time, global)
dotnet tool install --global AgentEval.Cli --prerelease

# Use
agenteval init                                                 # bootstrap .agenteval/ workspace
agenteval bench --list                                         # discover available benchmark families
agenteval bench gdpr --preset smoke --subject MyAgent          # run a GDPR compliance benchmark
agenteval bench owasp --preset smoke --subject MyAgent --azure-from-env   # OWASP red-team against your real agent
agenteval mc serve                                             # open Mission Control on http://localhost:5000 (requires .NET 10)
agenteval doctor                                               # verify workspace integrity (audit-chain v2 hashing)
```

**Requirements**:
- **.NET 8 SDK or later** for the core surface (`bench`, `init`, `doctor`, `migrate`).
- **.NET 10 SDK** if you want `agenteval mc serve` — the Mission Control portal uses
  Hot Chocolate 16 + `MapStaticAssets`, which are net10-only. On .NET 8/9 installations
  the `mc serve` subcommand prints a graceful "requires .NET 10" message.

**Updating**:

```bash
dotnet tool update --global AgentEval.Cli --prerelease
```

**Uninstalling**:

```bash
dotnet tool uninstall --global AgentEval.Cli
```

See [CLI Reference](cli.md) for full documentation of every subcommand.

## Next Steps

- [Quick Start](getting-started.md) - Run your first agent evaluation
- [CLI Reference](cli.md) - Evaluate agents from the terminal
- [Cross-Framework Evaluation](cross-framework.md) - Use with any LLM provider
- [Walkthrough](walkthrough.md) - Step-by-step tutorial
- [Architecture](architecture.md) - Understand the framework design
