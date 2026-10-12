// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.Results;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Runner;

/// <summary>
/// One role's client for one case (or readiness probe). Reserve each call before I/O; retain that reservation
/// when usage is missing or execution is interrupted. The inner endpoint must have SDK retries disabled.
/// </summary>
internal sealed class BudgetedChatClient : DelegatingChatClient
{
    private readonly object _sync = new();
    private readonly string _model;
    private readonly int _inputCap, _outputCap, _callCap;
    private readonly decimal _inputRate, _outputRate;
    private int _calls, _reported;
    private long _inputTokens, _outputTokens;
    private decimal _usd;
    private bool _boundExceeded;

    internal BudgetedChatClient(IChatClient inner, string model, int inputCap, int outputCap, int callCap,
        decimal inputUsdPerMillion, decimal outputUsdPerMillion) : base(inner)
    {
        if (string.IsNullOrWhiteSpace(model) || inputCap <= 0 || outputCap <= 0 || callCap <= 0
            || inputUsdPerMillion < 0 || outputUsdPerMillion < 0)
            throw new AefInputException("Runner client requires a model, positive input/output/call caps and non-negative prices.");
        _model = model;
        _inputCap = inputCap;
        _outputCap = outputCap;
        _callCap = callCap;
        _inputRate = inputUsdPerMillion / 1_000_000m;
        _outputRate = outputUsdPerMillion / 1_000_000m;
        // Fail before execution if a configured bound cannot be represented.
        try { UsdBound = checked(CallBound * callCap); }
        catch (OverflowException) { throw new AefInputException("Runner client cost bound is too large."); }
    }

    private decimal CallBound => checked(_inputCap * _inputRate + _outputCap * _outputRate);
    internal decimal UsdBound { get; }
    internal decimal IncurredUsd { get { lock (_sync) return _usd; } }
    internal int Calls { get { lock (_sync) return _calls; } }
    internal bool BoundExceeded { get { lock (_sync) return _boundExceeded; } }
    internal long? InputTokens { get { lock (_sync) return _calls > 0 && _reported == _calls ? _inputTokens : null; } }
    internal long? OutputTokens { get { lock (_sync) return _calls > 0 && _reported == _calls ? _outputTokens : null; } }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = Prepare(messages, options, cancellationToken);
        Reserve(cancellationToken);
        var response = await base.GetResponseAsync(request.Messages, request.Options, cancellationToken).ConfigureAwait(false);
        Complete(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = Prepare(messages, options, cancellationToken);
        Reserve(cancellationToken);
        UsageDetails? usage = null;
        await foreach (var update in base.GetStreamingResponseAsync(request.Messages, request.Options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents.OfType<UsageContent>())
            {
                usage ??= new UsageDetails();
                usage.Add(content.Details);
                if (usage.InputTokenCount > _inputCap || usage.OutputTokenCount > _outputCap)
                    Complete(usage); // Stop immediately; an abandoned stream must not hide a known overrun.
            }
            yield return update;
        }
        // Only natural completion releases the reservation. Disposing an unfinished iterator keeps its bound.
        Complete(usage);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(IChatClient) || serviceType == typeof(BudgetedChatClient)) return this;
        // Do not expose an underlying SDK client or another wrapper that could bypass accounting.
        return serviceType == typeof(ChatClientMetadata) ? base.GetService(serviceType) : null;
    }

    private (ChatMessage[] Messages, ChatOptions Options) Prepare(IEnumerable<ChatMessage> messages,
        ChatOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(messages);
        options ??= new ChatOptions();
        if (options.ConversationId is not null || options.ContinuationToken is not null || options.AllowBackgroundResponses is true
            || options.RawRepresentationFactory is not null
            || options.AdditionalProperties is { Count: > 0 } || options.Tools is { Count: > 0 }
            || (options.ModelId is not null && !StringComparer.Ordinal.Equals(options.ModelId, _model)))
            throw new AefInputException("Runner cannot bound hidden context, background responses, raw options, tools or a model override.");
        if (options.MaxOutputTokens is <= 0)
            throw new AefInputException("Runner output token limit must be positive.");

        var supplied = messages.ToArray();
        if (supplied.Any(m => m.RawRepresentation is not null || m.AdditionalProperties is { Count: > 0 }
            || m.Contents.Any(c => c is not TextContent || c.RawRepresentation is not null || c.AdditionalProperties is { Count: > 0 })))
            throw new AefInputException("Runner input must be text without raw or additional provider payloads.");
        // Snapshot supported input before counting it. A caller cannot later mutate its prompt or options past the cap.
        var copy = supplied.Select(m => new ChatMessage(m.Role,
            m.Contents.Cast<TextContent>().Select(c => (AIContent)new TextContent(c.Text)).ToList())
            { AuthorName = m.AuthorName }).ToArray();
        var cloned = JsonSerializer.Deserialize<ChatOptions>(
            JsonSerializer.Serialize(options, AIJsonUtilities.DefaultOptions), AIJsonUtilities.DefaultOptions)!;
        cloned.ModelId = _model;
        cloned.MaxOutputTokens = Math.Min(cloned.MaxOutputTokens ?? _outputCap, _outputCap);
        // Include all roles, instructions and structured-output schema, not only the last user message.
        // JSON escaping is conservative for UTF-8 text; reserve additional framing for supported chat endpoints.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { messages = copy, options = cloned }, AIJsonUtilities.DefaultOptions);
        if ((long)bytes.Length + 1024L + 64L * copy.Length > _inputCap)
            throw new AefInputException("Runner input exceeds its declared UTF-8 byte cap, including message framing.");
        return (copy, cloned);
    }

    private void Reserve(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_boundExceeded) throw new IOException("Runner provider previously exceeded its declared token bound.");
            if (_calls >= _callCap) throw new AefInputException("Runner role exceeded its per-case call cap.");
            _usd += CallBound;
            _calls++;
        }
    }

    private void Complete(UsageDetails? usage)
    {
        lock (_sync)
        {
            var input = usage?.InputTokenCount;
            var output = usage?.OutputTokenCount;
            var overrun = input > _inputCap || output > _outputCap;
            if (overrun) _boundExceeded = true;
            if (input is >= 0 && output is >= 0)
            {
                _usd += input.Value * _inputRate + output.Value * _outputRate - CallBound;
                _inputTokens = checked(_inputTokens + input.Value);
                _outputTokens = checked(_outputTokens + output.Value);
                _reported++;
            }
            else if (overrun)
            {
                // Incomplete usage still cannot hide a known overrun on the other dimension.
                _usd += Math.Max(input ?? 0, _inputCap) * _inputRate
                    + Math.Max(output ?? 0, _outputCap) * _outputRate - CallBound;
            }
            if (overrun)
            {
                throw new IOException("Runner provider exceeded its declared token bound.");
            }
        }
    }
}
