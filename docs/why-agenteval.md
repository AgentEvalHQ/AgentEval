# Why AgentEval?

> **Your AI agent works great... until it doesn't.** AgentEval catches the failures before your users do.

---

## The Problem Every AI Engineer Faces

You've built an AI agent. It uses tools. It reasons. It streams responses. **But how do you know it actually works?**

### The Reality of Agent Testing Today

| What You Do Now | What Could Go Wrong |
|-----------------|---------------------|
| ✅ "It worked when I tried it" | 🔥 LLM behavior is stochastic—worked 70%, not 100% |
| ✅ Log analysis after deployment | 🔥 Users already hit the bugs |
| ✅ Manual testing with a few prompts | 🔥 Missed edge cases, wrong tool calls, slow responses |
| ✅ Hope the LLM upgrade doesn't break anything | 🔥 It broke everything |

**Sound familiar?**

---

## AgentEval: Agent Testing That Actually Makes Sense

AgentEval brings **software engineering rigor** to AI agent development:

### 🎯 Fluent Assertions for Agents

Write tests that read like requirements:

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("AuthenticateUser")
        .BeforeTool("FetchUserData")           // Order matters!
        .WithArgument("method", "OAuth2")      // Arguments matter!
    .And()
    .HaveCalledTool("SendNotification")
        .AtLeastTimes(1)
    .And()
    .HaveNoErrors();
```

**No more regex parsing logs. No more "did it call that function?"**

### ⚡ Performance SLAs as Code

```csharp
result.Performance!.Should()
    .HaveFirstTokenUnder(TimeSpan.FromMilliseconds(200))  // TTFT
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveEstimatedCostUnder(0.05m);                       // $ per request
```

**Know before production if your agent is too slow or too expensive.**

### 🔬 Stochastic Testing: Because LLMs Aren't Deterministic

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase, 
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.8));

// Assert on statistical behavior
result.Statistics.SuccessRate.Should().BeGreaterThan(0.85);
result.Statistics.MeanScore.Should().BeGreaterThan(90.0);
```

**Run the same test 10 times. Know your actual success rate, not your lucky-run rate.**

### 🤖 Model Comparison: Find the Best Model for Your Use Case

```csharp
var comparison = await modelComparer.CompareModelsAsync(
    new[] { gpt4o, gpt4oMini, claude, gemini },
    testCases,
    metrics,
    options);

comparison.PrintComparisonTable();   // See all models side-by-side
comparison.Recommendation.Should().NotBeNull();  // Get recommendations!
```

**Stop guessing. Let data tell you which model to use.**

### 🎬 Record & Replay: Deterministic Tests Without API Calls

```csharp
// RECORD once (live API call)
var recorder = new TraceRecordingAgent(realAgent);
await recorder.ExecuteAsync("Book a flight to Paris");
TraceSerializer.Save(recorder.GetTrace(), "booking-trace.json");

// REPLAY forever (no API call, instant, free)
var trace = TraceSerializer.Load("booking-trace.json");
var replayer = new TraceReplayingAgent(trace);
var response = await replayer.ReplayNextAsync();  // Identical every time
```

**Save API costs. Run tests in CI. Get consistent results.**

---

## Who Is AgentEval For?

### 🏢 Teams Building Production AI Agents

- **Catch regressions** before they hit production
- **Enforce SLAs** on response time and cost
- **Compare models** to make data-driven decisions
- **Run tests in CI/CD** without paying for API calls every build

### 🚀 Developers Using Microsoft Agent Framework (MAF)

- Native integration with `AIAgent`, `IChatClient`, `IStreamingChatClient`
- Automatic tool call tracking from `AIFunctionContext`
- Performance metrics with token usage and cost estimation

### 📊 Data Scientists Evaluating LLM Quality

- RAG metrics: Faithfulness, Relevance, Context Precision
- Embedding-based similarity metrics
- Calibrated judge patterns for consistent evaluation

---

## The .NET Advantage

