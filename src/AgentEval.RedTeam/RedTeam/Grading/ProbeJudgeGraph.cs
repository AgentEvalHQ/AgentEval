// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Prompts;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.RedTeam;

/// <summary>Bounded, invocation-owned binding of the supported probe-evaluator graph.</summary>
internal static class ProbeJudgeGraph
{
    internal static string? CapturedDigest(IProbeEvaluator? evaluator) => (evaluator as BoundGraph)?.Digest;
    internal static IReadOnlyList<JudgePromptDefinition> Export(IProbeEvaluator evaluator) => Build(evaluator, null).Definitions;

    internal static IProbeEvaluator Bind(IProbeEvaluator evaluator, JudgePromptOptions selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Build(evaluator, selection);
    }

    private static BoundGraph Build(IProbeEvaluator evaluator, JudgePromptOptions? selection)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        var path = new HashSet<IProbeEvaluator>(ReferenceEqualityComparer.Instance);
        var declaredDefinitions = new Dictionary<(string Key, string Locale), string>();
        var definitions = new Dictionary<(string Key, string Locale), (JudgePromptDefinition Definition, string Digest)>();
        var resolver = new JudgePromptResolver();
        var count = 0;
        var (bound, identity) = Walk(evaluator, 0, selection);
        return new(bound, selection, bound, Digest(identity),
            Array.AsReadOnly(definitions.Values.Select(v => v.Definition)
                .OrderBy(d => d.EvaluatorKey, StringComparer.Ordinal).ThenBy(d => d.Locale, StringComparer.Ordinal).ToArray()));

