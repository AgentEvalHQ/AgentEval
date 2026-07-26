// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Shared bounded target resolution and fail-closed snapshot evaluation for containment gates.</summary>
internal static class ContainmentGateEvaluator
{
    internal const int MaxApplicableTargets = 16;

    internal static bool TryResolve<TContext>(
        Func<TContext, IReadOnlyList<ContainmentTarget>> resolver,
        TContext context,
        bool requireAtLeastOne,
        CancellationToken cancellationToken,
        out ContainmentTarget[] targets)
    {
        targets = [];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = resolver(context);
            if (resolved is null)
            {
                return false;
            }

            var count = resolved.Count;
            if (count > MaxApplicableTargets || (requireAtLeastOne && count == 0))
            {
                return false;
            }

            var copy = new List<ContainmentTarget>(count);
            var unique = new HashSet<ContainmentTarget>();
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = resolved[index];
                if (target is null)
                {
                    return false;
                }

                if (unique.Add(target))
                {
                    copy.Add(target);
                }
            }

            targets = [.. copy];
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            return false;
        }
    }

    internal static bool TryCombine(
        IReadOnlyList<ContainmentTarget> requiredTargets,
        IReadOnlyList<ContainmentTarget> additionalTargets,
        out ContainmentTarget[] combined)
    {
        combined = [];
        var unique = new HashSet<ContainmentTarget>();
        var result = new List<ContainmentTarget>(requiredTargets.Count + additionalTargets.Count);
        foreach (var target in requiredTargets.Concat(additionalTargets))
        {
            if (unique.Add(target))
            {
                result.Add(target);
                if (result.Count > MaxApplicableTargets)
                {
                    return false;
                }
            }
        }

        combined = [.. result];
        return true;
    }

    internal static ContainmentGateDecision Evaluate(
        IContainmentStore store,
        IReadOnlyList<ContainmentTarget> targets,
        CancellationToken cancellationToken)
    {
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ContainmentSnapshot? snapshot;
            try
            {
                snapshot = store.GetCurrent(target);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
            {
                return ContainmentGateDecision.Block("store_read_failed");
            }

            if (snapshot is null || snapshot.Target != target)
            {
                return ContainmentGateDecision.Block("store_snapshot_invalid");
            }

            if (snapshot.State == ContainmentSnapshotState.Active)
            {
                return ContainmentGateDecision.Block("active");
            }

            if (snapshot.State == ContainmentSnapshotState.Indeterminate)
            {
                return ContainmentGateDecision.Block("indeterminate");
            }
        }

        return ContainmentGateDecision.Allow;
    }
}

/// <summary>Internal secret-free result of evaluating every applicable containment target.</summary>
internal readonly record struct ContainmentGateDecision(bool MustBlock, string? ReasonCode)
{
    internal static readonly ContainmentGateDecision Allow = new(false, null);

    internal static ContainmentGateDecision Block(string reasonCode) => new(true, reasonCode);
}
