// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentEval.Results.Runner;

/// <summary>
/// The runner protocol (contracts/aef/1/README.md, "Run plans and runners", "The event stream"): plan-to-runner
/// matching, and the verification of a finished event stream line by line. Vectors: contracts/aef/1/conformance/protocol/.
/// </summary>
public static class RunnerEventStream
{
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal) { "job.sealed", "job.failed", "job.cancelled", "job.refused" };

    private static readonly Regex Timeout = new("^PT(?=[0-9])(?:([0-9]{1,5})H)?(?:([0-9]{1,5})M)?\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A runner can take a plan when it carries every tag of the plan's runnerSelector, supports the plan's provider,
    /// and, for a remote-zone plan, is in the plan's zone.
    /// </summary>
    public static bool Matches(JsonNode plan, JsonNode runner)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(runner);
        var tags = (runner["tags"]?.AsArray() ?? []).Select(t => (string?)t).ToHashSet(StringComparer.Ordinal);
        var providers = (runner["providers"]?.AsArray() ?? []).Select(p => (string?)p).ToHashSet(StringComparer.Ordinal);
        return (plan["runnerSelector"]?.AsArray() ?? []).All(t => tags.Contains((string?)t))
               && providers.Contains((string?)plan["provider"])
               && ((string?)plan["isolation"] != "remote-zone" || (string?)runner["networkZone"] == (string?)plan["zone"]);
    }

    /// <summary>
    /// Every problem as (where, problem): where is <c>event:&lt;n&gt;</c> (the 1-based line) or <c>stream</c>; problems
    /// in event order, then by name, with <c>stream</c> last. Empty when the stream keeps every rule.
    /// </summary>
    /// <param name="events">The stream's events, each valid against the reader runner-event schema, in file order.</param>
    /// <param name="plan">The run plan the job ran, when known: planId and its limits are checked.</param>
    /// <param name="planDigest">The SHA-256 (lower-case hex) of the plan file's bytes, when known.</param>
    public static IReadOnlyList<(string Where, string Problem)> Verify(IReadOnlyList<JsonNode> events, JsonNode? plan = null, string? planDigest = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        var problems = new List<(string, string)>();
        long previousSeq = 0;
        AefTime? previousAt = null;
        double? spent = null;
        bool terminal = false, accepted = false, overTime = false;
        var cases = 0;
        var announced = new Dictionary<string, string>(StringComparer.Ordinal);
        var job = events.Count > 0 ? (string?)events[0]["jobId"] : null;
        var start = events.Count > 0 ? AefTime.Parse((string)events[0]["at"]!) : default;
        var limit = plan?["limits"]?["timeout"] is { } t ? TimeoutSeconds((string)t!) : (long?)null;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var found = new List<string>();
            var kind = (string)e["kind"]!;
            if (i == 0 && kind is not ("job.accepted" or "job.refused")) found.Add("first");

            var seq = AefJson.Integer(e["seq"]!);
            if (seq != previousSeq + 1) found.Add("seq");
            previousSeq = seq;

            if ((string?)e["jobId"] != job) found.Add("job-id");

            var at = AefTime.Parse((string)e["at"]!);
            if (previousAt is { } before && at < before) found.Add("time");
            previousAt = at;
            if (limit is { } seconds && !overTime && at > start.AddSeconds(seconds))
            {
                found.Add("over-time");
                overTime = true;
            }

            if (terminal) found.Add("after-terminal");

            switch (kind)
            {
                case "job.accepted" or "job.refused":
                    if (kind == "job.accepted" && accepted) found.Add("accepted-twice");
                    accepted |= kind == "job.accepted";
                    if (plan is not null && (string?)e["planId"] != (string?)plan["planId"]) found.Add("plan-id");
                    if (planDigest is not null && (string?)e["planDigest"] != planDigest) found.Add("plan-digest");
                    break;
                case "plan.estimated" when (double)e["usdLow"]! > (double)e["usdHigh"]!:
                    found.Add("estimate");
                    break;
                case "spend.updated":
                    var value = (double)e["spentUsd"]!;
                    if (spent is { } last && value < last) found.Add("spend-decreased");
                    if (plan?["limits"]?["maxUsd"] is { } max && value > (double)max) found.Add("over-budget");
                    spent = value;
                    break;
                case "case.completed":
                    cases++;
                    if (plan?["limits"]?["cases"] is { } allowed && cases == AefJson.Integer(allowed) + 1) found.Add("over-cases");
                    break;
                case "evidence.produced":
                    var runId = (string)e["runId"]!;
                    if (announced.TryGetValue(runId, out var hash) && hash != (string?)e["runHash"]) found.Add("run-hash-changed");
                    announced.TryAdd(runId, (string)e["runHash"]!);
                    break;
            }

            if (kind is "job.sealed" or "job.failed")
            {
                var named = (e["runs"]?.AsArray() ?? []).Select(r => (string)r!).ToList();
                if (named.Any(r => !announced.ContainsKey(r))) found.Add("unannounced-run");
                if (announced.Keys.Any(r => !named.Contains(r, StringComparer.Ordinal))) found.Add("unsealed-run");
            }

            if (Terminal.Contains(kind)) terminal = true;
            problems.AddRange(found.Order(StringComparer.Ordinal).Select(p => ($"event:{i + 1}", p)));
        }

        if (!terminal) problems.Add(("stream", "no-terminal"));
        return problems;
    }

    private static long TimeoutSeconds(string text)
    {
        var m = Timeout.Match(text);
        if (!m.Success) throw new FormatException($"'{text}' is not a timeout in hours and minutes.");
        long hours = m.Groups[1].Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        long minutes = m.Groups[2].Success ? long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return (hours * 3600) + (minutes * 60);
    }
}
