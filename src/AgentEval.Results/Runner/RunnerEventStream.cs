// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentEval.Results.Runner;

/// <summary>
/// Verifies a finished runner event stream (contracts/aef/v2/README.md, "The event stream"): the order and the rules a
/// schema cannot check, line by line. Its vectors are contracts/aef/v2/conformance/protocol/streams/.
/// </summary>
public static class RunnerEventStream
{
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal) { "job.sealed", "job.failed", "job.cancelled" };

    /// <summary>
    /// Every problem as (where, problem): where is <c>event:&lt;n&gt;</c> (the 1-based line) or <c>stream</c>; problems
    /// in event order, then by name, with <c>stream</c> last. Empty when the stream keeps every rule.
    /// </summary>
    /// <param name="events">The stream's events, each already valid against runner-event.schema.json, in file order.</param>
    /// <param name="plan">The run plan the job ran, when known: its planId and limits.maxUsd are checked.</param>
    public static IReadOnlyList<(string Where, string Problem)> Verify(IReadOnlyList<JsonNode> events, JsonNode? plan = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        var problems = new List<(string, string)>();
        long previousSeq = 0;
        DateTimeOffset? previousAt = null;
        double? spent = null;
        var terminal = false;
        var announced = new HashSet<string>(StringComparer.Ordinal);
        var job = events.Count > 0 ? (string?)events[0]["jobId"] : null;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var found = new List<string>();
            var kind = (string)e["kind"]!;
            if (i == 0 && kind != "job.accepted") found.Add("first");

            var seq = (long)e["seq"]!;
            if (seq != previousSeq + 1) found.Add("seq");
            previousSeq = seq;

            if ((string?)e["jobId"] != job) found.Add("job-id");

            var at = DateTimeOffset.Parse((string)e["at"]!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            if (previousAt is { } before && at < before) found.Add("time");
            previousAt = at;

            if (terminal) found.Add("after-terminal");

            switch (kind)
            {
                case "job.accepted" when plan is not null && (string?)e["planId"] != (string?)plan["planId"]:
                    found.Add("plan-id");
                    break;
                case "spend.updated":
                    var value = (double)e["spentUsd"]!;
                    if (spent is { } last && value < last) found.Add("spend-decreased");
                    if (plan?["limits"]?["maxUsd"] is { } max && value > (double)max) found.Add("over-budget");
                    spent = value;
                    break;
                case "evidence.produced":
                    announced.Add((string)e["runId"]!);
                    break;
                case "job.sealed" when e["runs"]!.AsArray().Any(r => !announced.Contains((string)r!)):
                    found.Add("unannounced-run");
                    break;
            }

            if (Terminal.Contains(kind)) terminal = true;
            problems.AddRange(found.Order(StringComparer.Ordinal).Select(p => ($"event:{i + 1}", p)));
        }

        if (!terminal) problems.Add(("stream", "no-terminal"));
        return problems;
    }
}
