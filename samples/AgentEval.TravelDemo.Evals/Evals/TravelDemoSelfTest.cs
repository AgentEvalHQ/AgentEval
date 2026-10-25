// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Assertions;
using Microsoft.Extensions.AI;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// Offline invariants of the TravelDemo plumbing, run by <c>--selftest</c> next to <see cref="BookFlightSelfTest"/>.
/// No model, no network, no credentials: every provider setup below is a fake dictionary, and nothing reads
/// <see cref="Config.Settings"/> (the process environment).
/// </summary>
public static class TravelDemoSelfTest
{
    private static int _failures;

    public static int Run()
    {
        _failures = 0;
        Console.WriteLine();
        Console.WriteLine("  ── TravelDemo provider resolution vs AgentEval Core ──");
        ParityMatrix();
        NamedResolutionCases();
        DriftReportHidesTheProvider();
        ReasoningLevelsMatchCore();
        Console.WriteLine("  ── What the console may show ──");
        ShowModelPrecedence();
        RedactionCoversInnerExceptions();
        RedactionDropsTheProvidersFreeText();
        Console.WriteLine("  ── Run accounting ──");
        ErroredRunsAreNotScores();
        StochasticVerdictsMatchLibrary();
        WorkflowRunNeedsStructureAndScore();
        StructureFailureBeatsAJudgeFailure();
        NoOutputRunIsAFailedUnscoredRun();
        FailedStageNamesTheJudge();
        ComparisonNeedsTheSameModel();
        ForbiddenToolCallIsAPolicyViolation();
        Console.WriteLine("  ── Verdicts: a failed dimension is never hidden ──");
        VerdictRule();
        AgentRunWithNoToolCallIsAFail();
        SnapshotsRecordTheChecks();
        ComparisonNeedsPassedChecks();
        StochasticConfirmationNeedsTheThreshold();
        Console.WriteLine("  ── Eval console output ──");
        AgentBoxLabelsTheQualityGate();
        GroupedTimelineUsesEachStepsOwnCalls();
        StochasticSummaryPrintsItsOwnThreshold();
        StochasticSummaryShowsUnscoredRuns();
        LowerIsBetterRowsNameTheRightWinner();
        Console.WriteLine("  ── Printed verdicts reach the process outcome ──");
        PrintedVerdictsRecordExitCodes();

        Console.WriteLine(_failures == 0 ? "  TravelDemo selftest OK" : $"  TravelDemo selftest FAILED ({_failures} check(s))");
        return _failures == 0 ? 0 : 1;
    }

    // The agent's copy of MinimalReasoningChatClient sends what Core's sends: none, low when the provider rejects none,
    // the provider's default when it rejects both, the remembered level on the next call, and a level the code set as set.
    private static void ReasoningLevelsMatchCore()
    {
        ReasoningEffort?[][] cases = [[], [ReasoningEffort.None], [ReasoningEffort.None, ReasoningEffort.Low]];
        foreach (var rejects in cases)
        {
            var travel = LevelsSent(inner => new AgentEval.TravelDemo.MinimalReasoningChatClient(inner), rejects);
            var core = LevelsSent(inner => new AgentEval.Core.MinimalReasoningChatClient(inner), rejects);
            Check($"reasoning, provider rejects [{Names(rejects)}] → [{Names(travel)}] as in Core",
                  travel.SequenceEqual(core), $"core sent [{Names(core)}]");
        }

        foreach (var metadata in new[] { false, true })
        foreach (var streaming in new[] { false, true })
        {
            var expected = new (string?, ReasoningEffort?)[]
            {
                ("model-a", ReasoningEffort.None), ("model-a", ReasoningEffort.Low), ("model-a", null),
                ("model-b", ReasoningEffort.None), ("model-a", null),
            };
            var travel = RoutedLevelsSent(inner => new AgentEval.TravelDemo.MinimalReasoningChatClient(inner), metadata, streaming);
            var core = RoutedLevelsSent(inner => new AgentEval.Core.MinimalReasoningChatClient(inner), metadata, streaming);
            Check($"Travel reasoning cache follows requested model (metadata={metadata}, stream={streaming})", travel.SequenceEqual(expected));
            Check($"Core reasoning cache follows requested model (metadata={metadata}, stream={streaming})", core.SequenceEqual(expected));
        }

        static string Names(IEnumerable<ReasoningEffort?> levels) =>
            string.Join(", ", levels.Select(l => l?.ToString().ToLowerInvariant() ?? "default"));
    }

    private static List<(string?, ReasoningEffort?)> RoutedLevelsSent(
        Func<IChatClient, IChatClient> wrap, bool metadata, bool streaming)
    {
        var provider = new ReasoningLevelRecorder([ReasoningEffort.None, ReasoningEffort.Low], metadata, requestModels: true);
        using var client = wrap(provider);
        foreach (var model in new[] { "model-a", "model-b", "model-a" })
        {
            var options = new ChatOptions { ModelId = model };
            if (streaming)
                Consume().GetAwaiter().GetResult();
            else
                client.GetResponseAsync("hi", options).GetAwaiter().GetResult();

            async Task Consume()
            {
                await foreach (var _ in client.GetStreamingResponseAsync("hi", options)) { }
            }
        }
        return provider.Requests;
    }

