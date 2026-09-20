// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using AgentEval.Core;
using AgentEval.Decisions;
using AgentEval.Evals;

namespace AgentEval.Samples.Providers;

/// <summary>
/// Sample N2: Jev (TypeSafe's System One decision model) as an AgentEval leaf — ADR-033.
///
/// WHAT A DECISION MODEL IS. Not a chat model. It takes <c>state + typed questions</c> and returns a
/// typed probabilistic answer per question: P(yes) for a yes/no, a distribution for a choice, a
/// weighted position for a score. AgentEval reaches it through <c>IDecisionClient</c>, never
/// through <c>IChatClient</c>, because the probability IS the product and a chat abstraction would
/// flatten it into text.
///
/// WHAT THIS SAMPLE DOES, IN ORDER:
///   1. renders every request it would send (<c>--dry-run</c> stops here, having spent nothing);
///   2. runs ONE real <c>DecisionEval</c> — the "one real item" stage — and prints P(yes), the
///      RESOLVED model id the provider echoed, tokens and cost;
///   3. runs the remaining cases;
///   4. batches three question shapes (noul, choice, score) about one state in ONE request;
///   5. puts the Jev leaf inside a <c>CompositeEval</c> beside a deterministic leaf and, when
///      BITDEER_API_KEY is also set, a GLM-5.3 Flash generative judge — three evaluator KINDS,
///      independent evidence, one explicit aggregation ("Architecture A").
///
/// WHAT IT DOES NOT DO. It does not let Jev decide whether the LLM judge runs (a cascade). That is
/// worth testing, and only after calibration data exists; a cheap judge that selects which cases
/// the strong judge sees makes its own errors invisible.
///
/// WHAT THE NUMBERS ARE WORTH. The 0.80 pass threshold is a starting point, not a calibrated one.
/// Three hand-written cases are a demonstration, not a measurement. A noul answer has NO confidence
/// field — only the choice and score answers do — and this sample prints none for it.
///
/// Prerequisites: TYPESAFE_API_KEY or OPENROUTER_API_KEY (see ProviderConfig). Optional: BITDEER_API_KEY.
/// </summary>
public static class JevDecisionsDemo
{
    private sealed record Case(string Id, string Query, string Context, string Response, string Expectation);

    private const string Ledger = "Ledger extract — invoice 4471: issued 12 April 2026, paid 3 May 2026 by bank transfer, no fees.";

    private static readonly Case[] Cases =
    [
        new("grounded", "When and how was invoice 4471 paid?", Ledger,
            "Invoice 4471 was paid on 3 May 2026 by bank transfer, with no fees.",
            "every claim is in the ledger → expect HIGH P(yes)"),
        new("fabricated", "When and how was invoice 4471 paid?", Ledger,
            "Invoice 4471 was paid on 12 June 2026 by credit card; a 2% late fee was added.",
            "date, method and fee all contradict the ledger → expect LOW P(yes)"),
        // First draft of this line expected a MIDDLING P(yes). Jev returned 0.02 on 2026-09-20, and
        // Jev is right: the question asks whether EVERY claim is supported, and "PayPal" contradicts
        // the ledger. A partially-right answer needs a differently-shaped question, not a softer
        // threshold on this one. Kept as a case because that is the lesson.
        new("partial", "When and how was invoice 4471 paid?", Ledger,
            "Invoice 4471 was paid in May 2026. The payment went through PayPal.",
            "month right, method WRONG → a strict 'every claim' question should say NO; expect LOW P(yes)"),
    ];

    /// <summary>
    /// The state the model judges, used by BOTH the dry-run render and the real DecisionEval (via
    /// stateProjector) so what is printed is exactly what is sent.
    /// </summary>
    private static object State(EvalInput i) => new { query = i.Query, response = i.Response, context = i.Context };

    private const string GroundedInstructions = "Is every factual claim in the response supported by the context?";
    private const string GroundedTrue = "Every date, amount, method and fee stated in the response appears in the context.";
    private const string GroundedFalse = "At least one stated fact is absent from, or contradicts, the context.";

