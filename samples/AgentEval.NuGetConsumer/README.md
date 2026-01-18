# AgentEval NuGet Consumer Sample

> A standalone showcase demonstrating AgentEval as an **external NuGet consumer** would use it.

This sample references **AgentEval from NuGet**, not as a project reference. This validates that the published NuGet package works correctly and showcases all major AgentEval features.

## 🚀 Quick Start

```bash
# Run the sample
dotnet run --project samples/AgentEval.NuGetConsumer
```

You'll see an interactive menu:

```
  Select mode:

    [1] 🎭 MOCK MODE - No Azure credentials needed (instant, offline)
    [2] 🚀 REAL MODE - Use Azure OpenAI (actual LLM calls)

  Enter choice [1/2]:
```

## 🔧 Configuration (for Real Mode)

To run with actual LLM calls, set these environment variables:

```powershell
$env:AZURE_OPENAI_ENDPOINT = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY = "your-api-key"
$env:AZURE_OPENAI_DEPLOYMENT = "gpt-4o"  # or your model deployment name
```

**Mock Mode** works without any configuration.

## ✨ Features Demonstrated

| Feature | Mock Mode | Real Mode | Description |
|---------|-----------|-----------|-------------|
| **Tool Chain Assertions** | ✅ | ✅ | `HaveCalledTool`, `WithArgument`, `BeforeTool`, `AfterTool` |
| **Performance Assertions** | ✅ | ✅ | Duration, TTFT, Cost, Token limits |
| **Behavioral Policies** | ✅ | ✅ | `NeverCallTool`, `NeverPassArgumentMatching` |
| **Confirmation Gates** | ✅ | ✅ | `MustConfirmBefore` for risky actions |
| **Response Assertions** | ✅ | ✅ | `Contain`, `NotContain`, length validation |
| **Stochastic Testing** | ℹ️ Explained | ✅ **REAL** | Statistical analysis over N runs |

## 📁 Project Structure

```
AgentEval.NuGetConsumer/
├── Program.cs           # Entry point with interactive menu
├── Demos.cs             # All demo scenarios (mock/real mode)
├── Config.cs            # Azure OpenAI configuration
├── AgentFactory.cs      # Creates mock or real agents
├── MockDataFactory.cs   # Test data for mock mode
├── Tools/
│   ├── TravelTools.cs   # Travel booking tools (SearchFlights, etc.)
│   └── CalculatorTool.cs # Simple math tool
└── README.md
```

## 📝 Code Highlights

### Fluent Tool Assertions

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights", because: "must search before booking")
        .WithArgument("destination", "Paris")
        .WithDurationUnder(TimeSpan.FromSeconds(5))
    .And()
    .HaveCalledTool("BookFlight")
        .AfterTool("SearchFlights")
    .And()
    .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
    .HaveNoErrors();
```

### Performance SLAs

```csharp
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10), because: "UX requirement")
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(1000))
    .HaveEstimatedCostUnder(0.10m, because: "budget constraint");
```

### Real Stochastic Testing

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions(Runs: 5, SuccessRateThreshold: 0.8));

Console.WriteLine($"Success rate: {result.SuccessRate:P0}");
Console.WriteLine($"Mean: {result.Statistics.Mean:F1}");
Console.WriteLine($"StdDev: {result.Statistics.StandardDeviation:F2}");
```

## 🎯 Architecture: SOLID & DRY

This sample follows clean architecture principles:

- **Single Responsibility**: Each file has one purpose
- **Open/Closed**: Factory pattern allows mock/real without code changes
- **Dependency Inversion**: Demos depend on abstractions (IChatClient)
- **DRY**: MockDataFactory centralizes test data creation

## 🔗 Why This Sample Exists

1. **Package Validation** - Proves the NuGet package works correctly
2. **Feature Showcase** - Demonstrates all major AgentEval capabilities
3. **Real Testing** - Actually runs stochastic tests (not just printed output)
4. **Onboarding Reference** - Quick start for new users
5. **CI Verification** - Can be run in pipelines to test package integrity

## 📚 Learn More

- [Full Documentation](https://github.com/joslat/AgentEval)
- [Assertions Reference](https://github.com/joslat/AgentEval/blob/main/docs/assertions.md)
- [Stochastic Testing Guide](https://github.com/joslat/AgentEval/blob/main/docs/stochastic-testing.md)
- [Model Comparison](https://github.com/joslat/AgentEval/blob/main/docs/model-comparison.md)

