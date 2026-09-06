// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// ADR-031 <b>S2</b> — <c>ScenarioResult.Input</c> and <c>stimulusHash</c>: persist what was ASKED,
/// and hash it, so two runs can be SHOWN to have been given the same stimulus.
/// </summary>
/// <remarks>
/// <para>
/// Every test here is wired to the RULE rather than to the type existing. The three that matter
/// most are the ones that fail in the flattering direction if they are got wrong: a digest that
/// moves with a git checkout, a null that reads as agreement, and a new field that silently changes
/// every stored scenario file.
/// </para>
/// </remarks>
public class StimulusHashTests
{
    // The store's own serializer settings. The byte-identity claim below is only true under these,
    // so it is asserted under these rather than under the defaults.
    private static readonly JsonSerializerOptions s_storeLike = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── the digest itself ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Digest_IsIdenticalAcrossLineEndings()
    {
        // THE POINT OF THE NORMALISATION. The same prompt checked out on Windows and on Linux
        // differs by \r per line. A digest that moved with the checkout would make every
        // cross-platform comparison read "different stimulus", `agenteval compare` would refuse
        // everything, and an --allow-incomparable escape hatch would be in every CI script inside a
        // month — ADR-031 finding V2, one layer down.
        string lf = "line one\nline two\nline three";
        string crlf = "line one\r\nline two\r\nline three";
        string cr = "line one\rline two\rline three";

        Assert.Equal(StimulusHash.Of(lf), StimulusHash.Of(crlf));
        Assert.Equal(StimulusHash.Of(lf), StimulusHash.Of(cr));
        Assert.True(StimulusHash.SameStimulus(StimulusHash.Of(lf), StimulusHash.Of(crlf)));
    }

    [Fact]
    public void Digest_PreservesEverythingElse()
    {
        // Two prompts differing in case, in surrounding whitespace, or in one character are two
        // prompts. A digest that erased any of that would let an incomparable pair report as
        // comparable, which is the flattering direction.
        Assert.NotEqual(StimulusHash.Of("Delete the record"), StimulusHash.Of("delete the record"));
        Assert.NotEqual(StimulusHash.Of("ask"), StimulusHash.Of(" ask"));
        Assert.NotEqual(StimulusHash.Of("ask"), StimulusHash.Of("ask "));
        Assert.NotEqual(StimulusHash.Of("budget 600"), StimulusHash.Of("budget 601"));
    }

    [Fact]
    public void Digest_IsAbsentRatherThanEmpty_WhenThereIsNothingToHash()
    {
        // An "empty digest" that looked like a value would let two producers that both recorded
        // nothing compare as "the same stimulus". Absent means absent.
        Assert.Null(StimulusHash.Of(null));
        Assert.Null(StimulusHash.Of(""));
    }

    [Fact]
    public void Digest_NamesItsAlgorithm()
    {
        string? digest = StimulusHash.Of("anything");
        Assert.NotNull(digest);
        Assert.StartsWith(StimulusHash.Prefix, digest, StringComparison.Ordinal);
        Assert.Equal(StimulusHash.Prefix.Length + 64, digest!.Length);
        Assert.Equal(digest, digest.ToLowerInvariant());
    }

    [Fact]
    public void Digest_IsStableAcrossCalls()
    {
        // A comparability fact recomputed per process is not a comparability fact.
        Assert.Equal(StimulusHash.Of("a stimulus"), StimulusHash.Of("a stimulus"));
    }

    // ── "unknown" is never "the same" ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("sha256:abc", null)]
    [InlineData(null, "sha256:abc")]
    [InlineData("", "")]
    public void SameStimulus_RefusesWhenEitherSideRecordedNothing(string? left, string? right)
    {
        // ⚠ THE ONE THAT FAILS FLATTERINGLY IF IT IS WRONG. Two runs that both recorded no digest
        // are not two runs that were asked the same thing. Reading null as agreement is the
        // silent-{} shape ADR-030 §4.2 rejects, and it would let `agenteval compare` emit deltas
        // across a pair it cannot compare — which is the exact behaviour S5 exists to refuse.
        Assert.False(StimulusHash.SameStimulus(left, right));
    }

    [Fact]
    public void SameStimulus_AgreesOnlyOnAnActualMatch()
    {
        string? a = StimulusHash.Of("what should I take to Iceland?");
        string? b = StimulusHash.Of("what should I take to Iceland?");
        string? c = StimulusHash.Of("what should I take to Iceland");

        Assert.True(StimulusHash.SameStimulus(a, b));
        Assert.False(StimulusHash.SameStimulus(a, c));
    }

