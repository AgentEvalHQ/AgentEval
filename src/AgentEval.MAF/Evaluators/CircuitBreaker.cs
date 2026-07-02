// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Collections.Concurrent;

namespace AgentEval.MAF.Evaluators;

/// <summary>Minimal consecutive-failure circuit breaker, keyed by source. Thread-safe. Opt-in (off unless injected).</summary>
public sealed class CircuitBreaker
{
    private sealed class State { public int Failures; public DateTimeOffset OpenedUntil; }

    private readonly int _threshold;
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, State> _states = new();

    /// <param name="threshold">Consecutive failures before the breaker opens for a source.</param>
    /// <param name="cooldown">How long the breaker stays open after tripping. Defaults to 1 minute.</param>
    /// <param name="now">Clock; inject a fake in tests. Defaults to UTC now.</param>
    public CircuitBreaker(int threshold = 3, TimeSpan? cooldown = null, Func<DateTimeOffset>? now = null)
    {
        if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
        _cooldown = cooldown ?? TimeSpan.FromMinutes(1);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True while the breaker for <paramref name="source"/> is open (caller should skip the call).</summary>
    public bool IsOpen(string source)
    {
        if (!_states.TryGetValue(source, out var s)) return false;
        lock (s) { return s.OpenedUntil > _now(); }
    }

    /// <summary>Records a success for <paramref name="source"/>, resetting its failure count.</summary>
    public void OnSuccess(string source)
    {
        var s = _states.GetOrAdd(source, _ => new State());
        lock (s) { s.Failures = 0; s.OpenedUntil = default; }
    }

    /// <summary>Records a failure for <paramref name="source"/>; opens the breaker at the threshold.</summary>
    public void OnFailure(string source)
    {
        var s = _states.GetOrAdd(source, _ => new State());
        lock (s) { if (++s.Failures >= _threshold) s.OpenedUntil = _now() + _cooldown; }
    }
}
