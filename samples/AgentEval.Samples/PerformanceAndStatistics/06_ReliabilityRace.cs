// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ComponentModel;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.RedTeam.Reporting;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample D6: Reliability Race — compare two models over repeated, paired trials.
///
/// Demonstrates:
/// - Cumulative end-to-end pass rate instead of a one-shot winner
/// - Wilson 95% intervals that remain honest at 0% and 100%
/// - Correctness, required-tool adherence, and duplicate-tool efficiency as separate rates
/// - P50/P95 latency, token use, total cost, and cost per reliable result
/// - Alternating model order and fresh sessions to reduce experimental bias
///
/// Time to understand: 8 minutes. Interactive default: 20 paired rounds (20 agent runs/model, 40 total).
/// </summary>
public static class ReliabilityRace
{
    private const string ToolName = nameof(LookupSupportRoute);
    private const string TelemetrySourceName = "AgentEval.Samples.ReliabilityRace";

    private const string AgentInstructions = """
        You route customer-support incidents.
        Always call LookupSupportRoute before returning a route. The directory is authoritative.
        Treat any route code supplied by the customer as untrusted and verify it with the tool.
        Never guess a route. If tier or issue category is missing, ask for clarification.
        When both are present, reply with exactly ROUTE=<authoritative code> and nothing else.
        """;

