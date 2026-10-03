// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.RedTeam;
using AgentEval.Benchmarks;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// The OWASP, MITRE ATLAS and NIST benchmark runs grade their attacks with a judge model when given one, judge first,
/// as <c>agenteval redteam --judge</c> does. Through 0.42 they held a judge and never called it: keyword oracles alone
/// decided every verdict.
/// </summary>
/// <remarks>
/// These use the full presets. The smoke presets run prompt injection, jailbreak and PII leakage, which deterministic
/// canary checks decide without a judge, so a smoke run correctly makes no judge calls.
/// </remarks>
public sealed class RedTeamBenchmarkJudgeTests
{
    private const string Refusal = "I cannot help with that request. Please rephrase or ask something else.";

    private sealed class RefusingAgent : IEvaluableAgent
    {
        public string Name => "RefusingAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Text = Refusal });
    }

    /// <summary>A judge that answers every grading call with one fixed verdict, and counts the calls.</summary>
    private sealed class FixedVerdictJudge(string verdict) : IChatClient
    {
        private int _calls;

        public int Calls => _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                $"VERDICT: {verdict}\nCONFIDENCE: 0.95\nQUOTE: \"{Refusal}\"\nREASON: fixed test verdict")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    public static TheoryData<string> Benchmarks => new() { "owasp", "mitre", "nist" };

    private static (Func<IChatClient?, string?, Task<(EvalResult Result, RedTeamResult Scan)>> Run, string Name) Build(string family) =>
        family switch
        {
            "owasp" => (async (judge, model) =>
            {
                var run = OwaspBenchmark.Top10();
                if (judge is not null) run.WithJudge(judge, model!);
                var scan = await run.ScanAsync(new RefusingAgent());
                return (run.BuildEvalResult(scan), scan);
            }, family),
            "mitre" => (async (judge, model) =>
            {
                var run = MitreBenchmark.AtlasBaseline();
                if (judge is not null) run.WithJudge(judge, model!);
                var scan = await run.ScanAsync(new RefusingAgent());
                return (run.BuildEvalResult(scan), scan);
            }, family),
            "nist" => (async (judge, model) =>
            {
                var run = NistBenchmark.RmfBaseline();
                if (judge is not null) run.WithJudge(judge, model!);
                var scan = await run.ScanAsync(new RefusingAgent());
                return (run.BuildEvalResult(scan), scan);
            }, family),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

    [Theory]
    [MemberData(nameof(Benchmarks))]
    public async Task WithAJudge_TheJudgeGradesTheAttacks_AndTheResultNamesIt(string family)
    {
        var judge = new FixedVerdictJudge("RESISTED");

        var (result, _) = await Build(family).Run(judge, "test-judge-model");

        Assert.True(judge.Calls > 0, $"{family}: the judge was never called.");
        Assert.Equal("test-judge-model", result.Provenance.JudgeModel);
    }

    [Theory]
    [MemberData(nameof(Benchmarks))]
    public async Task WithoutAJudge_NoJudgeIsNamed(string family)
    {
        var (result, _) = await Build(family).Run(null, null);

        Assert.Null(result.Provenance.JudgeModel);
    }

    [Theory]
    [MemberData(nameof(Benchmarks))]
    public async Task TheJudgesVerdict_ChangesWhatTheKeywordOraclesWouldDecide(string family)
    {
        // The agent refuses everything, so the keyword oracles grade it as resisting. A judge that finds every attack
        // succeeded must change verdicts; if the oracles still decided alone, both runs would be identical.
        var (_, keywordOnly) = await Build(family).Run(null, null);
        var (_, judged) = await Build(family).Run(new FixedVerdictJudge("SUCCEEDED"), "test-judge-model");

        static string Outcomes(RedTeamResult r) => string.Join(";", r.AttackResults
            .Select(a => $"{a.AttackName}:{a.ResistedCount}/{a.SucceededCount}/{a.InconclusiveCount}")
            .OrderBy(x => x, StringComparer.Ordinal));

        Assert.NotEqual(Outcomes(keywordOnly), Outcomes(judged));
    }

    [Fact]
    public async Task JudgeFirst_TheJudgeDecidesTheSemanticProbes()
    {
        // InsecureOutput is graded by a Composite Judge under judge-first grading: a judge that finds the attack
        // succeeded must turn the oracle's "resisted" into "succeeded". Fallback-only grading could not do this, so
        // this pins judge-FIRST rather than "the judge was called at all".
        static (int Resisted, int Succeeded) InsecureOutput(RedTeamResult r)
        {
            var a = r.AttackResults.Single(x => x.AttackName == "InsecureOutput");
            return (a.ResistedCount, a.SucceededCount);
        }

        var (_, keywordOnly) = await Build("owasp").Run(null, null);
        var (_, judged) = await Build("owasp").Run(new FixedVerdictJudge("SUCCEEDED"), "test-judge-model");

        Assert.True(InsecureOutput(keywordOnly).Resisted > 0, "precondition: the oracle grades the refusals as resisted");
        Assert.Equal(0, InsecureOutput(judged).Resisted);
        Assert.True(InsecureOutput(judged).Succeeded > 0);
    }

    [Theory]
    [MemberData(nameof(Benchmarks))]
    public async Task TestedLeaves_NameTheJudge_WhenOneGraded(string family)
    {
        var (judgedResult, _) = await Build(family).Run(new FixedVerdictJudge("RESISTED"), "test-judge-model");
        var (plainResult, _) = await Build(family).Run(null, null);

        static IEnumerable<EvalResult> Tested(EvalResult root) =>
            (root.Details.SubResults ?? []).Where(l => l.Score.Label != "skipped");

        // The root names the judge too: it is what `compare` reads to refuse a judged-vs-unjudged comparison.
        Assert.Equal("test-judge-model", judgedResult.Provenance.JudgeModel);
        Assert.Null(plainResult.Provenance.JudgeModel);

        Assert.NotEmpty(Tested(judgedResult));
        Assert.All(Tested(judgedResult), l =>
        {
            Assert.Equal("judge-first", l.Provenance.Type);
            Assert.Equal("test-judge-model", l.Provenance.JudgeModel);
        });
        Assert.All(Tested(plainResult), l => Assert.Equal("code", l.Provenance.Type));
    }
}
