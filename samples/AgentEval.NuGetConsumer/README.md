# AgentEval NuGet Consumer Sample

> A standalone showcase demonstrating AgentEval as an **external NuGet consumer** would use it.

This sample references **AgentEval from NuGet**, not as a project reference. This validates that the published NuGet package works correctly and showcases all major AgentEval features.

## 🚀 Quick Start

```bash
# Run the sample
dotnet run --project samples/AgentEval.NuGetConsumer
```

**Note:** This sample uses mock data - no Azure OpenAI credentials required!

## ✨ Features Demonstrated

| Feature | Description | Status |
|---------|-------------|--------|
| **Tool Chain Assertions** | `HaveCalledTool`, `WithArgument`, `BeforeTool`, `AfterTool` | ✅ |
| **Performance Assertions** | Duration, TTFT, Cost, Token limits | ✅ |
| **Behavioral Policies** | `NeverCallTool`, `NeverPassArgumentMatching` | ✅ |
| **Confirmation Gates** | `MustConfirmBefore` for risky actions | ✅ |
| **Response Assertions** | `Contain`, `NotContain`, length validation | ✅ |
| **Mock Testing** | `FakeChatClient` for unit tests | ✅ |
| **Stochastic Testing** | Statistical analysis over N runs | ✅ |
| **Model Comparison** | Side-by-side model evaluation | ✅ |
| **Agentic Metrics** | Tool success, selection, efficiency | ✅ |

## 📝 Code Highlights

### Fluent Tool Assertions

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights", because: "must search before booking")
        .WithArgument("destination", "Paris")
        .WithDurationUnder(TimeSpan.FromSeconds(2))
    .And()
    .HaveCalledTool("BookFlight", because: "booking follows search")
        .AfterTool("SearchFlights")
    .And()
    .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
    .HaveNoErrors();
```

### Performance SLAs

```csharp
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5), because: "UX requires sub-5s")
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500))
    .HaveEstimatedCostUnder(0.05m, because: "budget constraint");
```

### Behavioral Policies

```csharp
result.ToolUsage!.Should()
    .NeverCallTool("DeleteAllUsers", because: "requires admin console")
    .NeverCallTool("ExecuteRawSQL", because: "SQL injection risk")
    .MustConfirmBefore("TransferFunds", confirmationToolName: "GetUserApproval");
```

## 🔗 Why This Sample Exists

This sample serves as:

1. **Package Validation** - Proves the NuGet package works correctly
2. **Feature Showcase** - Demonstrates all major AgentEval capabilities
3. **Onboarding Reference** - Quick start for new users
4. **CI Verification** - Can be run in pipelines to test package integrity

## 📦 Project Structure

```
AgentEval.NuGetConsumer/
├── AgentEval.NuGetConsumer.csproj  # References AgentEval from NuGet
├── Program.cs                       # Complete feature showcase
└── README.md                        # This file
```

## 🛠️ Technical Details

- **Target Framework:** .NET 9.0
- **AgentEval Version:** 0.1.3-alpha (from NuGet)
- **Dependencies:** Azure.AI.OpenAI, Microsoft.Agents.AI, Microsoft.Extensions.AI

## 📚 Learn More

- [Full Documentation](https://github.com/joslat/AgentEval)
- [Assertions Reference](https://github.com/joslat/AgentEval/blob/main/docs/assertions.md)
- [Stochastic Testing Guide](https://github.com/joslat/AgentEval/blob/main/docs/stochastic-testing.md)
- [Model Comparison](https://github.com/joslat/AgentEval/blob/main/docs/model-comparison.md)
