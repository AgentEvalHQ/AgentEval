// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365.Conversation;

/// <summary>
/// The card of one conversation, discovered once through the client (which caches it too). This copy serves the
/// synchronous handle builders. A turn records the card, or the failure that keeps it from starting. An explicit task
/// read gets the client's <see cref="WorkIqProtocolException"/>.
/// </summary>
internal sealed class CardDiscovery
{
    private readonly IWorkIqA2AClient _client;
    private readonly TurnFailures _failures;
    private RawAgentCard? _card;

    public CardDiscovery(IWorkIqA2AClient client, TurnFailures failures)
    {
        _client = client;
        _failures = failures;
    }

    /// <summary>The discovered card, or null before the first discovery.</summary>
    public RawAgentCard? Card => Volatile.Read(ref _card);

    /// <summary>The card, discovered on first use. A failure propagates as the client's <see cref="WorkIqProtocolException"/>.</summary>
    /// <param name="call">The per-call context: a turn's, or null for an explicit task read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<RawAgentCard> DiscoverAsync(WorkIqCallContext? call, CancellationToken cancellationToken)
    {
        if (Card is { } cached)
        {
            return cached;
        }

        var card = await _client.DiscoverAsync(cancellationToken, call).ConfigureAwait(false);
        Volatile.Write(ref _card, card);
        return card;
    }

    /// <summary>
    /// Gives the turn the card. Discovery runs under the caller's token and the turn's abandonment token, because the
    /// first sign-in of a run can wait inside it. A discovery failure is recorded on the turn, which then cannot start.
    /// A turn abandoned while the card was fetched is recorded as abandoned and sends nothing, even when the fetch
    /// itself completed.
    /// </summary>
    /// <param name="ctx">The turn.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>True when the turn has the card and may send.</returns>
    public async Task<bool> TryDiscoverForTurnAsync(TurnContext ctx, CancellationToken cancellationToken)
    {
        if (Card is { } cached)
        {
            ctx.Card = cached;
        }
        else
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ctx.AbandonToken);
            try
            {
                var card = await DiscoverAsync(ctx.Call(), linked.Token).ConfigureAwait(false);
                ctx.Card = card;
                ctx.Trace(
                    WorkIqTraceStage.Discovery,
                    "discovery.card_recorded",
                    $"card sha256 {card.Sha256} from {card.CardUri.Host}{card.CardUri.AbsolutePath}; endpoint {card.SelectedEndpoint.Host}{card.SelectedEndpoint.AbsolutePath} (A2A {card.SelectedProtocolVersion}) recorded for this conversation");
            }
            catch (Exception ex)
            {
                _failures.RecordDiscoveryFailure(ctx, ex, cancellationToken);
                return false;
            }
        }

        if (ctx.AbandonToken.IsCancellationRequested)
        {
            _failures.RecordAbandonedBeforeSend(ctx);
            return false;
        }

        return true;
    }
}
