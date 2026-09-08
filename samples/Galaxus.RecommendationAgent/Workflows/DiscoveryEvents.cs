// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>What kind of thing happened. One record with typed slots, never a string bag.</summary>
public enum DiscoveryEventKind
{
    /// <summary>The run began; the header is printed.</summary>
    RunStarted,

    /// <summary>An executor started work.</summary>
    NodeStarted,

    /// <summary>An executor finished, with its model-call count and elapsed time.</summary>
    NodeCompleted,

    /// <summary>The interest map is ready.</summary>
    InterestMap,

    /// <summary>A discovery round is about to run its query plan.</summary>
    RoundStarted,

    /// <summary>One retrieval call returned.</summary>
    Search,

    /// <summary>A discovery round finished; new/duplicate counts are known.</summary>
    RoundComplete,

    /// <summary>The deterministic pre-gate rejected before a token was spent.</summary>
    PreGate,

    /// <summary>The coverage ledger, one row per interest.</summary>
    CoverageLedger,

    /// <summary>An edge was taken. TRACE ONLY — never derive a round number from these.</summary>
    Route,

    /// <summary>The §0.5 / D-3 vocabulary constraint refused a model-proposed term.</summary>
    QueryTermDropped,

    /// <summary>The reviewer put a new interest on the map.</summary>
    InterestProposed,

    /// <summary>The Ranker finished, post-checks included.</summary>
    Ranked,

    /// <summary>A deterministic post-check removed a SKU.</summary>
    SkuDropped,

    /// <summary>The Presenter finished.</summary>
    Presented,

    /// <summary>Something degraded to its deterministic path. A WARNING, never a failure.</summary>
    Degraded,

    /// <summary>The run finished; the summary is printed.</summary>
    RunComplete
}

