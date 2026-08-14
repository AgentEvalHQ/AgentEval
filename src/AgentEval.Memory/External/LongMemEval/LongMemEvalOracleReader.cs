// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using Microsoft.Extensions.AI;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Retrieval-bypassing answer adapter that reads an evaluator-projected oracle history directly.
/// It owns no memory provider, performs no retrieval, and clears all per-question state after
/// every invocation.
/// </summary>
/// <remarks>
/// <para>
/// This is the ceiling arm: what the answer model scores when the evidence it needs is already in
/// context. That number is a property of the dataset and the answer model, not of any memory
/// system — so every arm measured against it should be measured against the <i>same</i> one.
/// It is public for exactly that reason, together with
/// <see cref="LongMemEvalOracleProjector"/>, so a caller comparing their memory system to a ceiling
/// is comparing to AgentEval's ceiling rather than to a re-implementation of it.
/// </para>
/// <para>
/// It implements <see cref="IAnswerSamplingConfigurableAgent"/>, so
/// <see cref="Models.ExternalBenchmarkOptions.AnswerTemperature"/> and
/// <see cref="Models.ExternalBenchmarkOptions.AnswerSeed"/> reach the wire on this arm. Only values
/// the provider itself returned are surfaced back as echoes: re-reporting the requested value would
/// manufacture the confirmation the request was meant to establish.
/// </para>
/// </remarks>
public sealed class LongMemEvalOracleReader
    : IEvaluableAgent, IHistoryInjectableAgent, ITimestampedHistoryInjectableAgent,
      ISessionResettableAgent, IAnswerSamplingConfigurableAgent
{
    private readonly IChatClient _answerClient;
    private readonly List<(string UserMessage, string AssistantResponse)> _history = [];
    private double? _temperature;
    private int? _seed;
    private DateTimeOffset? _queryTime;

    /// <summary>Creates a reader over the answer client that will produce oracle answers.</summary>
    /// <param name="answerClient">Chat client used for the answer call. Required.</param>
    public LongMemEvalOracleReader(IChatClient answerClient)
        => _answerClient = answerClient ?? throw new ArgumentNullException(nameof(answerClient));

    /// <inheritdoc/>
    public string Name => "LongMemEval Oracle Reader";

    /// <inheritdoc/>
    public void InjectConversationHistory(
        IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
    {
        ArgumentNullException.ThrowIfNull(conversationTurns);
        _history.Clear();
        _history.AddRange(conversationTurns);
    }

    /// <inheritdoc/>
    public void InjectTimestampedConversationHistory(TimestampedConversationHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history.Clear();
        _history.AddRange(history.Turns.Select(turn => (
            UserMessage: Stamp(turn.Timestamp, turn.UserMessage),
            AssistantResponse: Stamp(turn.Timestamp, turn.AssistantResponse))));
        _queryTime = history.QueryTime;
    }

    /// <inheritdoc/>
    public AnswerSamplingAcknowledgement ConfigureAnswerSampling(AnswerSamplingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _temperature = request.Temperature;
        _seed = request.Seed;
        return AnswerSamplingAcknowledgement.AppliedFrom(request);
    }

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var messages = new List<ChatMessage>(_history.Count * 2 + 2);

        // Only present on a time-grounded run: the "now" that decides already-happened from
        // not-yet-happened. Stated as a system message so it does not collide with the harness's own
        // "Current Date:" line, which the control arm still prints into the prompt.
        if (_queryTime is { } queryTime)
            messages.Add(new ChatMessage(ChatRole.System, ChatClientAgentAdapter.QueryTimeSystemMessage(queryTime)));

        foreach (var (userMessage, assistantResponse) in _history)
        {
            messages.Add(new ChatMessage(ChatRole.User, userMessage));
            messages.Add(new ChatMessage(ChatRole.Assistant, assistantResponse));
        }
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        try
        {
            var response = await _answerClient.GetResponseAsync(
                messages,
                BuildChatOptions(),
                cancellationToken: cancellationToken);
            return new AgentResponse
            {
                Text = response.Text ?? string.Empty,
                AdditionalProperties = BuildObservedProperties(response)
            };
        }
        finally
        {
            _history.Clear();
            _queryTime = null;
        }
    }

    /// <inheritdoc/>
    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _history.Clear();
        _queryTime = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a turn's instant into the text the model reads.
    /// </summary>
    /// <remarks>
    /// Deliberate. The reader is a system that places messages in time perfectly, by construction:
    /// it receives the instants through the typed channel and puts them where its model can use
    /// them. That is an implementation of the time-grounding contract, not a leak of the scaffolding
    /// the contract removes — nothing in the corpus text told it these dates. The rendering is
    /// shared with <see cref="ChatClientAgentAdapter"/> so the ceiling arm and the in-context
    /// baseline are never measured under two different formats.
    /// </remarks>
    private static string Stamp(DateTimeOffset timestamp, string text)
        => ChatClientAgentAdapter.Stamp(timestamp, text);

    /// <summary>
    /// Null when no sampling was requested, so a run that did not ask to pin the answer model sends
    /// exactly the request it always sent.
    /// </summary>
    private ChatOptions? BuildChatOptions()
        => _temperature is null && _seed is null
            ? null
            : new ChatOptions
            {
                Temperature = (float?)_temperature,
                Seed = _seed
            };

    /// <summary>
    /// Surfaces only what the provider actually returned: the sampling values it echoed and its
    /// backend build identifier. Returns null when it returned neither, leaving the response shape
    /// untouched for runs that observe nothing.
    /// </summary>
    private Dictionary<string, object?>? BuildObservedProperties(ChatResponse response)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (_temperature is not null &&
            ProviderSamplingEcho.TemperatureFromChatResponse(response) is { } echoedTemperature)
        {
            properties["temperature"] = echoedTemperature;
        }
        if (_seed is not null &&
            ProviderSamplingEcho.SeedFromChatResponse(response) is { } echoedSeed)
        {
            properties["seed"] = echoedSeed;
        }
        if (ProviderFingerprint.FromChatResponse(response) is { } fingerprint)
            properties["system_fingerprint"] = fingerprint;

        return properties.Count > 0 ? properties : null;
    }
}
