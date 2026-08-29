// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>What one TypedMemEval judge call decided, and what it cost.</summary>
public sealed class TypedMemEvalJudgment
{
    /// <summary>The verbal outcome, or null when the judge produced no usable verdict.</summary>
    public required TypedMemEvalOutcome? Outcome { get; init; }

    /// <summary>The judge's own reasoning field; null under a failed parse.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Bounded raw response, when retained.</summary>
    public string? RawResponse { get; init; }

    /// <summary>Whether the response asserted a superseded value as current (Forgetting only).</summary>
    public bool StaleValueAsserted { get; init; }

    /// <summary>
    /// Whether the response asserted the fact is no longer known or valid (Forgetting only).
    /// </summary>
    /// <remarks>
    /// An observation about what was said, not about whether saying it was right. It is what
    /// separates over-forgetting and mis-attributed forgetting from an ordinary wrong answer, which
    /// the outcome alone cannot do.
    /// </remarks>
    public bool ClaimedNoLongerKnown { get; init; }

    /// <summary>Observed item pairs placed in the right order (list-order only).</summary>
    public int? OrderedPairsCorrect { get; init; }

    /// <summary>Observed item pairs considered (list-order only).</summary>
    public int? OrderedPairsTotal { get; init; }

    /// <summary>
    /// Which branch of the vertical's discriminator the judge reports having taken, or null where
    /// the contract does not ask for one.
    /// </summary>
    /// <remarks>
    /// An independent observation of the step the outcome was derived from. Without it a template
    /// that suppresses a label outright is indistinguishable from one that discriminates properly,
    /// which is how the first Bitemporal body scored 24/24 while being overfitted.
    /// </remarks>
    public string? QuestionAsks { get; init; }

    /// <summary>Bounded AgentEval-owned failure code; never provider exception text.</summary>
    public string? SafeFailureCode { get; init; }

    /// <summary>Provider calls attempted, including retries and response-format fallbacks.</summary>
    public int LlmCallCount { get; init; }

    /// <summary>Provider calls made by the first attempt.</summary>
    public int PrimaryLlmCallCount { get; init; }

    /// <summary>Provider calls made by retries.</summary>
    public int RetryLlmCallCount => LlmCallCount - PrimaryLlmCallCount;

    /// <summary>Logical judge attempts used.</summary>
    public int AttemptsUsed { get; init; }

    /// <summary>Judge tokens consumed across attempts.</summary>
    public int TokensUsed { get; init; }

    /// <summary>Provider backend build identifier, when returned.</summary>
    public string? SystemFingerprint { get; init; }
}

