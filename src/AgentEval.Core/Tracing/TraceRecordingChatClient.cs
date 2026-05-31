// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.AI;

// AgentEval.Tracing also declares its own ChatRole (ChatExecutionResult); alias the MEAI one so the
// unqualified local enum cannot silently shadow it in role comparisons below.
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tracing;

/// <summary>
/// Records every LLM round-trip at the <see cref="IChatClient"/> boundary into an <see cref="AgentTrace"/>
/// (<see cref="TraceEntryScope.ChatTurn"/>) — the Glass Box chat-boundary recorder. Complements
/// <see cref="TraceRecordingAgent"/> (agent boundary) and is distinct from <see cref="ChatTraceRecorder"/>
/// (conversation replay). See <c>docs/tracing.md</c> §"Two recording layers".
/// </summary>
/// <remarks>
/// <b>Composition is load-bearing.</b> To capture <em>every</em> round-trip, this client must be composed
/// <b>inner</b> of <c>UseFunctionInvocation</c> (i.e. appear <em>after</em> <c>.UseFunctionInvocation()</c>
/// in the <c>.Use(...)</c> chain) so the tool loop calls it once per model round-trip. Placing it outer of
/// the function-invocation loop records only one entry for the whole loop. A recorder instance wraps one
/// invocation/session and is invoked sequentially (single-threaded contract, like the other recorders).
/// </remarks>
public sealed class TraceRecordingChatClient : DelegatingChatClient
{
    private readonly AgentTrace _trace;
    private readonly ToolDefinitionDeduplicator _dedup;
    private int _index;

    /// <summary>Wraps <paramref name="inner"/>, recording each round-trip into <paramref name="trace"/>.</summary>
    /// <param name="inner">The inner chat client (closest to the raw model when composed inner of FICC).</param>
    /// <param name="agentName">Agent name, applied to the trace only if not already set.</param>
    /// <param name="trace">The accumulating trace (shared with tool-execution recording when correlated).</param>
    /// <param name="preset">Capture preset; <see cref="SamplePreset.AuditGrade"/> disables tool-definition de-dup.</param>
    public TraceRecordingChatClient(IChatClient inner, string agentName, AgentTrace trace, SamplePreset preset = SamplePreset.Standard)
        : base(inner)
    {
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        // Set the agent name only if not already provided (e.g. when wrapped by another recorder).
        _trace.AgentName ??= agentName;
        _trace.Version = "1.1";
        _dedup = new ToolDefinitionDeduplicator(enabled: preset != SamplePreset.AuditGrade);
    }

