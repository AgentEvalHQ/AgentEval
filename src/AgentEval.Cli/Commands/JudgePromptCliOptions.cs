// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.CommandLine;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Prompts;

namespace AgentEval.Cli.Commands;

/// <summary>Shared CLI selection and preflight; a selection is immutable and scoped to one invocation.</summary>
internal sealed class JudgePromptCliOptions
{
    private readonly Option<FileInfo?> _pack = new("--judge-prompt-pack")
        { Description = "Versioned judge prompt manifest (.json/.yaml), independent of the target's instructions." };
    private readonly Option<string?> _locale = new("--judge-locale")
        { Description = "Explicit judge locale for the pack (default: en)." };
    private readonly Option<string?> _fallback = new("--judge-fallback-locale")
        { Description = "Explicit fallback locale; missing definitions otherwise fail before evaluation." };

    internal JudgePromptCliOptions(Command command)
    {
        command.Add(_pack);
        command.Add(_locale);
        command.Add(_fallback);
    }

    internal Selection Read(ParseResult parse) => new(parse.GetValue(_pack), parse.GetValue(_locale), parse.GetValue(_fallback));

    internal sealed record Selection(FileInfo? Pack, string? Locale = null, string? FallbackLocale = null)
    {
        internal Task<JudgePromptOptions?> LoadAsync(CancellationToken ct) => JudgePromptCliOptions.LoadAsync(Pack, Locale, FallbackLocale, ct);
    }

    internal static async Task<JudgePromptOptions?> LoadAsync(FileInfo? pack, string? locale, string? fallback, CancellationToken ct)
    {
        if (pack is null)
        {
            if (locale is not null || fallback is not null)
                throw new ArgumentException("--judge-locale and --judge-fallback-locale require --judge-prompt-pack.");
            return null;
        }
        return new()
        {
            Pack = await new JudgePromptPackLoader().LoadAsync(pack.FullName, ct),
            Locale = locale ?? "en", FallbackLocale = fallback,
            CaptureContent = AefCliOptions.ContentCapture == AgentEval.Results.Writing.AefContentCapture.On
        };
    }

    internal static void Preflight(IEval graph, IEvaluator judge, JudgePromptOptions? options)
    {
        if (options is null) return;
        var definitions = BuiltInJudgePrompts.ForEval(graph);
        if (definitions.Count == 0)
            throw new ArgumentException("Expected: a benchmark with a configurable judge. Actual: this selection has no judge instruments. Suggestions: omit --judge-prompt-pack or select a judged preset. Because: a pack cannot localize code-only rules.");
        if (judge is not IJudgePromptBindable bindable)
            throw new NotSupportedException("Expected: a prompt-pack-capable judge. Actual: this judge does not support binding. Suggestions: use ChatClientEvaluator or implement IJudgePromptBindable. Because: requested localization cannot be ignored.");
        foreach (var definition in definitions)
        {
            var resolved = new JudgePromptResolver().Resolve(definition, options);
            var bound = bindable.WithPromptDefinition(definition, options);
            if ((bound as IResolvedJudgePromptSource)?.ResolvedPrompt?.Provenance.Digest != resolved.Provenance.Digest)
                throw new NotSupportedException("Expected: the selected prompt identity after binding. Actual: the judge did not preserve it. Suggestions: implement IResolvedJudgePromptSource consistently. Because: preflight and execution must use the same instrument.");
        }
    }

    internal static bool IsConfigurationError(Exception ex) =>
        ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException;
}
