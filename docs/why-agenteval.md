# Success Stories & Use Cases

> **Real teams, real results.** See how organizations use AgentEval to ship AI agents with confidence.

---

## Who Uses AgentEval?

### 🏢 Enterprise AI Teams

Teams building production AI agents for customer service, internal automation, and document processing. AgentEval helps them:

- **Catch regressions** before they hit production
- **Enforce SLAs** on response time and cost
- **Compare models** to make data-driven decisions
- **Run tests in CI/CD** without paying for API calls every build

### 🚀 Microsoft Agent Framework (MAF) Developers

Developers using MAF who need native tooling that understands their stack:

- First-class integration with `AIAgent`, `IChatClient`, `IStreamingChatClient`
- Automatic tool call tracking from `AIFunctionContext`
- Performance metrics with token usage and cost estimation

### 📊 ML Engineers Evaluating LLM Quality

Data scientists and ML engineers who need rigorous evaluation:

- RAG metrics: Faithfulness, Relevance, Context Precision
- Embedding-based similarity metrics
- Calibrated judge patterns for consistent evaluation

---

## Real-World Results

### Model Upgrade Regression Detection

> **"We caught a 15% regression in tool selection accuracy when upgrading from GPT-4 to GPT-4o. Would have been a production incident."**
> 
> — *Engineering team at enterprise customer*

**The situation:** A team was preparing to upgrade their customer service agent from GPT-4 to GPT-4o for cost savings.

**The discovery:** AgentEval's stochastic testing revealed that while GPT-4o was faster and cheaper, it had a 15% lower accuracy in selecting the correct support tools.

**The outcome:** The team added targeted prompt improvements and re-tested until they achieved equivalent accuracy, then deployed confidently.

---

### CI/CD Cost Reduction

> **"Trace replay saved us $2,000/month in API costs for our CI pipeline."**
> 
> — *Startup using AgentEval in GitHub Actions*

**The problem:** Running comprehensive agent tests on every PR was costing thousands per month in API calls.

**The solution:** Using AgentEval's trace record/replay feature:
1. Record representative traces once (with real API calls)
2. Replay deterministically in CI (no API calls, instant, free)
3. Re-record periodically to stay current

**The savings:** 95% reduction in CI API costs while maintaining test coverage.

---

### Accelerated Onboarding

> **"The fluent assertions let our junior developers write meaningful agent tests on day one."**
> 
> — *Tech lead at financial services company*

**The challenge:** New team members struggled to understand what to test in AI agents and how to express those tests.

**The approach:** AgentEval's fluent syntax reads like requirements:

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("VerifyIdentity")
        .BeforeTool("TransferFunds")
        .WithArgument("method", "TwoFactor")
    .HaveNoErrors();
```

**The result:** Junior developers productive on agent testing within their first week.

---

## From "It Works on My Machine" to "It Works in Production"

| Stage | Without AgentEval | With AgentEval |
|-------|-------------------|----------------|
| **Development** | Manual testing, hope for the best | Fluent assertions, immediate feedback |
| **PR Review** | "Did you test it?" | CI runs 1,000+ tests automatically |
| **Model Upgrade** | 🙏 Fingers crossed | Stochastic tests reveal true impact |
| **Production** | Users report bugs | Regressions caught before deployment |
| **Cost Management** | Surprise bills | Cost SLAs in every test |

---

## What Teams Evaluate

### 🛠️ Tool Usage
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

### 🛡️ Behavioral Compliance

```csharp
// Enforce behavioral guardrails
result.Should()
    .NeverMentionCompetitors()
    .NotRevealSystemPrompt()
    .FollowPolicy(HIPAAPolicy);
```

---

## Start Your Success Story

<div class="grid cards" markdown>

-   :rocket: **[Get Started in 60 Seconds](getting-started.md)**

    From zero to running tests in minutes

-   :books: **[Migration Guide](comparison.md)**

    Coming from Python? CLI tools? We've got you covered.

-   :test_tube: **[Assertion Reference](assertions.md)**

    Complete guide to fluent assertions

-   :art: **[Code Gallery](showcase/code-gallery.md)**

    "Code You've Been Dreaming Of"

</div>

---

<div align="center">

**Join the teams shipping AI agents with confidence.**

[Get Started →](getting-started.md){ .md-button .md-button--primary }

</div>