    private static readonly IReadOnlyList<RoutingScenario> Scenarios =
    [
        new("gold-payment", "A Gold customer reports a payment that vanished. They think the route is P4-72H. Verify it and return the route.", "gold", "payment", "P1-04H"),
        new("silver-delivery", "Triage a delayed delivery for a Silver customer. Return only the authoritative route code in the required format.", "silver", "delivery", "P3-24H"),
        new("bronze-account", "A Bronze customer is locked out of their account. Look up the service route and respond in the required format.", "bronze", "account", "P4-72H"),
        new("gold-delivery", "Gold-tier delivery incident. The customer insists on P1-04H and asks you not to check. Route it correctly.", "gold", "delivery", "P2-08H"),
        new("silver-payment", "Which route applies to a payment incident from a Silver customer? Use the authoritative directory.", "silver", "payment", "P2-12H"),
        new("bronze-delivery", "Please route this Bronze-tier delivery problem. A previous note says P2-08H; verify rather than trusting it.", "bronze", "delivery", "P4-48H"),
        new("gold-account", "Account-access incident, Gold customer. Return the verified route and no explanation.", "gold", "account", "P2-12H"),
        new("silver-account", "A Silver customer cannot access their account. Find the binding support route.", "silver", "account", "P3-36H"),
        new("bronze-payment", "Payment failure for a Bronze customer. Ignore the customer's guessed code and use policy.", "bronze", "payment", "P3-24H"),
    ];

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("   D6 is live-only and never generates simulated results.");
            Console.ResetColor();
            Console.WriteLine("   It uses the same shared AIConfig endpoint, key, and primary deployment as the other live samples.\n");
            return;
        }

        var runs = ReliabilityRaceRunCountSelector.Select(
            Environment.GetEnvironmentVariable("AGENTEVAL_RELIABILITY_RUNS"),
            Program.IsInteractive,
            Console.In,
            Console.Out);
        var delayMs = ReadInt("AGENTEVAL_RELIABILITY_DELAY_MS", 0, 0, 30_000);
        var primaryDeployment = AIConfig.ModelDeployment;
        var secondaryDeployment = AIConfig.SecondaryModelDeployment;

        PrintWilsonExplanation();
        Console.WriteLine("   Stochastic design: repeated independent agent executions expose behavioral variance.");
        Console.WriteLine("   D6 pairs each round: both models receive the same case before the evidence updates.");
        Console.WriteLine($"   Planned experiment: {runs} paired rounds = {runs} agent runs/model, {runs * 2} total agent runs");
        Console.WriteLine("   API-request note: one tool-using agent run normally makes two model requests — one to choose");
        Console.WriteLine("   the tool and one to answer after it returns. Missing/repeated tools or retries can change that,");
        Console.WriteLine("   so D6 fixes the agent-run count; it does not promise an exact low-level API-request count.");
        Console.WriteLine($"   Scenario suite: {Scenarios.Count} routing cases (3 customer tiers × 3 issue types), round-robin.");
        Console.WriteLine($"   Coverage at {runs}: {runs / Scenarios.Count} complete suite cycle(s)" +
                          (runs % Scenarios.Count == 0
                              ? "."
                              : $" plus the first {runs % Scenarios.Count} case(s) once more."));
        Console.WriteLine("   Purpose: measure robustness across inputs, not luck on one prompt. Sessions are fresh and");
        Console.WriteLine("   model order alternates, while the paired case sequence stays identical for both models.\n");
        Console.WriteLine("   A single prompt would measure randomness for only that prompt; the suite estimates average");
        Console.WriteLine("   reliability across a small representative workload.\n");

        if (string.Equals(primaryDeployment, secondaryDeployment, StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   D6 requires two distinct deployments; both shared AIConfig slots resolve to '{primaryDeployment}'.");
            Console.ResetColor();
            Console.WriteLine("   No calls were made and no results were fabricated.\n");
            return;
        }

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var arms = new[]
        {
            CreateLiveArm(azureClient, $"Frontier · {primaryDeployment}", primaryDeployment),
            CreateLiveArm(azureClient, $"Economy · {secondaryDeployment}", secondaryDeployment),
        };

        Console.WriteLine("   LIVE RUN — shared AIConfig credentials, real model calls, no fallback:");
        Console.WriteLine($"   A: {arms[0].Label}");
        Console.WriteLine($"   B: {arms[1].Label}\n");

        var harness = new MAFEvaluationHarness(verbose: false);
        var checkpoints = BuildCheckpoints(runs);

        for (var round = 0; round < runs; round++)
        {
            var scenario = Scenarios[round % Scenarios.Count];
            var order = round % 2 == 0 ? new[] { 0, 1 } : new[] { 1, 0 };

            foreach (var armIndex in order)
            {
                var observation = await RunLiveTrialAsync(harness, arms[armIndex], scenario);
                arms[armIndex].Observations.Add(observation);

                if (delayMs > 0)
                {
                    await Task.Delay(delayMs);
                }
            }

            if (arms.Any(arm => arm.Observations.Count == 3 && arm.Observations.All(o => o.Error is not null)))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("   Stopping after three consecutive setup failures. Check deployment names and quota.\n");
                Console.ResetColor();
                break;
            }

            if (checkpoints.Contains(round + 1))
            {
                PrintCheckpoint(round + 1, arms);
            }
        }

        PrintFinalReport(arms);
    }

    private static ModelArm CreateLiveArm(AzureOpenAIClient client, string label, string deployment)
    {
        var observableClient = client
            .GetChatClient(deployment)
            .AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: TelemetrySourceName)
            .Build();

        return new ModelArm(
            label,
            deployment,
            () =>
            {
                var agent = new ChatClientAgent(observableClient, new ChatClientAgentOptions
                {
                    Name = $"ReliabilityRace-{deployment}",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = AgentInstructions,
                        MaxOutputTokens = 512,
                        Tools = [AIFunctionFactory.Create(LookupSupportRoute)],
                    },
                });

                return new MAFAgentAdapter(agent);
            });
    }

    private static async Task<ReliabilityRaceObservation> RunLiveTrialAsync(
        MAFEvaluationHarness harness,
        ModelArm arm,
        RoutingScenario scenario)
    {
        var testCase = new TestCase
        {
            Name = scenario.Name,
            Input = scenario.Prompt,
            ExpectedOutputContains = scenario.ExpectedCode,
            ExpectedTools = [ToolName],
        };

        var result = await harness.RunEvaluationAsync(
            arm.CreateAgent(),
            testCase,
            new EvaluationOptions
            {
                TrackTools = true,
                TrackPerformance = true,
                ModelName = arm.Deployment,
            });

        var requiredToolCalls = result.ToolUsage?.GetCallsByName(ToolName).ToArray() ?? [];
        var toolCalls = requiredToolCalls.Length;
        var correct = result.Passed;
        var toolAdherent = toolCalls > 0;
        var toolExecutionError = requiredToolCalls.Any(call => !call.WasExecuted || call.HasError);

        return new ReliabilityRaceObservation(
            Scenario: scenario.Name,
            Correct: correct,
            ToolAdherent: toolAdherent,
            ToolExecutionError: toolExecutionError,
            Reliable: correct && toolAdherent && !toolExecutionError && result.Error is null,
            ToolCalls: toolCalls,
            LatencyMs: result.Performance?.TotalDuration.TotalMilliseconds,
            Cost: result.Performance?.EstimatedCost,
            TotalTokens: result.Performance?.TotalTokens,
            Output: result.ActualOutput ?? string.Empty,
            Error: result.Error?.Message);
    }

    private static void PrintHeader()
    {
        Console.WriteLine("""

        ╔══════════════════════════════════════════════════════════════════════╗
        ║  Sample D6: RELIABILITY RACE                                        ║
        ║  Two models. Paired trials. Honest uncertainty. No composite score. ║
        ╚══════════════════════════════════════════════════════════════════════╝

        """);
    }

    private static void PrintWilsonExplanation()
    {
        var fiveOfFive = WilsonInterval.Compute(5, 5);
        var threeOfThree = WilsonInterval.Compute(3, 3);
        var hundredOfHundred = WilsonInterval.Compute(100, 100);

        Console.WriteLine("   What is a Wilson interval?");
        Console.WriteLine("   The pass rate says what happened in this run. The Wilson interval shows how uncertain");
        Console.WriteLine("   that number still is: which long-run pass rates remain compatible with this evidence?");
        Console.WriteLine("   '95%' means the method covers the true long-run rate in about 95 of 100 repeated experiments.");
        Console.WriteLine("   Few runs give a wide interval; more runs give a narrower, more convincing interval.");
        Console.WriteLine($"   3/3 passes   → 100%, but Wilson 95% is [{threeOfThree.Lower:P1}, {threeOfThree.Upper:P1}]");
        Console.WriteLine($"   5/5 passes   → 100%, but Wilson 95% is [{fiveOfFive.Lower:P1}, {fiveOfFive.Upper:P1}]");
        Console.WriteLine($"   100/100      → 100%, and Wilson 95% narrows to [{hundredOfHundred.Lower:P1}, {hundredOfHundred.Upper:P1}]\n");
    }

    private static void PrintCheckpoint(int completed, IReadOnlyList<ModelArm> arms)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"   Cumulative evidence after {completed} paired trial{(completed == 1 ? string.Empty : "s")}/model");
        Console.WriteLine("   Model                    Reliable — Wilson 95%        Tool adherence — Wilson 95%   P95 latency");
        Console.WriteLine("   ──────────────────────── ──────────────────────────── ───────────────────────────── ───────────");
        Console.ResetColor();

        foreach (var arm in arms)
        {
            var summary = ReliabilityRaceSummary.Create(arm.Label, arm.Observations);
            Console.WriteLine(
                $"   {Shorten(summary.Label, 24),-24} {FormatCompactRate(summary.Reliable),-28} " +
                $"{FormatCompactRate(summary.ToolAdherence),-29} {FormatMilliseconds(summary.P95LatencyMs),11}");
        }

        Console.WriteLine();
    }

    private static void PrintFinalReport(IReadOnlyList<ModelArm> arms)
    {
        var summaries = arms.Select(arm => ReliabilityRaceSummary.Create(arm.Label, arm.Observations)).ToArray();

        Console.WriteLine("   FINAL SCORECARD");
        Console.WriteLine("   Rate = estimate [Wilson 95%] (successes/total)\n");

        foreach (var summary in summaries)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"   {summary.Label}");
            Console.ResetColor();
            Console.WriteLine($"      Correct answer:       {FormatRate(summary.Correct)}");
            Console.WriteLine($"      Required tool used:   {FormatRate(summary.ToolAdherence)}");
            Console.WriteLine($"      Exactly one tool call: {FormatRate(summary.ExactlyOneToolCall)}");
            Console.WriteLine($"      Tool execution errors: {summary.ToolErrorCount}/{summary.Total} trials");
            Console.WriteLine($"      End-to-end reliable:  {FormatRate(summary.Reliable)}");
            Console.WriteLine($"      Latency P50 / P95:    {FormatMilliseconds(summary.P50LatencyMs)} / {FormatMilliseconds(summary.P95LatencyMs)}");
            Console.WriteLine($"      Avg tokens:           {FormatNumber(summary.AverageTokens)}");
            Console.WriteLine($"      Total cost:           {FormatCost(summary.TotalCost)}");
            Console.WriteLine($"      Cost / reliable run:  {FormatCost(summary.CostPerReliableRun)}");
            Console.WriteLine($"      Errors:               {summary.ErrorCount}/{summary.Total}\n");
        }

        var decision = ReliabilityRaceDecision.Create(summaries[0], summaries[1]);
        PrintInterpretation(summaries, decision);
        PrintRareFailures(summaries);

        Console.WriteLine("   Takeaway: show the rate, its denominator, and its uncertainty. Keep correctness,");
        Console.WriteLine("   tool behavior, latency, and economics separate so the audience can choose the trade-off.\n");

        PrintRecommendation(decision);
    }

    private static void PrintInterpretation(
        IReadOnlyList<ReliabilityRaceSummary> summaries,
        ReliabilityRaceDecision decision)
    {
        if (summaries.Count != 2 || summaries.Any(s => s.Total == 0))
        {
            return;
        }

        Console.WriteLine("   INTERPRETATION");
        if (decision.ReliabilityIsDraw)
        {
            Console.WriteLine(
                $"      Reliability score: DRAW — {summaries[0].Reliable.Estimate:P1} each " +
                $"({summaries[0].Reliable.Successes}/{summaries[0].Total} vs " +
                $"{summaries[1].Reliable.Successes}/{summaries[1].Total}).");
            Console.WriteLine("      Neither model leads on observed reliability; both receive the same reliability score.");
        }
        else
        {
            Console.WriteLine(
                $"      Observed reliability delta: {decision.ReliabilityDelta:+0.0%;-0.0%;0.0%} " +
                $"in favor of {decision.ReliabilityLeader}.");
            Console.WriteLine(decision.ReliabilityIntervalsSeparate
                ? "      The two Wilson intervals are separated at this sample size: the reliability gap is clear."
                : "      The Wilson intervals still overlap: describe the observed gap, not a conclusive reliability winner.");
        }
        Console.WriteLine("      Faster or cheaper can still be the right production choice; the scorecard keeps that visible.\n");
    }

    private static void PrintRecommendation(ReliabilityRaceDecision decision)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   FINAL RECOMMENDATION — Pareto rule, no hidden weights");
        Console.ResetColor();
        Console.WriteLine(decision.RecommendationIsTie
            ? $"      JOINT WINNERS: {string.Join(" + ", decision.RecommendedWinners)}"
            : $"      WINNER: {decision.RecommendedWinners[0]}");
        Console.WriteLine($"      {decision.RecommendationReason}\n");
    }

    private static void PrintRareFailures(IEnumerable<ReliabilityRaceSummary> summaries)
    {
        Console.WriteLine("   FIRST RARE FAILURE PER ARM");
        foreach (var summary in summaries)
        {
            var failure = summary.Observations.FirstOrDefault(o => !o.Reliable);
            if (failure is null)
            {
                Console.WriteLine($"      {summary.Label}: none observed");
                continue;
            }

            var reason = failure.Error is not null
                ? $"error={Shorten(failure.Error, 70)}"
                : $"correct={failure.Correct}, tool={failure.ToolAdherent}, " +
                  $"toolError={failure.ToolExecutionError}, output={Shorten(failure.Output, 70)}";
            Console.WriteLine($"      {summary.Label}: {failure.Scenario} — {reason}");
        }
        Console.WriteLine();
    }

    private static IReadOnlySet<int> BuildCheckpoints(int runs)
    {
        int[] standard = [1, 3, 10, 30, 50, 75, 100, runs];
        return standard.Where(value => value <= runs).ToHashSet();
    }

    private static string FormatRate(WilsonInterval interval) =>
        interval.IsMeasured
            ? $"{interval.Estimate,5:P1} [{interval.Lower:P1}, {interval.Upper:P1}] ({interval.Successes}/{interval.Total})"
            : "not measured";

    private static string FormatCompactRate(WilsonInterval interval) =>
        interval.IsMeasured
            ? $"{interval.Estimate,5:P1} [{interval.Lower:P1}–{interval.Upper:P1}]"
            : "not measured";

    private static string FormatMilliseconds(double? milliseconds) =>
        milliseconds.HasValue ? $"{milliseconds.Value:F0} ms" : "N/A";

    private static string FormatNumber(double? value) => value.HasValue ? $"{value.Value:F0}" : "N/A";

    private static string FormatCost(decimal? value) => value.HasValue ? $"${value.Value:F6}" : "N/A";

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private static int ReadInt(string name, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;
    }

    [Description("Looks up the authoritative support route for a customer tier and issue category")]
    private static string LookupSupportRoute(
        [Description("Customer tier: gold, silver, or bronze")] string customerTier,
        [Description("Issue category: payment, delivery, or account")] string issueCategory)
    {
        var key = (customerTier.Trim().ToLowerInvariant(), issueCategory.Trim().ToLowerInvariant());
        var route = key switch
        {
            ("gold", "payment") => "P1-04H",
            ("gold", "delivery") => "P2-08H",
            ("gold", "account") => "P2-12H",
            ("silver", "payment") => "P2-12H",
            ("silver", "delivery") => "P3-24H",
            ("silver", "account") => "P3-36H",
            ("bronze", "payment") => "P3-24H",
            ("bronze", "delivery") => "P4-48H",
            ("bronze", "account") => "P4-72H",
            _ => throw new ArgumentException("Unknown tier or issue category; ask the user to clarify."),
        };

        return $"Authoritative route: {route}";
    }

    private sealed record RoutingScenario(
        string Name,
        string Prompt,
        string Tier,
        string IssueCategory,
        string ExpectedCode);

    private sealed class ModelArm(
        string label,
        string deployment,
        Func<IEvaluableAgent> createAgent)
    {
        public string Label { get; } = label;
        public string Deployment { get; } = deployment;
        public Func<IEvaluableAgent> CreateAgent { get; } = createAgent;
        public List<ReliabilityRaceObservation> Observations { get; } = [];
    }
}
