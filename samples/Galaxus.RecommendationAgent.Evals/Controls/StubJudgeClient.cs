// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// A scripted judge <see cref="IChatClient"/> for <c>--judge --dry-run</c> (plan item 8.5). It
/// spends nothing and it is deliberately AWKWARD.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it cycles through five shapes instead of returning one good verdict.</b>
/// <c>RUN_PROTOCOL</c>'s hardest lesson is that a stub which behaves BETTER than any real model
/// makes the free stage of the protocol blind to exactly the faults the paid stage will hit — the
/// Eval 05 stub that stripped the ordinal, the Eval 02 stub that presented a constant k. A judge
/// stub that always answers <c>SUPPORTED</c> in clean JSON exercises one branch of
/// <c>RecommendationJustificationJudge.Parse</c> and certifies four.
/// </para>
/// <para>
/// The five shapes, in order, repeating: a clean <c>SUPPORTED</c>; an <c>UNSUPPORTED</c> naming an
/// offending claim; an <c>INCONCLUSIVE</c>; a verdict string the parser must NOT map to the
/// nearest bucket (<c>MAYBE</c> → <see cref="JustificationVerdict.InstrumentFailure"/>); and prose
/// that is not JSON at all. Two of the five are instrument failures, on purpose: the tally's
/// <c>InstrumentBroken</c> ceiling is 10 %, so the dry run reaches the branch that says "this
/// channel is reporting an instrument failure, not a score" instead of only the branch that says
/// it is fine.
/// </para>
/// <para>
/// ⚠ <b>The prose says DRY RUN in capitals</b>, for the same reason
/// <see cref="StubChatClient.StubText"/> does: if one of these explanations ever appears in a
/// report meant to be real, the run did not reach Azure and that must be visible rather than
/// plausible.
/// </para>
/// </remarks>
public sealed class StubJudgeClient : IChatClient
{
    /// <summary>The verdict strings this stub emits, in the order it emits them.</summary>
    /// <remarks>
    /// Public because the dry-run check compares what the tally RECORDED against what the stub SAYS
    /// it sent. A check that read the tally alone would be the artifact under test supplying the
    /// input to its own pass criterion.
    /// </remarks>
    public static readonly IReadOnlyList<string> Script =
    [
        "SUPPORTED",
        "UNSUPPORTED",
        "INCONCLUSIVE",
        "MAYBE",              // unrecognised → must become an InstrumentFailure, never the nearest bucket
        "(not json at all)",  // unparseable  → must become an InstrumentFailure
    ];

    private readonly List<(string Shape, string Reply)> _sent = [];

    /// <summary>Every reply the stub has emitted, in order. Non-empty proves the judged path RAN.</summary>
    public IReadOnlyList<string> Sent => [.. _sent.Select(s => s.Reply)];

    /// <summary>How many times the judge asked this stub for a verdict.</summary>
    public int CallCount => _sent.Count;

    /// <summary>
    /// The distinct SHAPES this stub has emitted so far, in <see cref="Script"/> order.
    /// </summary>
    /// <remarks>
    /// ⚠ Recorded at emission, not recovered by searching the reply text. The first build of this
    /// stub inferred the shape by looking for the label inside the reply, and the one shape whose
    /// label does not appear in its own text — the non-JSON prose — was reported as never
    /// exercised while it had been sent five times. Reading a fact out of the artefact's output
    /// instead of off its input is the same shape as reading applicability out of a result.
    /// </remarks>
    public IReadOnlyList<string> ShapesEmitted =>
        [.. Script.Where(shape => _sent.Any(s => string.Equals(s.Shape, shape, StringComparison.Ordinal)))];

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        string shape = Script[_sent.Count % Script.Count];
        string reply = shape switch
        {
            "(not json at all)" =>
                "DRY RUN — THIS JUDGE REPLY CAME FROM A STUB. It is deliberately not JSON, so the "
              + "parser's instrument-failure branch is exercised for free.",
            "UNSUPPORTED" =>
                "{\"verdict\":\"UNSUPPORTED\",\"offendingClaim\":\"a claim the stub invented\","
              + "\"explanation\":\"DRY RUN — stub judge. Not a verdict about anything.\"}",
            _ =>
                "{\"verdict\":\"" + shape + "\",\"offendingClaim\":null,"
              + "\"explanation\":\"DRY RUN — stub judge. Not a verdict about anything.\"}",
        };

        _sent.Add((shape, reply));

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
        {
            ModelId = "stub-judge — dry run",
            FinishReason = ChatFinishReason.Stop,
        });
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc/>
    public void Dispose() { }
}
