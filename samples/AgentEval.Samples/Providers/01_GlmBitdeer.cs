// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using AgentEval.Core;
using AgentEval.Evals;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples.Providers;

/// <summary>
/// Sample N1: GLM-5.3 Flash served by Bitdeer — as the SUBJECT and as the JUDGE, through the one
/// <c>IChatClient</c> path AgentEval already has.
///
/// WHAT THIS SHOWS. Nothing in AgentEval knows the word "Bitdeer". The client below is built the
/// way the CLI builds any <c>--endpoint --model --api-key</c> target: an OpenAI client pointed at a
/// different base URL. That client then goes two places:
///
///   - <c>AsEvaluableAgent(name: "zai-org/GLM-5.3-Flash@bitdeer")</c> — the subject. The provider
///     is IN THE NAME because serving infrastructure changes latency, throughput, quantisation and
///     tool-call reliability; "GLM-5.3 Flash" with the host stripped describes a model nobody ran.
///   - <c>new ChatClientEvaluator(client)</c> inside an <c>AtomicLlmEval</c> — the judge.
///
/// Both feed ONE <c>CompositeEval</c>: a deterministic leaf (does the answer contain the fact the
/// context supplies?) beside the generative one. Every result prints its provenance type, the judge
/// model, tokens and cost, and the run says plainly that judge and subject are the SAME model —
/// which is a shape to be aware of, not a shape to hide.
///
/// STAGES. Dry run first (<c>--dry-run</c>: prints every prompt, sends nothing), then one real
/// call, then the remaining cases. That is the order for anything that spends money.
///
/// PRICING. Bitdeer's list price for this model was not verifiable from their public pages on
/// 2026-09-20. The sample prices a run only when BITDEER_PRICE_INPUT_PER_1M and
/// BITDEER_PRICE_OUTPUT_PER_1M are set; otherwise it prints tokens and the words "not priced"
/// rather than a number from a default table that does not know this model.
///
/// Prerequisites: BITDEER_API_KEY (see ProviderConfig).
/// </summary>
public static class GlmBitdeerProviderDemo
{
    private sealed record Case(string Id, string Query, string Context, string RequiredTerm);

