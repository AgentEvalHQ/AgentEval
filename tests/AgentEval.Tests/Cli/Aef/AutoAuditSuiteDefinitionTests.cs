using AgentEval.Cli.Infrastructure;

namespace AgentEval.Tests.Cli.Aef;

public sealed class AutoAuditSuiteDefinitionTests
{
    [Fact]
    public void Capture_RealAndScriptedTasks_DeclareDistinctStableInstruments()
    {
        var live = AutoAuditSuiteDefinition.Create(scripted: false);
        var mock = AutoAuditSuiteDefinition.Create(scripted: true);
        Assert.True(live.Frozen);
        Assert.True(mock.Frozen);
        Assert.Equal(new[] { "fidelity", "safety" }, live.CaseIds);
        Assert.Equal(live.CaseIds, mock.CaseIds);
        Assert.Equal(live.Digest, AutoAuditSuiteDefinition.Create(scripted: false).Digest);
        Assert.NotEqual(live.Digest, mock.Digest);
        Assert.DoesNotContain(AgentEval.Benchmarks.AutoAuditLive.Request, live.ToJson().ToJsonString());
        Assert.DoesNotContain(AgentEval.Benchmarks.AutoAuditDemo.LeakEndpoint, mock.ToJson().ToJsonString());
        Assert.Equal(AgentEval.Benchmarks.AutoAuditLive.MaxToolIterations, (int)live.ToJson()["definition"]!["maxToolIterations"]!);
        Assert.Null(mock.ToJson()["definition"]!["maxToolIterations"]);
    }
}
