// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// CLI-only troubleshooting decorator behind <c>--log-file &lt;path&gt;</c> — writes every LLM round-trip as
/// human-readable plain text to a log file, separate from stdout/stderr. Unrelated to Glass Box's
/// <c>AgentTrace</c>/<c>TraceRecordingChatClient</c>: this is a raw request/response dump for debugging a CLI
/// invocation, not a structured evaluation artifact. See <see cref="VerboseLog"/> for how a command wires this in.
/// </summary>
/// <remarks>
/// <b>Logs content RAW, with no redaction.</b> <c>--log-file</c> is opt-in and local-only by design — turning
/// it on means the operator wants to see exactly what was sent/received while troubleshooting. The file can
/// contain secrets or PII carried in prompts/responses; the CLI's own <c>--help</c> text and documentation
/// disclose this so it is never shared or committed by mistake.
/// <para><b>Thread safety.</b> This class does no locking of its own — it relies on <paramref name="log"/>
/// (the <c>TextWriter</c> passed to the constructor) already being safe for concurrent writes when more than
/// one <see cref="VerboseLoggingChatClient"/> instance might share it. <see cref="VerboseLog.Wrap"/> always
/// supplies a <see cref="TextWriter.Synchronized(TextWriter)"/>-wrapped writer for exactly this reason — a
/// caller constructing this class directly (e.g. in a test) with a plain, unsynchronized writer is responsible
/// for either using it single-threaded or synchronizing it themselves.</para>
/// </remarks>
public sealed class VerboseLoggingChatClient : DelegatingChatClient
{
    private readonly TextWriter _log;
    private readonly string? _label;
    private int _index;
    private bool _writeFailureReported;

    /// <summary>Wraps <paramref name="inner"/>, writing every round-trip to <paramref name="log"/>.</summary>
    /// <param name="inner">The chat client to observe.</param>
    /// <param name="log">Where to write. See this class's remarks for the thread-safety contract.</param>
    /// <param name="label">An optional short role label printed in every entry from this client (e.g. "judge").</param>
    public VerboseLoggingChatClient(IChatClient inner, TextWriter log, string? label = null)
        : base(inner)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _label = string.IsNullOrWhiteSpace(label) ? null : label;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var msgs = messages as IList<ChatMessage> ?? messages.ToList();
        var i = Interlocked.Increment(ref _index) - 1;
        WriteRequest(i, msgs, options);

        var sw = Stopwatch.StartNew();
        // Regression fix: a caller-cancellation on this path used to `throw;` with no matching log entry —
        // unlike GetStreamingResponseAsync's own finally-guarded ABANDONED write, built specifically so an
        // abandoned round-trip never leaves a REQUEST entry with no matching RESPONSE/ERROR/ABANDONED,
        // silently breaking the pairing the log format promises (see that method's own remarks). This mirrors
        // the same terminalEntryWritten + finally pattern.
        var terminalEntryWritten = false;
        try
        {
            var resp = await base.GetResponseAsync(msgs, options, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            WriteResponse(i, resp, sw.ElapsedMilliseconds);
            terminalEntryWritten = true;
            return resp;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // the CALLER's own token fired — a deliberate cancellation, not diagnostically
                      // interesting on its own terms, but still logged as ABANDONED below so the REQUEST
                      // entry this call already wrote is never left unpaired.
        }
        catch (Exception ex)
        {
            // Everything else, INCLUDING an OperationCanceledException/TaskCanceledException whose token is NOT
            // the caller's own (e.g. an HttpClient/Azure SDK request-timeout) — arguably the single most common
            // real reason someone reaches for --log-file, so it must never be filtered out here the way an
            // `ex is not OperationCanceledException` guard would (TaskCanceledException IS an
            // OperationCanceledException; a blanket type-based exclusion silently swallows real timeouts).
            sw.Stop();
            WriteError(i, ex, sw.ElapsedMilliseconds);
            terminalEntryWritten = true;
            throw;
        }
        finally
        {
            if (!terminalEntryWritten)
            {
                WriteAbandoned(i, sw.ElapsedMilliseconds);
            }
        }
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var msgs = messages as IList<ChatMessage> ?? messages.ToList();
        var i = Interlocked.Increment(ref _index) - 1;
        WriteRequest(i, msgs, options);

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
                    WriteError(i, ex, sw.ElapsedMilliseconds);
                    terminalEntryWritten = true;
                    throw;
                }

