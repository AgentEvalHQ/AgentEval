// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.Evals;

/// <summary>
/// Glass Box Part 2 (P2.A1): <see cref="EvalInputTraceAccessor"/> — the metadata bridge that carries a
/// Glass Box trace on an <see cref="EvalInput"/> for trace-aware evaluators.
/// </summary>
public class EvalInputTraceAccessorTests
{
    [Fact]
    public void GetTrace_NoMetadata_ReturnsNull()
    {
        var input = new EvalInput(Query: "q");

        Assert.Null(input.GetTrace());
    }

    [Fact]
    public void GetTrace_MetadataWithoutTraceKey_ReturnsNull()
    {
        var input = new EvalInput(Query: "q", Metadata: new Dictionary<string, object> { ["other"] = 1 });

        Assert.Null(input.GetTrace());
    }

    [Fact]
    public void GetTrace_WrongTypeUnderKey_ReturnsNull()
    {
        var input = new EvalInput(
            Query: "q",
            Metadata: new Dictionary<string, object> { [EvalInput.TraceMetadataKey] = "not a trace" });

        Assert.Null(input.GetTrace());
    }

    [Fact]
    public void WithTrace_ThenGetTrace_RoundTripsSameInstance()
    {
        var trace = new AgentTrace { AgentName = "planner" };
        var input = new EvalInput(Query: "q");

        var withTrace = input.WithTrace(trace);

        Assert.Same(trace, withTrace.GetTrace());
    }

    [Fact]
    public void WithTrace_DoesNotMutateOriginalInputOrItsMetadata()
    {
        var original = new EvalInput(Query: "q", Metadata: new Dictionary<string, object> { ["keep"] = 1 });

        var withTrace = original.WithTrace(new AgentTrace());

        // Original is untouched (copy-on-write); the new input carries both the old key and the trace.
        Assert.Null(original.GetTrace());
        Assert.False(original.Metadata!.ContainsKey(EvalInput.TraceMetadataKey));
        Assert.NotNull(withTrace.GetTrace());
        Assert.Equal(1, withTrace.Metadata!["keep"]);
    }

    [Fact]
    public void WithTrace_ReplacesExistingTrace()
    {
        var first = new AgentTrace { AgentName = "first" };
        var second = new AgentTrace { AgentName = "second" };

        var input = new EvalInput(Query: "q").WithTrace(first).WithTrace(second);

        Assert.Same(second, input.GetTrace());
    }
}