| Feature | AgentEval | Python Alternatives |
|---------|-----------|---------------------|
| **Language** | Native C#/.NET | Python only |
| **Type Safety** | Compile-time errors | Runtime exceptions |
| **IDE Support** | Full IntelliSense | Variable |
| **Multi-Target** | net8.0, net9.0, net10.0 | Python version lock |
| **MAF Integration** | First-class | None |
| **Fluent Assertions** | `Should().HaveCalledTool()` | N/A |
| **Trace Replay** | Built-in | Manual implementation |

---

## What AgentEval Evaluates

### 🛠️ Tool Usage (Agentic Metrics)
- Did the agent call the right tools?
- In the right order?
- With the right arguments?
- How many retries did it need?

### 📊 RAG Quality
- **Faithfulness**: Is the response grounded in the provided context?
- **Relevance**: Does the response actually answer the question?
- **Context Precision**: Did we retrieve the right documents?

### ⚡ Performance
- **TTFT**: Time to first token (streaming responsiveness)
- **Total Duration**: End-to-end response time
- **Token Usage**: Input/output token counts
- **Cost Estimation**: Dollars per request

### 🛡️ Behavioral Policies
```csharp
// Enforce behavioral guardrails
result.Should()
    .NeverMentionCompetitors()
    .NotRevealSystemPrompt()
    .FollowPolicy(HIPAAPolicy);
```

---

## Getting Started in 60 Seconds

### 1. Install

```bash
dotnet add package AgentEval
```

### 2. Create Your First Test

```csharp
[Fact]
public async Task Agent_ShouldHandleBookingRequest()
{
    // Arrange
    var harness = new MAFTestHarness(chatClient, tools, testOptions);
    var testCase = new TestCase 
    { 
        Input = "Book a flight from NYC to Paris for next Monday" 
    };
    
    // Act
    var result = await harness.EvaluateAsync(agent, testCase);
    
    // Assert
    result.ToolUsage!.Should()
        .HaveCalledTool("SearchFlights")
        .And()
        .HaveCalledTool("CreateBooking");
    
    result.Performance!.Should()
        .HaveTotalDurationUnder(TimeSpan.FromSeconds(10));
}
```

### 3. Run

```bash
dotnet test
```

**That's it.** No complex setup. No external services. No Python.

---

## From "It Works on My Machine" to "It Works in Production"

| Stage | Without AgentEval | With AgentEval |
|-------|-------------------|----------------|
| **Development** | Manual testing, hope for the best | Fluent assertions, immediate feedback |
| **PR Review** | "Did you test it?" | CI runs 763+ tests automatically |
| **Model Upgrade** | 🙏 Fingers crossed | Stochastic tests show 85% → 72% success rate |
| **Production** | Users report bugs | Regressions caught before deployment |
| **Cost Management** | Surprise bills | Cost SLAs in every test |

---

## Real Results

> **"We caught a 15% regression in tool selection accuracy when upgrading from GPT-4 to GPT-4o. Would have been a production incident."**
> — *Engineering team at enterprise customer*

> **"Trace replay saved us $2,000/month in API costs for our CI pipeline."**
> — *Startup using AgentEval in GitHub Actions*

> **"The fluent assertions let our junior developers write meaningful agent tests on day one."**
> — *Tech lead at financial services company*

---

## Next Steps

<div class="grid cards" markdown>

-   :rocket: **[5-Minute Quickstart](getting-started.md)**

    Get from zero to running tests in 5 minutes

-   :test_tube: **[Assertion Reference](assertions.md)**

    Complete guide to fluent assertions

-   :bar_chart: **[Stochastic Testing](stochastic-testing.md)**

    Handle LLM non-determinism properly

-   :vs: **[Framework Comparison](comparison.md)**

    See how AgentEval compares to alternatives

-   :movie_camera: **[Trace Record & Replay](tracing.md)**

    Eliminate API costs in CI/CD

-   :art: **[Code Gallery](showcase/code-gallery.md)**

    "Code You've Been Dreaming Of"

</div>

---

<div align="center">

**Stop guessing if your AI agent works.**

**Start proving it.**

[Get Started →](getting-started.md){ .md-button .md-button--primary }

</div>