/// <summary>
/// The family's five-way outcome judge.
/// </summary>
/// <remarks>
/// Structured-JSON only, by design (ADR-026 §3): a free-text protocol cannot carry a five-way
/// outcome without recovering it from prose, which is the failure mode the 0.19 judge hardening
/// was built to end. Retry accounting, response-format fallback, and raw-response retention behave
/// exactly as the LongMemEval judge's do, so a caller reconciling judge spend across the two
/// benchmarks reads the same numbers the same way.
/// </remarks>
public sealed class TypedMemEvalJudge
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<TypedMemEvalJudge> _logger;

    // Remembered per instance: once a provider has rejected a response-format constraint there is
    // no value in paying for that rejection on every subsequent question.
    private bool _jsonSchemaUnsupported;
    private bool _jsonModeUnsupported;

    /// <summary>The pinned fingerprint of the templates this judge sends.</summary>
    public static string PromptFingerprint => TypedMemEvalJudgePrompts.Fingerprint;

    /// <summary>Creates a judge over a chat client.</summary>
    public TypedMemEvalJudge(IChatClient chatClient, ILogger<TypedMemEvalJudge>? logger = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _logger = logger ?? NullLogger<TypedMemEvalJudge>.Instance;
    }

    /// <summary>Judges one answer against gold under the vertical's template.</summary>
    public async Task<TypedMemEvalJudgment> JudgeAsync(
        string agentResponse,
        string question,
        string goldAnswer,
        TypedMemEvalVertical vertical,
        TypedMemEvalExtension extension,
        ExternalBenchmarkOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentResponse);
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(options);

        var kind = SelectKind(vertical, extension);
        var prompt = BuildPrompt(question, goldAnswer, agentResponse, vertical, extension, kind);

        var totalTokens = 0;
        var providerCalls = 0;
        var primaryCalls = 0;
        var attempts = 0;
        TypedMemEvalVerdict.Parsed parsed = default;
        string? raw = null;
        string? fingerprint = null;

        while (attempts <= options.MaxJudgeRetries)
        {
            attempts++;
            try
            {
                var response = await InvokeProviderAsync(prompt, kind, options, ct, () => providerCalls++)
                    .ConfigureAwait(false);

                totalTokens += (int)(response.Usage?.TotalTokenCount ?? 0);
                fingerprint = ProviderFingerprint.FromChatResponse(response);
                raw = response.Text;
                parsed = TypedMemEvalVerdict.Parse(response.Text, response.FinishReason?.Value);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var safeCode = ex is TimeoutException ? "timeout" : "provider_error";
                _logger.LogWarning(
                    "TypedMemEval judge provider failure on a {Vertical} question: {FailureCode}",
                    vertical, safeCode);
                parsed = new TypedMemEvalVerdict.Parsed(null, null, false, false, null, null, null, safeCode);
                raw = null;
            }

            if (attempts == 1)
                primaryCalls = providerCalls;

            if (parsed.Outcome is not null)
                break;

            if (options.JudgeFailurePolicy == JudgeFailurePolicy.FailRun)
            {
                throw new InvalidOperationException(
                    $"TypedMemEval judge produced no verdict ({parsed.FailureCode}) and " +
                    $"JudgeFailurePolicy is FailRun.");
            }
        }

        var bounded = raw?.Trim();
        if (bounded is { Length: > 4096 })
            bounded = bounded[..4096];

        return new TypedMemEvalJudgment
        {
            Outcome = parsed.Outcome,
            Reasoning = parsed.Reasoning,
            RawResponse = options.JudgeEvidenceMode == JudgeEvidenceMode.Raw || options.RetainRawJudgeResponse
                ? bounded
                : null,
            StaleValueAsserted = parsed.StaleValueAsserted,
            ClaimedNoLongerKnown = parsed.ClaimedNoLongerKnown,
            OrderedPairsCorrect = parsed.OrderedPairsCorrect,
            OrderedPairsTotal = parsed.OrderedPairsTotal,
            QuestionAsks = parsed.QuestionAsks,
            SafeFailureCode = parsed.FailureCode,
            LlmCallCount = providerCalls,
            PrimaryLlmCallCount = primaryCalls,
            AttemptsUsed = attempts,
            TokensUsed = totalTokens,
            SystemFingerprint = fingerprint
        };
    }

    /// <summary>
    /// Chooses the verdict contract. Driven by shape rather than vertical alone, because Episodic
    /// needs the ordering tally only for its list-order questions and asking for it elsewhere would
    /// invite the model to invent a tally for a question that has no items to order.
    /// </summary>
    internal static TypedMemEvalVerdict.Kind SelectKind(
        TypedMemEvalVertical vertical, TypedMemEvalExtension extension)
        => vertical switch
        {
            TypedMemEvalVertical.Forgetting => TypedMemEvalVerdict.Kind.Forgetting,
            TypedMemEvalVertical.Bitemporal => TypedMemEvalVerdict.Kind.Bitemporal,
            TypedMemEvalVertical.Temporal => TypedMemEvalVerdict.Kind.Temporal,
            TypedMemEvalVertical.Episodic when extension.Shape == "list-order"
                => TypedMemEvalVerdict.Kind.ListOrder,
            _ => TypedMemEvalVerdict.Kind.Base
        };

    private static string BuildPrompt(
        string question,
        string goldAnswer,
        string agentResponse,
        TypedMemEvalVertical vertical,
        TypedMemEvalExtension extension,
        TypedMemEvalVerdict.Kind kind)
    {
        var body = vertical switch
        {
            TypedMemEvalVertical.Prospective =>
                TypedMemEvalJudgePrompts.Prospective(question, goldAnswer, agentResponse),

            TypedMemEvalVertical.Bitemporal =>
                TypedMemEvalJudgePrompts.Bitemporal(question, goldAnswer, agentResponse),

            TypedMemEvalVertical.Temporal =>
                TypedMemEvalJudgePrompts.Temporal(question, goldAnswer, agentResponse),

            TypedMemEvalVertical.Conjunction =>
                TypedMemEvalJudgePrompts.Conjunction(question, goldAnswer, agentResponse),

            TypedMemEvalVertical.Forgetting =>
                TypedMemEvalJudgePrompts.Forgetting(question, goldAnswer, agentResponse),

            TypedMemEvalVertical.Episodic when extension.Shape == "list-order" =>
                TypedMemEvalJudgePrompts.ListOrder(question, goldAnswer, agentResponse),

            TypedMemEvalVertical.Arithmetic when extension.Derivation is { } derivation =>
                TypedMemEvalJudgePrompts.Arithmetic(
                    question, goldAnswer, agentResponse,
                    derivation.Operation,
                    string.Join(", ", derivation.Inputs.Select(i =>
                        i.Value.ToString("0.####", CultureInfo.InvariantCulture))),
                    derivation.Value.ToString("0.####", CultureInfo.InvariantCulture),
                    derivation.Unit),

            _ => TypedMemEvalJudgePrompts.Standard(question, goldAnswer, agentResponse)
        };

        return body + TypedMemEvalJudgePrompts.ContractFor(kind);
    }

    /// <summary>
    /// Issues one judge call, preferring the provider's schema-constrained format and degrading to
    /// plain JSON mode and then to an unconstrained call. The prompt carries the JSON contract
    /// either way, so an unconstrained provider can still comply — and the parser still refuses to
    /// guess if it does not.
    /// </summary>
    private async Task<ChatResponse> InvokeProviderAsync(
        string prompt,
        TypedMemEvalVerdict.Kind kind,
        ExternalBenchmarkOptions options,
        CancellationToken ct,
        Action countCall)
    {
        var messages = new[] { new ChatMessage(ChatRole.User, prompt) };

        if (!_jsonSchemaUnsupported)
        {
            try
            {
                countCall();
                return await _chatClient.GetResponseAsync(
                    messages,
                    ChatOptionsFor(options, ChatResponseFormat.ForJsonSchema(
                        TypedMemEvalVerdict.Schema(kind),
                        TypedMemEvalVerdict.SchemaName,
                        "Closed-set TypedMemEval judge outcome with separate reasoning.")),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsResponseFormatUnsupported(ex))
            {
                _jsonSchemaUnsupported = true;
                _logger.LogInformation(
                    "Judge provider rejected a JSON-schema response format; falling back to JSON mode.");
            }
        }

        if (!_jsonModeUnsupported)
        {
            try
            {
                countCall();
                return await _chatClient
                    .GetResponseAsync(messages, ChatOptionsFor(options, ChatResponseFormat.Json), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsResponseFormatUnsupported(ex))
            {
                _jsonModeUnsupported = true;
                _logger.LogInformation(
                    "Judge provider rejected JSON mode; falling back to an unconstrained judge call.");
            }
        }

        countCall();
        return await _chatClient
            .GetResponseAsync(messages, ChatOptionsFor(options, null), ct)
            .ConfigureAwait(false);
    }

    private static ChatOptions ChatOptionsFor(
        ExternalBenchmarkOptions options, ChatResponseFormat? responseFormat)
        => new()
        {
            // Null by default on purpose: an explicit temperature is rejected by some
            // reasoning-model deployments, including ones that reject 0 specifically.
            Temperature = (float?)options.JudgeTemperature,
            MaxOutputTokens = options.JudgeMaxOutputTokens,
            ResponseFormat = responseFormat
        };

    // A provider that does not support the requested response format surfaces an HTTP 400
    // invalid_request_error naming the parameter. Recognise THAT and only that, so a genuine judge
    // error (network, timeout, overload) still propagates instead of being retried as a capability
    // problem.
    private static bool IsResponseFormatUnsupported(Exception ex)
    {
        var m = ex.Message;
        var namesFormat =
            m.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || m.Contains("response format", StringComparison.OrdinalIgnoreCase)
            || m.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
            || m.Contains("json_object", StringComparison.OrdinalIgnoreCase)
            || m.Contains("json mode", StringComparison.OrdinalIgnoreCase)
            || m.Contains("structured output", StringComparison.OrdinalIgnoreCase);
        var looksRejected =
            m.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || m.Contains("does not support", StringComparison.OrdinalIgnoreCase)
            || m.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || m.Contains("400", StringComparison.OrdinalIgnoreCase);
        return namesFormat && looksRejected;
    }
}
