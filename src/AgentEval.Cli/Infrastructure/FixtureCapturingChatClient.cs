// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// CLI-only decorator behind <c>--capture-fixture &lt;path&gt;</c> — writes every LLM round-trip as a
/// structured JSONL line, with FULL fidelity (complete message array, complete options, complete response),
/// for later replay or fixture generation via <c>agenteval log-file to-fixture</c>. Sibling to
/// <see cref="VerboseLoggingChatClient"/>, NOT a replacement — the two solve different problems
/// (human troubleshooting vs. faithful replay) and both can be active on the same client at once; see
/// <see cref="CliChatClientDiagnostics.Wrap"/> for the single call-site hook that composes them.
/// </summary>
/// <remarks>
/// <b>Logs content RAW, with no redaction — same disclosure as <see cref="VerboseLoggingChatClient"/>.</b>
/// <c>--capture-fixture</c> is opt-in and local-only by design; the file can contain secrets/PII carried in
/// prompts/responses.
/// <para><b>Thread safety.</b> Identical contract to <see cref="VerboseLoggingChatClient"/>: this class does
/// no locking of its own — <see cref="CliChatClientDiagnostics.Wrap"/> always supplies a
/// <see cref="TextWriter.Synchronized(TextWriter)"/>-wrapped sink.</para>
/// <para><b>One JSON line per round-trip, request AND outcome together</b> — unlike
/// <see cref="VerboseLoggingChatClient"/>'s two-writes-per-round-trip (REQUEST then RESPONSE/ERROR/ABANDONED),
/// a fixture consumer needs each entry to be self-contained, not paired by matching index across two lines.
/// The request is captured but only written once the outcome (response/error/abandoned) is known.</para>
/// </remarks>
public sealed class FixtureCapturingChatClient : DelegatingChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly TextWriter _sink;
    private readonly string? _label;
    private readonly bool _redactSecrets;
    private int _index;
    private bool _writeFailureReported;

    /// <summary>Wraps <paramref name="inner"/>, writing every round-trip to <paramref name="sink"/>.</summary>
    /// <param name="inner">The chat client to observe.</param>
    /// <param name="sink">Where to write. See this class's remarks for the thread-safety contract.</param>
    /// <param name="label">An optional short role label recorded in every entry from this client (e.g. "sut").</param>
    /// <param name="redactSecrets">When <see langword="true"/> (default) recognized credential shapes are masked
    /// before writing, so a captured fixture doesn't persist a live secret in plaintext (Fable 5 §12). Set
    /// <see langword="false"/> only for a trusted local debugging session where you need the raw values.</param>
    public FixtureCapturingChatClient(IChatClient inner, TextWriter sink, string? label = null, bool redactSecrets = true)
        : base(inner)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _label = string.IsNullOrWhiteSpace(label) ? null : label;
        _redactSecrets = redactSecrets;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var msgs = messages as IList<ChatMessage> ?? messages.ToList();
        var i = Interlocked.Increment(ref _index) - 1;
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await base.GetResponseAsync(msgs, options, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            WriteEntry(i, "response", sw.ElapsedMilliseconds, msgs, options, resp, error: null);
            return resp;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // the CALLER's own token fired — a deliberate cancellation, not diagnostically interesting
        }
        catch (Exception ex)
        {
            sw.Stop();
            WriteEntry(i, "error", sw.ElapsedMilliseconds, msgs, options, response: null, ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var msgs = messages as IList<ChatMessage> ?? messages.ToList();
        var i = Interlocked.Increment(ref _index) - 1;
        var sw = Stopwatch.StartNew();
        var updates = new List<ChatResponseUpdate>();
        var terminalEntryWritten = false;
        await using var enumerator = base.GetStreamingResponseAsync(msgs, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ChatResponseUpdate current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    current = enumerator.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;   // the caller's own token fired — see GetResponseAsync's matching remark
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    WriteEntry(i, "error", sw.ElapsedMilliseconds, msgs, options, response: null, ex);
                    terminalEntryWritten = true;
                    throw;
                }

                updates.Add(current);
                yield return current;
            }

            sw.Stop();
            var resp = updates.Count == 0 ? new ChatResponse() : updates.ToChatResponse();
            WriteEntry(i, "response", sw.ElapsedMilliseconds, msgs, options, resp, error: null);
            terminalEntryWritten = true;
        }
        finally
        {
            // Same "the consumer stopped enumerating early" case VerboseLoggingChatClient guards — see its
            // matching remark for why this must not be skipped.
            if (!terminalEntryWritten)
            {
                WriteEntry(i, "abandoned", sw.ElapsedMilliseconds, msgs, options, response: null, error: null);
            }
        }
    }

    private void WriteEntry(
        int index, string kind, long elapsedMs, IList<ChatMessage> messages, ChatOptions? options,
        ChatResponse? response, Exception? error)
    {
        var entry = new FixtureCaptureEntry(
            index,
            _label,
            kind,
            DateTimeOffset.UtcNow,
            elapsedMs,
            BuildRequest(messages, options),
            response is null ? null : BuildResponse(response),
            error?.ToString());

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        Write(_redactSecrets ? ToolResultSecretGate.MaskSecrets(json) : json);
    }

    private static FixtureCaptureRequest BuildRequest(IList<ChatMessage> messages, ChatOptions? options)
    {
        var fixtureOptions = options is null
            ? null
            : new FixtureCaptureOptions(
                options.Temperature,
                options.MaxOutputTokens,
                options.ModelId,
                options.Tools is { Count: > 0 } ? options.Tools.Select(BuildTool).ToList() : null);

        var fixtureMessages = messages.Select(m => new FixtureCaptureMessage(m.Role.Value, m.Text)).ToList();
        return new FixtureCaptureRequest(options?.Instructions, fixtureOptions, fixtureMessages);
    }

    private static FixtureCaptureTool BuildTool(AITool tool)
    {
        // Only AIFunction carries a JSON schema — a bare AITool (rare; most tools in this codebase are
        // AIFunction instances) has none to capture, same "honestly report what's there" discipline used
        // elsewhere in this codebase rather than fabricating a schema.
        JsonElement? schema = tool is AIFunction f && f.JsonSchema.ValueKind != JsonValueKind.Undefined
            ? f.JsonSchema
            : null;
        return new FixtureCaptureTool(tool.Name, tool.Description, schema);
    }

    private static FixtureCaptureResponse BuildResponse(ChatResponse response)
    {
        var toolCalls = (response.Messages ?? []).SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Select(c => new FixtureCaptureToolCall(c.CallId, c.Name, c.Arguments))
            .ToList();

        var usage = response.Usage is null
            ? null
            : new FixtureCaptureUsage(response.Usage.InputTokenCount, response.Usage.OutputTokenCount);

        return new FixtureCaptureResponse(
            response.Text,
            toolCalls.Count > 0 ? toolCalls : null,
            response.FinishReason?.Value,
            usage);
    }

    private void Write(string line)
    {
        // Same "a purely diagnostic, opt-in side channel must never break an otherwise-successful command"
        // discipline as VerboseLoggingChatClient.Write — see its matching remark.
        try
        {
            _sink.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or UnauthorizedAccessException)
        {
            if (!_writeFailureReported)
            {
                _writeFailureReported = true;
                Console.Error.WriteLine($"  Warning: --capture-fixture write failed ({ex.GetType().Name}: {ex.Message}) — further fixture-capture entries from this client may be incomplete.");
            }
        }
    }
}
