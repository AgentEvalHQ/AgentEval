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

```xml
<PackageReference Include="AgentEval" Version="0.2.1-beta" />
```

**NuGet Gallery:** https://www.nuget.org/packages/AgentEval

---

## Supported Frameworks

AgentEval targets multiple .NET versions:

| Framework | Status |
|-----------|--------|
| .NET 8.0 | ✅ Supported |
| .NET 9.0 | ✅ Supported |
| .NET 10.0 | ✅ Supported |

---

## Dependencies

AgentEval has minimal dependencies:

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.Extensions.AI.Abstractions | 9.0.0+ | AI abstractions (IChatClient) |
| System.Text.Json | 9.0.0+ | JSON serialization |

---

## CLI Tool

AgentEval includes a command-line tool for CI/CD integration:

```bash
# Install globally
dotnet tool install -g AgentEval.Cli

# Verify installation
agenteval --version

# Run evaluation
agenteval eval --dataset tests.yaml --format junit --output results.xml
```

See [CLI Reference](cli.md) for full command documentation.

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

## Next Steps

- [Quick Start](getting-started.md) - Run your first agent test
- [Walkthrough](walkthrough.md) - Step-by-step tutorial
- [Architecture](architecture.md) - Understand the framework design
