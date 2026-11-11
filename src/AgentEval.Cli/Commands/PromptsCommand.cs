// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.CommandLine;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Cli.Commands.Gatekeeper;
using AgentEval.Guardrails.Judges;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Prompts;

namespace AgentEval.Cli.Commands;

/// <summary>Offline prompt-pack authoring and inspection; never creates a provider client.</summary>
internal static class PromptsCommand
{
    internal static Command Create()
    {
        var command = new Command("prompts", "Export, validate and inspect versioned judge prompt packs.");
        var export = new Command("export", "Export supported English judge definitions and Markdown messages to a new directory.");
        var directory = new Argument<string>("directory") { Description = "New output directory; existing directories are never overwritten." };
        var evaluator = new Option<string?>("--evaluator") { Description = "One evaluator key; omit to export the selected family/preset, or the default RAG and generic judges." };
        var family = FamilyOption();
        var preset = PresetOption();
        export.Add(family);
        export.Add(preset);
        export.Add(directory);
        export.Add(evaluator);
        export.SetAction((ParseResult parse, CancellationToken ct) => ExportAsync(parse.GetValue(directory)!, parse.GetValue(evaluator), Console.Out, Console.Error, ct, parse.GetValue(family), parse.GetValue(preset)));
        command.Add(export);
        foreach (var name in new[] { "validate", "inspect" })
        {
            var action = new Command(name, name == "validate" ? "Validate metadata, Markdown files and evaluator contracts without model calls." : "Show resolved instrument identities, locales and translation status without message content.");
            var file = new Argument<string>("manifest") { Description = "Prompt-pack .json, .yaml or .yml manifest." };
            var json = new Option<bool>("--json") { Description = "Print machine-readable metadata." };
            var inspectFamily = FamilyOption();
            var inspectPreset = PresetOption();
            action.Add(inspectFamily);
            action.Add(inspectPreset);
            action.Add(file);
            action.Add(json);
            action.SetAction((ParseResult parse, CancellationToken ct) => InspectAsync(parse.GetValue(file)!, parse.GetValue(json), Console.Out, Console.Error, ct, parse.GetValue(inspectFamily), parse.GetValue(inspectPreset)));
            command.Add(action);
        }
        return command;
    }

    private static Option<string?> FamilyOption() => new("--family")
        { Description = "Judge family: agentic | gdpr | eu-ai-act | redteam | gatekeeper. Omit for RAG and generic judges." };
    private static Option<string?> PresetOption() => new("--preset")
        { Description = "Family preset; redteam uses an attack name or supported. Requires --family." };

    internal static IReadOnlyList<JudgePromptDefinition> SupportedDefinitions(string? family = null, string? preset = null)
    {
        if (family == "gatekeeper")
        {
            // This hybrid is authored here but executed through the library structured-evidence API.
            // Registering only its support rubric as a CLI gate would skip source-existence validation.
            if (preset == "hallucinated-citation") return [GateJudgePrompts.ForCitationSupport()];
            var axes = preset is null or "supported" ? JudgeAxisRegistry.Axes : [preset];
            return axes.Select(axis => GateJudgePrompts.ForRubric((JudgeAxisRegistry.For(axis)
                ?? throw new ArgumentException($"Unknown gatekeeper axis '{axis}'.")).Rubric())).ToArray();
        }
        if (family is not null) return BenchmarkPromptDefinitions.ForPreset(family, preset);
        if (preset is not null) throw new ArgumentException("--preset requires --family.");
        BenchListCommand.AnchorAssemblies();
        var relevance = new RelevanceEval(new DescriptorEvaluator());
        return [.. BuiltInJudgePrompts.Rag, .. BuiltInJudgePrompts.Metrics,
            ((AtomicLlmEval)relevance.DefinitionInner).CapturePromptDefinition(), BuiltInJudgePrompts.ForEvaluator("judge.criteria", [])];
    }

    internal static async Task<int> ExportAsync(string directory, string? key, TextWriter output, TextWriter error, CancellationToken ct = default, string? family = null, string? preset = null)
    {
        try
        {
            var selected = SupportedDefinitions(family, preset).Where(d => key is null || d.EvaluatorKey == key).ToArray();
            if (selected.Length == 0) throw new ArgumentException("Unknown or unsupported evaluator key. Select a judged family/preset, or omit --evaluator to export the selected definitions.");
            await new JudgePromptPackLoader().ExportAsync(new(selected.Select(d => d with { Status = "draft" })), directory, ct);
            output.WriteLine($"Exported {selected.Length} English draft instrument(s) to {Path.Combine(directory, "prompts.json")}. Translate all four Markdown messages and criteria, set the locale, then validate. No semantic calibration is implied.");
            return ExitCodes.Success;
        }
        catch (Exception ex) when (JudgePromptCliOptions.IsConfigurationError(ex))
        {
            error.WriteLine(ex.Message);
            return ExitCodes.UsageError;
        }
    }

    internal static async Task<int> InspectAsync(string manifest, bool json, TextWriter output, TextWriter error, CancellationToken ct = default, string? family = null, string? preset = null)
    {
        try
        {
            var pack = await new JudgePromptPackLoader().LoadAsync(manifest, ct);
            var supported = (family is null ? SupportedDefinitions().Concat(SupportedDefinitions("gatekeeper")).Append(GateJudgePrompts.ForCitationSupport()) : SupportedDefinitions(family, preset)).ToDictionary(d => d.EvaluatorKey, StringComparer.Ordinal);
            var resolver = new JudgePromptResolver();
            var instruments = pack.Definitions.Select(definition =>
            {
                if (!supported.TryGetValue(definition.EvaluatorKey, out var baseline))
                    throw new ArgumentException($"Unsupported CLI evaluator '{definition.EvaluatorKey}'. Select the matching --family and --preset, or use the library binding API for custom evaluators.");
                if (definition.EvaluatorKey == "judge.criteria") baseline = baseline with { Criteria = definition.Criteria };
                var resolved = resolver.Resolve(baseline, new() { Pack = pack, Locale = definition.Locale });
                return new
                {
                    key = definition.EvaluatorKey, id = definition.Id, version = definition.Version, locale = definition.Locale,
                    status = definition.Status, digest = resolved.Provenance.Digest,
                    responseContract = definition.ResponseContract.ToString(), scale = definition.Scale.ToString(),
                    passAt = definition.PassAt, reviewAt = definition.ReviewAt
                };
            }).ToArray();
            if (json) output.WriteLine(JsonSerializer.Serialize(new { valid = true, instruments }));
            else foreach (var instrument in instruments)
                output.WriteLine($"{instrument.key} [{instrument.locale}] {instrument.id}@{instrument.version} ({instrument.status}) {instrument.digest}");
            return ExitCodes.Success;
        }
        catch (Exception ex) when (JudgePromptCliOptions.IsConfigurationError(ex))
        {
            if (json) output.WriteLine(JsonSerializer.Serialize(new { valid = false, error = ex.Message }));
            else error.WriteLine(ex.Message);
            return ExitCodes.UsageError;
        }
    }

    // Binding supplies the embedded reader policy to AtomicLlmEval's descriptor. Export never evaluates.
    private sealed class DescriptorEvaluator : IEvaluator, IRubricBindable
    {
        public IEvaluator WithRubric(EvalRubric rubric, string? dimension) => this;
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Prompt export never invokes a judge.");
    }
}
