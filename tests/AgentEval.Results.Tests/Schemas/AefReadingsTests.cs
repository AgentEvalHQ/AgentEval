using System.Text.Json.Nodes;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Tests.Schemas;

/// <summary>§7.3 ([VER-8]): how a reader takes a value this version does not know.</summary>
public class AefReadingsTests
{
    private static Dictionary<string, AefReading> Read(string schema, string json) =>
        AefReadings.Read(schema, AefSchemaValidatorTests.Json(json)).ToDictionary(r => r.Field);

    [Fact]
    public void EveryRule_TakesItsKnownValuesFromAWriterSubschemaThatExists()
    {
        foreach (var (schema, field, known) in AefReadings.KnownSchemaNames())
        {
            Assert.True(known.Count > 0, $"{schema} {field}: no writer subschema");
            Assert.True(AefSchemas.Reader.Has(schema), schema);
        }
    }

    [Fact]
    public void AKnownValue_ReadsAsWritten_AndAnUnknownOne_AsTheTableSays()
    {
        var run = Read("run", """
            {"execution": {"targetMode": "replayed", "stimulus": "crowd"}, "contentCapture": "partial",
             "subject": {"kind": "agent"}, "judges": [{"mode": "panel"}, {"mode": "jury"}],
             "config": {"thresholds": {"m": {"op": "~="}, "n": {"op": ">="}}}}
            """);

        Assert.Equal(("replayed", true), ((string)run["execution.targetMode"].Read!, run["execution.targetMode"].Known));
        Assert.Equal(("other", false), ((string)run["execution.stimulus"].Read!, run["execution.stimulus"].Known));
        Assert.Equal("partial", (string)run["contentCapture"].Written!);
        Assert.Equal("on", (string)run["contentCapture"].Read!);
        Assert.Equal("agent", (string)run["subject.kind"].Read!);
        Assert.Equal("panel", (string)run["judges[0].mode"].Read!);
        Assert.Equal("other", (string)run["judges[1].mode"].Read!);
        Assert.Equal("~=", (string)run["config.thresholds.m.op"].Read!);     // descriptive: as written
        Assert.False(run["config.thresholds.m.op"].Known);
        Assert.True(run["config.thresholds.n.op"].Known);
    }

    [Fact]
    public void AnUnknownTargetMode_IsNeverLive()
    {
        Assert.Equal("mocked", (string)Read("run", """{"execution": {"targetMode": "simulated"}}""")["execution.targetMode"].Read!);
    }

    [Fact]
    public void AnAssurance_ReadsAsSelfAttested_KnownOrNot()
    {
        Assert.Equal("self-attested", (string)Read("overlay-event", """{"by": {"assurance": "signed"}}""")["by.assurance"].Read!);
        Assert.Equal("self-attested", (string)Read("overlay-event", """{"by": {"assurance": "notarized"}}""")["by.assurance"].Read!);
    }

    [Fact]
    public void LaneRuleFields_AreReadOnlyForTheirKindOfRule()
    {
        var checkpoint = Read("checkpoint", """
            {"state": "decided", "outcome": null, "lanes": [
              {"rule": {"kind": "severity", "max": "catastrophic"}},
              {"rule": {"kind": "comparison", "axes": ["judges", "moon-phase"]}},
              {"rule": {"kind": "threshold", "max": "whatever"}},
              {"rule": {"kind": "vibes"}}]}
            """);

        Assert.Equal("decided", (string)checkpoint["state"].Read!);
        Assert.Null(checkpoint["outcome"].Read);
        Assert.True(checkpoint["outcome"].Known);
        Assert.Equal("not_measured", (string)checkpoint["lanes[0].rule.max"].Read!);
        Assert.Equal("judges", (string)checkpoint["lanes[1].rule.axes[0]"].Read!);
        Assert.Equal("incomparable", (string)checkpoint["lanes[1].rule.axes[1]"].Read!);
        Assert.False(checkpoint.ContainsKey("lanes[2].rule.max"));
        Assert.Equal("threshold", (string)checkpoint["lanes[2].rule.kind"].Read!);
        Assert.Equal("not_measured", (string)checkpoint["lanes[3].rule.kind"].Read!);
    }

    [Fact]
    public void RunnerEvents_AndPlans()
    {
        Assert.Equal("skipped", (string)Read("runner-event", """{"kind": "job.paused"}""")["kind"].Read!);
        Assert.Equal("not_measured", (string)Read("runner-event", """{"kind": "lane.completed", "status": "deferred"}""")["status"].Read!);
        Assert.False(Read("runner-event", """{"kind": "job.refused", "status": "deferred"}""").ContainsKey("status"));
        Assert.Equal("refused", (string)Read("run-plan", """{"provider": "nomad"}""")["provider"].Read!);
        Assert.Equal("ci:github", (string)Read("run-plan", """{"provider": "ci:github"}""")["provider"].Read!);
        Assert.Equal("plan9", (string)Read("runner", """{"os": "plan9"}""")["os"].Read!);
    }

    [Fact]
    public void DecisionStatuses_InTheInputAndTheOutput()
    {
        Assert.Equal("not_measured", (string)Read("decision#/$defs/input", """{"lanes": [{"result": {"status": "deferred"}}]}""")["lanes[0].result.status"].Read!);
        Assert.Equal("failed", (string)Read("decision#/$defs/input", """{"lanes": [{"result": {"status": "failed"}}]}""")["lanes[0].result.status"].Read!);
        // §7.3: a decision document with a status or outcome this version does not know cannot be recomputed ([CKP-7]).
        Assert.Equal("unverifiable", (string)Read("decision", """{"lanes": [{"status": "deferred"}]}""")["lanes[0].status"].Read!);
        Assert.Equal("unverifiable", (string)Read("decision", """{"outcome": "approved_by_quorum"}""")["outcome"].Read!);
        Assert.Equal("self-attested", (string)Read("decision#/$defs/input", """{"exceptions": [{"by": {"assurance": "signed"}}]}""")["exceptions[0].by.assurance"].Read!);
    }

    [Fact]
    public void ASchemaWithoutSection73Fields_HasNoReadings()
    {
        Assert.Empty(AefReadings.Read("overlay-seal", new JsonObject { ["kind"] = "x" }));
        Assert.Empty(AefReadings.Read("common#/$defs/id", JsonValue.Create("x")));
    }
}
