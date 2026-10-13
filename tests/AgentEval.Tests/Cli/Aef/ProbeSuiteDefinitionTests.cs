using AgentEval.Cli.Commands;
using AgentEval.Cli.Infrastructure;
using AgentEval.RedTeam;

namespace AgentEval.Tests.Cli.Aef;

public sealed class ProbeSuiteDefinitionTests
{
    [Fact]
    public void Create_MaterializedContentAndPolicy_ChangesIdentityWithoutExposingText()
    {
        var attack = Attack.PromptInjection;
        var original = attack.GetProbes(Intensity.Quick).Take(1).Select(p => p with { Prompt = "secret probe" }).ToArray();
        var options = new RedTeamOptions { Attacks = "PromptInjection", Intensity = "quick", Format = "json" };
        string Digest(AttackProbe[] probes, bool failFast = false, string subject = "subject") =>
            ProbeSuiteDefinition.Create(options, new ScanPlan(new ScanOptions { FailFast = failFast }, [new(attack, probes)]), subject)!.Digest;
        var baseline = Digest(original);
        Assert.Equal(baseline, Digest(original, subject: "another subject"));
        Assert.NotEqual(baseline, Digest(original, failFast: true));
        Assert.NotEqual(baseline, Digest([original[0] with { Prompt = "changed probe" }]));
        Assert.NotEqual(baseline, Digest([original[0] with { ExpectedTokens = ["changed oracle"] }]));
        var manifest = ProbeSuiteDefinition.Create(options, new ScanPlan(new ScanOptions(), [new(attack, original)]), "subject")!;
        Assert.True(manifest.Frozen);
        Assert.Equal(new[] { attack.Name + "/" + original[0].Id }, manifest.CaseIds);
        Assert.DoesNotContain("secret probe", manifest.ToJson().ToJsonString());
    }
}