    // ── the persistence boundary ────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioResult_WithNoStimulus_SerialisesExactlyAsBefore()
    {
        // ⚠ THE NON-BREAKING CLAIM, ASSERTED RATHER THAN ANNOUNCED. Adding a member to a record
        // that 46 stored content hashes cover is only safe if a producer that does not set it emits
        // the same bytes. Under the store's own options (WhenWritingNull) it does.
        var before = Scenario(stimulusHash: null);

        string json = JsonSerializer.Serialize(before, s_storeLike);

        Assert.DoesNotContain("stimulusHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, JsonDocument.Parse(json).RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void ScenarioResult_WithAStimulus_CarriesTheDigest()
    {
        var withHash = Scenario(stimulusHash: StimulusHash.Of("the question"));

        string json = JsonSerializer.Serialize(withHash, s_storeLike);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("stimulusHash", out var value));
        Assert.Equal(StimulusHash.Of("the question"), value.GetString());

        var round = JsonSerializer.Deserialize<ScenarioResult>(json, s_storeLike)!;
        Assert.Equal(withHash.StimulusHash, round.StimulusHash);
    }

    // ── the producer ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToScenarioResult_WithoutAnInput_IsUnchanged()
    {
        // The default is what the method hard-coded before this change: Input "", no digest.
        var scenario = EvalResultPersistence.ToScenarioResult(Result(), "scen-1", "Scenario 1");

        Assert.Equal(string.Empty, scenario.Input);
        Assert.Null(scenario.StimulusHash);
    }

    [Fact]
    public void ToScenarioResult_WithAnInput_RecordsItAndItsDigest()
    {
        const string stimulus = "Does this system retain personal data beyond the stated period?";

        var scenario = EvalResultPersistence.ToScenarioResult(
            Result(), "scen-1", "Scenario 1", assertions: null, input: stimulus);

        Assert.Equal(stimulus, scenario.Input);
        Assert.Equal(StimulusHash.Of(stimulus), scenario.StimulusHash);

        // …and the recorded digest is a digest OF THE RECORDED INPUT. A hash of something else
        // would compare two runs on a fact neither file carries.
        Assert.True(StimulusHash.SameStimulus(scenario.StimulusHash, StimulusHash.Of(scenario.Input)));
    }

    [Fact]
    public void ToScenarioResult_WithAnEmptyInput_RecordsNoDigest()
    {
        var scenario = EvalResultPersistence.ToScenarioResult(
            Result(), "scen-1", "Scenario 1", assertions: null, input: "");

        Assert.Equal(string.Empty, scenario.Input);
        Assert.Null(scenario.StimulusHash);
    }

    // ── the FIRST CONSUMER, because a field with no producer is dead data ───────────────────

    [Fact]
    public void TheCompositeRunners_PassTheirInputThrough()
    {
        // ADR-031 finding V7 cuts a field that "gates nothing and cannot go stale detectably".
        // StimulusHash would be exactly that if nothing wrote it, so the three composite runners
        // that HAVE a stimulus were wired to pass it. This asserts the wiring by source, and — the
        // lesson of 8f3e11c7 — it asserts its own INPUT too: a scan that found no files and a scan
        // that found no offenders are indistinguishable otherwise.
        string root = RepositoryRoot();
        string[] runners =
        [
            Path.Combine(root, "src", "AgentEval.Compliance.Gdpr", "Articles", "GdprBenchmarkRunner.cs"),
            Path.Combine(root, "src", "AgentEval.Compliance.EuAiAct", "Articles", "EuAiActBenchmarkRunner.cs"),
            Path.Combine(root, "src", "AgentEval.Evals.Agentic", "Composition", "AgenticBenchmarkRunner.cs"),
        ];

        foreach (string runner in runners)
        {
            Assert.True(File.Exists(runner), $"{runner} is not where this test expects it — the scan is asserting nothing.");

            string body = File.ReadAllText(runner);
            Assert.Contains("ToScenarioResult(", body, StringComparison.Ordinal);
            Assert.Contains("input: input.Query", body, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"AgentEval.sln was not found above {AppContext.BaseDirectory}.");
    }

    private static ScenarioResult Scenario(string? stimulusHash) => new(
        Id: "scen-1",
        Name: "Scenario 1",
        Input: "",
        Output: "{}",
        Passed: true,
        Score: 1.0,
        Metrics: new Dictionary<string, double>(),
        Assertions: [],
        Duration: TimeSpan.Zero,
        EstimatedCost: 0)
    {
        StimulusHash = stimulusHash,
    };

    private static EvalResult Result() =>
        EvalResult.Skipped(new StubEval(), "nothing to evaluate — this test is about persistence, not scoring");

    private sealed class StubEval : IEval
    {
        public string Key => "stub";
        public string Name => "Stub";
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
