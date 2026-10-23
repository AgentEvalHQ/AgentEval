// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Results.Ingestion;

namespace AgentEval.Results.Adapters.Reading;

// One serial workspace read charges every captured input, including discarded legacy files and converted output.
internal sealed class AefReadBudget(long bytes)
{
    internal long Remaining { get; private set; } = bytes;

    internal long Limit(long maximum)
    {
        if (Remaining <= 0) throw new InvalidDataException("Expected: remaining workspace read capacity. Actual: byte budget exhausted. " +
            "Suggestions: narrow the source or increase the bounded budget. Because: legacy conversion must not bypass source limits.");
        return Math.Min(Remaining, maximum);
    }

    internal void Charge(long capturedBytes)
    {
        if (capturedBytes < 0 || capturedBytes > Remaining)
            throw new InvalidDataException("Expected: evidence within the remaining workspace budget. Actual: byte budget exhausted. " +
                "Suggestions: narrow the source or increase the bounded budget. Because: input and conversion work share one limit.");
        Remaining -= capturedBytes;
    }

    internal AefOwnedSnapshot Capture(string directory, AefStagingOptions limits, CancellationToken ct)
    {
        var maxBytes = Limit(limits.MaxBytes);
        var snapshot = AefOwnedSnapshot.Capture(directory, new()
        {
            MaxFiles = limits.MaxFiles, MaxBytes = maxBytes, MaxFileBytes = Math.Min(limits.MaxFileBytes, maxBytes),
        }, ct);
        Charge(snapshot.Bytes);
        return snapshot;
    }
}
