// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.EvalPort;

/// <summary>Caller-supplied facts absent from an EvalPort ResultSet.</summary>
public sealed record AefEvalPortImportOptions : AefConversionOptions
{
    /// <summary>How the target was driven; EvalPort does not establish this.</summary>
    public required AefTargetMode TargetMode { get; init; }
    /// <summary>Explicit subject, required when provider.model is absent.</summary>
    public AefSubject? Subject { get; init; }
    /// <summary>Exact suite version when absent from the ResultSet; never a digest of result bytes.</summary>
    public string? SuiteVersion { get; init; }
}

// Planning validates temporal and identity inputs before any run directory is created.
internal sealed record EvalPortImportPlan(EvalPortResultSet Source, AefRunHeader Header,
    AefRunStatus Status, AefTime? EndedAt, string? AbortReason, IReadOnlyList<string> Notes)
{
    internal static EvalPortImportPlan Create(EvalPortResultSet source, AefEvalPortImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(options);
        var doc = source.ToJson(); var asserted = new List<string> { "execution.targetMode", "contentCapture" };
        var notes = new List<string>();
        var subject = options.Subject;
        if (subject is not null) asserted.AddRange(["subject.ref", "subject.kind"]);
        else if (doc["provider"]?["model"] is JsonValue model && model.TryGetValue<string>(out var modelName) && modelName.Length > 0)
        {
            subject = new() { Ref = AefConverter.TypedRef("model", modelName), Kind = AefSubjectKind.Model };
            notes.Add("The provider model is mapped as the subject; the ResultSet does not identify a surrounding agent deployment.");
        }
        else throw Refuse("subject is absent; supply an explicit subject");

        var suiteVersion = doc["suite_version"]?.GetValue<string>();
        if (suiteVersion is null)
        {
            suiteVersion = options.SuiteVersion;
            asserted.Add("suite.version");
        }
        else if (options.SuiteVersion is not null && options.SuiteVersion != suiteVersion)
            throw Refuse("supplied suite version conflicts with source");
        if (!AefConverter.IsExactVersion(suiteVersion)) throw Refuse("an exact suite version is required");

        var started = EvalPortTimestamp.Parse(doc["started_at"]!.GetValue<string>());
        AefTime? ended = doc["completed_at"] is null ? null : EvalPortTimestamp.Parse(doc["completed_at"]!.GetValue<string>());
        if (ended < started) throw Refuse("completion precedes start");
        foreach (var result in doc["results"]!.AsArray())
            if (result!["completed_at"] is { } completed)
            {
                var time = EvalPortTimestamp.Parse(completed.GetValue<string>());
                if (time < started || (ended is { } end && time > end)) throw Refuse("result completion lies outside the run interval");
            }
        var partial = false;
        if (doc["metadata"] is JsonObject metadata && metadata.ContainsKey("openeval.partial"))
        {
            if (metadata["openeval.partial"] is not JsonValue value || !value.TryGetValue<bool>(out partial))
                throw Refuse("openeval.partial must be boolean");
        }
        var status = ended is null ? AefRunStatus.Running : partial ? AefRunStatus.Aborted : AefRunStatus.Completed;
        if (ended is null) notes.Add("No completed_at: the imported run remains running and cannot be sealed.");
        if (ended is { } endTime && options.Seal && AefTime.FromDateTimeOffset((options.TimeProvider ?? TimeProvider.System).GetUtcNow()) < endTime)
            throw Refuse("conversion clock precedes source completion");

        var runId = AefConverter.IsId(source.RunId) ? source.RunId :
            "evalport-" + AefConverter.Sha256Of(Encoding.UTF8.GetBytes(source.RunId))[7..];
        var origin = doc["runner"]?["name"]?.GetValue<string>();
        var originVersion = doc["runner"]?["version"]?.GetValue<string>();
        var from = string.IsNullOrWhiteSpace(origin) ? "evalport " + doc["version"]!.GetValue<string>() :
            origin + (originVersion is null ? "" : " " + originVersion);
        var header = new AefRunHeader
        {
            RunId = runId, Producer = options.Producer ?? AefConverter.DefaultProducer, Subject = subject,
            Suite = new() { Ref = AefConverter.TypedRef("suite", source.SuiteId), Version = suiteVersion! },
            StartedAt = started, ContentCapture = options.ContentCapture,
            Execution = new() { TargetMode = options.TargetMode },
            Imported = new() { From = from, Asserted = asserted.AsReadOnly() },
            Ext = new JsonObject { ["evalport"] = new JsonObject
            { ["version"] = doc["version"]!.DeepClone(), ["run_id"] = source.RunId, ["suite_id"] = source.SuiteId,
                ["providerModel"] = doc["provider"]?["model"]?.DeepClone(), ["runner"] = doc["runner"]?.DeepClone() } },
        };
        return new(source, header, status, ended,
            status == AefRunStatus.Aborted ? "Source ResultSet declares openeval.partial." : null, notes.AsReadOnly());
    }

    private static AefInputException Refuse(string actual) => new(
        $"Expected: explicit, consistent EvalPort import facts. Actual: {actual}. " +
        "Suggestions: supply missing identity or correct source timestamps. Because: conversion cannot invent provenance.");
}

internal static partial class EvalPortTimestamp
{
    [GeneratedRegex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2})[Tt]([0-9]{2}:[0-9]{2}:[0-9]{2})(\.[0-9]{1,9})?([Zz]|[+-][0-9]{2}:[0-9]{2})\z")]
    private static partial Regex Pattern();

    internal static AefTime Parse(string text)
    {
        var match = Pattern().Match(text);
        if (!match.Success || match.Groups[4].Value == "-00:00") throw Invalid();
        try
        {
            var utc = AefTime.Parse(match.Groups[1].Value + "T" + match.Groups[2].Value + match.Groups[3].Value + "Z");
            var offset = match.Groups[4].Value;
            if (offset.Length > 1)
            {
                var hours = int.Parse(offset.AsSpan(1, 2), CultureInfo.InvariantCulture);
                var minutes = int.Parse(offset.AsSpan(4, 2), CultureInfo.InvariantCulture);
                if (hours > 23 || minutes > 59) throw Invalid();
                utc = utc.AddSeconds((offset[0] == '+' ? -1 : 1) * (hours * 3600 + minutes * 60));
            }
            // Refuse offsets that move outside AEF's representable calendar.
            _ = utc.ToString();
            return utc;
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException) { throw Invalid(); }
    }

    private static AefInputException Invalid() => new("Expected: RFC3339 timestamp with known offset and at most nanosecond precision. " +
        "Actual: unsupported timestamp. Suggestions: provide an exact valid instant. Because: conversion must not round or guess event time.");
}
