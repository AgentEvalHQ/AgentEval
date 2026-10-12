using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runner;
using AgentEval.Results.Tests.Corpus;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Runner;

public sealed class AefJobRunnerTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "aef-async-job-" + Guid.NewGuid().ToString("N"));
    private static readonly AefTime Start = AefTime.Parse("2026-10-09T09:00:00Z");

    public void Dispose()
    {
        if (Directory.Exists(_output)) Directory.Delete(_output, recursive: true);
    }

    [Fact]
    public void ElapsedClock_AdvancesMonotonicallyAcrossFractionalSecondBoundary()
    {
        var provider = new ManualTime();
        var clock = new AefElapsedJobClock(AefTime.Parse("2026-10-09T09:00:00.999999999Z"), provider);
        provider.Timestamp = 2;
        Assert.Equal(AefTime.Parse("2026-10-09T09:00:01.000000199Z"), clock.Now);
        clock.Advance(50); // Execution time is measured; synthetic durations never advance a real clock.
        Assert.Equal(AefTime.Parse("2026-10-09T09:00:01.000000199Z"), clock.Now);
    }

    [Fact]
    public async Task RunAsync_AwaitingCase_LeavesRunningFolderAndThenSealsLiveEvidence()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = Run(new Case(async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return Passed();
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(execution.IsCompleted);
            var dir = RunDirectory();
            Assert.Equal("running", (string?)Json(dir, "run.json")["status"]);
            Assert.Empty(File.ReadAllText(Path.Combine(dir, "results.ndjson")));
        }
        finally { release.TrySetResult(); }
        var result = await execution;
        Assert.Equal("job.sealed", result.Terminal);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
        Assert.Equal("live", (string?)Json(RunDirectory(), "run.json")["execution"]!["targetMode"]);
        AssertStream();
    }

    [Fact]
    public async Task RunAsync_CancelAwaitedCase_SealsEmptyAbortedRunBeforeCancellationEvent()
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = Run(new Case(async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Passed();
        }), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        var result = await execution;
        Assert.Equal("job.cancelled", result.Terminal);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
        Assert.Equal("aborted", (string?)Json(RunDirectory(), "run.json")["status"]);
        Assert.Empty(File.ReadAllText(Path.Combine(RunDirectory(), "results.ndjson")));
        var events = Events();
        Assert.Equal("evidence.produced", (string?)events[^2]["kind"]);
        Assert.Equal("job.cancelled", (string?)events[^1]["kind"]);
        AssertStream();
    }

    [Theory]
    [InlineData(3, "maxUsd")]
    [InlineData(1.5, null)]
    public async Task RunAsync_CaseExceedsCostBound_ReportsActualCostAndStops(double usd, string? limit)
    {
        var result = await Run(new Case(_ => ValueTask.FromResult(Passed() with { Usd = usd })));
        Assert.Equal("job.failed", result.Terminal);
        Assert.Equal(limit, result.Limit);
        Assert.Equal(usd, (double)Json(RunDirectory(), "summary.json")["cost"]!["totalUsd"]!);
        Assert.Equal("aborted", (string?)Json(RunDirectory(), "run.json")["status"]);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
    }

    [Fact]
    public async Task RunAsync_CaseExceedsTimeBound_CancelsCaseAndSealsError()
    {
        var result = await Run(new Case(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Passed();
        }, secondsBound: 1)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("job.failed", result.Terminal);
        Assert.Null(result.Limit); // This case's bound expired; the plan has no timeout limit.
        Assert.Contains("time bound", result.Reason);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
        Assert.Equal("error", (string?)JsonNode.Parse(File.ReadAllLines(Path.Combine(RunDirectory(), "results.ndjson")).Single())!["state"]);
        AssertStream();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CaseThrows_RetainsEvidenceWithoutExceptionContent(bool ioFailure)
    {
        var result = await Run(new Case(_ => throw (ioFailure
            ? new IOException("secret path and key") : new InvalidOperationException("secret prompt")), incurredUsd: 0.2));
        Assert.Equal(ioFailure ? "job.failed" : "job.sealed", result.Terminal);
        Assert.Null(result.Limit);
        var dir = RunDirectory();
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(dir).Outcome);
        Assert.Equal(0.2, (double)Json(dir, "summary.json")["cost"]!["totalUsd"]!);
        var lines = File.ReadAllLines(Path.Combine(dir, "results.ndjson"));
        if (ioFailure) Assert.Empty(lines);
        else Assert.Equal("error", (string?)JsonNode.Parse(Assert.Single(lines))!["state"]);
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(_output, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
        AssertStream();
    }

    private Task<AefJobResult> Run(IAefJobCase testCase, CancellationToken ct = default) =>
        AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase), _output, new AefJobOptions { At = Start }, ct);

    private static AefJobCaseResult Passed() => new(new AefResult
    {
        CaseId = "case-1", Path = "check", Evaluator = new AefEvaluator("test-async"), State = AefState.Passed,
    }, 0.1, 1);

    private string RunDirectory() => Path.GetDirectoryName(Assert.Single(Directory.GetFiles(_output, "run.json", SearchOption.AllDirectories)))!;
    private static JsonNode Json(string dir, string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, name)))!;
    private List<JsonNode> Events() => File.ReadAllLines(Path.Combine(_output, "events.ndjson")).Select(l => JsonNode.Parse(l)!).ToList();
    private void AssertStream()
    {
        Assert.Empty(RunnerEventStream.Verify(Events(), JsonNode.Parse(Plan())));
    }

    private static byte[] Plan() => Encoding.UTF8.GetBytes("""
        {"schemaVersion":"1.0","planId":"async-unit","subject":{"ref":"model:test","version":"1"},
         "suites":[{"ref":"suite:test","version":"1","lane":"quality"}],"limits":{"maxUsd":2},
         "contentCapture":"off","targetMode":"live","isolation":"process","provider":"local"}
        """);

    private static JsonObject Manifest()
    {
        var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AefCorpus.Conformance, "jobs", "runner.json")))!.AsObject();
        manifest["targetModes"] = new JsonArray("live");
        return manifest;
    }

    private sealed class Case(Func<CancellationToken, ValueTask<AefJobCaseResult>> run, long secondsBound = 10, double incurredUsd = 0) : IAefJobCase
    {
        public string CaseId => "case-1";
        public double UsdLow => 0;
        public double UsdBound => 1;
        public long SecondsBound => secondsBound;
        public double IncurredUsd => incurredUsd;
        public ValueTask<AefJobCaseResult> RunAsync(CancellationToken cancellationToken) => run(cancellationToken);
    }

    private sealed class Target(IAefJobCase testCase) : IAefJobTarget
    {
        public AefTargetMode TargetMode => AefTargetMode.Live;
        public string Isolation => "process";
        public string PriceTable => "unit-prices";
        public string Fingerprint => "unit-target";
        public long CloseSeconds => 1;
        public IReadOnlyList<IAefJobSuite> Suites => [new Suite(testCase)];
    }

    private sealed class Suite(IAefJobCase testCase) : IAefJobSuite
    {
        public string Ref => "suite:test";
        public string Version => "1";
        public string Digest => "sha256:" + new string('a', 64);
        public string CasePath => "check";
        public IReadOnlyList<IAefJobCase> Cases => [testCase];
        public IReadOnlyList<AefMetric> Metrics => [AefScriptedRunner.PassRate];
        public IReadOnlyList<AefSummaryEntry> SummaryEntries => [new() { Metric = "pass-rate", Path = "check" }];
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
