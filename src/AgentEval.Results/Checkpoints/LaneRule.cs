// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Checkpoints;

/// <summary>
/// The suite a lane's rule names ([LANE-1]): only runs of this suite are eligible, with the same <c>ref</c>, and the same
/// <c>version</c> and <c>digest</c> for those the rule gives.
/// </summary>
public sealed record SuiteBinding(string Ref, string? Version, string? Digest)
{
    /// <summary>Whether run.json's <c>suite</c> is this one (an absent suite never is).</summary>
    public bool Matches(JsonNode? suite) =>
        AefNode.String(AefNode.Get(suite, "ref")) == Ref
        && (Version is null || AefNode.String(AefNode.Get(suite, "version")) == Version)
        && (Digest is null || AefNode.String(AefNode.Get(suite, "digest")) == Digest);
}

/// <summary>One exact sealed run: its <c>runId</c> and its run hash ([SEAL-4]), as a comparison's baseline names it.</summary>
public sealed record AefRunPointer(string RunId, string RunHash);

/// <summary>
/// How a checkpoint lane's result follows from its runs (contracts/aef/1/spec/05-checkpoints.md, §5.3): one of the four
/// kinds this version defines, or a kind a later minor adds (<see cref="UnknownLaneRule"/>, whose result is
/// <c>not_measured</c>, [VER-3]). Read from a manifest the reader checkpoint schema accepts; values this version does not
/// know (a threshold's <c>op</c>, a severity rule's <c>max</c>, a comparison axis) are kept as written, and §7.3 says how
/// each reads.
/// </summary>
/// <param name="Kind">The rule's <c>kind</c>, as written.</param>
/// <param name="Suite">The suite the rule binds its runs to, or null.</param>
public abstract record LaneRule(string Kind, SuiteBinding? Suite)
{
    /// <summary>The kinds this version defines.</summary>
    public static IReadOnlyList<string> KnownKinds { get; } = ["threshold", "severity", "comparison", "evidence-present"];

    /// <summary>
    /// Whether the rule holds a value this version does not know: its kind, a severity rule's <c>max</c>, a threshold's
    /// <c>op</c> or a comparison axis (§7.3). A later minor recorded a result for it that this version cannot recompute,
    /// so [CKP-8] does not compare it: the lane is <c>unverifiable</c>.
    /// </summary>
    public virtual bool HoldsUnknownValue => false;

    /// <summary>Reads a lane's <c>rule</c>.</summary>
    /// <exception cref="FormatException">A field the reader schema requires is absent or of another type.</exception>
    public static LaneRule Read(JsonNode? rule)
    {
        if (rule is not JsonObject obj || AefNode.String(obj["kind"]) is not { } kind)
        {
            throw new FormatException("A lane's rule is an object with a kind.");
        }

        var suite = obj["suite"] is JsonObject s
            ? new SuiteBinding(Text(s, "ref"), AefNode.String(s["version"]), AefNode.String(s["digest"]))
            : null;
        return kind switch
        {
            "threshold" => new ThresholdRule(Text(obj, "lane"), Text(obj, "metric"), Text(obj, "path"), Text(obj, "op"), Number(obj, "value"),
                Count(obj["minimumN"]), suite),
            "severity" => new SeverityRule(Text(obj, "max"), AefNode.String(obj["lane"]), AefNode.String(obj["path"]), Count(obj["minimumN"]), suite),
            "comparison" => new ComparisonRule(Text(obj, "lane"), Text(obj, "metric"), Text(obj, "path"),
                obj["baseline"] is JsonObject b ? new AefRunPointer(Text(b, "runId"), Text(b, "runHash")) : throw new FormatException("A comparison names its baseline."),
                Number(obj, "significance"), Count(obj["minimumPairs"]) ?? throw new FormatException("A comparison sets minimumPairs."),
                [.. AefNode.Strings(obj["axes"])], suite),
            "evidence-present" => new EvidencePresentRule(Count(obj["runs"]) ?? throw new FormatException("An evidence-present rule sets runs."), suite),
            _ => new UnknownLaneRule(kind),
        };
    }

    private static string Text(JsonObject obj, string name) =>
        AefNode.String(obj[name]) ?? throw new FormatException($"A lane rule's {name} is a string.");

    private static double Number(JsonObject obj, string name) =>
        AefNode.Number(obj[name]) ?? throw new FormatException($"A lane rule's {name} is a number.");

