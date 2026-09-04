// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using System.Text;

namespace Galaxus.RecommendationAgent.Tools;

/// <summary>
/// The per-turn tool ledger (§F.9): the refusable-call cap, the distinct-search cap, the
/// answer-channel count, and the memo that stops identical work being re-run. An
/// <see cref="AsyncLocal{T}"/> scope opened by the demo immediately before <c>RunAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why here and not in MAF.</b> Whether <c>ChatClientAgent</c> exposes a per-run iteration
/// cap in MAF 1.17.0 is UNVERIFIED, and the demo must bound cost and latency regardless of
/// which MAF version it is built against. So the budget is enforced where it is certainly
/// enforceable: inside the tools. A tool that cannot run cannot cost anything.
/// </para>
/// <para>
/// <b>Three counters, kept apart — and the reason they used to be one.</b> The first version of
/// this class kept ONE list: <c>TryConsume</c> appended to it and so did <c>Record</c>, and
/// <c>Used</c> was the list's length. So the four <c>PresentRecommendation</c> calls of an
/// answer counted against the same cap as the searches, and the 2026-09-04 live run printed
/// "24 of 24 — the model stopped on its own". It had not stopped on its own: 20 refusable calls
/// plus 4 presentations had saturated a counter that was measuring two different things, which
/// is the n/n extreme this repository's rules flag as a wiring fault. The counters are now:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Used"/> / <see cref="Cap"/> — REFUSABLE calls: the three
///   semantic and seven structured tools. Past the cap they return
///   <see cref="ToolJson.BudgetExhausted"/>. Default <see cref="DefaultMaxCalls"/>, unchanged
///   from the first version so the agent may do exactly what it could do before.</description></item>
///   <item><description><see cref="DistinctSearches"/> / <see cref="DistinctSearchCap"/> — how
///   many DISTINCT (tool, arguments) semantic searches ran. A replayed search does not count.
///   Default <see cref="DefaultMaxDistinctSearches"/>, a stated number, see its remarks.</description></item>
///   <item><description><see cref="Presented"/> — the ANSWER channel. Counted, never refused, and
///   never charged to either cap: a spent budget must bound the spend, not silence the answer
///   (<c>RecommendationSet.Abstain</c> says the same thing, and an eval that scores silence as a
///   pass is a broken instrument rather than a cautious agent).</description></item>
/// </list>
/// <para>
/// <b>The memo.</b> C-09 of the 2026-09-04 live run issued four byte-identical
/// <c>SearchProductsByMeaning</c> calls and then four more, burning 12 of 24 refusable slots on
/// about three distinct queries — and roughly half of a 148-second turn. Within one scope an
/// identical (tool, arguments) call is now answered from the memo with
/// <see cref="ToolJson.AlreadyReturned"/> and consumes nothing. Nothing the agent MAY do has
/// changed: the same arguments produce the same answer within a turn, because the catalogue does
/// not move within a turn. <c>PresentRecommendation</c> is deliberately NOT memoised — a duplicate
/// presentation is a defect that must stay visible.
/// </para>
/// <para>
/// <b>Outside a scope the ledger is inert.</b> <see cref="Admit"/> admits everything, counts
/// nothing and remembers nothing, so a unit test or a one-off tool call needs no ceremony. The
/// demo always opens one. Scopes nest: disposing restores the enclosing scope, so a nested run
/// cannot silently reset its parent's counters.
/// </para>
/// </remarks>
public static class ToolCallBudget
{
    /// <summary>The refusable-call cap the demo opens by default. Chosen, not measured, and unchanged.</summary>
    public const int DefaultMaxCalls = 24;

