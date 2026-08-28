// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Memory.Tests;

/// <summary>
/// The judge-drift tripwire for the TypedMemEval family.
/// </summary>
/// <remarks>
/// <para>
/// ADR-026 §6 fixes the precedence rules the judge templates implement — a hedged answer is graded
/// on the value it states, a negative gold makes the negative answer correct, recalling a
/// superseded value while marking it superseded is ideal memory rather than a mistake. Those rules
/// are what the family's outcome counts <i>mean</i>. A template edit that loses one moves every
/// number the benchmark produces without moving anything about the system under test, which is
/// exactly the drift the pinned fingerprint exists to detect.
/// </para>
/// <para>
/// The fingerprint alone says the templates changed; it cannot say the semantics survived. This set
/// of hand-labelled cases is what says that, and the recorded-result file is the join between the
/// two: CI fails the moment a template edit lands without a re-measurement, and the recorded
/// measurement names the model and date it came from, so a number nobody can reproduce is legible
/// as one rather than passing as evidence.
/// </para>
/// </remarks>
public sealed class TypedMemEvalJudgeCalibrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>Share of calibration cases the judge must label as the humans labelled them.</summary>
    /// <remarks>
    /// Below 1.0 because the set is deliberately built out of near-miss pairs that sit on the
    /// precedence boundaries — two cases differing by a clause and by one outcome — and a defensible
    /// judge can lose a handful of those without having lost the rules. Well above chance on this
    /// label mix: answering "correct" to everything scores 0.43. What it catches is a judge that has
    /// broadly stopped implementing §6; what catches a single edited template is the fingerprint
    /// assertion below, which needs no credentials and no budget.
    /// </remarks>
    private const double AgreementThreshold = 0.85;

    private const string CalibrationResource = "Data.typedmemeval-judge-calibration.json";
    private const string RecordedResultResource = "Data.typedmemeval-judge-calibration-result.json";

    // Every §6 precedence rule, keyed by the calibration set's own rule field, hard-coded here on
    // purpose: derived from the file, this list would shrink silently with the file. A case deleted
    // for being inconvenient has to fail the build instead.
    private static readonly string[] s_requiredRules =
    [
        "hedged-states-value",
        "genuine-refusal-abstains",
        "confident-denial-missed",
        "negative-gold-correct",
        "premature-fires-early",
        "forgetting-invalidation-acknowledged",
        "forgetting-stale-value-asserted",
        "forgetting-over-forgetting",
        "forgetting-mis-attributed",
        "arith-exact-correct",
        "arith-rounding-matches",
        "arith-rounding-mismatch",
        "arith-correct-number-wrong-reasoning",
        "listorder-all-pairs-ordered",
        "listorder-inversion",
        "listorder-under-two-items-denied"
    ];

    // The five the judge can return. Inconclusive and Unrun are harness states, never labels.
    private static readonly TypedMemEvalOutcome[] s_emittableOutcomes =
    [
        TypedMemEvalOutcome.Correct,
        TypedMemEvalOutcome.Wrong,
        TypedMemEvalOutcome.Abstained,
        TypedMemEvalOutcome.Missed,
        TypedMemEvalOutcome.Premature
    ];

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false
    };

    private static readonly Lazy<IReadOnlyList<CalibrationCase>> s_cases = new(() =>
        JsonSerializer.Deserialize<List<CalibrationCase>>(ReadResource(CalibrationResource), s_json)
        ?? throw new InvalidOperationException("The TypedMemEval calibration set deserialized to null."));

    private static IReadOnlyList<CalibrationCase> Cases => s_cases.Value;

    [Fact]
    public void CalibrationSet_ParsesWithUniqueIdsAndUsableLabels()
    {
        // Scaled to the number of verticals rather than pinned to a literal range. Every vertical
        // carries at least 20 cases (see EveryVertical_HasEnoughCasesToDiagnoseItsOwnTemplate), so a
        // fixed upper bound fails on the day a vertical is added and says nothing about what broke —
        // which it did, at 168 against a ceiling of 150.
        var verticals = TypedMemEvalVerticals.All.Count;
        Assert.InRange(Cases.Count, verticals * 20, verticals * 30);

        var duplicates = Cases
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.True(duplicates.Length == 0, $"duplicate calibration ids: {string.Join(", ", duplicates)}");

        foreach (var calibrationCase in Cases)
        {
            Assert.True(
                Enum.TryParse<TypedMemEvalOutcome>(calibrationCase.Expected, out var outcome) &&
                Enum.IsDefined(outcome),
                $"{calibrationCase.Id}: '{calibrationCase.Expected}' is not a TypedMemEvalOutcome.");

            // A label the judge cannot return is not a label. Inconclusive means the judge failed and
            // Unrun means the question was skipped, and neither is something an answer can deserve.
            Assert.True(
                s_emittableOutcomes.Contains(outcome),
                $"{calibrationCase.Id}: {outcome} is a harness state, not a verdict the judge can emit.");

            Assert.True(
                Enum.TryParse<TypedMemEvalVertical>(calibrationCase.Vertical, out _),
                $"{calibrationCase.Id}: '{calibrationCase.Vertical}' is not a TypedMemEvalVertical.");

            Assert.False(string.IsNullOrWhiteSpace(calibrationCase.Question), calibrationCase.Id);
            Assert.False(string.IsNullOrWhiteSpace(calibrationCase.Gold), calibrationCase.Id);
            Assert.False(string.IsNullOrWhiteSpace(calibrationCase.Answer), calibrationCase.Id);
            Assert.False(string.IsNullOrWhiteSpace(calibrationCase.Rule), calibrationCase.Id);

            // The note is the label's justification. An unlabelled disagreement in a live run is a
            // fight between two opinions; a noted one is a fight against a written rule.
            Assert.False(string.IsNullOrWhiteSpace(calibrationCase.Note), calibrationCase.Id);
        }
    }

    [Fact]
    public void ArithmeticCases_CarryTheGoldDerivation()
    {
        // Without a derivation the runner falls back to the standard template, so an arithmetic case
        // would be measuring the wrong prompt — and the numeric-normalization rules the arithmetic
        // rule keys claim to cover would not be in front of the judge at all.
        var withoutDerivation = Cases
            .Where(c => string.Equals(c.Vertical, nameof(TypedMemEvalVertical.Arithmetic), StringComparison.Ordinal))
            .Where(c => c.Derivation is null)
            .Select(c => c.Id)
            .ToArray();

        Assert.True(
            withoutDerivation.Length == 0,
            $"arithmetic cases with no derivation: {string.Join(", ", withoutDerivation)}");
    }

    [Fact]
    public void CalibrationSet_CoversEveryPrecedenceRule()
    {
        var present = Cases.Select(c => c.Rule).ToHashSet(StringComparer.Ordinal);
        var missing = s_requiredRules.Where(rule => !present.Contains(rule)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"§6 precedence rules with no calibration case left: {string.Join(", ", missing)}. " +
            $"Restore the case or amend the ADR — an unrepresented rule is one a template edit can " +
            $"drop without any test noticing.");
    }

    [Fact]
    public void CalibrationSet_RepresentsEveryOutcomeTheJudgeCanEmit()
    {
        var labelled = Cases
            .Select(c => Enum.Parse<TypedMemEvalOutcome>(c.Expected))
            .ToHashSet();

        var absent = s_emittableOutcomes.Where(outcome => !labelled.Contains(outcome)).ToArray();

        // An outcome with no case is an outcome the measurement cannot see the judge lose.
        Assert.True(absent.Length == 0, $"outcomes with no calibration case: {string.Join(", ", absent)}");
    }

    [Fact]
    public void EveryVertical_HasEnoughCasesToDiagnoseItsOwnTemplate()
    {
        foreach (var vertical in Enum.GetValues<TypedMemEvalVertical>())
        {
            var count = Cases.Count(c =>
                string.Equals(c.Vertical, vertical.ToString(), StringComparison.Ordinal));

            // Each vertical has its own template and its own failure modes, so agreement has to be
            // readable per vertical rather than only as one family-wide number.
            Assert.True(count >= 20, $"{vertical} has {count} calibration cases, fewer than 20.");
        }
    }

    [Fact]
    public void CalibrationShapes_AreShapesTheShippedCorporaUse()
    {
        // The shape string is not decoration: it routes the question to a template, and the Episodic
        // list-order contract is selected by the literal "list-order". A typo there would silently
        // grade list-order cases under the base contract and the agreement number would look fine.
        foreach (var vertical in Enum.GetValues<TypedMemEvalVertical>())
        {
            var corpusShapes = ShapesInCorpus(vertical);
            var unknown = Cases
                .Where(c => string.Equals(c.Vertical, vertical.ToString(), StringComparison.Ordinal))
                .Where(c => !corpusShapes.Contains(c.Shape))
                .Select(c => $"{c.Id}:{c.Shape}")
                .ToArray();

            Assert.True(
                unknown.Length == 0,
                $"{vertical} calibration cases naming shapes the corpus does not have: " +
                $"{string.Join(", ", unknown)}");
        }
    }

    [Fact]
    public void RecordedResult_WasMeasuredAgainstTheCurrentJudgePrompt()
    {
        using var document = JsonDocument.Parse(ReadResource(RecordedResultResource));
        var root = document.RootElement;

        var recorded = root.GetProperty("judge_prompt_fingerprint").GetString();

        // This is the tripwire. Editing any judge template changes the fingerprint, and a stored
        // agreement number taken under the old text describes a judge that no longer exists — so the
        // build stops until someone re-measures rather than letting the old number carry forward.
        Assert.Equal(TypedMemEvalJudge.PromptFingerprint, recorded);

        var agreement = root.GetProperty("agreement");
        if (agreement.ValueKind is JsonValueKind.Null)
        {
            // Honest state: the templates are pinned and nobody has run the live arm yet. The set is
            // still a build-time guard; it just is not yet a measurement, and it says so.
            Assert.Equal("not_measured", root.GetProperty("status").GetString());
            return;
        }

        Assert.True(
            agreement.GetDouble() >= AgreementThreshold,
            $"recorded judge agreement {agreement.GetDouble():0.###} is below the {AgreementThreshold} floor.");

        // A number with no model and no date is not a measurement anyone can repeat or dispute.
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("model").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("measured_at").GetString()));
        Assert.Equal("measured", root.GetProperty("status").GetString());

        // The record has to describe the set it is read as validating. Without this the fingerprint
        // is the only thing holding it, and the fingerprint covers the prompt TEXT, not the case
        // list — so cases can be added under an unchanged fingerprint and the stored agreement
        // silently describes a shrinking fraction of them. That is exactly what happened: Bitemporal
        // and Temporal arrived in 0.26.0-beta with 48 cases between them, both routed to
        // StandardBody, which was already in the fingerprint. Nothing changed, nothing fired, and a
        // measurement over 120 of 168 cases went on being read as a family-wide number.
        var recordedCases = root.GetProperty("cases").GetInt32();
        Assert.True(
            recordedCases == Cases.Count,
            $"the recorded measurement covers {recordedCases} cases but the calibration set holds " +
            $"{Cases.Count}. Growing the set cannot invalidate a stored number on its own — the " +
            $"fingerprint pins the prompt text, not the case list — so re-measure and restate the " +
            $"count. A record that describes a subset is not evidence about the whole.");

        // Same failure, per vertical: a vertical added with no entry here would be absent rather
        // than low, and absence reads as nothing to see. Checked against the enum and not against
        // the calibration file, so the expectation cannot shrink alongside the thing it measures.
        var perVertical = root.GetProperty("per_vertical");
        var unmeasured = Enum.GetValues<TypedMemEvalVertical>()
            .Where(vertical => !perVertical.TryGetProperty(vertical.ToString(), out _))
            .ToArray();

        Assert.True(
            unmeasured.Length == 0,
            $"verticals with no recorded per-vertical agreement: {string.Join(", ", unmeasured)}. " +
            $"Each one has its own template and its own failure modes; one missing here is one the " +
            $"family-wide number is averaging over without ever having seen.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LiveJudge_LabelsTheCalibrationSetAsTheHumansDid()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(deployment))
        {
            // Returns having asserted nothing about agreement, on purpose. xUnit cannot skip a fact
            // from inside it, and the alternatives are both worse: a green test that measured nothing
            // reads as a passed calibration, and a red one would fail every contributor's build for
            // not holding a key. The ordinary CI workflow supplies endpoint and key but no deployment,
            // so this arm stays unspent there and runs from the on-demand LLM workflow.
            return;
        }

        var options = new ExternalBenchmarkOptions
        {
            JudgeVerdictProtocol = JudgeVerdictProtocol.StructuredJson,
            JudgeMaxOutputTokens = AzureOpenAIJudgeClient.MinimumCompletionBudget,
            MaxJudgeRetries = 1
        };
        options.Validate();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var observed = new ConcurrentBag<(CalibrationCase Case, TypedMemEvalOutcome? Actual, string Detail)>();

        // Bounded rather than unbounded: the whole set is one burst of judge calls, and a deployment
        // that throttles would turn a calibration measurement into a retry-accounting measurement.
        using var gate = new SemaphoreSlim(4);

        await Task.WhenAll(Cases.Select(async calibrationCase =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // One judge per case: the type remembers a provider's response-format capabilities on
                // the instance, and that is per-run state rather than something a parallel measurement
                // should be sharing.
                var judge = new TypedMemEvalJudge(
                    new AzureOpenAIJudgeClient(http, endpoint, apiKey, deployment));

                var judgment = await judge.JudgeAsync(
                    calibrationCase.Answer,
                    calibrationCase.Question,
                    calibrationCase.Gold,
                    Enum.Parse<TypedMemEvalVertical>(calibrationCase.Vertical),
                    ExtensionFor(calibrationCase),
                    options).ConfigureAwait(false);

                // A judge failure is recorded as a disagreement rather than dropped: a verdict that
                // never arrived is not a case this calibration measured successfully.
                observed.Add((
                    calibrationCase,
                    judgment.Outcome,
                    judgment.Outcome is null
                        ? $"no verdict ({judgment.SafeFailureCode})"
                        : judgment.Reasoning ?? "(no reasoning)"));
            }
            finally
            {
                gate.Release();
            }
        }));

        var results = observed.ToArray();
        var agreed = results.Count(r => r.Actual == Enum.Parse<TypedMemEvalOutcome>(r.Case.Expected));
        var agreement = (double)agreed / Cases.Count;

        // Printed, not just asserted: this output is what the recorded-result file is filled in from,
        // and a run that only says pass or fail leaves the maintainer nothing to record.
        _output.WriteLine($"deployment: {deployment}");
        _output.WriteLine($"agreement: {agreement:0.###} ({agreed}/{Cases.Count})");
        foreach (var vertical in Enum.GetValues<TypedMemEvalVertical>())
        {
            var subset = results
                .Where(r => string.Equals(r.Case.Vertical, vertical.ToString(), StringComparison.Ordinal))
                .ToArray();
            if (subset.Length == 0)
                continue;

            var matched = subset.Count(r => r.Actual == Enum.Parse<TypedMemEvalOutcome>(r.Case.Expected));
            _output.WriteLine(
                $"  {vertical}: {(double)matched / subset.Length:0.###} ({matched}/{subset.Length})");
        }

        var disagreements = results
            .Where(r => r.Actual != Enum.Parse<TypedMemEvalOutcome>(r.Case.Expected))
            .OrderBy(r => r.Case.Id, StringComparer.Ordinal)
            .Select(r =>
                $"{r.Case.Id} [{r.Case.Rule}] expected {r.Case.Expected}, got " +
                $"{r.Actual?.ToString() ?? "none"} - judge said: {r.Detail}")
            .ToArray();

        foreach (var line in disagreements)
            _output.WriteLine(line);

        Assert.True(
            agreement >= AgreementThreshold,
            $"judge agreement with the calibration set was {agreement:0.###} over {Cases.Count} cases " +
            $"against deployment '{deployment}', below the {AgreementThreshold} floor. " +
            $"Disagreements:{Environment.NewLine}{string.Join(Environment.NewLine, disagreements.Take(40))}");
    }

    private static TypedMemEvalExtension ExtensionFor(CalibrationCase calibrationCase) => new()
    {
        Vertical = calibrationCase.Vertical.ToLowerInvariant(),
        Shape = calibrationCase.Shape,

        // Empty on purpose. The judge reads the shape and the derivation and nothing else; coverage
        // is the report's other axis and has no bearing on whether a verdict was the right one.
        GoldSessionIndices = [],
        SessionIds = [],

        Derivation = calibrationCase.Derivation is not { } derivation
            ? null
            : new TypedMemEvalDerivation(
                derivation.Operation,
                [.. derivation.Inputs.Select(i => new TypedMemEvalDerivationInput(i.SessionIndex, i.Value))],
                derivation.Value,
                derivation.Unit)
    };

    private static HashSet<string> ShapesInCorpus(TypedMemEvalVertical vertical)
    {
        using var document = JsonDocument.Parse(TypedMemEvalCorpus.ReadJson(vertical));
        return document.RootElement.EnumerateArray()
            .Select(entry => entry.GetProperty("typedmemeval").GetProperty("shape").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ReadResource(string suffix)
    {
        var assembly = typeof(TypedMemEvalJudgeCalibrationTests).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new FileNotFoundException(
                $"Embedded calibration resource '{suffix}' was not found in {assembly.GetName().Name}.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>One hand-labelled case: an authored answer and the outcome §6 says it deserves.</summary>
    private sealed record CalibrationCase(
        string Id,
        string Vertical,
        string Shape,
        string Question,
        string Gold,
        string Answer,
        string Expected,
        string Rule,
        string Note,
        CalibrationDerivation? Derivation);

    /// <summary>The gold derivation an Arithmetic case's judge call carries.</summary>
    private sealed record CalibrationDerivation(
        string Operation,
        IReadOnlyList<CalibrationDerivationInput> Inputs,
        double Value,
        string Unit);

    private sealed record CalibrationDerivationInput(int SessionIndex, double Value);

    /// <summary>
    /// A minimal Azure OpenAI chat client, written here rather than taken as a package reference so
    /// the test project's dependency set does not grow for one env-gated arm.
    /// </summary>
    /// <remarks>
    /// Two deliberate omissions, both because this deployment family is a reasoning model: no
    /// temperature, which such deployments reject outright, and no response-format constraint. The
    /// judge's own fallback ladder would discover the second by spending a rejected call per
    /// question; the JSON contract is in the prompt either way, and the parser still refuses to
    /// guess when a response does not honour it.
    /// </remarks>
    private sealed class AzureOpenAIJudgeClient : IChatClient
    {
        /// <summary>
        /// Floor on the completion budget. Reasoning tokens are drawn from it before a single
        /// visible token is emitted, so a small budget returns an empty message and the run would
        /// score every case as a judge failure instead of a verdict.
        /// </summary>
        internal const int MinimumCompletionBudget = 1500;

        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly Uri _uri;

        internal AzureOpenAIJudgeClient(HttpClient http, string endpoint, string apiKey, string deployment)
        {
            _http = http;
            _apiKey = apiKey;
            _uri = new Uri(
                $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/chat/completions" +
                "?api-version=2024-12-01-preview");
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n\n", chatMessages.Select(message => message.Text));
            var payload = JsonSerializer.Serialize(new
            {
                messages = new[] { new { role = "user", content = prompt } },
                max_completion_tokens = Math.Max(MinimumCompletionBudget, options?.MaxOutputTokens ?? 0)
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, _uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("api-key", _apiKey);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Status and a bounded body: enough to tell a throttle from a bad deployment name,
                // without pasting a provider payload of unknown length into a test log.
                throw new InvalidOperationException(
                    $"Azure OpenAI returned {(int)response.StatusCode}: " +
                    $"{body[..Math.Min(400, body.Length)]}");
            }

            using var document = JsonDocument.Parse(body);
            var choice = document.RootElement.GetProperty("choices")[0];
            var text = choice.GetProperty("message").TryGetProperty("content", out var content)
                ? content.GetString()
                : null;
            var finishReason = choice.TryGetProperty("finish_reason", out var reason)
                ? reason.GetString()
                : null;

            // The finish reason is passed through because the verdict parser reads it: a response
            // truncated by the token budget is reported as such rather than as an unparseable judge.
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text ?? string.Empty))
            {
                FinishReason = string.IsNullOrEmpty(finishReason)
                    ? null
                    : new ChatFinishReason(finishReason)
            };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        // The HttpClient belongs to the test that created it and outlives every client wrapped
        // around it, so disposing one here would break the others.
        public void Dispose()
        {
        }
    }
}