/// <summary>
/// One domain event from inside the loop. This is the SECOND observability channel, beside
/// MAF's own <c>WatchStreamAsync</c> stream — MAF tells you which executor ran, this tells you
/// what it decided.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="NodeId">The executor id, or empty for run-level events.</param>
/// <param name="Message">The human-readable line.</param>
/// <param name="Round">The discovery round this belongs to, or 0.</param>
/// <param name="Detail">Extra lines printed under the message, indented.</param>
/// <param name="ModelCalls">Model calls this node made, or -1 when it does not apply.</param>
/// <param name="Elapsed">How long the node took, or null.</param>
public sealed record DiscoveryEvent(
    DiscoveryEventKind Kind,
    string NodeId,
    string Message,
    int Round = 0,
    IReadOnlyList<string>? Detail = null,
    int ModelCalls = -1,
    TimeSpan? Elapsed = null)
{
    /// <summary>The run header.</summary>
    /// <param name="state">The state, for the customer strip.</param>
    /// <param name="mode">"live" or "offline (deterministic)".</param>
    public static DiscoveryEvent RunStarted(DiscoveryState state, string mode)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new DiscoveryEvent(DiscoveryEventKind.RunStarted, string.Empty,
            $"Customer {state.CustomerId} · {state.Language} · market {state.Market} · " +
            $"personalization: {(state.PersonalizationConsent ? "GRANTED" : "WITHDRAWN")} · {mode}",
            Detail: state.PersonalizationConsent
                ? null
                : ["history is not in this prompt because it is not in the state (§F.6)"]);
    }

    /// <summary>An executor started.</summary>
    /// <param name="nodeId">Executor id.</param>
    /// <param name="note">Optional parenthetical, e.g. "no model call — queries come from the map".</param>
    public static DiscoveryEvent NodeStarted(string nodeId, string? note = null) =>
        new(DiscoveryEventKind.NodeStarted, nodeId, note ?? string.Empty);

    /// <summary>An executor finished.</summary>
    /// <param name="nodeId">Executor id.</param>
    /// <param name="modelCalls">Model calls it made.</param>
    /// <param name="elapsed">How long it took.</param>
    public static DiscoveryEvent NodeCompleted(string nodeId, int modelCalls, TimeSpan elapsed) =>
        new(DiscoveryEventKind.NodeCompleted, nodeId, string.Empty, ModelCalls: modelCalls, Elapsed: elapsed);

    /// <summary>The interest map panel.</summary>
    /// <param name="nodeId">Executor id.</param>
    /// <param name="lines">One line per interest, plus anti-interests and constraints.</param>
    public static DiscoveryEvent InterestMap(string nodeId, IReadOnlyList<string> lines) =>
        new(DiscoveryEventKind.InterestMap, nodeId, "Interest map", Detail: lines);

    /// <summary>A round is starting.</summary>
    /// <param name="round">1-based round about to run.</param>
    /// <param name="maxRounds">The cap.</param>
    public static DiscoveryEvent RoundStarted(int round, int maxRounds) =>
        new(DiscoveryEventKind.RoundStarted, "Discovery", $"Round {round} of {maxRounds}", Round: round);

    /// <summary>One retrieval call, and the products it actually discovered.</summary>
    /// <remarks>
    /// The hit COUNT alone is not the demo's payload — "→ 6" says a query worked, it does not say
    /// what the loop learned. <paramref name="discovered"/> names the products this query put in
    /// front of the reviewer for the first time, which is what makes round 2's vocabulary shift
    /// legible: the audience can see that the item round 2 found was not in round 1's list.
    /// </remarks>
    /// <param name="round">The round.</param>
    /// <param name="entry">The plan line that was executed.</param>
    /// <param name="hits">How many candidates came back.</param>
    /// <param name="discovered">One line per NEW product id this query added, or empty.</param>
    public static DiscoveryEvent Search(
        int round, QueryPlanEntry entry, int hits, IReadOnlyList<string>? discovered = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var category = entry.CategoryPathPrefix is { Length: > 0 } path ? $", cat={path}" : string.Empty;
        var attributes = entry.Attributes is { Count: > 0 } a
            ? ", " + string.Join(", ", a.Select(kv => $"{kv.Key}={kv.Value}"))
            : string.Empty;

        // Clipped for the CONSOLE only — the query itself goes to retrieval whole. An in-session
        // stated need is a whole sentence and would otherwise wrap the terminal.
        var query = entry.Query.Length <= 58 ? entry.Query : entry.Query[..57] + "…";

        return new DiscoveryEvent(DiscoveryEventKind.Search, "Discovery",
            $"[{entry.InterestId}·{entry.Origin}] Search(\"{query}\"{category}{attributes}) → {hits}",
            Round: round,
            Detail: discovered is { Count: > 0 } ? discovered : null);
    }

    /// <summary>A round finished.</summary>
    /// <param name="round">The round.</param>
    /// <param name="newIds">New product ids added.</param>
    /// <param name="duplicates">Hits suppressed because the id had already been seen.</param>
    /// <param name="total">Running candidate total.</param>
    public static DiscoveryEvent RoundComplete(int round, int newIds, int duplicates, int total) =>
        new(DiscoveryEventKind.RoundComplete, "Discovery",
            $"+ {newIds} new product id(s)   ({total} total · {duplicates} duplicate(s) suppressed)",
            Round: round);

    /// <summary>The deterministic pre-gate fired.</summary>
    /// <param name="round">The round.</param>
    /// <param name="lines">One line per starved interest.</param>
    public static DiscoveryEvent PreGate(int round, IReadOnlyList<string> lines) =>
        new(DiscoveryEventKind.PreGate, "CoverageReviewer",
            "pre-model gate: a DIRECT interest is structurally starved — gap raised before spending a token",
            Round: round, Detail: lines);

    /// <summary>The coverage ledger panel.</summary>
    /// <param name="round">The round.</param>
    /// <param name="lines">One row per interest.</param>
    /// <param name="verdict">The verdict line printed under the rows.</param>
    public static DiscoveryEvent CoverageLedger(int round, IReadOnlyList<string> lines, string verdict) =>
        new(DiscoveryEventKind.CoverageLedger, "CoverageReviewer", verdict, Round: round, Detail: lines);

    /// <summary>
    /// An edge was taken.
    /// </summary>
    /// <remarks>
    /// ⚠ MAF may evaluate an edge predicate more than once per super-step. These are a TRACE of
    /// routing, not an authoritative count — never derive a round number from them. The console
    /// sink suppresses immediate repeats for exactly this reason.
    /// </remarks>
    /// <param name="routeId">The route identifier, e.g. <c>"review-to-more-discovery"</c>.</param>
    /// <param name="description">What the route means, printed.</param>
    public static DiscoveryEvent Route(string routeId, string description) =>
        new(DiscoveryEventKind.Route, routeId, description);

    /// <summary>The §0.5 / D-3 constraint refused a term.</summary>
    /// <param name="round">The round.</param>
    /// <param name="dropped">The refusal.</param>
    public static DiscoveryEvent QueryTermDropped(int round, DroppedQueryTerm dropped)
    {
        ArgumentNullException.ThrowIfNull(dropped);
        return new DiscoveryEvent(DiscoveryEventKind.QueryTermDropped, "CoverageReviewer",
            $"query term REFUSED by the vocabulary constraint: {dropped}", Round: round);
    }

    /// <summary>The reviewer added an interest.</summary>
    /// <param name="round">The round.</param>
    /// <param name="interest">The accepted interest, after clamping and filtering.</param>
    /// <param name="evidenceProductId">The product whose review revealed it.</param>
    public static DiscoveryEvent InterestProposed(int round, Interest interest, string evidenceProductId)
    {
        ArgumentNullException.ThrowIfNull(interest);
        return new DiscoveryEvent(DiscoveryEventKind.InterestProposed, "CoverageReviewer",
            string.Create(CultureInfo.InvariantCulture,
                $"{interest.Id} NEW ⟨inferred {interest.Confidence:0.00}⟩ {interest.Label} ← review text of {evidenceProductId}"),
            Round: round,
            Detail: [$"query terms after the vocabulary filter: {string.Join(" · ", interest.QueryTerms)}"]);
    }

    /// <summary>The Ranker finished.</summary>
    /// <param name="lines">The post-check lines.</param>
    public static DiscoveryEvent Ranked(IReadOnlyList<string> lines) =>
        new(DiscoveryEventKind.Ranked, "Ranker", "deterministic post-checks", Detail: lines);

    /// <summary>A post-check removed a SKU.</summary>
    /// <param name="dropped">The removal.</param>
    public static DiscoveryEvent SkuDropped(DroppedSku dropped)
    {
        ArgumentNullException.ThrowIfNull(dropped);
        return new DiscoveryEvent(DiscoveryEventKind.SkuDropped, "Ranker",
            $"excluded {dropped.ProductId} — {dropped.Reason}");
    }

    /// <summary>The Presenter finished.</summary>
    /// <param name="message">What it did, e.g. the live price/stock read.</param>
    public static DiscoveryEvent Presented(string message) =>
        new(DiscoveryEventKind.Presented, "Presenter", message);

    /// <summary>Something degraded. A warning, never a failure.</summary>
    /// <param name="nodeId">Which node.</param>
    /// <param name="message">What happened and what it fell back to.</param>
    public static DiscoveryEvent Degraded(string nodeId, string message) =>
        new(DiscoveryEventKind.Degraded, nodeId, message);

    /// <summary>The run summary.</summary>
    /// <param name="state">The final state.</param>
    /// <param name="elapsed">Wall time.</param>
    public static DiscoveryEvent RunComplete(DiscoveryState state, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new DiscoveryEvent(DiscoveryEventKind.RunComplete, string.Empty, state.ToSummaryLine(),
            Round: state.DiscoveryRound, Elapsed: elapsed);
    }
}

