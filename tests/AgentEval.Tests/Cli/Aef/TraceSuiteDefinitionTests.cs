using AgentEval.Benchmarks;
using AgentEval.Tracing;
using AgentEval.Cli.Infrastructure;

namespace AgentEval.Tests.Cli.Aef;

public sealed class TraceSuiteDefinitionTests
{
    [Fact]
    public void Capture_TraceChecks_DeclaresFixedRosterAndAppliedPreset()
    {
        var definition = TraceSuiteDefinition.Create("standard", SamplePreset.Standard);
        Assert.True(definition.Frozen);
        Assert.Equal(6, definition.CaseIds.Count);
        Assert.Equal(TraceFidelityRubric.Classes.Select(c => "trace_fidelity." + c).OrderBy(c => c, StringComparer.Ordinal), definition.CaseIds);
        Assert.Equal(definition.Digest, TraceSuiteDefinition.Create("standard", SamplePreset.Standard).Digest);
    }

    [Fact]
    public void Capture_WorkflowExecutors_UsesReconcilerGroupingBeforeResults()
    {
        var definition = TraceSuiteDefinition.Create("standard", SamplePreset.Standard, ["Writer", "writer", "review"]);
        Assert.Equal(new[] { "workflow_trace_fidelity.executor.Writer", "workflow_trace_fidelity.executor.review" }, definition.CaseIds);
        Assert.Equal(definition.Digest, TraceSuiteDefinition.Create("standard", SamplePreset.Standard, ["Writer", "review"]).Digest);
        Assert.NotEqual(definition.Digest, TraceSuiteDefinition.Create("standard", SamplePreset.Standard, ["Writer"]).Digest);
        Assert.Equal(new[] { "workflow_trace_fidelity" }, TraceSuiteDefinition.Create("standard", SamplePreset.Standard, []).CaseIds);
    }
}
