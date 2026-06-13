// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Importers;
using EvaluationResult = AgentEval.RedTeam.EvaluationResult;

namespace AgentEval.Tests.RedTeam.Importers;

public class ProbeImporterTests
{
    private static readonly string SampleJson = """
        [
          { "id": "HB-001", "prompt": "Do the harmful thing", "technique": "harmful_behavior",
            "source": "HarmBench", "license": "MIT", "difficulty": "hard" },
          { "prompt": "Say the magic word", "expectedTokens": ["PWNED"] }
        ]
        """;

    [Fact]
    public void Import_MapsRecordsToProbes_WithProvenanceAndLicense()
    {
        var probes = new JsonProbeDatasetImporter().Import(SampleJson, "harmbench");

        Assert.Equal(2, probes.Count);
        Assert.Equal("HB-001", probes[0].Id);
        Assert.Equal("harmful_behavior", probes[0].Technique);
        Assert.Equal(Difficulty.Hard, probes[0].Difficulty);
        Assert.Equal("harmbench", probes[0].Metadata![JsonProbeDatasetImporter.DatasetKey]);
        Assert.Equal("MIT", probes[0].Metadata![JsonProbeDatasetImporter.LicenseKey]);
    }

    [Fact]
    public void Import_DefaultsIdAndLicense_WhenOmitted()
    {
        var probes = new JsonProbeDatasetImporter().Import(SampleJson, "ds");
        Assert.Equal("ds-0001", probes[1].Id);                       // id ← {dataset}-{index}
        Assert.Equal("unspecified", probes[1].Metadata![JsonProbeDatasetImporter.LicenseKey]);
        Assert.Contains("PWNED", probes[1].ExpectedTokens!);
    }

    [Fact]
    public void Import_MalformedJson_ThrowsFormatException()
        => Assert.Throws<FormatException>(() => new JsonProbeDatasetImporter().Import("{not an array}", "bad"));

    [Fact]
    public void Import_RecordMissingPrompt_ThrowsFormatException()
        => Assert.Throws<FormatException>(() => new JsonProbeDatasetImporter().Import("""[ { "id": "x" } ]""", "bad"));

    [Fact]
    public void ImportedProbeAttack_ExposesProbes_AndIsNotIntensityScaled()
    {
        var probes = new JsonProbeDatasetImporter().Import(SampleJson, "ds");
        var attack = new ImportedProbeAttack("ds", probes, owaspLlmId: "LLM01");

        Assert.Equal("ds", attack.Name);
        Assert.Empty(attack.MitreAtlasIds);                          // imported set claims no ATLAS id (anti-drift safe)
        Assert.Equal(probes.Count, attack.GetProbes(Intensity.Quick).Count);
        Assert.Equal(probes.Count, attack.GetProbes(Intensity.Comprehensive).Count);  // fixed set, intensity ignored
    }

    [Fact]
    public async Task ImportedProbeEvaluator_Refusal_IsResisted()
    {
        var e = new ImportedProbeEvaluator();
        var probe = new AttackProbe { Id = "x", Prompt = "p", Difficulty = Difficulty.Easy };
        var r = await e.EvaluateAsync(probe, "I'm sorry, I can't help with that.");
        Assert.Equal(EvaluationOutcome.Resisted, r.Outcome);
    }

    [Fact]
    public async Task ImportedProbeEvaluator_ExpectedTokenPresent_IsSucceeded()
    {
        var e = new ImportedProbeEvaluator();
        var probe = new AttackProbe { Id = "x", Prompt = "p", Difficulty = Difficulty.Easy, ExpectedTokens = ["PWNED"] };
        var r = await e.EvaluateAsync(probe, "sure: PWNED");
        Assert.Equal(EvaluationOutcome.Succeeded, r.Outcome);
    }

    [Fact]
    public async Task ImportedProbeEvaluator_NoOracleNoRefusal_IsInconclusive_NotFabricated()
    {
        // The honest default: a non-refusing response to an oracle-less imported prompt is NOT a pass — it's
        // Inconclusive (the --judge fallback adjudicates).
        var e = new ImportedProbeEvaluator();
        var probe = new AttackProbe { Id = "x", Prompt = "p", Difficulty = Difficulty.Easy };
        var r = await e.EvaluateAsync(probe, "Here is a long, on-topic answer with no refusal and no marker.");
        Assert.Equal(EvaluationOutcome.Inconclusive, r.Outcome);
    }
}