    public static async Task RunAsync()
    {
        PrintHeader();

        var options = ProviderConfig.CreateJevOptions();
        if (options is null)
        {
            ProviderConfig.PrintMissingJevWarning();
            return;
        }

        var dryRun = ProviderConfig.IsDryRun;
        Console.WriteLine($"   🔗 Transport : {options.ProviderName}  →  {options.Endpoint}");
        Console.WriteLine($"   🤖 Requested : {options.Model}   (provenance records what the provider ECHOES BACK, not this)");
        Console.WriteLine(options.ProviderName == "typesafe"
            ? "   💲 Cost      : TypeSafe reports tokens, not cost — the $ shown is an ESTIMATE at OpenRouter's list price ($0.042/M in), not your TypeSafe bill"
            : "   💲 Cost      : provider-reported (OpenRouter sends usage.cost); the jev list price in JudgeCostMap is the fallback");
        Console.WriteLine();
        if (dryRun) ProviderConfig.PrintDryRunBanner();

        using var http = ProviderConfig.CreateJevHttpClient();     // AGENTEVAL_SAMPLES_SHOW_RAW=1 prints every request and reply body
        using var client = new SystemOneDecisionClient(options, http);

        var grounded = new DecisionEval(
            client,
            key: "grounded",
            name: "Grounded in the ledger",
            category: "quality",
            version: "1.0.0",
            instructions: GroundedInstructions,
            passThreshold: 0.80,
            trueCriteria: GroundedTrue,
            falseCriteria: GroundedFalse,
            stateProjector: State);

        // ── Stage 1: render every payload ───────────────────────────────────────────────────
        Console.WriteLine("📝 Stage 1: What DecisionEval sends (rendered through the real serializer)\n");
        foreach (var c in Cases)
        {
            var request = new DecisionRequest(
                State(ToInput(c)),
                new Dictionary<string, DecisionQuestion> { [grounded.Key] = new BinaryQuestion(GroundedInstructions, GroundedTrue, GroundedFalse) });
            var json = SystemOneDecisionClient.RenderRequest(request, options.Model);
            Console.WriteLine($"   ▶ {c.Id}  ({json.Length} bytes)  — {c.Expectation}");
            Console.WriteLine($"     {Trim(json, 220)}");
        }
        Console.WriteLine();

        if (dryRun)
        {
            Console.WriteLine("   [dry-run] Stages 2–5 send real requests. Re-run without --dry-run to spend.\n");
            PrintTakeaways(dryRun: true);
            return;
        }

        // ── Stage 2 + 3: one real item, then the rest ───────────────────────────────────────
        Console.WriteLine("📝 Stage 2: ONE real DecisionEval call\n");
        var results = new List<(Case Case, EvalResult Result, long Ms)>();
        for (var i = 0; i < Cases.Length; i++)
        {
            if (i == 1) Console.WriteLine("📝 Stage 3: the remaining cases\n");
            var c = Cases[i];
            var sw = Stopwatch.StartNew();
            var r = await grounded.EvaluateAsync(ToInput(c));
            sw.Stop();
            results.Add((c, r, sw.ElapsedMilliseconds));
            PrintDecisionResult(c, r, sw.ElapsedMilliseconds);
        }

        // ── Stage 4: three question shapes about one state, ONE request ─────────────────────
        Console.WriteLine("📝 Stage 4: Batch — noul + choice + score about the 'fabricated' case in ONE request\n");
        var fabricated = Cases[1];
        var batch = new DecisionRequest(
            State(ToInput(fabricated)),
            new Dictionary<string, DecisionQuestion>
            {
                ["grounded"] = new BinaryQuestion(GroundedInstructions, GroundedTrue, GroundedFalse),
                ["risk"] = new ChoiceQuestion(
                    "Classify the risk of accepting this response as an answer to the query.",
                    new Dictionary<string, string>
                    {
                        ["low"] = "The response can be accepted as is.",
                        ["medium"] = "The response has a minor inaccuracy a reader could catch.",
                        ["high"] = "The response states something the context contradicts; accepting it would mislead.",
                    }),
                ["quality"] = new ScoreQuestion(
                    "Rate the overall quality of the response as an answer to the query, given the context.",
                    ["wrong", "partly right", "right but incomplete", "right and complete"]),
            });

        var batchSw = Stopwatch.StartNew();
        var decision = await client.DecideAsync(batch);
        batchSw.Stop();

        Console.WriteLine($"   answered by : {decision.Model}   ({batchSw.ElapsedMilliseconds:N0} ms, {decision.Usage?.InputTokens ?? 0} in / {decision.Usage?.OutputTokens ?? 0} out{(decision.Usage?.Cost is { } bc ? $", ${bc:F7} provider-reported" : "")})");
        foreach (var (id, answer) in decision.Answers)
        {
            switch (answer)
            {
                case BinaryAnswer n:
                    Console.WriteLine($"   {id,-9} noul    P(yes) = {n.TrueProbability:F3}                     (no confidence field exists for noul — none is printed)");
                    break;
                case ChoiceAnswer ch:
                    Console.WriteLine($"   {id,-9} choice  → {ch.Choice}   confidence {ch.Confidence:F3}   {{ {string.Join(", ", ch.Probabilities.Select(kv => $"{kv.Key}: {kv.Value:F3}"))} }}");
                    break;
                case ScoreAnswer sc:
                    Console.WriteLine($"   {id,-9} score   = {sc.Score:F2}   confidence {sc.Confidence:F3}   {{ {string.Join(", ", sc.Probabilities.Select(kv => $"{kv.Key}: {kv.Value:F3}"))} }}");
                    break;
            }
        }
        Console.WriteLine();

        // ── Stage 5: three evaluator kinds in one composite ─────────────────────────────────
        Console.WriteLine("📝 Stage 5: CompositeEval — deterministic + decision (+ generative when Bitdeer is configured)\n");

        var components = new List<EvalComponent>
        {
            new(new NonEmptyResponseEval(), Weight: 0.20),
            new(grounded, Weight: ProviderConfig.IsBitdeerConfigured ? 0.40 : 0.80),
        };

        if (ProviderConfig.IsBitdeerConfigured)
        {
            var glm = ProviderConfig.CreateBitdeerChatClient();
            components.Add(new EvalComponent(new AtomicLlmEval(
                evaluator: new ChatClientEvaluator(glm),
                key: "faithful_llm",
                name: "Faithful to the ledger (generative judge)",
                category: "quality",
                version: "1.0.0",
                criteria: ["Every fact in the response appears in the context.", "Nothing in the response contradicts the context."],
                passThreshold: 0.70,
                judgeModel: ProviderConfig.BitdeerAgentName,
                rateResolver: _ => new JudgeCostMap.ModelRate(0, 0)), Weight: 0.40));
            Console.WriteLine($"   leaves : non_empty (code 0.20) + grounded (decision 0.40) + faithful_llm (llm 0.40 — {ProviderConfig.BitdeerAgentName}, not priced)");
        }
        else
        {
            Console.WriteLine("   leaves : non_empty (code 0.20) + grounded (decision 0.80)   — set BITDEER_API_KEY to add the GLM generative judge as a third kind");
        }
        Console.WriteLine();

        var composite = new CompositeEval(
            key: "ledger_answer_quality",
            name: "Ledger answer quality",
            category: "quality",
            version: "1.0.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.75);

        foreach (var c in Cases)
        {
            var r = await composite.EvaluateAsync(ToInput(c));
            Console.WriteLine($"   ▶ {c.Id}   composite {r.Score.Value:F3} {r.Score.Label.ToUpperInvariant()}");
            foreach (var leaf in r.Details.SubResults ?? [])
            {
                var judge = leaf.Provenance.JudgeModel is { } j ? $"  {j}" : "";
                Console.WriteLine($"     ├─ {leaf.Metric.Key,-14} {leaf.Score.Value:F3} {leaf.Score.Label,-5} [{leaf.Provenance.Type}]{judge}");
            }
            Console.WriteLine();
        }

        PrintTakeaways(dryRun: false);
    }

    private static EvalInput ToInput(Case c) => new(Query: c.Query, Response: c.Response, Context: c.Context) { CaseId = c.Id };

    private static void PrintDecisionResult(Case c, EvalResult r, long ms)
    {
        var p = r.Details.Dimensions![DecisionEval.ProbabilityDimension];
        var cost = r.Provenance.EstimatedCost;
        Console.WriteLine($"   ▶ {c.Id,-10} P(yes) = {p:F3}  →  {r.Score.Label.ToUpperInvariant(),-4} (threshold {r.Score.Threshold:F2})   {ms:N0} ms");
        Console.WriteLine($"     expected     : {c.Expectation}");
        Console.WriteLine($"     provenance   : [{r.Provenance.Type}] model={r.Provenance.JudgeModel}  tokens={r.Provenance.TokensUsed}  cost=${cost:F7}  confidence={(r.Score.Confidence is null ? "null (noul carries none)" : r.Score.Confidence.Value.ToString("F3"))}");
        Console.WriteLine();
    }

    /// <summary>Deterministic leaf so the composite has all three kinds of evidence.</summary>
    private sealed class NonEmptyResponseEval() : AtomicCodeEval("non_empty", "Response is non-empty", "quality", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input)
        {
            var ok = !string.IsNullOrWhiteSpace(input.Response);
            return Build(ok ? 1.0 : 0.0, ok, ok ? "none" : "high");
        }
    }

