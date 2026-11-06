// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// Before the benchmark makes any judge call, the screen says which judge grades the criteria — the provider, the model
/// and the variables that chose it — or that none does, at every verbosity: a tester with an exported OPENAI_API_KEY sees
/// that key's provider being used. The key itself is never printed or written.
/// </summary>
public sealed class JudgeLineTests : IDisposable
{
    private const string Key = "sk-test-not-a-real-key-7d41";

    /// <summary>A loopback port nothing listens on: a call to it fails on this machine and never reaches a provider.</summary>
    private const string ClosedLoopbackEndpoint = "http://127.0.0.1:9/v1";

    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task HostJudge_TheQuietScreenNamesTheJudgeBeforeTheResult()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();

        var run = await _rig.RunAsync(DemoRig.Consent, "--verbosity", "quiet");

        run.AssertExit(0);
        var lines = run.OutLines;
        var judge = Array.FindIndex(lines, l => l.StartsWith("  judge: the judge client the host supplied", StringComparison.Ordinal));
        var result = Array.FindIndex(lines, l => l.StartsWith("Result: ", StringComparison.Ordinal));
        Assert.True(judge >= 0, "The quiet screen does not name the judge.\n" + run.Describe());
        Assert.True(judge < result, "The judge line must come before the result.\n" + run.Describe());
    }

    [Fact]
    public async Task ExportedOpenAiKey_TheJudgeLineNamesTheProviderTheModelAndTheVariable_AndNeverTheKey()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Environment["OPENAI_API_KEY"] = Key;
        _rig.Environment["OPENAI_BASE_URL"] = ClosedLoopbackEndpoint;

        // The prompt takes the one turn the cap allows, so the benchmark stops before its first case is sent and no judge
        // call is made; the judge is still resolved from the environment and named.
        var run = await _rig.RunAsync(DemoRig.Consent, "--verbosity", "quiet", "--max-turns", "1");

        run.AssertExit(11);
        Assert.Contains(run.OutLines, l => l.StartsWith(
            "  judge: OpenAI, model gpt-4o-mini (auto-detected from OPENAI_API_KEY; AI_INFERENCE_PROVIDER is not set)", StringComparison.Ordinal));
        Assert.Contains("auto-detected from OPENAI_API_KEY", run.Text("summary.md"), StringComparison.Ordinal);
        Assert.Equal(1, _rig.Handler.CountOf("SendMessage"));
        Assert.All(run.Json("results.json").GetProperty("cases").EnumerateArray(), c => Assert.Equal("NOT MEASURED", c.GetProperty("verdict").GetString()));

        Assert.DoesNotContain(Key, run.Screen, StringComparison.Ordinal);
        var hits = LeakScanner.ScanFiles([Key], run.Folder);
        Assert.True(hits.Count == 0, "The judge key was written:\n" + string.Join("\n", hits));
    }

    [Fact]
    public async Task NoJudgeConfigured_TheQuietScreenSaysSoBeforeTheResult()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();

        var run = await _rig.RunAsync(DemoRig.Consent, "--verbosity", "quiet");

        run.AssertExit(11);
        Assert.Contains(run.OutLines, l => l.StartsWith("  judge: none: no judge model is configured", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_AnExplicitSelector_IsNamedWithItsValue()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AI_INFERENCE_PROVIDER"] = "openai",
            ["OPENAI_API_KEY"] = Key,
            ["OPENAI_BASE_URL"] = ClosedLoopbackEndpoint,
            ["OPENAI_MODEL"] = "gpt-test-model",
        };

        using var judge = JudgeResolver.Resolve(noJudge: false, injected: null, name => env.TryGetValue(name, out var value) ? value : null);

        Assert.True(judge.IsAvailable);
        Assert.StartsWith("OpenAI, model gpt-test-model (AI_INFERENCE_PROVIDER=openai)", judge.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, judge.Description, StringComparison.Ordinal);
    }
}
