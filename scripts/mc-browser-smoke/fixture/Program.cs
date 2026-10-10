using System.Text.Json;
using AgentEval.Tests.MissionControl.Fixtures;

// Writes the fixture into args[0]/.agenteval and prints the ids the smoke script navigates to.
var manifest = await MissionControlFixtureBuilder.BuildAsync(args[0]);
var run = manifest.RunIds.First(kv => kv.Key.Kind == FixtureRunKind.Eval);
var evidence = manifest.EvidenceRefs.First();
Console.WriteLine(JsonSerializer.Serialize(new
{
    runId = run.Value,
    subjectKind = run.Key.Subject.Kind.ToString().ToLowerInvariant(),
    subjectName = run.Key.Subject.Name,
    regulation = evidence.Regulation,
    evidenceKind = evidence.Subject.Kind.ToString().ToLowerInvariant(),
    evidenceName = evidence.Subject.Name,
    evidenceTs = evidence.Timestamp,
    campaignId = manifest.CampaignId,
}));