    // An integer field (the schema bounds it to 1 .. 2^53 − 1, written plain or as 2.0, [ENC-4]).
    private static long? Count(JsonNode? node) => AefNode.Number(node) is { } value ? (long)value : null;
}

/// <summary>
/// <c>threshold</c> ([LANE-2]): the summary entry with this <paramref name="Lane"/>, <paramref name="Metric"/> and
/// <paramref name="Path"/>, compared with <paramref name="Value"/> by <paramref name="Op"/> (<c>&gt;=</c>, <c>&gt;</c>,
/// <c>&lt;=</c> or <c>&lt;</c>, as binary64; another reads as the lane's result <c>not_measured</c>, §7.3).
/// </summary>
public sealed record ThresholdRule(string Lane, string Metric, string Path, string Op, double Value, long? MinimumN, SuiteBinding? Suite)
    : LaneRule("threshold", Suite)
{
    /// <summary>The operators this version defines.</summary>
    public static IReadOnlyList<string> KnownOps { get; } = [">=", ">", "<=", "<"];

    /// <inheritdoc />
    public override bool HoldsUnknownValue => !KnownOps.Contains(Op, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="value"/> holds against the rule (null for an operator this version does not know).</summary>
    public bool? Holds(double value) => Op switch
    {
        ">=" => value >= Value,
        ">" => value > Value,
        "<=" => value <= Value,
        "<" => value < Value,
        _ => null,
    };
}

/// <summary>
/// <c>severity</c> ([LANE-3]): the worst severity allowed among failures, <paramref name="Max"/> (<c>none</c>, <c>low</c>,
/// <c>medium</c> or <c>high</c>; another reads as the lane's result <c>not_measured</c>, §7.3), over the result lines of
/// summary lane <paramref name="Lane"/> and at <paramref name="Path"/> or below it, when given.
/// </summary>
public sealed record SeverityRule(string Max, string? Lane, string? Path, long? MinimumN, SuiteBinding? Suite)
    : LaneRule("severity", Suite)
{
    /// <summary>The values of <c>max</c> this version defines.</summary>
    public static IReadOnlyList<string> KnownMax { get; } = ["none", "low", "medium", "high"];

    /// <inheritdoc />
    public override bool HoldsUnknownValue => !KnownMax.Contains(Max, StringComparer.Ordinal);

    /// <summary>Whether a line at <paramref name="linePath"/> is in scope: the rule's path itself, or below it (<c>path/…</c>).</summary>
    public bool InScope(string? linePath) =>
        Path is null || (linePath is not null && (linePath == Path || linePath.StartsWith(Path + "/", StringComparison.Ordinal)));
}

/// <summary>
/// <c>comparison</c> ([LANE-5]–[LANE-8]): no significant regression of the lane's one run against
/// <paramref name="Baseline"/> at <paramref name="Path"/> in summary lane <paramref name="Lane"/>, by an exact one-sided sign
/// test at <paramref name="Significance"/>, when the runs agree on every axis of <paramref name="Axes"/>.
/// </summary>
public sealed record ComparisonRule(
    string Lane, string Metric, string Path, AefRunPointer Baseline, double Significance, long MinimumPairs,
    IReadOnlyList<string> Axes, SuiteBinding? Suite)
    : LaneRule("comparison", Suite)
{
    /// <summary>The comparability axes this version names ([LANE-6]); another counts as differing.</summary>
    public static IReadOnlyList<string> KnownAxes { get; } =
        ["subject", "suite", "suite-content", "judges", "rubrics", "target-mode", "deployment", "producer"];

    /// <inheritdoc />
    public override bool HoldsUnknownValue => Axes.Any(a => !KnownAxes.Contains(a, StringComparer.Ordinal));
}

/// <summary><c>evidence-present</c> ([LANE-4]): at least <paramref name="Runs"/> runs, every one eligible.</summary>
public sealed record EvidencePresentRule(long Runs, SuiteBinding? Suite) : LaneRule("evidence-present", Suite);

/// <summary>A rule kind this version does not know: the lane's result is <c>not_measured</c> ([VER-3], §7.3).</summary>
public sealed record UnknownLaneRule(string Name) : LaneRule(Name, null)
{
    /// <inheritdoc />
    public override bool HoldsUnknownValue => true;
}
