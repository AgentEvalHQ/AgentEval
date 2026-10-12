// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Runner;

/// <summary>A monotonic execution clock anchored at a supplied UTC time.</summary>
public sealed class AefElapsedJobClock : IAefJobClock
{
    private readonly AefTime _start;
    private readonly TimeProvider _time;
    private readonly long _timestamp;

    /// <summary>Starts a clock whose elapsed duration comes from <paramref name="timeProvider"/>.</summary>
    public AefElapsedJobClock(AefTime start, TimeProvider? timeProvider = null)
    {
        if (!start.IsValid) throw new ArgumentException("The clock requires a valid start time.", nameof(start));
        _start = start;
        _time = timeProvider ?? TimeProvider.System;
        _timestamp = _time.GetTimestamp();
    }

    /// <inheritdoc/>
    public AefTime Now
    {
        get
        {
            var ticks = _time.GetElapsedTime(_timestamp).Ticks;
            var nanos = _start.Nanoseconds + ticks % TimeSpan.TicksPerSecond * 100;
            return new AefTime(_start.Seconds + ticks / TimeSpan.TicksPerSecond + nanos / 1_000_000_000,
                (int)(nanos % 1_000_000_000));
        }
    }

    /// <inheritdoc/>
    public void Advance(long seconds) => ArgumentOutOfRangeException.ThrowIfNegative(seconds);
}