/// <summary>
/// Where the loop's domain events go. Injected into every executor so the loop has no
/// <c>Console</c> dependency of its own — the eval lane substitutes a recording sink.
/// </summary>
public interface IDiscoveryProgressSink
{
    /// <summary>Publishes one event. Implementations must never throw.</summary>
    /// <param name="discoveryEvent">The event.</param>
    void Publish(DiscoveryEvent discoveryEvent);
}

/// <summary>Discards everything. The default when no sink is supplied.</summary>
public sealed class NullDiscoveryProgressSink : IDiscoveryProgressSink
{
    /// <summary>The shared instance.</summary>
    public static NullDiscoveryProgressSink Instance { get; } = new();

    private NullDiscoveryProgressSink() { }

    /// <inheritdoc />
    public void Publish(DiscoveryEvent discoveryEvent) { }
}

/// <summary>
/// Records every event in order. Used by the eval lane and by the termination tests, which
/// assert on the ROUTE events rather than on console text.
/// </summary>
public sealed class RecordingDiscoveryProgressSink : IDiscoveryProgressSink
{
    private readonly List<DiscoveryEvent> _events = [];
    private readonly Lock _gate = new();

    /// <summary>Every event, in publication order.</summary>
    public IReadOnlyList<DiscoveryEvent> Events
    {
        get { lock (_gate) return [.. _events]; }
    }

