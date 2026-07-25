// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// A shared token + call budget across inline LLM judges (Phase 5, P5-2) — a denial-of-wallet / runaway-cost
/// bound that a single <see cref="CompositeJudgeGate{TRubric}"/> can't enforce alone. Judges call
/// <see cref="TryReserve"/> before hitting the model; when the budget for the current window is exhausted the
/// reservation is refused and the judge degrades to a recorded "unjudged — budget exhausted" verdict (fail-open or
/// fail-closed per <see cref="JudgeGateOptions.FailClosedOnBudgetExhausted"/>). The budget refills each window.
/// Thread-safe; a <see cref="TimeProvider"/> makes the window testable.
/// </summary>
public sealed class JudgeSpendGovernor
{
    private readonly int _maxCalls;
    private readonly long _maxTokens;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private DateTimeOffset _windowStart;
    private int _calls;
    private long _tokens;

    /// <summary>Creates the governor.</summary>
    /// <param name="maxCalls">Max judge model calls per window.</param>
    /// <param name="maxTokens">Max estimated tokens per window.</param>
    /// <param name="window">The refill window (default 1 minute).</param>
    /// <param name="timeProvider">Clock (testability); defaults to <see cref="TimeProvider.System"/>.</param>
    public JudgeSpendGovernor(int maxCalls, long maxTokens, TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        if (maxCalls < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCalls), "must be at least 1.");
        }

        if (maxTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "must be at least 1.");
        }

        _maxCalls = maxCalls;
        _maxTokens = maxTokens;
        _window = window ?? TimeSpan.FromMinutes(1);
        if (_window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "must be positive.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowStart = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Atomically tries to reserve one call plus <paramref name="estimatedTokens"/> against the current window.
    /// Returns true (and records the spend) if it fits; false if either the call or token budget is exhausted.
    /// The window auto-refills once <see cref="_window"/> has elapsed.
    /// </summary>
    public bool TryReserve(long estimatedTokens)
    {
        if (estimatedTokens < 0)
        {
            estimatedTokens = 0;   // defensive: a negative estimate must never manufacture headroom
        }

        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            if (now - _windowStart >= _window)
            {
                _windowStart = now;   // refill
                _calls = 0;
                _tokens = 0;
            }

            if (_calls >= _maxCalls || _tokens + estimatedTokens > _maxTokens)
            {
                return false;
            }

            _calls++;
            _tokens += estimatedTokens;
            return true;
        }
    }
}