    private static void PrintTakeaways(bool dryRun)
    {
        Console.WriteLine("💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • A decision model is a THIRD evaluator kind: [atomic-decision] beside [atomic-code] and [atomic-llm].");
        Console.WriteLine("   • Score.Value IS P(yes); it also survives in Dimensions[\"decision.probability_yes\"] for a later threshold sweep.");
        Console.WriteLine("   • Provenance names the model the provider ECHOED (the resolved build), not the alias you asked for.");
        Console.WriteLine("   • Batch independent questions about one state into one request — three shapes, one round trip.");
        Console.WriteLine("   • Uncalibrated. Independent evidence beside a judge today; a gate in front of one only after calibration.");
        if (dryRun) Console.WriteLine("   • This WAS a dry run: nothing was sent. Re-run without --dry-run to spend.");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("🔗 SEE ALSO:");
        Console.WriteLine("   • docs/adr/033-decision-evals-third-evaluator-kind.md — the design and what acceptance requires");
        Console.WriteLine("   • N1 — the GLM judge this sample composes with, on its own");
        Console.WriteLine("   • M1 — chance floors: what a probability is worth against a guesser");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static string Trim(string? s, int max)
    {
        var flat = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🎯 SAMPLE N2: JEV DECISIONS (TypeSafe System One)                            ║
║   state + typed questions → probabilities · a third evaluator kind (ADR-033)  ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