    /// <summary>
    /// The route ids that fired, in order and with immediate repeats collapsed. MAF may
    /// evaluate a predicate more than once per super-step, so raw route events are a trace.
    /// </summary>
    public IReadOnlyList<string> RoutesTaken()
    {
        var routes = new List<string>();
        foreach (var item in Events)
        {
            if (item.Kind != DiscoveryEventKind.Route) continue;
            if (routes.Count > 0 && string.Equals(routes[^1], item.NodeId, StringComparison.Ordinal)) continue;
            routes.Add(item.NodeId);
        }
        return routes;
    }

    /// <inheritdoc />
    public void Publish(DiscoveryEvent discoveryEvent)
    {
        if (discoveryEvent is null) return;
        lock (_gate) _events.Add(discoveryEvent);
    }
}

/// <summary>
/// Fans one event out to several sinks. The demo uses it to print AND record in the same run,
/// so what the audience sees and what a test asserts on come from one stream.
/// </summary>
/// <param name="sinks">The sinks, in publication order.</param>
public sealed class CompositeDiscoveryProgressSink(params IDiscoveryProgressSink[] sinks) : IDiscoveryProgressSink
{
    private readonly IDiscoveryProgressSink[] _sinks = sinks ?? [];

    /// <inheritdoc />
    public void Publish(DiscoveryEvent discoveryEvent)
    {
        foreach (var sink in _sinks) sink.Publish(discoveryEvent);
    }
}

/// <summary>
/// The demo's visual payload: the loop, round by round, on the console.
/// </summary>
/// <remarks>
/// Two-space base indent, three for traces, TravelDemo's vocabulary throughout — so Demo 1 and
/// Demo 2 read as one system rather than two samples that happen to be in the same folder.
/// </remarks>
public sealed class ConsoleDiscoveryProgressSink : IDiscoveryProgressSink
{
    private const string Indent = "  ";
    private const string Trace = "     ";

    private string _lastRoute = string.Empty;
    private int _lastRoundPrinted;