    private static readonly Case[] Cases =
    [
        new("paid-date",
            "When was invoice 4471 paid? Answer in one sentence.",
            "Ledger extract — invoice 4471: issued 12 April 2026, paid 3 May 2026 by bank transfer, no fees.",
            "3 May"),
        new("method",
            "How was invoice 4471 paid? Answer in one sentence.",
            "Ledger extract — invoice 4471: issued 12 April 2026, paid 3 May 2026 by bank transfer, no fees.",
            "bank transfer"),
        new("fees",
            "Were any fees added to invoice 4471? Answer in one sentence.",
            "Ledger extract — invoice 4471: issued 12 April 2026, paid 3 May 2026 by bank transfer, no fees.",
            "no fees"),
    ];

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!ProviderConfig.IsBitdeerConfigured)
        {
            ProviderConfig.PrintMissingBitdeerWarning();
            return;
        }

        var dryRun = ProviderConfig.IsDryRun;
        var model = ProviderConfig.BitdeerModel;
        var agentName = ProviderConfig.BitdeerAgentName;

        Console.WriteLine($"   🔗 Endpoint : {ProviderConfig.BitdeerEndpoint}");
        Console.WriteLine($"   🤖 Model    : {model}");
        Console.WriteLine($"   🏷️  Identity : {agentName}   (model@provider — never just the model)");
        var price = ProviderConfig.BitdeerPricePer1K;
        Console.WriteLine(price is { } p
            ? $"   💲 Priced   : ${p.InputPer1K * 1000:F3}/M in, ${p.OutputPer1K * 1000:F3}/M out (from BITDEER_PRICE_*_PER_1M)"
            : "   💲 Priced   : NO — set BITDEER_PRICE_INPUT_PER_1M / BITDEER_PRICE_OUTPUT_PER_1M to price this run");
        Console.WriteLine();
        if (dryRun)
        {
            // Honest scope of THIS sample's dry run: it prints the prompts it would send and stops before
            // the OpenAI client builds a Chat Completions payload. N2 renders its exact request bytes
            // because the decision client exposes its serializer; the chat SDK does not.
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("   ── DRY RUN: prompts are listed below; NOTHING is sent and no Chat Completions payload is built. ──");
            Console.ResetColor();
            Console.WriteLine();
        }

        // ── Step 1: the client — the same construction the CLI's --endpoint path uses ────────
        Console.WriteLine("📝 Step 1: Build the IChatClient (OpenAI client, Bitdeer base URL)\n");
        var chat = ProviderConfig.CreateBitdeerChatClient();
        Console.WriteLine("   ✓ new OpenAIClient(key, new() { Endpoint = <bitdeer> }).GetChatClient(model).AsIChatClient()");
        Console.WriteLine("   ✓ identical to: agenteval eval --endpoint <bitdeer> --model <model> --api-key <key>\n");

        // ── Step 2: one smoke call — the \"one real item\" stage ─────────────────────────────
        Console.WriteLine("📝 Step 2: One real call (smoke)\n");
        const string smokePrompt = "Reply with exactly the single word: ready";
        if (dryRun)
        {
            Console.WriteLine($"   [dry-run] would send user message: \"{smokePrompt}\"\n");
        }
        else
        {
            var sw = Stopwatch.StartNew();
            var smoke = await chat.GetResponseAsync(smokePrompt);
            sw.Stop();
            Console.WriteLine($"   reply     : \"{Trim(smoke.Text, 60)}\"");
            Console.WriteLine($"   model id  : {smoke.ModelId ?? "(not reported)"}");
            Console.WriteLine($"   tokens    : {smoke.Usage?.InputTokenCount?.ToString() ?? "?"} in / {smoke.Usage?.OutputTokenCount?.ToString() ?? "?"} out");
            Console.WriteLine($"   wall-clock: {sw.ElapsedMilliseconds:N0} ms\n");
        }

        // ── Step 3: the subject and the judge, one composite ────────────────────────────────
        Console.WriteLine("📝 Step 3: Subject + judge in one CompositeEval\n");

        var subject = chat.AsEvaluableAgent(
            name: agentName,
            systemPrompt: "You answer questions using ONLY the ledger extract in the user message. Be brief.");

        // When the run is not priced, the in-memory results carry EstimatedCost = 0, which EvalProvenance
        // cannot distinguish from "free". This sample persists nothing, prints "not priced" instead of
        // a dollar figure, and says so here; a run that must be persisted should set the price variables.
        Func<string?, JudgeCostMap.ModelRate> rate = price is { } pr
            ? _ => new JudgeCostMap.ModelRate(pr.InputPer1K, pr.OutputPer1K)
            : _ => new JudgeCostMap.ModelRate(0, 0);
        if (price is null)
            Console.WriteLine("   ⚠ Unpriced run: EstimatedCost in the in-memory results is 0 because no rate is known, not because the calls were free. Nothing is persisted.\n");

        var judgeLeaf = new AtomicLlmEval(
            evaluator: new ChatClientEvaluator(chat),
            key: "faithful_and_brief",
            name: "Faithful to the ledger and brief",
            category: "quality",
            version: "1.0.0",
            criteria:
            [
                "Every fact in the answer appears in the ledger extract.",
                "The answer is one sentence.",
            ],
            passThreshold: 0.70,
            judgeModel: agentName,
            rateResolver: rate);

        Console.WriteLine($"   subject : {subject.Name}");
        Console.WriteLine($"   judge   : {agentName}  ⚠ SAME MODEL as the subject (EvalInput.SubjectModel is set so the result says so)");
        Console.WriteLine("   leaves  : contains_required_term (code, 0.30) + faithful_and_brief (llm, 0.70), threshold 0.70\n");

        if (dryRun)
        {
            foreach (var c in Cases)
            {
                Console.WriteLine($"   [dry-run] case {c.Id}");
                Console.WriteLine($"             subject prompt : \"{c.Query}\"  +  context ({c.Context.Length} chars)");
                Console.WriteLine($"             judge criteria : 2 criteria over (query, response) — no call made");
            }
            Console.WriteLine();
            PrintTakeaways(dryRun: true);
            return;
        }

        var totalTokens = 0L;
        var totalCost = 0.0;
        for (var i = 0; i < Cases.Length; i++)
        {
            var c = Cases[i];
            if (i == 1) Console.WriteLine("   (first item passed the wire — running the remaining cases)\n");

            var composite = new CompositeEval(
                key: $"ledger_answer.{c.Id}",
                name: $"Ledger answer — {c.Id}",
                category: "quality",
                version: "1.0.0",
                components:
                [
                    new EvalComponent(new ContainsRequiredTermEval(c.RequiredTerm), Weight: 0.30),
                    new EvalComponent(judgeLeaf, Weight: 0.70),
                ],
                aggregation: WeightedSumAggregation.Instance,
                threshold: 0.70);

            var sw = Stopwatch.StartNew();
            var reply = await subject.InvokeAsync($"{c.Query}\n\nLedger extract:\n{c.Context}");
            var subjectMs = sw.ElapsedMilliseconds;

            var input = new EvalInput(Query: c.Query, Response: reply.Text, Context: c.Context)
            {
                CaseId = c.Id,
                SubjectModel = agentName,
            };

            sw.Restart();
            var result = await composite.EvaluateAsync(input);
            var judgeMs = sw.ElapsedMilliseconds;

            Console.WriteLine($"   ▶ {c.Id}");
            Console.WriteLine($"     subject said : \"{Trim(reply.Text, 90)}\"   ({subjectMs:N0} ms, {reply.TokenUsage?.PromptTokens ?? 0}+{reply.TokenUsage?.CompletionTokens ?? 0} tok)");
            PrintTree(result, indent: "     ", price is not null);
            Console.WriteLine($"     composite    : {result.Score.Value:F3} {result.Score.Label.ToUpperInvariant()}   (judge round-trip {judgeMs:N0} ms)");
            Console.WriteLine();

            totalTokens += result.Provenance.TokensUsed ?? 0;
            totalCost += result.Provenance.EstimatedCost;
        }

        Console.WriteLine($"   Σ judge tokens: {totalTokens:N0}   Σ judge cost: {(price is null ? "not priced" : $"${totalCost:F6}")}\n");
        PrintTakeaways(dryRun: false);
    }

    /// <summary>Deterministic leaf: the answer contains the term the ledger supplies. Case-insensitive.</summary>
    private sealed class ContainsRequiredTermEval(string term) : AtomicCodeEval("contains_required_term", "Contains the required term", "quality", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input)
        {
            if (input.Response is null)
                return NotApplicable("No response to inspect.");
            var hit = input.Response.Contains(term, StringComparison.OrdinalIgnoreCase);
            return Build(
                value: hit ? 1.0 : 0.0,
                passed: hit,
                severity: hit ? "none" : "medium",
                evidence: [new EvalEvidence("code", "required-term", hit ? $"found \"{term}\"" : $"missing \"{term}\"")]);
        }
    }

    private static void PrintTree(EvalResult result, string indent, bool priced)
    {
        foreach (var leaf in result.Details.SubResults ?? [])
        {
            var cost = priced ? $"  ${leaf.Provenance.EstimatedCost:F6}" : "";
            var tokens = leaf.Provenance.TokensUsed is { } t ? $"  {t} tok" : "";
            var judge = leaf.Provenance.JudgeModel is { } j ? $"  judge={j}" : "";
            Console.WriteLine($"{indent}├─ {leaf.Metric.Key,-24} {leaf.Score.Value:F3} {leaf.Score.Label,-5} [{leaf.Provenance.Type}]{judge}{tokens}{cost}");
            if (leaf.Details.Summary is { } s)
                Console.WriteLine($"{indent}│    {Trim(s, 100)}");
        }
    }

    private static void PrintTakeaways(bool dryRun)
    {
        Console.WriteLine("💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • An OpenAI-compatible host needs ZERO provider code — IChatClient reaches it today.");
        Console.WriteLine("   • Keep the provider in the identity: glm-5.3-flash@bitdeer ≠ glm-5.3-flash@elsewhere.");
        Console.WriteLine("   • Judge == subject is visible in the result (SubjectModel), not hidden.");
        Console.WriteLine("   • Dry-run → one real item → the rest. Never the other order for paid calls.");
        if (dryRun) Console.WriteLine("   • This WAS a dry run: nothing was sent. Re-run without --dry-run to spend.");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("🔗 SEE ALSO:");
        Console.WriteLine("   • N2 — the same model as the escalation judge beside a Jev decision leaf");
        Console.WriteLine("   • F7 — the universal IChatClient adapter this sample relies on");
        Console.WriteLine("   • docs/adr/033-decision-evals-third-evaluator-kind.md §4.5");
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
║   🧪 SAMPLE N1: GLM-5.3 FLASH @ BITDEER                                        ║
║   Subject + judge through the ordinary IChatClient path · no provider code    ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