    /// <summary>
    /// The distinct-search cap the demo opens by default.
    /// </summary>
    /// <remarks>
    /// CHOSEN, not measured. The only live per-turn distinct-search count on record is C-09's:
    /// about three distinct queries behind twelve calls. Eight leaves room for one search per
    /// interest signal plus a complement search per owned anchor, which is what the system
    /// prompt's steps 4 and 5 ask for; it is well above anything a live turn has been observed to
    /// need, and it is one constant to change.
    /// </remarks>
    public const int DefaultMaxDistinctSearches = 8;

    private static readonly AsyncLocal<BudgetState?> Current = new();

    /// <summary>Opens a budget scope for the current asynchronous flow.</summary>
    /// <param name="maxCalls">The refusable-call cap. Must be at least 1.</param>
    /// <param name="maxDistinctSearches">The distinct-search cap. Must be at least 1.</param>
    /// <returns>A scope; dispose it to restore the enclosing budget (or none).</returns>
    /// <exception cref="ArgumentOutOfRangeException">A cap is below 1.</exception>
    public static IDisposable BeginScope(int maxCalls = DefaultMaxCalls, int maxDistinctSearches = DefaultMaxDistinctSearches)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCalls, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDistinctSearches, 1);

        var previous = Current.Value;
        Current.Value = new BudgetState(maxCalls, maxDistinctSearches);
        return new BudgetScope(previous);
    }

    /// <summary>True when a scope is open on the current asynchronous flow.</summary>
    public static bool IsActive => Current.Value is not null;

    /// <summary>REFUSABLE calls consumed so far in this turn; 0 when no scope is open. Presentations and replays are not in it.</summary>
    public static int Used => Current.Value?.Used ?? 0;

    /// <summary>The refusable-call cap for this turn; 0 when no scope is open.</summary>
    public static int Cap => Current.Value?.Cap ?? 0;

    /// <summary>Refusable calls still available; <see cref="int.MaxValue"/> when no scope is open.</summary>
    public static int Remaining => Current.Value is { } s ? Math.Max(0, s.Cap - s.Used) : int.MaxValue;

    /// <summary>True when the refusable-call cap has been reached. Always false when no scope is open.</summary>
    public static bool IsExhausted => Current.Value is { } s && s.Used >= s.Cap;

    /// <summary>DISTINCT semantic searches run so far in this turn; 0 when no scope is open.</summary>
    public static int DistinctSearches => Current.Value?.DistinctSearches ?? 0;

    /// <summary>The distinct-search cap for this turn; 0 when no scope is open.</summary>
    public static int DistinctSearchCap => Current.Value?.DistinctSearchCap ?? 0;

    /// <summary>Answer-channel calls (<c>PresentRecommendation</c>, and the commit tools) recorded this turn. Never capped.</summary>
    public static int Presented => Current.Value?.Presented ?? 0;

    /// <summary>Identical calls answered from the memo instead of being re-run this turn.</summary>
    public static int Replays => Current.Value?.Replays ?? 0;

    /// <summary>Refusable calls that were refused for the refusable cap this turn.</summary>
    public static int RefusedForCap => Current.Value?.RefusedForCap ?? 0;

    /// <summary>Semantic searches that were refused for the distinct-search cap this turn.</summary>
    public static int RefusedForSearchCap => Current.Value?.RefusedForSearchCap ?? 0;

    /// <summary>
    /// The tool names admitted in this run, in call order — refusable calls and answer-channel
    /// calls alike, replays excluded. Printed in the guardrail ledger and useful when a run stops
    /// earlier than expected.
    /// </summary>
    public static IReadOnlyList<string> Calls => Current.Value?.Snapshot() ?? [];

    /// <summary>
    /// One line of accounting for the console: every counter, each against its own cap, so a
    /// reader can see WHICH one saturated instead of one number that might have been either.
    /// </summary>
    public static string Summary
    {
        get
        {
            if (Current.Value is not { } s) return "no budget scope open";

            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture,
                $"refusable {s.Used}/{s.Cap} · distinct searches {s.DistinctSearches}/{s.DistinctSearchCap} · ");
            text.Append(CultureInfo.InvariantCulture,
                $"replays {s.Replays} · presentations {s.Presented} (answer channel, uncapped)");
            if (s.RefusedForCap > 0) text.Append(CultureInfo.InvariantCulture, $" · refused for cap {s.RefusedForCap}");
            if (s.RefusedForSearchCap > 0) text.Append(CultureInfo.InvariantCulture, $" · refused for search cap {s.RefusedForSearchCap}");
            return text.ToString();
        }
    }

    /// <summary>
    /// Admits, replays or refuses one REFUSABLE tool call. Call it before doing any work.
    /// </summary>
    /// <remarks>
    /// Order of the checks is deliberate. The memo is consulted FIRST, so an identical repeat
    /// never consumes budget and is answered even after the cap is spent — repeating a search
    /// you already ran is not new spend. Then the refusable cap, then, for a semantic search, the
    /// distinct-search cap.
    /// </remarks>
    /// <param name="toolName">The tool being invoked.</param>
    /// <param name="argumentsKey">The canonical argument key from <see cref="KeyOf"/>.</param>
    /// <param name="isSearch">True for the three semantic search tools.</param>
    public static ToolCallAdmission Admit(string toolName, string argumentsKey, bool isSearch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(argumentsKey);

        var state = Current.Value;
        if (state is null) return ToolCallAdmission.Admitted;      // inert outside a scope
        return state.Admit(toolName, argumentsKey, isSearch);
    }

    /// <summary>
    /// Remembers the result of an admitted refusable call so an identical call this turn is
    /// replayed instead of re-run.
    /// </summary>
    /// <param name="toolName">The tool.</param>
    /// <param name="argumentsKey">The canonical argument key the call was admitted under.</param>
    /// <param name="productIds">The product ids the result carried, for the compact replay envelope.</param>
    public static void Remember(string toolName, string argumentsKey, IReadOnlyList<string> productIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(argumentsKey);
        ArgumentNullException.ThrowIfNull(productIds);
        Current.Value?.Remember(toolName, argumentsKey, productIds);
    }

    /// <summary>
    /// Counts an ANSWER-CHANNEL call WITHOUT gating it and without charging either cap. Used by
    /// <c>PresentRecommendation</c> and the two commit tools — see the type remarks.
    /// </summary>
    /// <param name="toolName">The tool being invoked.</param>
    public static void Record(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        Current.Value?.Record(toolName);
    }

    /// <summary>
    /// The canonical key for a call's arguments: every value trimmed, strings compared
    /// case-insensitively, nulls spelled out — so two calls the model would call "the same" are
    /// the same key, and two that differ in any argument are not.
    /// </summary>
    /// <param name="arguments">The arguments, in declaration order.</param>
    public static string KeyOf(params object?[] arguments)
    {
        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            builder.Append('|');
            builder.Append(argument switch
            {
                null => "∅",
                string s => s.Trim().ToLowerInvariant(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => argument.ToString()
            });
        }
        return builder.ToString();
    }

    /// <summary>The mutable counters behind one scope. Locked because MAF may invoke tools concurrently.</summary>
    private sealed class BudgetState(int cap, int distinctSearchCap)
    {
        private readonly List<string> _calls = [];
        private readonly Dictionary<string, MemoEntry> _memo = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();
        private int _used;
        private int _distinctSearches;
        private int _presented;
        private int _replays;
        private int _refusedForCap;
        private int _refusedForSearchCap;

        public int Cap { get; } = cap;
        public int DistinctSearchCap { get; } = distinctSearchCap;

        public int Used { get { lock (_gate) return _used; } }
        public int DistinctSearches { get { lock (_gate) return _distinctSearches; } }
        public int Presented { get { lock (_gate) return _presented; } }
        public int Replays { get { lock (_gate) return _replays; } }
        public int RefusedForCap { get { lock (_gate) return _refusedForCap; } }
        public int RefusedForSearchCap { get { lock (_gate) return _refusedForSearchCap; } }

        public ToolCallAdmission Admit(string toolName, string argumentsKey, bool isSearch)
        {
            lock (_gate)
            {
                var memoKey = toolName + argumentsKey;
                if (_memo.TryGetValue(memoKey, out var hit))
                {
                    _replays++;
                    return ToolCallAdmission.Replay(hit.FirstReturnedAsCall, hit.ProductIds);
                }

                if (_used >= Cap)
                {
                    _refusedForCap++;
                    return ToolCallAdmission.RefusedForCap;
                }

                if (isSearch && _distinctSearches >= DistinctSearchCap)
                {
                    _refusedForSearchCap++;
                    return ToolCallAdmission.RefusedForSearchCap;
                }

                _used++;
                if (isSearch) _distinctSearches++;
                _calls.Add(toolName);
                return ToolCallAdmission.Admitted;
            }
        }

        public void Remember(string toolName, string argumentsKey, IReadOnlyList<string> productIds)
        {
            lock (_gate)
            {
                var memoKey = toolName + argumentsKey;
                if (_memo.ContainsKey(memoKey)) return;
                _memo[memoKey] = new MemoEntry(_calls.Count, [.. productIds]);
            }
        }

        public void Record(string toolName)
        {
            lock (_gate)
            {
                _presented++;
                _calls.Add(toolName);
            }
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate) return _calls.ToArray();
        }

        private sealed record MemoEntry(int FirstReturnedAsCall, IReadOnlyList<string> ProductIds);
    }

    /// <summary>Restores the enclosing budget on dispose. Idempotent.</summary>
    private sealed class BudgetScope(BudgetState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current.Value = previous;
        }
    }
}

