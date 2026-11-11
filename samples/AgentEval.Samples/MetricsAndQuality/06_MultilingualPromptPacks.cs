// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Metrics.RAG;
using AgentEval.Metrics.Safety;
using AgentEval.Prompts;
using AgentEval.Testing;

namespace AgentEval.Samples;

/// <summary>
/// Loads Swedish judge instructions from files without a custom evaluator class.
/// Time: under one minute. Prerequisites: none. Uses scripted replies to demonstrate wiring, not model quality.
/// </summary>
public static class MultilingualPromptPacks
{
    /// <summary>Runs the offline prompt-pack walkthrough.</summary>
    public static async Task RunAsync()
    {
        Console.WriteLine("Swedish prompt packs — scripted transport demonstration, not semantic calibration.");
        var manifest = Path.Combine(AppContext.BaseDirectory, "PromptPacks", "sv-SE", "prompts.json");
        var pack = await new JudgePromptPackLoader().LoadAsync(manifest);
        var prompts = new JudgePromptOptions { Pack = pack, Locale = "sv-SE" };

        // In a real application, supply the host's configured IChatClient here.
        using var metricClient = new FakeChatClient("""{"score":95,"faithfulClaims":["Mötet börjar klockan nio."],"hallucinatedClaims":[],"reasoning":"Svaret stöds av underlaget."}""");
        var metric = new FaithfulnessMetric(metricClient, prompts);
        var measured = await metric.EvaluateAsync(new EvaluationContext
        {
            Input = "När börjar mötet?", Output = "Klockan nio.", Context = "Mötet börjar klockan nio på måndag."
        });

        using var rubricClient = new FakeChatClient("""{"score":0.95,"reasoning":"Svaret besvarar frågan."}""");
        var relevance = new RelevanceEval(new ChatClientEvaluator(rubricClient).WithPrompts(prompts));
        var judged = await relevance.EvaluateAsync(new EvalInput("När börjar mötet?", "Klockan nio."));

        using var safetyClient = new FakeChatClient("""{"score":95,"groundedClaims":["Klockan nio"],"ungroundedClaims":[],"fabricatedElements":[],"uncertaintyAcknowledged":false,"reasoning":"Tidpunkten stöds."}""");
        var grounded = await new GroundednessMetric(safetyClient, prompts).EvaluateAsync(new()
        {
            Input = "När börjar mötet?", Output = "Klockan nio.", Context = "Mötet börjar klockan nio på måndag."
        });

        if (!measured.Measured || measured.JudgePrompt?.EffectiveLocale != "sv-SE" ||
            judged.Provenance.JudgePrompt?.EffectiveLocale != "sv-SE" || !grounded.Measured || grounded.JudgePrompt?.EffectiveLocale != "sv-SE")
            throw new InvalidOperationException("The scripted prompt-pack transport check failed.");

        Console.WriteLine($"RAG: {measured.MetricName}, locale={measured.JudgePrompt.EffectiveLocale}, scripted score={measured.Score}");
        Console.WriteLine($"Rubric: {judged.Metric.Key}, locale={judged.Provenance.JudgePrompt.EffectiveLocale}, scripted score={judged.Score.Value}");
        Console.WriteLine($"Safety: {grounded.MetricName}, locale={grounded.JudgePrompt!.EffectiveLocale}, scripted score={grounded.Score}");
        Console.WriteLine($"Instrument: {measured.JudgePrompt.Digest}");
        Console.WriteLine("Edit the Markdown messages or metadata, reload the pack, and rerun. The digest changes with the actual instrument.");
        Console.WriteLine("Before relying on quality scores, have Swedish reviewers label representative cases and calibrate the real judge separately.");
    }
}