        (IProbeEvaluator Evaluator, Node Identity) Walk(IProbeEvaluator current, int depth, JudgePromptOptions? requested)
        {
            if (current is null || depth >= 32 || ++count > 4096 || !path.Add(current))
                throw Invalid("null node, cycle, depth or node limit", "use an acyclic graph with depth below 32 and at most 4096 nodes");
            try
            {
                switch (current)
                {
                    case BoundGraph existing:
                        return Walk(existing.Source, depth + 1, requested ?? existing.Selection);
                    case BoundLeaf existing:
                        return Leaf(existing.Definition, existing.Settings, existing.Name, existing.Evaluate, existing.Options);
                    case LLMJudgeEvaluator judge when judge.GetType() == typeof(LLMJudgeEvaluator):
                        return Leaf(judge.CapturePromptDefinition(), judge.Settings, judge.Name, judge.EvaluateWithPromptsAsync);
                    case LikertJudgeEvaluator judge:
                        return Leaf(judge.CapturePromptDefinition(), judge.Settings, judge.Name, judge.EvaluateWithPromptsAsync);
                    case CompositeEvaluator composite when composite.GetType() == typeof(CompositeEvaluator):
                    {
                        if (!Enum.IsDefined(composite.Strategy)) throw Invalid("unknown aggregation strategy", "use a declared aggregation strategy");
                        var children = composite.Evaluators.Select(c => Walk(c, depth + 1, requested)).ToArray();
                        return (new CompositeEvaluator(composite.Strategy, children.Select(c => c.Evaluator).ToArray()),
                            new("aggregate", composite.Strategy.ToString(), children.Select(c => c.Identity).ToArray()));
                    }
                    case OutcomeFilterEvaluator filter:
                    {
                        if (!Enum.IsDefined(filter.Allowed)) throw Invalid("unknown allowed outcome", "use a declared evaluation outcome");
                        var child = Walk(filter.Inner, depth + 1, requested);
                        return (new OutcomeFilterEvaluator(child.Evaluator, filter.Allowed), new("outcome-filter", filter.Allowed.ToString(), [child.Identity]));
                    }
                    case ConjunctionGate gate:
                    {
                        if (gate.Result is not (EvaluationOutcome.Succeeded or EvaluationOutcome.Resisted))
                            throw Invalid("a conjunction cannot emit the specified outcome", "use Succeeded or Resisted for a conjunction");
                        var children = gate.Checks.Select(c => Walk(c, depth + 1, requested)).ToArray();
                        return (new ConjunctionGate(gate.Label, gate.Result, children.Select(c => c.Evaluator).ToArray()),
                            new("conjunction", JsonSerializer.Serialize(new { gate.Label, gate.Result }), children.Select(c => c.Identity).ToArray()));
                    }
                    case FidelityCompositeEvaluator fidelity:
                    {
                        var behavioral = Walk(fidelity.Behavioral, depth + 1, requested);
                        var verbal = Walk(fidelity.Verbal, depth + 1, requested);
                        return (new FidelityCompositeEvaluator(behavioral.Evaluator, verbal.Evaluator), new("behavioral-first", null, [behavioral.Identity, verbal.Identity]));
                    }
                    case ToolInvocationEvaluator tools:
                        return (tools, new("forbidden-tool-observation.v1", JsonSerializer.Serialize(tools.ForbiddenTools
                            .Select(t => t.ToUpperInvariant()).OrderBy(t => t, StringComparer.Ordinal)), []));
                    case DataPoisoningDecomposedEvaluator poison:
                    {
                        var child = Walk(poison.FalseFactJudge, depth + 1, requested);
                        return (new DataPoisoningDecomposedEvaluator(child.Evaluator), new("poison-trigger-dispatch.v1", null, [child.Identity]));
                    }
                    case GroundTruthStanceEvaluator stance:
                        return Metadata(stance.PromptLeaf(), [DataPoisoningAttack.FalseFactMetadataKey, DataPoisoningAttack.TrueFactMetadataKey]);
                    case MisinformationEntityJudge entity:
                        return Metadata(entity.PromptLeaf(), [MisinformationAttack.EntityMetadataKey]);
                    case MetadataRequiredEvaluator metadata:
                    {
                        var child = Walk(metadata.Inner, depth + 1, requested);
                        return (new MetadataRequiredEvaluator(child.Evaluator, metadata.Keys),
                            new("required-oracle-facts", JsonSerializer.Serialize(metadata.Keys), [child.Identity]));
                    }
                    case ReasonRedactingEvaluator redacting:
                    {
                        var child = Walk(redacting.Inner, depth + 1, requested);
                        // Capture/redaction is an evidence policy, not a change to the judge's decision.
                        return (new ReasonRedactingEvaluator(child.Evaluator), child.Identity);
                    }
                    case TimeoutEvaluator timeout:
                    {
                        if (timeout.Timeout <= TimeSpan.Zero) throw Invalid("nonpositive grading timeout", "use a positive grading timeout");
                        var child = Walk(timeout.Inner, depth + 1, requested);
                        return (new TimeoutEvaluator(child.Evaluator, timeout.Timeout), new("timeout", timeout.Timeout.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), [child.Identity]));
                    }
                    default:
                        throw new NotSupportedException($"Expected: a supported probe-judge graph. Actual: unsupported node '{current.GetType().Name}'. Suggestions: use explicit supported judges and structural nodes, or evaluate this oracle separately without prompt selection. Because: an unknown wrapper or language-dependent detector cannot silently retain English behavior.");
                }

                (IProbeEvaluator, Node) Metadata(LLMJudgeEvaluator judge, string[] keys)
                {
                    var child = Walk(judge, depth + 1, requested);
                    return (new MetadataRequiredEvaluator(child.Evaluator, keys), new("required-oracle-facts", JsonSerializer.Serialize(keys), [child.Identity]));
                }

                (IProbeEvaluator, Node) Leaf(JudgePromptDefinition definition, LLMJudgeOptions settings, string name,
                    Func<AttackProbe, string, JudgePromptOptions, CancellationToken, Task<EvaluationResult>> evaluate,
                    JudgePromptOptions? priorSelection = null)
                {
                    ConfiguredProbeJudge.ValidateSettings(settings);
                    var declaredKey = (definition.EvaluatorKey, definition.Locale);
                    var declared = JsonSerializer.Serialize(definition);
                    if (declaredDefinitions.TryGetValue(declaredKey, out var earlier) && earlier != declared)
                        throw Invalid("conflicting source rubrics for one evaluator key and locale", "assign distinct stable keys before translating different rubrics");
                    declaredDefinitions[declaredKey] = declared;
                    var options = requested ?? priorSelection ?? settings.JudgePrompts ?? new JudgePromptOptions { Locale = definition.Locale };
                    var resolved = resolver.Resolve(definition, options);
                    var key = (resolved.Definition.EvaluatorKey, resolved.Definition.Locale);
                    if (definitions.TryGetValue(key, out var previous) && previous.Digest != resolved.Provenance.Digest)
                        throw Invalid("conflicting definitions for one evaluator key and locale", "assign stable distinct PromptKey values to different rubrics");
                    definitions[key] = (resolved.Definition, resolved.Provenance.Digest);
                    var policy = JsonSerializer.Serialize(new { prompt = resolved.Provenance.Digest, settings.Temperature, settings.MaxTokens,
                        reader = "redteam.configured-json.v1" });
                    return (new BoundLeaf(name, definition, settings, options, evaluate), new("judge", policy, []));
                }
            }
            finally { path.Remove(current); }
        }
    }

    private static string Digest(Node identity) => "sha256:" + Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { graphVersion = "redteam.graph.v1", root = identity }))).ToLowerInvariant();

    private static ArgumentException Invalid(string actual, string suggestion) => new(
        $"Expected: a bounded unambiguous probe-judge graph. Actual: {actual}. Suggestions: {suggestion}. Because: every declared leaf must be validated before execution.");

    private sealed record Node(string Kind, string? Policy, IReadOnlyList<Node> Children);

    private sealed class BoundLeaf(string name, JudgePromptDefinition definition, LLMJudgeOptions settings, JudgePromptOptions options,
        Func<AttackProbe, string, JudgePromptOptions, CancellationToken, Task<EvaluationResult>> evaluate) : IProbeEvaluator
    {
        public string Name => name;
        internal JudgePromptDefinition Definition => definition;
        internal LLMJudgeOptions Settings => settings;
        internal JudgePromptOptions Options => options;
        internal Func<AttackProbe, string, JudgePromptOptions, CancellationToken, Task<EvaluationResult>> Evaluate => evaluate;
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken cancellationToken = default) =>
            evaluate(probe, response, options, cancellationToken);
    }

    private sealed class MetadataRequiredEvaluator(IProbeEvaluator inner, string[] keys) : IProbeEvaluator
    {
        internal IProbeEvaluator Inner => inner;
        internal string[] Keys => keys;
        public string Name => inner.Name;
        public Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(probe);
            ArgumentNullException.ThrowIfNull(response);
            cancellationToken.ThrowIfCancellationRequested();
            if (keys.Any(k => probe.Metadata?.TryGetValue(k, out var value) != true || value is not string text || string.IsNullOrWhiteSpace(text)))
                return Task.FromResult(EvaluationResult.Inconclusive("Expected: declared oracle facts. Actual: missing or non-string evidence. Suggestions: supply the probe's required oracle metadata. Because: missing ground truth cannot establish a verdict."));
            return inner.EvaluateAsync(probe, response, cancellationToken);
        }
    }

    private sealed class BoundGraph(IProbeEvaluator source, JudgePromptOptions? selection, IProbeEvaluator inner, string digest,
        IReadOnlyList<JudgePromptDefinition> definitions) : IProbeEvaluator
    {
        internal IProbeEvaluator Source => source;
        internal JudgePromptOptions? Selection => selection;
        internal string Digest => digest;
        internal IReadOnlyList<JudgePromptDefinition> Definitions => definitions;
        public string Name => inner.Name;
        public async Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken cancellationToken = default) =>
            Attach(await inner.EvaluateAsync(probe, response, cancellationToken).ConfigureAwait(false));
        public async Task<EvaluationResult> EvaluateAsync(AttackProbe probe, AgentResponse response, CancellationToken cancellationToken = default) =>
            Attach(await inner.EvaluateAsync(probe, response, cancellationToken).ConfigureAwait(false));

        private EvaluationResult Attach(EvaluationResult result)
        {
            // Only supported structural nodes can assert tool fidelity in this graph. Their conclusive
            // observations remain code-derived even when advisory text judges were also invoked.
            var source = result.GradedBy;
            if (source is null && result.Outcome != EvaluationOutcome.Inconclusive)
                source = GradingMetadata.FidelityOf(result) > EvidenceFidelity.Verbal || result.JudgeInstruments is null
                    ? GradingProvenanceKind.Heuristic : GradingProvenanceKind.Judge;
            return result with { JudgeGraphDigest = digest, GradedBy = source };
        }
    }
}
