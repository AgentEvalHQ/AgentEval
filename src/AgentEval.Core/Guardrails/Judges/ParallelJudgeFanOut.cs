// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// Runs several <see cref="IChatGate"/> judges over one turn <b>concurrently</b> and combines them — the "panel"
/// of The Tribunal. Wall-clock is the slowest judge, not the sum. The combiner is <b>fail-closed OR</b>: if any
/// judge blocks, the panel blocks (aggregating every blocking judge's reason and evidence spans); a judge that
/// throws is itself treated as a block (fail-closed), so a broken judge can never weaken the panel. Only the
/// caller's own cancellation propagates.
/// <para>Compose many <b>single-axis</b> judges here rather than widening one rubric — the repo's grading work
/// proved single-axis decomposition is the active ingredient; a fan-out keeps each axis honest.</para>
/// </summary>
public sealed class ParallelJudgeFanOut : IChatGate
{
    private readonly IReadOnlyList<IChatGate> _judges;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <summary>Creates the panel from the judge gates (at least one required).</summary>
    public ParallelJudgeFanOut(IEnumerable<IChatGate> judges, string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(judges);
        _judges = judges.ToList();
        if (_judges.Count == 0)
        {
            throw new ArgumentException("At least one judge is required.", nameof(judges));
        }

        if (_judges.Any(j => j is null))
        {
            throw new ArgumentException("Judges must not be null.", nameof(judges));
        }

        PolicyName = policyName ?? "judge-panel";
    }

    /// <inheritdoc/>
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        var verdicts = await Task.WhenAll(_judges.Select(j => RunOneAsync(j, text, cancellationToken))).ConfigureAwait(false);

        var blocks = verdicts.Where(v => v.Action == GateAction.Block).ToList();
        if (blocks.Count == 0)
        {
            return GateVerdict.Allow(PolicyName);
        }

        var reason = string.Join("; ", blocks.Select(b => $"{b.PolicyName}: {b.Reason}"));
        var matches = blocks
            .SelectMany(b => b.Matches ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return GateVerdict.Block(PolicyName, reason, matches.Count > 0 ? matches : null);
    }

    private static async Task<GateVerdict> RunOneAsync(IChatGate judge, string text, CancellationToken cancellationToken)
    {
        try
        {
            return await judge.InspectAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // the CALLER cancelled — propagate (don't turn it into a block)
        }
        catch (Exception ex)
        {
            // A judge that failed for any other reason can't vouch for the turn ⇒ fail-closed block.
            return GateVerdict.Block(judge.PolicyName, $"judge errored (fail-closed): {ex.GetType().Name}");
        }
    }
}
