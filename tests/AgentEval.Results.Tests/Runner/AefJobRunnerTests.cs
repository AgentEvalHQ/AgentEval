using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runner;
using AgentEval.Results.Signatures;
using AgentEval.Results.Tests.Corpus;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Tests.Runner;

public sealed class AefJobRunnerTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "aef-async-job-" + Guid.NewGuid().ToString("N"));
    private static readonly AefTime Start = AefTime.Parse("2026-10-09T09:00:00Z");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ContentFromTarget_ObeysPlanAndLinksExactText(bool capture)
    {
        var document = JsonNode.Parse(Plan())!;
        document["contentCapture"] = capture ? "on" : "off";
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        var target = new Target(new Case(_ => ValueTask.FromResult(Passed() with
        {
            Content = [new(AefEvidenceKind.Input, "private-input α\r\n"),
                new(AefEvidenceKind.JudgeReasoning, "private-reason")],
        })));
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, _output, new AefJobOptions { At = Start });
        Assert.Equal("job.sealed", result.Terminal);
        var folder = RunDirectory();
        var verified = AefRunVerifier.Verify(folder);
        Assert.Equal(AefOutcome.Intact, verified.Outcome);
        Assert.Empty(verified.Problems);
        var line = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(folder, "results.ndjson"))))!;
        if (capture)
        {
            Assert.Equal(2, line["evidence"]!.AsArray().Count);
            Assert.NotNull(line["reasoning"]);
            var blobs = Directory.GetFiles(Path.Combine(folder, "blobs"), "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
            Assert.Contains("private-input α\r\n", blobs);
            Assert.Contains("private-reason", blobs);
        }
        else
        {
            Assert.Null(line["evidence"]);
            Assert.Null(line["reasoning"]);
            Assert.False(Directory.Exists(Path.Combine(folder, "blobs")));
            Assert.DoesNotContain("private-", string.Concat(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunWorkspaceAsync_Signer_SignsCompletedAndCancelledEvidence(bool cancel)
    {
        using var signer = EcdsaP256Signer.Generate();
        using var cancellation = new CancellationTokenSource();
        var target = new Target(new Case(token =>
        {
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(Passed());
        }));
        var bytes = Plan();
        var result = await AefJobRunner.RunWorkspaceAsync(bytes, Manifest(), target, _output,
            new AefJobOptions { At = Start, Signer = signer }, cancellation.Token);
        Assert.Equal(cancel ? "job.cancelled" : "job.sealed", result.Terminal);
        var job = Path.Combine(_output, result.JobId);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(job, "plan.json")));
        Assert.Equal(signer.KeyId, (string?)Json(job, "runner-options.json")["signingKeyId"]);
        var verified = AefRunVerifier.Verify(RunDirectory(), new AefVerifyOptions
            { Policy = new TrustPolicy([new TrustedKey("test:runner", signer.PublicKey)]) });
        Assert.Equal(AefOutcome.Intact, verified.Outcome);
        Assert.Empty(verified.Problems);
        Assert.Equal("test:runner", Assert.Single(verified.SignedBy!));
        Assert.Equal(cancel ? "aborted" : "completed", (string?)Json(RunDirectory(), "run.json")["status"]);
        Assert.NotEmpty(signer.Sign([1, 2, 3])); // The engine never disposes its caller-owned signer.
    }

    [Fact]
    public async Task RunAsync_IoFailureWithIncurredOverrun_ReportsTheCostLimit()
    {
        var testCase = new Case(_ => throw new IOException("private provider error"), incurredUsd: 3);
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase), _output, new AefJobOptions { At = Start });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Equal("maxUsd", result.Limit);
        Assert.DoesNotContain("private", result.Reason);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
        Assert.Equal(3, (double)Json(RunDirectory(), "summary.json")["cost"]!["totalUsd"]!);
    }

    public void Dispose()
    {
        if (Directory.Exists(_output)) Directory.Delete(_output, recursive: true);
    }

    [Theory]
    [InlineData(false, "job.failed")]
    [InlineData(true, "job.refused")]
    public async Task RunAsync_AdditionalCap_StopsBeforeCallsAndOptionallyRefuses(bool upfront, string terminal)
    {
        var calls = 0;
        var testCase = new Case(_ => { calls++; return ValueTask.FromResult(Passed()); });
        var target = new Target(testCase);
        var options = new AefJobOptions { At = Start, MaxUsd = 0.5, RefuseOverBudget = upfront };
        Assert.Equal(!upfront, AefJobRunner.Analyze(Plan(), Manifest(), target, options).Accepted);
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), target, _output, options);
        Assert.Equal(terminal, result.Terminal);
        Assert.Equal(0, calls);
        Assert.Equal(0.5, (double)Events()[0]["ext"]!["agenteval.runner"]!["maxUsd"]!);
        AssertStream();
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(10.0, 2.0)]
    public async Task RunWorkspaceAsync_AdditionalCap_PreservesPlanAndRecordsEffectivePolicy(double requested, double effective)
    {
        var plan = Plan();
        var result = await AefJobRunner.RunWorkspaceAsync(plan, Manifest(), new Target(new Case(_ => ValueTask.FromResult(Passed()))),
            _output, new AefJobOptions { At = Start, MaxUsd = requested });
        var folder = Path.Combine(_output, result.JobId);
        Assert.Equal("job.sealed", result.Terminal);
        Assert.Equal(plan, File.ReadAllBytes(Path.Combine(folder, "plan.json")));
        Assert.Equal(requested, (double)Json(folder, "runner-options.json")["maxUsd"]!);
        Assert.Equal(effective, (double)Json(RunDirectory(), "run.json")["costPolicy"]!["maxUsd"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Analyze_InvalidAdditionalCap_RefusesWithoutWrites(double cap)
    {
        Assert.Throws<AefInputException>(() => AefJobRunner.Analyze(Plan(), Manifest(),
            new Target(new Case(_ => ValueTask.FromResult(Passed()))), new AefJobOptions { At = Start, MaxUsd = cap }));
        Assert.False(Directory.Exists(_output));
    }

    [Fact]
    public async Task RunAsync_ActualSpendOverAdditionalCap_StopsEvenBelowOriginalPlanCap()
    {
        var testCase = new Case(_ => ValueTask.FromResult(Passed() with { Usd = 1.5 }));
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase), _output,
            new AefJobOptions { At = Start, MaxUsd = 1 });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Equal("maxUsd", result.Limit);
        Assert.Equal(1.5, (double)Json(RunDirectory(), "summary.json")["cost"]!["totalUsd"]!);
        Assert.Equal(1, (double)Json(RunDirectory(), "run.json")["costPolicy"]!["maxUsd"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
    }

    [Fact]
    public async Task RunWorkspaceAsync_ExistingJob_RefusesWithoutChangingEvidence()
    {
        var plan = Plan();
        var manifest = Manifest();
        var target = new Target(new Case(_ => ValueTask.FromResult(Passed())));
        var options = new AefJobOptions { At = Start, Clock = new AefSimulatedJobClock(Start) };
        var result = await AefJobRunner.RunWorkspaceAsync(plan, manifest, target, _output, options);
        var folder = Path.Combine(_output, result.JobId);
        Assert.Equal(plan, File.ReadAllBytes(Path.Combine(folder, "plan.json")));
        var before = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        await Assert.ThrowsAsync<AefInputException>(() => AefJobRunner.RunWorkspaceAsync(plan, manifest,
            new Target(new Case(_ => ValueTask.FromResult(Passed()))), _output,
            new AefJobOptions { At = Start, Clock = new AefSimulatedJobClock(Start) }));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(Path.Combine(folder, "runs", Assert.Single(result.Runs))).Outcome);
    }

    [Fact]
    public async Task RunAsync_ProgressFailsAfterSealing_StillAnnouncesSealedRunAndCleansUp()
    {
        using var progress = new FailOnEvidenceProgress();
        var cleanups = 0;
        var testCase = new Case(_ => ValueTask.FromResult(Passed()));
        var suite = new Suite(testCase, close: _ => { cleanups++; return ValueTask.CompletedTask; });
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase, suite), _output,
            new AefJobOptions { At = Start, Progress = progress });
        Assert.Equal("job.failed", result.Terminal);
        var run = Assert.Single(result.Runs);
        Assert.Equal(run, (string?)Assert.Single(Events()[^1]["runs"]!.AsArray()));
        Assert.Equal(1, cleanups);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
        AssertStream();
    }

    [Fact]
    public async Task RunAsync_CleanupDeadline_CancelsCooperativeCleanupAndReportsFailure()
    {
        var testCase = new Case(_ => ValueTask.FromResult(Passed()));
        var suite = new Suite(testCase, close: async ct => await Task.Delay(Timeout.InfiniteTimeSpan, ct));
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase, suite, closeSeconds: 1), _output,
            new AefJobOptions { At = Start }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("job.failed", result.Terminal);
        Assert.Null(result.Limit);
        Assert.Contains("reserved time", result.Reason);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ClosingOverrunsTime_ReportsTheLimitActuallyExceeded(bool planTimeout)
    {
        var provider = new ManualTime();
        var clock = new AefElapsedJobClock(Start, provider);
        var testCase = new Case(_ => ValueTask.FromResult(Passed()));
        var suite = new Suite(testCase, close: _ =>
        {
            provider.Timestamp = 61 * TimeSpan.TicksPerSecond;
            return ValueTask.CompletedTask;
        });
        var plan = JsonNode.Parse(Plan())!;
        if (planTimeout) plan["limits"]!["timeout"] = "PT1M";
        var result = await AefJobRunner.RunAsync(Encoding.UTF8.GetBytes(plan.ToJsonString()), Manifest(), new Target(testCase, suite),
            _output, new AefJobOptions { At = Start, Clock = clock });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Equal(planTimeout ? "timeout" : null, result.Limit);
        Assert.Equal(Start.AddSeconds(61), result.EndsAt);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
    }

    [Fact]
    public async Task RunAsync_CleanupFails_KeepsSealedEvidenceAndReportsContentFreeFailure()
    {
        var calls = 0;
        var testCase = new Case(_ => ValueTask.FromResult(Passed()));
        var suite = new Suite(testCase, close: _ =>
        {
            calls++;
            throw new IOException("secret cleanup path");
        });
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase, suite), _output, new AefJobOptions { At = Start });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Null(result.Limit);
        Assert.Equal(1, calls);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(_output, "events.ndjson")));
        AssertStream();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("cancel")]
    [InlineData("io")]
    [InlineData("invalid-result")]
    public async Task RunAsync_EveryStartedRun_ReleasesResourcesOnce(string outcome)
    {
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        bool? cleanupCancelled = null;
        bool? wasSealed = null;
        var testCase = new Case(ct =>
        {
            if (outcome == "cancel") { cancel.Cancel(); throw new OperationCanceledException(ct); }
            if (outcome == "io") throw new IOException("secret path");
            var result = Passed();
            return ValueTask.FromResult(outcome == "invalid-result" ? result with { Root = result.Root with { CaseId = "wrong" } } : result);
        });
        var suite = new Suite(testCase, close: ct =>
        {
            calls++;
            cleanupCancelled = ct.IsCancellationRequested;
            wasSealed = File.Exists(Path.Combine(RunDirectory(), "seal.json"));
            return ValueTask.CompletedTask;
        });
        var execution = AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase, suite), _output, new AefJobOptions { At = Start }, cancel.Token);
        if (outcome == "invalid-result") await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        else
        {
            var result = await execution;
            Assert.Equal(outcome switch { "cancel" => "job.cancelled", "io" => "job.failed", _ => "job.sealed" }, result.Terminal);
        }
        Assert.Equal(1, calls);
        Assert.False(cleanupCancelled);
        Assert.Equal(outcome != "invalid-result", wasSealed);
    }

    [Fact]
    public async Task RunAsync_PreparationCost_IsEstimatedReservedAndChargedWithoutCountingACase()
    {
        var preparations = 0;
        var testCase = new Case(_ => ValueTask.FromResult(Passed()));
        var suite = new Suite(testCase, _ =>
        {
            preparations++;
            return ValueTask.FromResult(new AefJobPreparationResult(true, 0.25, 0));
        }, preparationBound: 0.5);
        var target = new Target(testCase, suite);
        var options = new AefJobOptions { At = Start };
        var estimate = AefJobRunner.Analyze(Plan(), Manifest(), target, options).Estimate!;
        Assert.Equal(1, estimate.Cases);
        Assert.Equal(1.5, estimate.UsdHigh);
        Assert.Equal(0, preparations);
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), target, _output, options);
        Assert.Equal("job.sealed", result.Terminal);
        Assert.Equal(1, preparations);
        Assert.Single(Events(), e => (string?)e["kind"] == "case.completed");
        Assert.Equal(0.35, (double)Json(RunDirectory(), "summary.json")["cost"]!["totalUsd"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
        AssertStream();
    }

    [Fact]
    public async Task RunAsync_PreparationAndCaseDoNotFit_StopsBeforeEitherCallOrRunCreation()
    {
        var testCase = new Case(_ => throw new InvalidOperationException("must not execute"));
        var suite = new Suite(testCase, _ => throw new InvalidOperationException("must not prepare"), preparationBound: 1.5);
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase, suite), _output, new AefJobOptions { At = Start });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Equal("maxUsd", result.Limit);
        Assert.Empty(result.Runs);
        Assert.Empty(Directory.GetFiles(_output, "run.json", SearchOption.AllDirectories));
        AssertStream();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CancelPreparation_RetainsItsCostInEmptyAbortedRun(bool returnsReady)
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testCase = new Case(_ => throw new InvalidOperationException("must not execute"));
        var suite = new Suite(testCase, async ct =>
        {
            entered.SetResult();
            if (returnsReady)
            {
                cancel.Cancel();
                return new AefJobPreparationResult(true, 0.2, 0);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new AefJobPreparationResult(true, 0, 0);
        }, preparationBound: 0.5, preparationIncurred: 0.2);
        var execution = AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase, suite), _output, new AefJobOptions { At = Start }, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        Assert.Equal("job.cancelled", (await execution).Terminal);
        Assert.Empty(File.ReadAllText(Path.Combine(RunDirectory(), "results.ndjson")));
        Assert.Equal(0.2, (double)Json(RunDirectory(), "summary.json")["cost"]!["totalUsd"]!);
        Assert.Equal("aborted", (string?)Json(RunDirectory(), "run.json")["status"]);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
        AssertStream();
    }

    [Fact]
    public async Task RunAsync_TargetNotReady_SealsPreparationCostWithoutExecutingCase()
    {
        var testCase = new Case(_ => throw new InvalidOperationException("must not execute"));
        var suite = new Suite(testCase, _ => ValueTask.FromResult(new AefJobPreparationResult(false, 0.2, 0)), preparationBound: 0.5);
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), new Target(testCase, suite), _output, new AefJobOptions { At = Start });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Equal("The target was not ready", result.Reason);
        Assert.Null(result.Limit);
        Assert.Empty(File.ReadAllText(Path.Combine(RunDirectory(), "results.ndjson")));
        Assert.Equal(0.2, (double)Json(RunDirectory(), "summary.json")["cost"]!["totalUsd"]!);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(RunDirectory()).Outcome);
        AssertStream();
    }

    [Fact]
    public async Task RunAsync_DeliversCredentialsByPurposeWithoutChangingEnvironmentOrWritingSecrets()
    {
        const string subjectName = "S1_UNIT_SUBJECT_KEY";
        var before = Environment.GetEnvironmentVariable(subjectName);
        var plan = JsonNode.Parse(Plan())!;
        plan["credentialRefs"] = new JsonArray(
            Credential(subjectName, "SUBJECT_SECRET_PATH", "subject"),
            Credential("S1_UNIT_JUDGE_KEY", "JUDGE_SECRET_PATH", "judge"));
        var bytes = Encoding.UTF8.GetBytes(plan.ToJsonString());
        var target = new Target(new Case(_ => ValueTask.FromResult(Passed())));
        var options = new AefJobOptions { At = Start, Environment = path => path == "SUBJECT_SECRET_PATH" ? "secret-subject-value" : "secret-judge-value" };
        Assert.True(AefJobRunner.Analyze(bytes, Manifest(), target, options).Accepted);
        Assert.Null(target.Credentials);
        Assert.False(Directory.Exists(_output));
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, _output, options);
        Assert.Equal("job.sealed", result.Terminal);
        Assert.Equal("secret-subject-value", Assert.Single(target.Credentials!.ForPurpose("subject")).Value);
        Assert.Equal("secret-judge-value", Assert.Single(target.Credentials.ForPurpose("judge")).Value);
        Assert.Empty(target.Credentials.ForPurpose("attacker"));
        Assert.Equal(before, Environment.GetEnvironmentVariable(subjectName));
        var evidence = string.Concat(Directory.GetFiles(_output, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("secret-subject-value", evidence);
        Assert.DoesNotContain("secret-judge-value", evidence);
        Assert.DoesNotContain("SECRET_PATH", evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Analyze_ConflictingNameOrUnsupportedPurpose_RefusesBeforeCredentialDelivery(bool unsupported)
    {
        var plan = JsonNode.Parse(Plan())!;
        plan["credentialRefs"] = unsupported
            ? new JsonArray(Credential("API_KEY", "SECRET_FIRST_PATH", "attacker"))
            : new JsonArray(Credential("API_KEY", "SECRET_FIRST_PATH", "subject"), Credential("API_KEY", "SECRET_SECOND_PATH", "judge"));
        var bytes = Encoding.UTF8.GetBytes(plan.ToJsonString());
        var target = new Target(new Case(_ => throw new InvalidOperationException("must not execute")));
        var options = new AefJobOptions { At = Start, Environment = path => path == "SECRET_FIRST_PATH" ? "secret-first" : "secret-second" };
        var analysis = AefJobRunner.Analyze(bytes, Manifest(), target, options);
        Assert.False(analysis.Accepted);
        Assert.Contains(unsupported ? "cannot receive" : "conflicts", analysis.Reason);
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, _output, options);
        Assert.Equal("job.refused", result.Terminal);
        Assert.Equal(analysis.Reason, result.Reason);
        Assert.Null(target.Credentials);
        var evidence = File.ReadAllText(Path.Combine(_output, "events.ndjson"));
        Assert.DoesNotContain("secret-", evidence);
        Assert.DoesNotContain("SECRET_", evidence);
        Assert.Single(Events());
    }

    private static JsonObject Credential(string name, string path, string purpose) => new()
    {
        ["name"] = name, ["scheme"] = "env", ["path"] = path, ["purpose"] = purpose,
    };

    [Fact]
    public async Task Analyze_DoesNotExecuteOrWrite_AndMatchesExecutionEstimateAndProgress()
    {
        var calls = 0;
        var target = new Target(new Case(_ =>
        {
            calls++;
            return ValueTask.FromResult(Passed());
        }));
        using var progress = new MemoryStream();
        var options = new AefJobOptions { At = Start, Progress = progress };
        var analysis = AefJobRunner.Analyze(Plan(), Manifest(), target, options);
        Assert.True(analysis.Accepted);
        Assert.Null(analysis.Reason);
        Assert.Equal(0, calls);
        Assert.Equal(0, progress.Length);
        Assert.False(Directory.Exists(_output));

        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), target, _output, options);
        Assert.Equal("job.sealed", result.Terminal);
        Assert.Equal(1, calls);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_output, "events.ndjson")), progress.ToArray());
        Assert.True(progress.CanWrite);
        var estimate = Assert.Single(Events(), e => (string?)e["kind"] == "plan.estimated");
        Assert.Equal(analysis.Estimate!.Cases, (long)estimate["cases"]!);
        Assert.Equal(analysis.Estimate.UsdLow, (double)estimate["usdLow"]!);
        Assert.Equal(analysis.Estimate.UsdHigh, (double)estimate["usdHigh"]!);
        Assert.Equal(analysis.Estimate.PriceTable, (string?)estimate["priceTable"]);
    }

    [Fact]
    public async Task Analyze_UnresolvedCredential_MatchesRefusalWithoutExposingPath()
    {
        var plan = JsonNode.Parse(Plan())!;
        plan["credentialRefs"] = new JsonArray(new JsonObject
        {
            ["name"] = "API_KEY", ["scheme"] = "env", ["path"] = "SECRET_VARIABLE_PATH", ["purpose"] = "subject",
        });
        var bytes = Encoding.UTF8.GetBytes(plan.ToJsonString());
        var target = new Target(new Case(_ => throw new InvalidOperationException("must not execute")));
        using var progress = new MemoryStream();
        var options = new AefJobOptions { At = Start, Environment = _ => null, Progress = progress };
        var analysis = AefJobRunner.Analyze(bytes, Manifest(), target, options);
        Assert.False(analysis.Accepted);
        Assert.Null(analysis.Estimate);
        Assert.DoesNotContain("SECRET_VARIABLE_PATH", analysis.Reason);
        Assert.Equal(0, progress.Length);
        Assert.False(Directory.Exists(_output));
        var result = await AefJobRunner.RunAsync(bytes, Manifest(), target, _output, options);
        Assert.Equal("job.refused", result.Terminal);
        Assert.Equal(analysis.Reason, result.Reason);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_output, "events.ndjson")), progress.ToArray());
    }

    [Fact]
    public async Task RunAsync_ProgressWriteFails_PersistsFailureWithUniqueEventSequence()
    {
        using var progress = new FailingProgress();
        var target = new Target(new Case(_ => throw new InvalidOperationException("must not execute")));
        var result = await AefJobRunner.RunAsync(Plan(), Manifest(), target, _output,
            new AefJobOptions { At = Start, Progress = progress });
        Assert.Equal("job.failed", result.Terminal);
        Assert.Null(result.Limit);
        Assert.Equal(new long[] { 1, 2 }, Events().Select(e => (long)e["seq"]!));
        AssertStream();
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

    [Theory]
    [InlineData("provider")]
    [InlineData("mode")]
    [InlineData("panelSize")]
    [InlineData("rubricDigest")]
    [InlineData("calibration")]
    public async Task RunAsync_JudgeIdentity_DeduplicatesOnlyExactInstruments(string differingField)
    {
        var first = new AefJudge
        {
            Model = "same-model", Provider = "provider-a", Mode = AefJudgeMode.Panel, PanelSize = 2,
            RubricDigest = "sha256:" + new string('a', 64),
            Calibration = new AefCalibration { LabelSet = "dataset:labels", N = 10, Accuracy = 0.8, MeasuredAt = Start },
        };
        var second = differingField switch
        {
            "provider" => first with { Provider = "provider-b" },
            "mode" => first with { Mode = AefJudgeMode.Other },
            "panelSize" => first with { PanelSize = 3 },
            "rubricDigest" => first with { RubricDigest = "sha256:" + new string('b', 64) },
            _ => first with { Calibration = first.Calibration with { Accuracy = 0.9 } },
        };
        var plan = JsonNode.Parse(Plan())!.AsObject();
        plan["judges"] = new JsonArray(new JsonObject { ["model"] = "same-model" }, new JsonObject { ["model"] = "same-model" });
        var testCase = new Case(_ => ValueTask.FromResult(Passed() with
        {
            Judges = [first, second, first with { Calibration = first.Calibration with { } }],
        }));
        var result = await AefJobRunner.RunAsync(Encoding.UTF8.GetBytes(plan.ToJsonString()), Manifest(),
            new Target(testCase), _output, new AefJobOptions { At = Start });
        Assert.Equal("job.sealed", result.Terminal);
        var directory = RunDirectory();
        var judges = Json(directory, "run.json")["judges"]!.AsArray();
        Assert.Equal(2, judges.Count);
        Assert.True(JsonNode.DeepEquals(first.ToJson(), judges[0]));
        Assert.True(JsonNode.DeepEquals(second.ToJson(), judges[1]));
        var sealedJudges = Json(directory, "seal.json")["predicate"]!["judges"]!.AsArray();
        Assert.Equal(2, sealedJudges.Count);
        for (var i = 0; i < judges.Count; i++)
        {
            Assert.Equal((string?)judges[i]!["model"], (string?)sealedJudges[i]!["model"]);
            Assert.Equal((string?)judges[i]!["rubricDigest"], (string?)sealedJudges[i]!["rubricDigest"]);
        }
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
    }

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

    private sealed class Target(IAefJobCase testCase, IAefJobSuite? suite = null, long closeSeconds = 30) : IAefJobTarget
    {
        public AefJobCredentials? Credentials { get; private set; }
        public IReadOnlyCollection<string> CredentialPurposes => ["subject", "judge"];
        public void SetCredentials(AefJobCredentials credentials) => Credentials = credentials;
        public AefTargetMode TargetMode => AefTargetMode.Live;
        public string Isolation => "process";
        public string PriceTable => "unit-prices";
        public string Fingerprint => "unit-target";
        // Evidence sealing shares this allowance. Non-timing tests must tolerate busy CI disks;
        // the cooperative-deadline test explicitly selects one second.
        public long CloseSeconds => closeSeconds;
        public IReadOnlyList<IAefJobSuite> Suites => [suite ?? new Suite(testCase)];
    }

    private sealed class Suite(IAefJobCase testCase, Func<CancellationToken, ValueTask<AefJobPreparationResult>>? prepare = null,
        double preparationBound = 0, double preparationIncurred = 0, Func<CancellationToken, ValueTask>? close = null) : IAefJobSuite
    {
        public ValueTask CloseAsync(CancellationToken ct) => close?.Invoke(ct) ?? ValueTask.CompletedTask;
        public double PreparationUsdBound => preparationBound;
        public double PreparationIncurredUsd => preparationIncurred;
        public long PreparationSecondsBound => prepare is null ? 0 : 5;
        public ValueTask<AefJobPreparationResult> PrepareAsync(CancellationToken ct) =>
            prepare?.Invoke(ct) ?? ValueTask.FromResult(new AefJobPreparationResult(true, 0, 0));
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

    private sealed class FailingProgress : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("secret output path");
    }

    private sealed class FailOnEvidenceProgress : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Encoding.UTF8.GetString(buffer).Contains("evidence.produced", StringComparison.Ordinal))
                throw new IOException("secret output path");
            base.Write(buffer);
        }
    }
}