    /// <inheritdoc />
    public void Publish(DiscoveryEvent discoveryEvent)
    {
        if (discoveryEvent is null) return;

        switch (discoveryEvent.Kind)
        {
            case DiscoveryEventKind.RunStarted:
                Write(ConsoleColor.DarkGray, $"{Indent}{discoveryEvent.Message}");
                WriteDetail(ConsoleColor.Yellow, discoveryEvent.Detail, $"{Indent}⚠  ");
                Console.WriteLine();
                break;

            case DiscoveryEventKind.NodeStarted:
                Write(ConsoleColor.Cyan,
                    $"{Indent}🤖 [{discoveryEvent.NodeId}] starting..."
                    + (discoveryEvent.Message.Length > 0 ? $"   ({discoveryEvent.Message})" : string.Empty));
                break;

            case DiscoveryEventKind.NodeCompleted:
                Write(ConsoleColor.Green,
                    $"{Indent}✓ [{discoveryEvent.NodeId}] completed   "
                    + $"{discoveryEvent.ModelCalls} model call(s) · {discoveryEvent.Elapsed?.TotalSeconds ?? 0:0.00} s");
                Console.WriteLine();
                break;

            case DiscoveryEventKind.InterestMap:
                Rule("Interest map");
                WriteDetail(ConsoleColor.White, discoveryEvent.Detail, Indent + "  ");
                break;

            case DiscoveryEventKind.RoundStarted:
                _lastRoundPrinted = discoveryEvent.Round;
                Console.WriteLine();
                Rule(discoveryEvent.Message);
                break;

            case DiscoveryEventKind.Search:
                Write(ConsoleColor.DarkGray, $"{Trace}🔍 {discoveryEvent.Message}");
                // The products, not just the count. This is the line that lets an audience see
                // that round 2 found something round 1 did not.
                WriteDetail(ConsoleColor.DarkGreen, discoveryEvent.Detail, Trace + "   + ");
                break;

            case DiscoveryEventKind.RoundComplete:
                Write(ConsoleColor.DarkCyan, $"{Trace}{discoveryEvent.Message}");
                break;

            case DiscoveryEventKind.PreGate:
                Write(ConsoleColor.Yellow, $"{Trace}⛔ {discoveryEvent.Message}");
                WriteDetail(ConsoleColor.Yellow, discoveryEvent.Detail, Trace + "   → ");
                break;

            case DiscoveryEventKind.CoverageLedger:
                Rule($"Coverage ledger · round {discoveryEvent.Round}");
                WriteDetail(ConsoleColor.White, discoveryEvent.Detail, Indent + "  ");
                Write(ConsoleColor.Yellow, $"{Indent}⚠  {discoveryEvent.Message}");
                break;

            case DiscoveryEventKind.Route:
                // Immediate repeats collapsed: a predicate may be evaluated more than once
                // per super-step, and printing the same arrow twice would teach the audience
                // to count arrows, which is exactly what these events cannot support.
                if (string.Equals(_lastRoute, discoveryEvent.NodeId, StringComparison.Ordinal)) break;
                _lastRoute = discoveryEvent.NodeId;
                Write(ConsoleColor.Magenta, $"{Indent}{discoveryEvent.Message}");
                break;

            case DiscoveryEventKind.QueryTermDropped:
                Write(ConsoleColor.Red, $"{Trace}🛡  {discoveryEvent.Message}");
                break;

            case DiscoveryEventKind.InterestProposed:
                Write(ConsoleColor.White, $"{Trace}✚ {discoveryEvent.Message}");
                WriteDetail(ConsoleColor.DarkGray, discoveryEvent.Detail, Trace + "  ");
                break;

            case DiscoveryEventKind.Ranked:
                WriteDetail(ConsoleColor.DarkGray, discoveryEvent.Detail, $"{Trace}✓ ");
                break;

            case DiscoveryEventKind.SkuDropped:
                Write(ConsoleColor.Yellow, $"{Trace}✗ {discoveryEvent.Message}");
                break;

            case DiscoveryEventKind.Presented:
                Write(ConsoleColor.DarkGray, $"{Trace}💰 {discoveryEvent.Message}");
                break;

            case DiscoveryEventKind.Degraded:
                Write(ConsoleColor.Yellow, $"{Indent}⚠  [{discoveryEvent.NodeId}] {discoveryEvent.Message}");
                break;

            case DiscoveryEventKind.RunComplete:
                Console.WriteLine();
                Rule("Run summary");
                Write(ConsoleColor.White, $"{Indent}  {discoveryEvent.Message}");
                Write(ConsoleColor.DarkGray,
                    $"{Indent}  wall time {discoveryEvent.Elapsed?.TotalSeconds ?? 0:0.00} s");
                Console.WriteLine();
                break;

            default:
                Write(ConsoleColor.Gray, $"{Indent}{discoveryEvent.Message}");
                break;
        }
    }

    /// <summary>The last round header this sink printed. Diagnostic only.</summary>
    public int LastRoundPrinted => _lastRoundPrinted;

    private static void Rule(string title)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        var text = $"{Indent}── {title} ";
        Console.WriteLine(text.PadRight(78, '─'));
        Console.ResetColor();
    }

    private static void Write(ConsoleColor colour, string line)
    {
        Console.ForegroundColor = colour;
        Console.WriteLine(line);
        Console.ResetColor();
    }

    private static void WriteDetail(ConsoleColor colour, IReadOnlyList<string>? detail, string prefix)
    {
        if (detail is null || detail.Count == 0) return;

        Console.ForegroundColor = colour;
        foreach (var line in detail) Console.WriteLine(prefix + line);
        Console.ResetColor();
    }
}