    private static List<ReasoningEffort?> LevelsSent(Func<IChatClient, IChatClient> wrap, ReasoningEffort?[] rejects)
    {
        var provider = new ReasoningLevelRecorder(rejects);
        var client = wrap(provider);
        client.GetResponseAsync("hi").GetAwaiter().GetResult();
        client.GetResponseAsync("hi").GetAwaiter().GetResult();
        client.GetResponseAsync("hi", new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } })
              .GetAwaiter().GetResult();
        return provider.Levels;
    }

    /// <summary>A provider that records each request's reasoning level and rejects the given ones with HTTP 400.</summary>
    private sealed class ReasoningLevelRecorder(
        ReasoningEffort?[] rejects, bool metadata = true, bool requestModels = false) : IChatClient
    {
        private readonly ChatClientMetadata _metadata = new("selftest", new Uri($"https://selftest-{Guid.NewGuid():N}.test/"), "model");

        public List<ReasoningEffort?> Levels { get; } = [];
        public List<(string?, ReasoningEffort?)> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var level = options?.Reasoning?.Effort;
            Levels.Add(level);
            Requests.Add((options?.ModelId, level));
            if (rejects.Contains(level) && (!requestModels || options?.ModelId == "model-a"))
                throw new HttpRequestException("invalid request", null, System.Net.HttpStatusCode.BadRequest);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            metadata && serviceType == typeof(ChatClientMetadata) ? _metadata : null;

        public void Dispose() { }
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  {name,-74} {(ok ? "OK" : "FAIL")}");
        if (!ok && detail is not null) Console.WriteLine($"      {detail}");
    }

    // ── Provider resolution ───────────────────────────────────────────────────

    // Fake values only. The endpoints cover a valid https URL, a refused plain-http one and a loopback one.
    private static readonly (string Name, string?[] Values)[] Matrix =
    [
        ("AI_INFERENCE_PROVIDER",      [null, "azure", "bitdeer", "openai", "foundry", "openai-compatible", " Azure-OpenAI ", "bogus"]),
        ("AZURE_OPENAI_ENDPOINT",      [null, "https://res.openai.azure.example/", "http://res.example/"]),
        ("AZURE_OPENAI_API_KEY",       [null, "ka"]),
        ("AZURE_OPENAI_DEPLOYMENT",    [null, "dep"]),
        ("BITDEER_API_KEY",            [null, "kb"]),
        ("BITDEER_ENDPOINT",           [null, "https://bd.example/v1"]),
        ("BITDEER_MODEL",              [null, "bm"]),
        ("OPENAI_API_KEY",             [null, "ko"]),
        ("OPENAI_MODEL",               [null, "om"]),
        ("FOUNDRY_ENDPOINT",           [null, "https://f.example/"]),
        ("FOUNDRY_API_KEY",            [null, "kf"]),
        ("FOUNDRY_MODEL",              [null, "fm"]),
        ("OPENAI_COMPATIBLE_ENDPOINT", [null, "http://127.0.0.1:1234/v1"]),
        ("OPENAI_COMPATIBLE_MODEL",    [null, "cm"]),
        ("OPENAI_COMPATIBLE_API_KEY",  [null, "kc"]),
    ];

    private static void ParityMatrix()
    {
        var index  = Matrix.Select((v, i) => (v.Name, i)).ToDictionary(p => p.Name, p => p.i);
        var digits = new int[Matrix.Length];
        string? Env(string name) => index.TryGetValue(name, out var i) ? Matrix[i].Values[digits[i]] : null;

        long combos = 0, mismatches = 0, halfDone = 0, diagnosticMismatches = 0;
        string? firstMismatch = null, firstDiagnosticMismatch = null;
        while (true)
        {
            combos++;
            var comparison  = ProviderDriftGuard.Compare(Env);
            var differences = comparison.Differences;
            if (differences.Count > 0)
            {
                mismatches++;
                // Variable NAMES and which fields differ — no values, so nothing here names a provider's model.
                firstMismatch ??= $"set: {SetNames()} → differ in {string.Join(", ", differences)}";
            }

            // U2: with the selector unset and nothing selected, both sides must explain it the same way — name the same
            // half-done providers with what they lack, or the same bad endpoint variable. The one deliberate difference:
            // TravelDemo does not need AZURE_OPENAI_DEPLOYMENT, so it never lists it as missing (see DiagnosticKind).
            if (Env("AI_INFERENCE_PROVIDER") is null && !comparison.Travel.IsConfigured && !comparison.Core.IsConfigured)
            {
                halfDone++;
                if (DiagnosticKind(comparison.Travel.Diagnostic) != DiagnosticKind(comparison.Core.Diagnostic))
                {
                    diagnosticMismatches++;
                    firstDiagnosticMismatch ??= $"set: {SetNames()}";
                }
            }

            int d = 0;
            while (d < digits.Length && ++digits[d] == Matrix[d].Values.Length) digits[d++] = 0;
            if (d == digits.Length) break;
        }
        Check($"Config.Resolve agrees with Core on {combos:N0} fake setups", mismatches == 0,
              mismatches == 0 ? null : $"{mismatches:N0} disagree; first: {firstMismatch}");
        Check($"no provider selected → the same diagnostic as Core ({halfDone:N0} auto-detect setups)", halfDone > 0 && diagnosticMismatches == 0,
              diagnosticMismatches == 0 ? null : $"{diagnosticMismatches:N0} differ; first: {firstDiagnosticMismatch}");

        string SetNames() => string.Join(", ", Matrix.Where((v, i) => digits[i] != 0).Select(v => v.Name));
    }

    private static readonly System.Text.RegularExpressions.Regex PartialClause =
        new(@"(?<name>[^.]+?) is partially configured — missing: (?<vars>[^.]+)\.", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

    /// <summary>
    /// What an auto-detect "nothing selected" diagnostic says, in a comparable form: "nothing set" (whose provider list
    /// describes Azure's deployment differently on purpose), each half-done provider with what it lacks (Azure's
    /// deployment left out — TravelDemo does not need it), or else the text itself (a bad endpoint).
    /// </summary>
    private static string DiagnosticKind(string? diagnostic)
    {
        if (diagnostic is null) return "(none)";
        if (diagnostic.StartsWith("AI_INFERENCE_PROVIDER is not set and no provider has credentials.", StringComparison.Ordinal))
            return "nothing set";
        var clauses = PartialClause.Matches(diagnostic);
        if (clauses.Count == 0) return diagnostic;
        return string.Join(" | ", clauses.Select(c =>
        {
            var name = c.Groups["name"].Value.Trim();
            var vars = c.Groups["vars"].Value.Split(',', StringSplitOptions.TrimEntries)
                .Where(v => !(name == "Azure OpenAI" && v == "AZURE_OPENAI_DEPLOYMENT"));
            return $"{name}: {string.Join(",", vars)}";
        }));
    }

    private static void NamedResolutionCases()
    {
        static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
            name => vars.FirstOrDefault(v => v.Name == name).Value;

        // V3: Azure without a deployment must not outrank a provider Core accepts.
        var mixed = ProviderDriftGuard.Compare(Env(
            ("AZURE_OPENAI_ENDPOINT", "https://res.openai.azure.example/"), ("AZURE_OPENAI_API_KEY", "ka"),
            ("BITDEER_API_KEY", "kb")));
        Check("Azure endpoint+key (no deployment) + Bitdeer key → both pick Bitdeer",
              mixed.Travel.ProviderTag == "bitdeer" && mixed.Core.ProviderTag == "bitdeer" && mixed.Differences.Count == 0,
              $"travel={mixed.Travel.ProviderTag} core={mixed.Core.ProviderTag} differ=[{string.Join(", ", mixed.Differences)}]");

        // The deliberate default still applies where it cannot change the provider.
        var azureOnly = ProviderDriftGuard.Compare(Env(
            ("AZURE_OPENAI_ENDPOINT", "https://res.openai.azure.example/"), ("AZURE_OPENAI_API_KEY", "ka")));
        Check("Azure endpoint+key only → Azure on the preferred deployment, no drift",
              azureOnly.Travel.ProviderTag == "azure" && azureOnly.Travel.Model == Config.PreferredDeployment && azureOnly.Differences.Count == 0);

        var azureExplicit = ProviderDriftGuard.Compare(Env(
            ("AI_INFERENCE_PROVIDER", "azure"), ("BITDEER_API_KEY", "kb"),
            ("AZURE_OPENAI_ENDPOINT", "https://res.openai.azure.example/"), ("AZURE_OPENAI_API_KEY", "ka")));
        Check("AI_INFERENCE_PROVIDER=azure, endpoint+key → Azure on the preferred deployment",
              azureExplicit.Travel.ProviderTag == "azure" && azureExplicit.Travel.Model == Config.PreferredDeployment && azureExplicit.Differences.Count == 0);

        // The comparator is not vacuous: a changed model or provider on the TravelDemo side is drift.
        var bitdeer = ProviderDriftGuard.Compare(Env(("BITDEER_API_KEY", "kb")));
        Check("a different model on one side is reported as drift",
              ProviderDriftGuard.Differences(bitdeer.Travel with { Model = "other-model" }, bitdeer.Core).Contains("model"));
        Check("a different provider on one side is reported as drift",
              ProviderDriftGuard.Differences(bitdeer.Travel with { ProviderTag = "azure" }, bitdeer.Core).Contains("provider"));

        // U2: a deployment-only Azure setup is a half-done setup on both sides, with the same diagnostic.
        var deploymentOnly = ProviderDriftGuard.Compare(Env(("AZURE_OPENAI_DEPLOYMENT", "dep")));
        Check("deployment-only Azure → the same 'partially configured' diagnostic as Core",
              deploymentOnly.Travel.Diagnostic == deploymentOnly.Core.Diagnostic
              && deploymentOnly.Travel.Diagnostic?.Contains("partially configured") == true,
              $"travel: {deploymentOnly.Travel.Diagnostic}");
    }

    // A Bitdeer setup with a fake key: its published endpoint and default model are what a real one would leak.
    private static TravelProviderSettings FakeBitdeer() =>
        Config.Resolve(name => name == "BITDEER_API_KEY" ? "fake-bitdeer-key-0123456789" : null);

    // What hidden mode must never print for that setup (the leak grep the stage check runs).
    private static readonly string[] BitdeerIdentity = ["bitdeer", "glm", "zai-org", "api-inference"];

    private static bool NamesNone(string text, IEnumerable<string> terms) =>
        !terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    // U3/U6: a drift that trips on stage says THAT the resolvers disagree and in which fields — not on which host.
    private static void DriftReportHidesTheProvider()
    {
        var bitdeer = ProviderDriftGuard.Compare(name => name == "BITDEER_API_KEY" ? "fake-bitdeer-key-0123456789" : null);
        var drifted = bitdeer.Travel with { Model = "zai-org/GLM-6-Preview" };
        var comparison = new ProviderDriftGuard.Comparison(drifted, bitdeer.Core, ProviderDriftGuard.Differences(drifted, bitdeer.Core));

        bool agreedHidden = true;
        var hidden = Capture(() => agreedHidden = ProviderDriftGuard.Report(comparison, showModel: false));
        Check("drift report, hidden: stops, names the differing field, never the host or model",
              !agreedHidden && hidden.Contains("They differ in: model") && hidden.Contains("--show-model") && NamesNone(hidden, BitdeerIdentity),
              EvalPrinter.OneLine(hidden));

        var shown = Capture(() => ProviderDriftGuard.Report(comparison, showModel: true));
        Check("drift report, --show-model: names both sides (the check above is not vacuous)",
              shown.Contains("bitdeer") && shown.Contains("GLM-6-Preview"));
    }

    // U7: the command line decides when it said anything; --hide-model beats TRAVELDEMO_SHOW_MODEL=1.
    private static void ShowModelPrecedence()
    {
        Check("TRAVELDEMO_SHOW_MODEL=1 with no flag → shown",          Config.ResolveShowModel(null, "1"));
        Check("TRAVELDEMO_SHOW_MODEL=' Yes ' with no flag → shown",    Config.ResolveShowModel(null, " Yes "));
        Check("TRAVELDEMO_SHOW_MODEL=0 / unset with no flag → hidden",  !Config.ResolveShowModel(null, "0") && !Config.ResolveShowModel(null, null));
        Check("--hide-model with TRAVELDEMO_SHOW_MODEL=1 → hidden",    !Config.ResolveShowModel(false, "1"));
        Check("--show-model with the variable unset → shown",          Config.ResolveShowModel(true, null));
    }

    // V1/V5: what an SDK failure looks like on its way to the console — a 404 naming the model, and a DNS failure
    // wrapped in an AggregateException naming the host. Hidden mode keeps the error and drops the identity.
    private static void RedactionCoversInnerExceptions()
    {
        var settings = FakeBitdeer();
        var notFound = new InvalidOperationException(
            "HTTP 404 (invalid_request_error: model_not_found)\n\nThe model `zai-org/GLM-5.3-Flash` does not exist.");
        var dns = new AggregateException("Retry failed after 4 tries.",
            new HttpRequestException("No such host is known. (api-inference.bitdeer.ai:443)",
                new System.Net.Sockets.SocketException(11001)));
        var keyEcho = new InvalidOperationException("HTTP 401: Incorrect API key provided: fake-bitdeer-key-0123456789.");

        // MAF wraps an agent's failure in an exception whose own message names neither the host nor the cause.
        var wrapped = new InvalidOperationException("The agent run failed.", dns);

        var hidden404 = Config.Redact(notFound, includeStackTrace: false, settings, modelOverride: null, hideIdentity: true);
        var hiddenDns = Config.Redact(wrapped,  includeStackTrace: false, settings, modelOverride: null, hideIdentity: true);
        Check("hidden: a 404 keeps 'HTTP 404 … model_not_found', loses the model",
              hidden404.Contains("HTTP 404") && NamesNone(hidden404, BitdeerIdentity), EvalPrinter.OneLine(hidden404));
        Check("hidden: inner exceptions (an AggregateException's DNS failure) are shown, without the host",
              hiddenDns.Contains("No such host is known") && hiddenDns.Contains("SocketException") && NamesNone(hiddenDns, BitdeerIdentity),
              EvalPrinter.OneLine(hiddenDns));

        var override404 = Config.Redact(new InvalidOperationException("The model `my-private-model` does not exist."),
                                        includeStackTrace: false, settings, modelOverride: "my-private-model", hideIdentity: true);
        Check("hidden: a --model override is taken out too", !override404.Contains("my-private-model"));

        var shownKey = Config.Redact(keyEcho, includeStackTrace: false, settings, modelOverride: null, hideIdentity: false);
        var shown404 = Config.Redact(notFound, includeStackTrace: false, settings, modelOverride: null, hideIdentity: false);
        Check("--show-model: the model is shown, an echoed API key never is",
              shown404.Contains("zai-org/GLM-5.3-Flash") && !shownKey.Contains("fake-bitdeer-key-0123456789"));
    }

    // NB2: hidden mode must not depend on predicting how a host spells itself. The reviewer's loopback host answered
    // 404 with "The model GLM-5.3 Flash (glm-5.3-flash / zai-org/GLM-5.3-Flash) by Zhipu z.ai is not served by Bitdeer
    // AI here." and three of those spellings reached the screen. A provider's HTTP error now keeps its status line only.
    private static void RedactionDropsTheProvidersFreeText()
    {
        var settings = Config.Resolve(name => name switch
        {
            "BITDEER_API_KEY"  => "fake-bitdeer-key-0123456789",
            "BITDEER_ENDPOINT" => "http://127.0.0.1:18464/v1",
            _                  => null,
        });
        string[] leaks = ["bitdeer", "glm", "zai-org", "zhipu", "z.ai", "api-inference", "127.0.0.1", "18464", "5.3 flash"];
        const string Body = "The model GLM-5.3 Flash (glm-5.3-flash / zai-org/GLM-5.3-Flash) by Zhipu z.ai is not served by Bitdeer AI here.";
        const string Http404 = "HTTP 404 (invalid_request_error: model_not_found)\n\n" + Body;

        string Hidden(Exception ex) => Config.Redact(ex, includeStackTrace: false, settings, modelOverride: null, hideIdentity: true);

        var sdk     = Hidden(new System.ClientModel.ClientResultException(Http404));
        var wrapped = Hidden(new InvalidOperationException("Error invoking handler for TurnToken", new System.ClientModel.ClientResultException(Http404)));
        var copied  = Config.Redact(Http404, settings, modelOverride: null, hideIdentity: true);   // WorkflowError.Message: a string
        Check("hidden: a provider 404 (SDK exception) keeps 'HTTP 404 … model_not_found', drops the host's text",
              sdk.Contains("HTTP 404") && sdk.Contains("model_not_found") && sdk.Contains(Config.ProviderMessageHidden) && NamesNone(sdk, leaks),
              EvalPrinter.OneLine(sdk));
        Check("hidden: the same 404 wrapped (MAF) or copied into a string leaks no spelling of host or model",
              NamesNone(wrapped, leaks) && wrapped.Contains("HTTP 404") && NamesNone(copied, leaks) && copied.Contains("model_not_found"),
              $"{EvalPrinter.OneLine(wrapped)} | {EvalPrinter.OneLine(copied)}");

        // No status line to cut at: the brand fragments, the model's spacing variants and host:port still go.
        var dns  = Hidden(new HttpRequestException("No such host is known. (api-inference.bitdeer.ai:443)", new System.Net.Sockets.SocketException(11001)));
        var refused = Hidden(new HttpRequestException("No connection could be made (127.0.0.1:18464)"));
        var prose = Config.Redact("Upstream note: served by Zhipu z.ai via GLM-5.3 Flash (glm 5.3 flash).", settings, null, hideIdentity: true);
        Check("hidden: free text without a status line (DNS, refused, prose) loses every brand spelling and host:port",
              NamesNone(dns + refused + prose, leaks) && dns.Contains("No such host is known") && refused.Contains("No connection could be made"),
              $"{EvalPrinter.OneLine(dns)} | {EvalPrinter.OneLine(refused)} | {prose}");

        // System.ClientModel's default shape: Status line, then Content and Headers — both free text.
        var clientModel = Config.Redact("Service request failed.\nStatus: 404 (Not Found)\n\nContent:\n{\"error\":\"glm-5.3 by zhipu\"}\n\nHeaders:\nServer: bitdeer-gw",
                                        settings, null, hideIdentity: true);
        Check("hidden: a 'Status: 404 (Not Found)' message keeps the status, drops Content and Headers",
              clientModel.Contains("Status: 404 (Not Found)") && !clientModel.Contains("Content:") && NamesNone(clientModel, leaks),
              EvalPrinter.OneLine(clientModel));

        var shown = Config.Redact(new System.ClientModel.ClientResultException(Http404), includeStackTrace: false, settings, null, hideIdentity: false);
        Check("--show-model: the provider's own message is shown (the checks above are not vacuous)",
              shown.Contains("Zhipu") && shown.Contains("GLM-5.3 Flash") && !shown.Contains(Config.ProviderMessageHidden));
    }

    // U5: a failed model call or judge is not a score of 0; known passes can still settle the verdict.
    private static void ErroredRunsAreNotScores()
    {
        static TestResult Run(int score, bool passed, Exception? error = null, bool judgeFailed = false) =>
            new() { TestName = "t", Score = score, Passed = passed, Error = error, JudgeFailed = judgeFailed };
        static string Describe(Exception ex) => ex.Message;

        var outcomes = new[]
        {
            RunClassifier.FromAgentRun(1, Run(90, true), 60, Describe),
            RunClassifier.FromAgentRun(2, Run(80, true), 60, Describe),
            RunClassifier.FromAgentRun(3, Run(0, false, error: new InvalidOperationException("HTTP 429")), 60, Describe),
            RunClassifier.FromAgentRun(4, Run(70, true), 60, Describe),
            RunClassifier.FromAgentRun(5, Run(0, false, judgeFailed: true), 60, Describe),
        };
        var tally = new StochasticTally(outcomes, 0.60);
        Check("a failed model call / judge is not measured, and not a score",
              !outcomes[2].Measured && !outcomes[4].Measured && outcomes[2].Score is null && outcomes[4].Score is null);
        Check("errored runs are left out of the scores and the pass rate (3/3 measured, scores 90/80/70)",
              tally.Errored == 2 && tally.Measured == 3 && tally.PassRate == 1.0 && tally.Scores.SequenceEqual([90, 80, 70]),
              $"errored={tally.Errored} measured={tally.Measured} passRate={tally.PassRate} scores=[{string.Join(",", tally.Scores)}]");
        Check("three known passes reach 60% of five even with two missing results", tally.Verdict == StochasticVerdict.Passed);

        var hopeless = new StochasticTally(
            [RunOutcome.NotMeasured(1, "x"), new RunOutcome(2, 10, false), new RunOutcome(3, 20, false), new RunOutcome(4, 30, false), new RunOutcome(5, 40, false)], 0.60);
        Check("…but still a FAIL when even all unmeasured runs passing would miss the threshold", hopeless.Verdict == StochasticVerdict.Failed);
        Check("no measured run → NOT MEASURED, not a 0% pass rate",
              new StochasticTally([RunOutcome.NotMeasured(1, "x"), RunOutcome.NotMeasured(2, "y")], 0.6).Verdict == StochasticVerdict.NotMeasured);
    }

    private static void StochasticVerdictsMatchLibrary()
    {
        foreach (var (passed, failed, missing, threshold, expected) in new (int, int, int, double, StochasticVerdict)[]
        {
            (3, 0, 2, 0.6, StochasticVerdict.Passed),
            (3, 0, 2, 0.8, StochasticVerdict.Undecided),
            (0, 4, 1, 0.6, StochasticVerdict.Failed),
            (2, 2, 1, 0.6, StochasticVerdict.Undecided),
            (0, 0, 5, 0, StochasticVerdict.NotMeasured),
            (0, 0, 0, 0.6, StochasticVerdict.NotMeasured),
            (0, 1, 0, 0, StochasticVerdict.Passed),
            (5, 0, 0, 1, StochasticVerdict.Passed),
        })
        {
            var runs = Enumerable.Range(0, passed + failed + missing).Select(i => new TestResult
            {
                TestName = "selftest", Score = i < passed ? 90 : 0, Passed = i < passed,
                JudgeFailed = i >= passed + failed,
            }).ToArray();
            var result = new StochasticResult(new() { Name = "selftest", Input = "offline" }, runs, null,
                new(SuccessRateThreshold: threshold), expected == StochasticVerdict.Passed);
            var tally = new StochasticTally(runs.Select((r, i) =>
                RunClassifier.FromAgentRun(i + 1, r, 60, ex => ex.Message)).ToArray(), threshold);
            Check($"shared stochastic verdict: {passed} pass / {failed} fail / {missing} missing at {threshold:P0}",
                result.Verdict == expected && tally.Verdict == result.Verdict);
        }

        SampleOutcome.Reset();
        var partial = new StochasticTally([new(1, 90, true), new(2, 80, true), new(3, 70, true),
            RunOutcome.NotMeasured(4, "model failure"), RunOutcome.NotMeasured(5, "judge failure")], 0.6);
        var text = Capture(() => EvalPrinter.PrintStochasticSummary("selftest", partial, 60));
        Check("settled partial PASS records exit 0 and distinguishes planned counts from observed statistics",
            text.Contains("PASSED") && text.Contains("3/5 planned runs") && text.Contains("3/3")
            && text.Contains("measured runs only") && text.Contains("over 3 of 5 runs")
            && SampleOutcome.ExitCode == 0 && !partial.SpreadCoversEveryRun);
        SampleOutcome.Reset();
    }

    // U15: Eval 05 shows a pass only when the harness's structural checks AND the judge's score pass.
    private static void WorkflowRunNeedsStructureAndScore()
    {
        static WorkflowTestResult Test(bool orderOk, string output = "an itinerary", IReadOnlyList<WorkflowError>? errors = null, Exception? error = null) => new()
        {
            TestName = "t", WorkflowName = "TripPlanner", Input = "x", Passed = orderOk && errors is null && error is null, Error = error,
            ExecutionResult = error is not null ? null : new WorkflowExecutionResult { FinalOutput = output, Steps = [], Errors = errors },
            AssertionResults = [new WorkflowAssertionResult { AssertionName = "Executor Order", Passed = orderOk, FailureMessage = orderOk ? null : "order" }],
        };
        static EvaluationResult Judge(int score, bool failed = false) => new() { OverallScore = score, EvaluationFailed = failed };
        static string Describe(Exception ex) => ex.Message;
        static string Same(string? s) => s ?? "";

        var structureFails = RunClassifier.AfterJudging(1, Test(orderOk: false), Judge(100), 60);
        Check("structure failed + score 100 → FAILED, not ✅", structureFails.Measured && !structureFails.Passed && structureFails.StructureFailed,
              structureFails.ToString());
        var absentScore = RunClassifier.AfterJudging(1, Test(orderOk: true), new EvaluationResult(), 0);
        Check("unset judge score is not measured at a zero threshold", !absentScore.Measured && !absentScore.Passed);
        var bothPass = RunClassifier.AfterJudging(1, Test(orderOk: true), Judge(85), 60);
        Check("structure passed + score 85 → passed", bothPass.Measured && bothPass.Passed && bothPass.Score == 85);
        var lowScore = RunClassifier.AfterJudging(1, Test(orderOk: true), Judge(40), 60);
        Check("structure passed + score 40 → failed on quality", lowScore.Measured && !lowScore.Passed && !lowScore.StructureFailed);
        Check("the judge produced no verdict → not measured", !RunClassifier.AfterJudging(1, Test(orderOk: true), Judge(0, failed: true), 60).Measured);

        var agentErrored = RunClassifier.BeforeJudging(1,
            Test(orderOk: true, output: "", errors: [new WorkflowError { ExecutorId = "TripPlanner", Message = "HTTP 404" }]), Describe, Same);
        Check("an agent call recorded as failed (not thrown) → not measured, not judged", agentErrored is { Measured: false });
        Check("a harness error (timeout) → not measured",
              RunClassifier.BeforeJudging(1, Test(orderOk: true, error: new TimeoutException("t")), Describe, Same) is { Measured: false });
        var noOutput = RunClassifier.BeforeJudging(1, Test(orderOk: true, output: ""), Describe, Same);
        Check("no error and no output → a measured failure with no score (unscored, not a 0)", noOutput is { Measured: true, Passed: false, Score: null });
        Check("a clean run with output goes to the judge", RunClassifier.BeforeJudging(1, Test(orderOk: true), Describe, Same) is null);
    }

    private static WorkflowTestResult WorkflowTest(bool orderOk, string output = "an itinerary") => new()
    {
        TestName = "t", WorkflowName = "TripPlanner", Input = "x", Passed = orderOk,
        ExecutionResult = new WorkflowExecutionResult { FinalOutput = output, Steps = [] },
        AssertionResults = [new WorkflowAssertionResult { AssertionName = "Executor Order", Passed = orderOk, FailureMessage = orderOk ? null : "order" }],
    };

    // NB1: the structure is read BEFORE the judge. A broken judge used to turn a broken workflow into "not measured".
    private static void StructureFailureBeatsAJudgeFailure()
    {
        var noVerdict = RunClassifier.AfterJudging(1, WorkflowTest(orderOk: false), new EvaluationResult { OverallScore = 0, EvaluationFailed = true }, 60);
        Check("structure failed + the judge gave no verdict → a measured FAIL (unscored), not NOT MEASURED",
              noVerdict is { Measured: true, Passed: false, StructureFailed: true, Score: null }, noVerdict.ToString());
        var threw = RunClassifier.JudgeThrew(1, WorkflowTest(orderOk: false), "HTTP 429");
        Check("structure failed + the judge call threw → a measured FAIL (unscored)",
              threw is { Measured: true, Passed: false, StructureFailed: true, Score: null }, threw.ToString());
        Check("structure passed + the judge gave no verdict / threw → not measured (unchanged)",
              !RunClassifier.AfterJudging(1, WorkflowTest(orderOk: true), new EvaluationResult { EvaluationFailed = true }, 60).Measured
              && !RunClassifier.JudgeThrew(1, WorkflowTest(orderOk: true), "HTTP 429").Measured);
    }

    // B3: a measured run with no output is a FAILED run, and the spread says it covers fewer runs — it never drops out
    // silently (it did: Score null, left out of the spread, and the snapshot recorded 0 errored runs).
    private static void NoOutputRunIsAFailedUnscoredRun()
    {
        static string Same(string? s) => s ?? "";
        var noOutput = RunClassifier.BeforeJudging(3, WorkflowTest(orderOk: true, output: ""), ex => ex.Message, Same)!;
        var tally = new StochasticTally(
            [new RunOutcome(1, 90, true), new RunOutcome(2, 85, true), noOutput, new RunOutcome(4, 88, true), new RunOutcome(5, 92, true)], 0.80);
        Check("a no-output run: FAILED in the pass rate (4/5), unscored, spread over 4 of 5 runs",
              tally.PassedCount == 4 && tally.Measured == 5 && tally.PassRate == 0.8 && tally.Unscored == 1
              && tally.Scores.Count == 4 && tally.Errored == 0 && !tally.SpreadCoversEveryRun,
              $"passed={tally.PassedCount} measured={tally.Measured} unscored={tally.Unscored} scores={tally.Scores.Count}");

        StochasticSnapshot Snap(int? unscored) => new()
        {
            Architecture = "x", Label = "x", ProviderTag = "bitdeer", Model = "m", Runs = 5, PassRate = 1, PassedCount = 5, Passed = true,
            UnscoredRuns = unscored, MinScore = 80, MaxScore = 90, MeanScore = 85, Scores = [80, 85, 85, 90, 90],
        };
        Check("Eval 3 gives no spread verdict when a snapshot has unscored runs",
              Eval03_HypothesisComparison.ForStochastic(Snap(0), Snap(1), showModel: false) is { Short: "some runs have no judge score" });
        Check("…nor when a snapshot does not record them (older file); every run scored → a verdict",
              Eval03_HypothesisComparison.ForStochastic(Snap(0), Snap(null), showModel: false) is not null
              && Eval03_HypothesisComparison.ForStochastic(Snap(0), Snap(0), showModel: false) is null);
        var old = System.Text.Json.JsonSerializer.Deserialize<StochasticSnapshot>("""{"Runs":5,"ErroredRuns":0,"Scores":[80,90]}""");
        Check("a stochastic snapshot JSON without UnscoredRuns loads as not recorded (null), not 0", old is { UnscoredRuns: null });
    }

    // NB7: MAFEvaluationHarness puts a judge call's exception in TestResult.Error too; "the agent run failed" blamed the agent.
    private static void FailedStageNamesTheJudge()
    {
        var error = new InvalidOperationException("HTTP 429");
        var agentFailed = new TestResult { TestName = "t", Error = error };
        var judgeThrew  = new TestResult { TestName = "t", Error = error, ToolUsage = new ToolUsageReport() };
        var afterJudge  = new TestResult { TestName = "t", Error = error, ToolUsage = new ToolUsageReport(), CriteriaResults = [] };
        Check("Error with no tool record → the agent run; with one → the judge call; with criteria → after the judge",
              RunClassifier.FailedStageOf(agentFailed) == FailedStage.AgentRun
              && RunClassifier.FailedStageOf(judgeThrew) == FailedStage.JudgeCall
              && RunClassifier.FailedStageOf(afterJudge) == FailedStage.AfterJudging);
        var reason = RunClassifier.FromAgentRun(1, judgeThrew, 60, ex => ex.Message).NotMeasuredReason ?? "";
        Check("Eval 4's reason for a judge failure says 'the judge call failed', not 'the model call'",
              reason.StartsWith("the judge call failed", StringComparison.Ordinal), reason);
    }

    // ── Verdicts ──────────────────────────────────────────────────────────────

    // The owner's rule, which Eval 1 and Eval 2 both print and save.
    private static void VerdictRule()
    {
        Check("RESULT rule: checks failed → FAIL whatever the quality gate; quality failed → WARN; unmeasured → INCOMPLETE",
              RunVerdicts.Combine(false, true) == RunVerdict.Fail && RunVerdicts.Combine(false, null) == RunVerdict.Fail
              && RunVerdicts.Combine(true, false) == RunVerdict.Warn && RunVerdicts.Combine(true, null) == RunVerdict.Incomplete
              && RunVerdicts.Combine(null, true) == RunVerdict.Incomplete && RunVerdicts.Combine(true, true) == RunVerdict.Pass);
    }

    private static ToolUsageReport Report(params string[] tools)
    {
        var report = new ToolUsageReport();
        int order = 0;
        foreach (var name in tools)
            report.AddCall(new ToolCallRecord { Name = name, CallId = $"c{++order}", Order = order, WasExecuted = true });
        return report;
    }

    // B2: the reviewer's okvar run — no tool called, judge 90 — printed "✅ PASSED" and then "HYPOTHESIS CONFIRMED".
    private static void AgentRunWithNoToolCallIsAFail()
    {
        var answer = "Tokyo and Cologne. " + new string('x', 600);
        var none   = Report();
        var checks = Eval01_TravelAgentEvals.RunChecks(none, [], answer);
        var failed = checks.Where(c => c.Passed == false).Select(c => c.Name).ToList();
        Check("Eval 1, no tool called + judge passed → RESULT FAIL (tools, legs, confirmations failed)",
              RunVerdicts.Combine(Eval01_TravelAgentEvals.ChecksPassed(checks), qualityPassed: true) == RunVerdict.Fail
              && failed.Contains("Tool order & completeness") && failed.Contains("Flight legs (BookFlight ≥ 2)")
              && !failed.Contains("Response content"),
              string.Join(", ", failed));
        var note = Eval01_TravelAgentEvals.HypothesisNote(none, checks) ?? "";
        Check("…and no hypothesis claim: it says no tool was called, never 'non-deterministic'",
              note.Contains("No tool was called") && !note.Contains("HYPOTHESIS") && !note.Contains("non-deterministic"), note);

        string[] all = ["GetInfoAbout", "SearchFlights", "GetUserConfirmation", "BookFlight", "SearchFlights", "GetUserConfirmation", "BookFlight",
                        "SearchHotel", "GetUserConfirmation", "BookHotel", "SearchHotel", "GetUserConfirmation", "BookHotel", "SendConfirmation"];
        var clean = Eval01_TravelAgentEvals.RunChecks(Report(all), [.. all.Select(n => new AgentEval.Evals.ToolCall(n, null, "ok"))], answer);
        Check("Eval 1, every tool in order + judge passed → PASS; judge failed → WARN (the checks above are not vacuous)",
              Eval01_TravelAgentEvals.ChecksPassed(clean) == true
              && RunVerdicts.Combine(Eval01_TravelAgentEvals.ChecksPassed(clean), true) == RunVerdict.Pass
              && RunVerdicts.Combine(Eval01_TravelAgentEvals.ChecksPassed(clean), false) == RunVerdict.Warn
              && Eval01_TravelAgentEvals.HypothesisNote(Report(all), clean) is null,
              string.Join(", ", clean.Where(c => c.Passed != true).Select(c => $"{c.Name}: {c.Detail}")));

        string[] oneLeg = ["GetInfoAbout", "SearchFlights", "GetUserConfirmation", "BookFlight", "SearchHotel", "GetUserConfirmation", "BookHotel", "SendConfirmation"];
        var partial = Eval01_TravelAgentEvals.RunChecks(Report(oneLeg), [.. oneLeg.Select(n => new AgentEval.Evals.ToolCall(n, null, "ok"))], answer);
        Check("Eval 1, one flight leg booked → FAIL with a one-run HYPOTHESIS SIGNAL (was a yellow note next to PASSED)",
              Eval01_TravelAgentEvals.ChecksPassed(partial) == false
              && (Eval01_TravelAgentEvals.HypothesisNote(Report(oneLeg), partial) ?? "").Contains("HYPOTHESIS SIGNAL"));
        Check("Eval 1, no tool-call record → INCOMPLETE, not FAIL",
              RunVerdicts.Combine(Eval01_TravelAgentEvals.ChecksPassed(Eval01_TravelAgentEvals.RunChecks(null, null, answer)), true) == RunVerdict.Incomplete);
    }

    // B1: a snapshot carries the checks next to the quality gate, and an older one without them reads as not recorded.
    private static void SnapshotsRecordTheChecks()
    {
        var execution = new WorkflowExecutionResult { FinalOutput = "itinerary", Steps = [] };
        bool refusedMissingScore = false;
        try
        {
            Eval02_TripPlannerEvals.BuildSnapshot(execution, new EvaluationResult(), true, true, "offline", "m");
        }
        catch (ArgumentException) { refusedMissingScore = true; }
        Check("snapshot refuses an unset judge score", refusedMissingScore);
        var judge95   = new EvaluationResult { OverallScore = 95, CriteriaResults = [] };
        var workflow  = Eval02_TripPlannerEvals.BuildSnapshot(execution, judge95, structurePassed: false, qualityPassed: true, "bitdeer", "m");
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<EvalSnapshot>(System.Text.Json.JsonSerializer.Serialize(workflow));
        Check("Eval 2 snapshot of a structure-failed run: quality gate passed, StructurePassed false, RESULT FAIL (survives JSON)",
              workflow is { Passed: true, StructurePassed: false, Result: RunVerdict.Fail }
              && roundTrip is { StructurePassed: false, Result: RunVerdict.Fail },
              $"{workflow.StructurePassed} {workflow.Result} / {roundTrip?.StructurePassed} {roundTrip?.Result}");
        var agent = Eval01_TravelAgentEvals.BuildSnapshot(new TestResult { TestName = "t", Score = 90, Passed = true, ToolUsage = Report() },
                                                          checksPassed: false, RunVerdict.Fail, "bitdeer", "m");
        Check("Eval 1 snapshot of a tool-failed run: StructurePassed false, RESULT FAIL", agent is { Passed: true, StructurePassed: false, Result: RunVerdict.Fail });
        var old = System.Text.Json.JsonSerializer.Deserialize<EvalSnapshot>("""{"Architecture":"Workflow","LlmScore":95,"Passed":true}""");
        Check("a snapshot JSON without StructurePassed / Result loads as not recorded (null)", old is { StructurePassed: null, Result: null });
    }

    private static EvalSnapshot Single(int score, bool? structurePassed) => new()
    {
        Architecture = "x", Label = "x", ProviderTag = "bitdeer", Model = "m", LlmScore = score, CriteriaMetCount = score / 10, CriteriaTotal = 10,
        Passed = true, StructurePassed = structurePassed, Result = structurePassed is { } s ? RunVerdicts.Combine(s, true) : null,
    };

    // B1: the reviewer's ok run — workflow structure FAILED (no tool called), quality 95 vs 60 — became a green
    // "HYPOTHESIS CONFIRMED". Eval 3 now withholds on a failed or unrecorded side.
    private static void ComparisonNeedsPassedChecks()
    {
        var reason = Eval03_HypothesisComparison.ForSingleRuns(Single(60, true), Single(95, false), showModel: false);
        var text   = Capture(() => EvalPrinter.PrintComparison(Single(60, true), Single(95, false), "selftest", reason?.Short));
        Check("Eval 3: workflow structure FAILED (95 vs 60) → no verdict, never 'HYPOTHESIS CONFIRMED'",
              reason is not null && reason.Long.Contains("Eval 2 FAILED") && !text.Contains("HYPOTHESIS CONFIRMED") && text.Contains("NO VERDICT"),
              reason?.Long);
        Check("Eval 3: a snapshot that does not record its checks (older file) → no verdict",
              Eval03_HypothesisComparison.ForSingleRuns(Single(60, null), Single(95, true), showModel: false) is not null);
        var sound = Eval03_HypothesisComparison.ForSingleRuns(Single(60, true), Single(95, true), showModel: false);
        Check("Eval 3: both sides' checks passed → a verdict (the checks above are not vacuous)",
              sound is null && Capture(() => EvalPrinter.PrintComparison(Single(60, true), Single(95, true), "selftest")).Contains("HYPOTHESIS CONFIRMED"));
    }

    // B1: the reviewer's okvar run — five structure-failed workflow runs at 80 (spread 0, pass rate 0%) — "confirmed"
    // greater consistency against an agent at 5/5 passed. A spread of failing runs is not consistency.
    private static void StochasticConfirmationNeedsTheThreshold()
    {
        StochasticSnapshot Snap(int min, int max, bool passed, int passedCount) => new()
        {
            Architecture = "x", Label = "x", ProviderTag = "bitdeer", Model = "m", Runs = 5, ErroredRuns = 0, UnscoredRuns = 0,
            PassRate = passedCount / 5.0, PassedCount = passedCount, Passed = passed,
            MinScore = min, MaxScore = max, MeanScore = (min + max) / 2.0, Scores = [min, max],
        };
        var missed = Capture(() => EvalPrinter.PrintStochasticComparison(Snap(60, 100, true, 5), Snap(80, 80, false, 0), "selftest"));
        Check("stochastic: a tighter spread does NOT confirm when the workflow missed its pass threshold",
              !missed.Contains("HYPOTHESIS CONFIRMED") && missed.Contains("missed its own pass threshold"));
        var met = Capture(() => EvalPrinter.PrintStochasticComparison(Snap(60, 100, true, 5), Snap(80, 90, true, 5), "selftest"));
        Check("stochastic: a tighter spread with the threshold met → CONFIRMED (not vacuous)", met.Contains("HYPOTHESIS CONFIRMED"));
    }

    // U5: a failed model call or judge is not a score of 0 — kept out of the scores and the pass rate, never a PASS.

    // V4: Eval 3 gives an architecture verdict only between snapshots of the same model; it warns without naming them.
    private static void ComparisonNeedsTheSameModel()
    {
        const string glm = "zai-org/GLM-5.3-Flash@bitdeer", mini = "gpt-5-mini@azure";
        Check("same model on both sides → a verdict", Eval03_HypothesisComparison.NoVerdictReason("Eval 1", glm, "Eval 2", glm, 0, showModel: false) is null);

        var different = Eval03_HypothesisComparison.NoVerdictReason("Eval 1", mini, "Eval 2", glm, 0, showModel: false);
        Check("different models → no verdict, and hidden mode names neither",
              different is not null && NamesNone(different.Short + different.Long, ["glm", "zai-org", "bitdeer", "gpt-5-mini", "azure"]),
              different?.Long);
        Check("different models, --show-model → names both",
              Eval03_HypothesisComparison.NoVerdictReason("Eval 1", mini, "Eval 2", glm, 0, showModel: true)?.Long is { } l && l.Contains(mini) && l.Contains(glm));
        Check("a snapshot without a model (older file) → no verdict",
              Eval03_HypothesisComparison.NoVerdictReason("Eval 1", null, "Eval 2", glm, 0, showModel: false) is not null);
        Check("unmeasured runs in a stochastic snapshot → no verdict",
              Eval03_HypothesisComparison.NoVerdictReason("Eval 4", glm, "Eval 5", glm, erroredRuns: 1, showModel: false) is not null);

        // An old snapshot file — written before the provenance fields existed — still loads, as Unknown.
        var old = System.Text.Json.JsonSerializer.Deserialize<EvalSnapshot>("""{"Architecture":"Single Agent","Label":"x","LlmScore":80}""");
        Check("a snapshot JSON without provenance fields still loads (model unknown)", old is { LlmScore: 80, ModelIdentity: null });
    }

    // Eval 01's catch list depends on this: a forbidden tool call is reported by NeverCallTool with an exception that
    // is NOT a ToolAssertionException. Eval 01 caught only the latter, so the first live run where the agent cancelled
    // a booking ended in an unhandled exception (no verdict, no snapshot). If the library changes the type, this flags it.
    private static void ForbiddenToolCallIsAPolicyViolation()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "CancelHotelBooking", CallId = "c1", WasExecuted = true });
        Exception? thrown = null;
        try { report.Should().NeverCallTool("CancelHotelBooking", because: "selftest"); }
        catch (Exception ex) { thrown = ex; }
        Check("NeverCallTool throws BehavioralPolicyViolationException, not ToolAssertionException (Eval 01 catches both)",
              thrown is BehavioralPolicyViolationException, thrown?.GetType().Name ?? "no exception");
    }

    // ── Console output ────────────────────────────────────────────────────────

    // The stochastic summary prints the eval's own threshold (Eval 05 is 80%), not a hard-coded 60%.
    private static void StochasticSummaryPrintsItsOwnThreshold()
    {
        var tally = new StochasticTally([new RunOutcome(1, 90, true), new RunOutcome(2, 50, false)], 0.80);
        var text = Capture(() => EvalPrinter.PrintStochasticSummary("Workflow", tally, passingScore: 60, label: "selftest"));
        Check("stochastic summary prints the eval's own threshold (80%)",
              text.Contains("threshold ≥ 80%") && text.Contains("threshold: 80%") && !text.Contains("threshold: 60%"));
    }

    // B3: the summary says a run had no score and that the spread covers fewer runs than were attempted.
    private static void StochasticSummaryShowsUnscoredRuns()
    {
        var tally = new StochasticTally(
            [new RunOutcome(1, 90, true), new RunOutcome(2, 85, true), new RunOutcome(3, null, false, FailureReason: "no final output"),
             new RunOutcome(4, 88, true), new RunOutcome(5, 92, true)], 0.80);
        var text = Capture(() => EvalPrinter.PrintStochasticSummary("Workflow", tally, passingScore: 60, label: "selftest"));
        Check("stochastic summary: the no-output run is shown as unscored, the spread as over 4 of 5 runs, the pass rate 4/5",
              text.Contains("Unscored") && text.Contains("over 4 of 5 runs") && text.Contains("4/5"));
    }

    // B2: the agent box's verdict is the QUALITY GATE (the judge's score) and says so; a bare "✅ PASSED" over a run that
    // called no tool read as the run's verdict.
    private static void AgentBoxLabelsTheQualityGate()
    {
        var result = new TestResult { TestName = "t", Score = 90, Passed = true, ToolUsage = Report() };
        var text = Capture(() => EvalPrinter.PrintAgentResult(result, ["GetInfoAbout"], "selftest", scoreMeasured: true, passingScore: 70));
        var verdictLines = text.Split('\n').Where(l => l.Contains("PASSED") || l.Contains("FAILED")).ToList();
        Check("Eval 1 box: 'Quality gate: ✅ PASSED (judge score 90/100 ≥ 70)', no unlabelled PASSED",
              text.Contains("Quality gate: ✅ PASSED") && text.Contains("90/100 ≥ 70") && verdictLines.All(l => l.Contains("Quality gate")),
              string.Join(" | ", verdictLines.Select(l => l.Trim())));
    }

    // A lower-is-better row (score spread) names the side with the LOWER number as the winner.
    private static void LowerIsBetterRowsNameTheRightWinner()
    {
        StochasticSnapshot Snap(int min, int max) => new()
        {
            Architecture = "x", Label = "x", ProviderTag = "bitdeer", Model = "m", Runs = 5, PassRate = 1, PassedCount = 5,
            MinScore = min, MaxScore = max, MeanScore = (min + max) / 2.0, Scores = [min, max],
        };
        var text = Capture(() => EvalPrinter.PrintStochasticComparison(Snap(88, 95), Snap(85, 95), "selftest"));
        var spreadRow = text.Split('\n').FirstOrDefault(l => l.Contains("Score Spread")) ?? "";
        Check("comparison: the tighter spread (agent 7 vs workflow 10) wins, not the larger",
              spreadRow.Contains("Agent ▼") && !spreadRow.Contains("Workflow"), spreadRow.Trim());
    }

    // U16: a repeated executor's step rows list that step's calls, not every call the executor made.
    private static void GroupedTimelineUsesEachStepsOwnCalls()
    {
        static ToolCallRecord Call(string name, string executor) =>
            new() { Name = name, CallId = Guid.NewGuid().ToString(), ExecutorId = executor, WasExecuted = true };

        var execution = new WorkflowExecutionResult
        {
            FinalOutput = "done",
            Steps =
            [
                new ExecutorStep { ExecutorId = "Planner", Output = "a", StepIndex = 0, ToolCalls = [Call("Search", "Planner")] },
                new ExecutorStep { ExecutorId = "Planner", Output = "b", StepIndex = 1, ToolCalls = [Call("Search", "Planner"), Call("Book", "Planner")] },
            ],
        };
        var result = new WorkflowTestResult { TestName = "selftest", WorkflowName = "selftest", Input = "x", ExecutionResult = execution };

        var text = Capture(() => EvalPrinter.PrintWorkflowResult(result, ["Search", "Book"], "selftest"));
        Check("grouped tool timeline lists each step's own calls (1 call, then 2)",
              text.Contains("▸ Planner  (1 call)") && text.Contains("▸ Planner  (2 calls)") && !text.Contains("(3 calls)"));
    }

    private static void PrintedVerdictsRecordExitCodes()
    {
        try
        {
            foreach (var (verdict, code, text, checks, quality) in new (RunVerdict, int, string, bool, bool?)[]
            {
                (RunVerdict.Pass, 0, "RESULT: PASS", true, true),
                (RunVerdict.Fail, 1, "RESULT: FAIL", false, true),
                (RunVerdict.Warn, 10, "RESULT: WARN", true, false),
                (RunVerdict.Incomplete, 11, "RESULT: INCOMPLETE", true, null),
            })
            {
                SampleOutcome.Reset();
                var agentText = Capture(() => Eval01_TravelAgentEvals.PrintOverallVerdict(verdict, [], quality));
                Check($"Eval 1 printed {verdict} records exit {code}",
                    agentText.Contains(text) && SampleOutcome.ExitCode == code);
                SampleOutcome.Reset();
                var workflowText = Capture(() => Eval02_TripPlannerEvals.PrintOverallVerdict(checks, quality));
                Check($"Eval 2 printed {verdict} records exit {code}",
                    workflowText.Contains(text) && SampleOutcome.ExitCode == code);
            }

            foreach (var (runs, code, text) in new (RunOutcome[], int, string)[]
            {
                ([new(1, 90, true)], 0, "PASSED"),
                ([new(1, 20, false)], 1, "FAILED"),
                ([new(1, 90, true), RunOutcome.NotMeasured(2, "unavailable")], 11, "INCOMPLETE"),
                ([RunOutcome.NotMeasured(1, "unavailable")], 11, "NOT MEASURED"),
            })
            {
                SampleOutcome.Reset();
                var output = Capture(() => EvalPrinter.PrintStochasticSummary("selftest", new(runs, 1), 60));
                Check($"Stochastic panel printed {text} records exit {code}",
                    output.Contains(text) && SampleOutcome.ExitCode == code);
            }

            SampleOutcome.Reset();
            Capture(() => Eval02_TripPlannerEvals.PrintOverallVerdict(false, true));
            Capture(() => Eval02_TripPlannerEvals.PrintOverallVerdict(true, true));
            Check("A later printed PASS does not erase an earlier FAIL", SampleOutcome.ExitCode == 1);

            SampleOutcome.Reset();
            try
            {
                Eval01_TravelAgentEvals.PrintOverallVerdict((RunVerdict)42, [], true);
                Check("An unknown displayed verdict cannot report completion", false);
            }
            catch (ArgumentOutOfRangeException) { Check("An unknown displayed verdict cannot report completion", true); }
            try
            {
                EvalProcessOutcome.Record((StochasticVerdict)42, "corrupt verdict");
                Check("An unknown stochastic verdict cannot report completion", false);
            }
            catch (ArgumentOutOfRangeException) { Check("An unknown stochastic verdict cannot report completion", true); }
        }
        finally { SampleOutcome.Reset(); }
    }

    private static string Capture(Action print)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { print(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }
}