/// <summary>What the ledger decided about one refusable tool call.</summary>
public enum ToolCallAdmissionKind
{
    /// <summary>Do the work; the call has been charged.</summary>
    Admitted,

    /// <summary>Identical arguments were already answered this turn. Return the replay envelope; nothing was charged.</summary>
    Replayed,

    /// <summary>The refusable-call cap is spent.</summary>
    RefusedForCap,

    /// <summary>The distinct-search cap is spent (semantic searches only).</summary>
    RefusedForSearchCap,
}

/// <summary>The ledger's decision for one refusable call, with what a replay needs to answer with.</summary>
/// <param name="Kind">The decision.</param>
/// <param name="FirstReturnedAsCall">For a replay: the 1-based position of the call that first answered these arguments.</param>
/// <param name="ProductIds">For a replay: the product ids that first answer carried.</param>
public readonly record struct ToolCallAdmission(
    ToolCallAdmissionKind Kind,
    int FirstReturnedAsCall = 0,
    IReadOnlyList<string>? ProductIds = null)
{
    /// <summary>Do the work.</summary>
    public static ToolCallAdmission Admitted { get; } = new(ToolCallAdmissionKind.Admitted);

    /// <summary>The refusable cap is spent.</summary>
    public static ToolCallAdmission RefusedForCap { get; } = new(ToolCallAdmissionKind.RefusedForCap);

    /// <summary>The distinct-search cap is spent.</summary>
    public static ToolCallAdmission RefusedForSearchCap { get; } = new(ToolCallAdmissionKind.RefusedForSearchCap);

    /// <summary>An identical call was already answered this turn.</summary>
    /// <param name="firstReturnedAsCall">The 1-based position of the first answer.</param>
    /// <param name="productIds">The product ids it carried.</param>
    public static ToolCallAdmission Replay(int firstReturnedAsCall, IReadOnlyList<string> productIds) =>
        new(ToolCallAdmissionKind.Replayed, firstReturnedAsCall, productIds);

    /// <summary>True when the caller should do the work.</summary>
    public bool IsAdmitted => Kind == ToolCallAdmissionKind.Admitted;
}