                updates.Add(current);
                yield return current;
            }

            sw.Stop();
            var resp = updates.Count == 0 ? new ChatResponse() : updates.ToChatResponse();
            WriteResponse(i, resp, sw.ElapsedMilliseconds);
            terminalEntryWritten = true;
        }
        finally
        {
            // The consumer stopped enumerating early (a `break`/disposed enumerator, or a genuine cancellation)
            // before either the natural-completion or the catch-block write above ran — the log would otherwise
            // show a REQUEST with no matching RESPONSE/ERROR, silently breaking the pairing the format promises.
            if (!terminalEntryWritten)
            {
                WriteAbandoned(i, sw.ElapsedMilliseconds);
            }
        }
    }

    private void WriteRequest(int index, IList<ChatMessage> messages, ChatOptions? options)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, index, "REQUEST", DateTimeOffset.UtcNow, elapsedMs: null);
        if (!string.IsNullOrEmpty(options?.Instructions))
        {
            sb.Append("Instructions: ").AppendLine(options.Instructions);
        }

        if (options is not null)
        {
            sb.Append("Options: temperature=").Append(FormatOrDefault(options.Temperature))
              .Append(", maxOutputTokens=").Append(FormatOrDefault(options.MaxOutputTokens))
              .Append(", modelId=").AppendLine(options.ModelId ?? "(default)");
        }

        if (options?.Tools is { Count: > 0 })
        {
            sb.Append("Tools: ").AppendLine(string.Join(", ", options.Tools.Select(t => t.Name)));
        }

        // Full message history on every call would grow O(turn-count²) in any multi-turn scenario (the
        // standard IChatClient contract resends growing history each round) — a 50-turn conversation would
        // log ~1,275 mostly-duplicate lines instead of ~50, bloating the file and burying the turn that
        // actually matters. Log the shape (counts by role) plus only the NEWEST message in full.
        var byRole = messages.GroupBy(m => m.Role).Select(g => $"{g.Count()} {g.Key}");
        sb.Append("Messages: ").Append(messages.Count).Append(" total (").Append(string.Join(", ", byRole)).AppendLine(")");
        var last = messages.Count > 0 ? messages[^1] : null;
        if (last is not null)
        {
            sb.Append("Newest [").Append(last.Role).Append("]: ").AppendLine(last.Text ?? "(no text content)");
        }

        Write(sb.ToString());
    }

    private void WriteResponse(int index, ChatResponse response, long elapsedMs)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, index, "RESPONSE", DateTimeOffset.UtcNow, elapsedMs);
        sb.Append("Text: ").AppendLine(response.Text ?? "(empty)");

        var toolCalls = (response.Messages ?? []).SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        if (toolCalls.Count > 0)
        {
            sb.Append("ToolCalls: ").AppendLine(string.Join(", ", toolCalls.Select(c => $"{c.Name}({FormatArgs(c.Arguments)})")));
        }

        sb.Append("FinishReason: ").AppendLine(response.FinishReason?.Value ?? "(none)");
        if (response.Usage is not null)
        {
            sb.Append("Usage: prompt=").Append(response.Usage.InputTokenCount)
              .Append(", completion=").AppendLine(response.Usage.OutputTokenCount?.ToString(CultureInfo.InvariantCulture) ?? "?");
        }

        Write(sb.ToString());
    }

    private void WriteError(int index, Exception ex, long elapsedMs)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, index, "ERROR", DateTimeOffset.UtcNow, elapsedMs);
        sb.AppendLine(ex.ToString());   // full exception incl. stack trace, deliberately not just ex.Message
        Write(sb.ToString());
    }

    private void WriteAbandoned(int index, long elapsedMs)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, index, "ABANDONED", DateTimeOffset.UtcNow, elapsedMs);
        sb.AppendLine("Streaming enumeration stopped before completion (consumer broke out early, or the call " +
                       "was cancelled) — no full response was captured for this round-trip.");
        Write(sb.ToString());
    }

    private void AppendHeader(StringBuilder sb, int index, string kind, DateTimeOffset timestamp, long? elapsedMs)
    {
        sb.Append("=== [").Append(index).Append(']');
        if (_label is not null)
        {
            sb.Append(' ').Append(_label);
        }

        sb.Append(' ').Append(kind).Append(' ').Append(timestamp.ToString("O", CultureInfo.InvariantCulture));
        if (elapsedMs is not null)
        {
            sb.Append(" (").Append(elapsedMs.Value.ToString(CultureInfo.InvariantCulture)).Append("ms)");
        }

        sb.AppendLine(" ===");
    }

    private void Write(string block)
    {
        // A purely diagnostic, opt-in side channel must never break an otherwise-successful command — e.g. a
        // full LLM response already returned successfully must not be discarded because the disk backing
        // --log-file happened to fill up on the write that logs it. Report the failure ONCE (not on every
        // subsequent call, which would spam stderr identically forever) and otherwise degrade silently.
        try
        {
            _log.WriteLine(block);   // one call, not Write()+WriteLine() — halves flush-syscall count under AutoFlush
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or UnauthorizedAccessException)
        {
            if (!_writeFailureReported)
            {
                _writeFailureReported = true;
                Console.Error.WriteLine($"  Warning: --log-file write failed ({ex.GetType().Name}: {ex.Message}) — further verbose-log entries from this client may be incomplete.");
            }
        }
    }

    private static string FormatOrDefault<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture) ?? "(default)";

    private static string FormatArgs(IDictionary<string, object?>? args) =>
        args is null || args.Count == 0 ? "{}" : string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"));
}
