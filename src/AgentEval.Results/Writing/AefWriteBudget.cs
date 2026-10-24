// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Results.Writing;

// Conservative cumulative work budget: rewrites and discarded temporary blob
// bytes count too. This bounds conversion work, not only the final folder size.
internal sealed class AefWriteBudget(long maximum, CancellationToken cancellationToken = default, Action<long>? chargeParent = null)
{
    internal long Remaining { get; private set; } = maximum;

    internal void Check(long bytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes < 0 || bytes > Remaining)
            throw new InvalidDataException("Expected: conversion within its remaining byte budget. Actual: byte budget exhausted. " +
                "Suggestions: narrow the evidence source or increase the bounded budget. Because: conversion must stop before exceeding resource limits.");
    }

    internal void Charge(long bytes)
    {
        Check(bytes);
        chargeParent?.Invoke(bytes);
        Remaining -= bytes;
    }
}