    /// <summary>The accumulating trace. Call <see cref="FinalizePerformance"/> before reading aggregate performance.</summary>
    public AgentTrace Trace => _trace;

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var msgs = messages as IList<ChatMessage> ?? messages.ToList();
        var i = Interlocked.Increment(ref _index) - 1;   // distinct per round-trip (pairing key)
        var corr = ToolCorrelationScope.Current;          // per-invocation grouping key (best-effort; may be null)
        _trace.AddEntry(TraceEntry.ForChatRequest(
            i, corr,
            systemPrompt: ExtractSystemPrompt(msgs),
            promptText: ExtractLastUserText(msgs),
            toolDefinitions: _dedup.Process(ExtractToolDefinitions(options)),
            requestOptions: ExtractRequestOptions(options)));

        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await base.GetResponseAsync(msgs, options, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            _trace.ModelId ??= resp.ModelId;
            _trace.AddEntry(TraceEntry.ForChatResponse(
                i, corr, resp.Text, sw.ElapsedMilliseconds,
                MapUsage(resp.Usage), MapToolCalls(resp),
                resp.FinishReason?.Value, ExtractProviderMetadata(resp)));
            return resp;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _trace.AddEntry(TraceEntry.ForChatError(i, corr, ex, sw.ElapsedMilliseconds));
            throw;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var msgs = messages as IList<ChatMessage> ?? messages.ToList();
        var i = Interlocked.Increment(ref _index) - 1;
        var corr = ToolCorrelationScope.Current;
        _trace.AddEntry(TraceEntry.ForChatRequest(
            i, corr, ExtractSystemPrompt(msgs), ExtractLastUserText(msgs),
            _dedup.Process(ExtractToolDefinitions(options)), ExtractRequestOptions(options)));

        var sw = Stopwatch.StartNew();
        var chunks = new List<TraceStreamChunk>();
        var updates = new List<ChatResponseUpdate>();
        var idx = 0;
        var errorRecorded = false;
        await using var enumerator = base.GetStreamingResponseAsync(msgs, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ChatResponseUpdate cur;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    cur = enumerator.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sw.Stop();
                    _trace.AddEntry(TraceEntry.ForChatError(i, corr, ex, sw.ElapsedMilliseconds));
                    errorRecorded = true;
                    throw;
                }

                updates.Add(cur);
                if (!string.IsNullOrEmpty(cur.Text))
                {
                    chunks.Add(new TraceStreamChunk { Index = idx++, Text = cur.Text });
                }

                yield return cur;
            }
        }
        finally
        {
            sw.Stop();
            // Finalize on EVERY exit path — natural completion, early consumer break, or disposal — so a
            // partially-consumed stream never leaves a dangling, unpaired Request entry. Two exceptions: an
            // error entry was already recorded above, or the caller cancelled (the non-streaming path records
            // neither a response nor an error on OperationCanceledException — match that). The accumulated
            // updates are folded back into a ChatResponse so tool calls / usage / finish reason / provider
            // metadata are captured exactly as on the non-streaming path (they ride in ChatResponseUpdate.Contents).
            if (!errorRecorded && !cancellationToken.IsCancellationRequested)
            {
                var resp = updates.ToChatResponse();
                _trace.ModelId ??= resp.ModelId;
                var responseEntry = TraceEntry.ForChatResponse(
                    i, corr, resp.Text, sw.ElapsedMilliseconds,
                    MapUsage(resp.Usage), MapToolCalls(resp), resp.FinishReason?.Value, ExtractProviderMetadata(resp));
                responseEntry.IsStreaming = true;
                responseEntry.StreamingChunks = chunks;
                _trace.AddEntry(responseEntry);
            }
        }
    }

    /// <summary>Computes aggregate <see cref="TracePerformance"/> from the recorded ChatTurn entries.</summary>
    public void FinalizePerformance()
    {
        var responses = _trace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response)
            .ToList();
        _trace.Performance = new TracePerformance
        {
            TotalDurationMs = responses.Sum(r => r.DurationMs ?? 0),
            TotalPromptTokens = responses.Sum(r => r.TokenUsage?.PromptTokens ?? 0),
            TotalCompletionTokens = responses.Sum(r => r.TokenUsage?.CompletionTokens ?? 0),
            CallCount = responses.Count,
            ToolCallCount = responses.Sum(r => r.ToolCalls?.Count ?? 0),
        };
    }

    // ── helpers (private static) ──
    private static string? ExtractSystemPrompt(IList<ChatMessage> messages)
    {
        var system = string.Join("\n", messages.Where(m => m.Role.Equals(MeaiChatRole.System))
            .Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t)));
        return string.IsNullOrEmpty(system) ? null : system;
    }

    private static string? ExtractLastUserText(IList<ChatMessage> messages)
        => messages.LastOrDefault(m => m.Role.Equals(MeaiChatRole.User))?.Text;

    private static List<TraceToolDefinition>? ExtractToolDefinitions(ChatOptions? options)
    {
        if (options?.Tools is null || options.Tools.Count == 0)
        {
            return null;
        }

        return options.Tools.OfType<AIFunction>().Select(f => new TraceToolDefinition
        {
            Name = f.Name,
            Description = f.Description,
            // JsonSchema is a non-nullable JsonElement; a default/Undefined element throws from GetRawText().
            ParametersSchema = f.JsonSchema.ValueKind == JsonValueKind.Undefined ? null : f.JsonSchema.GetRawText(),
        }).ToList();
    }

    private static Dictionary<string, object?>? ExtractRequestOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        return new Dictionary<string, object?>
        {
            ["temperature"] = options.Temperature,
            ["maxOutputTokens"] = options.MaxOutputTokens,
            ["responseFormat"] = options.ResponseFormat?.GetType().Name,
            ["modelId"] = options.ModelId,
        };
    }

    private static TraceTokenUsage? MapUsage(UsageDetails? usage)
        => usage is null
            ? null
            : new TraceTokenUsage
            {
                PromptTokens = (int)(usage.InputTokenCount ?? 0),
                CompletionTokens = (int)(usage.OutputTokenCount ?? 0),
            };

    private static List<TraceToolCall>? MapToolCalls(ChatResponse response)
    {
        var calls = (response.Messages ?? []).SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        if (calls.Count == 0)
        {
            return null;
        }

        return calls.Select(c => new TraceToolCall
        {
            Name = c.Name,
            Arguments = c.Arguments is null ? null : JsonSerializer.Serialize(c.Arguments),
        }).ToList();
    }

    private static Dictionary<string, object?>? ExtractProviderMetadata(ChatResponse response)
        => response.AdditionalProperties is null || response.AdditionalProperties.Count == 0
            ? null
            : response.AdditionalProperties.ToDictionary(kv => kv.Key, kv => kv.Value);
}
