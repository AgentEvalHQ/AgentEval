// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Adapters.EvalPort;
using AgentEval.Results.Json;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

internal static partial class AefCommand
{
    private static Command CreateImportEvalPort()
    {
        var input = new Argument<string>("resultset-file");
        var output = new Argument<string>("out-dir");
        var mode = new Option<string>("--target-mode") { Required = true };
        var subject = new Option<string?>("--subject-ref");
        var kind = new Option<string?>("--subject-kind");
        var version = new Option<string?>("--suite-version");
        var (capture, noSeal, key) = ConversionOptions();
        var json = JsonOption();
        var cmd = new Command("evalport", "Import a bounded EvalPort ResultSet into a new AEF directory; reports assertions and conversion losses.");
        cmd.Add(input); cmd.Add(output);
        foreach (var option in new Option[] { mode, subject, kind, version, capture, noSeal, key, json }) cmd.Add(option);
        cmd.SetAction((ParseResult p, CancellationToken ct) => Task.FromResult(RunImportEvalPort(
            p.GetValue(input)!, p.GetValue(output)!, p.GetValue(mode)!, p.GetValue(subject), p.GetValue(kind),
            p.GetValue(version), p.GetValue(capture)!, p.GetValue(noSeal), p.GetValue(key), p.GetValue(json), Console.Out, Console.Error, ct)));
        return cmd;
    }

    internal static int RunImportEvalPort(string input, string output, string mode, string? subject, string? kind,
        string? version, string capture, bool noSeal, string? key, bool asJson, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        string? staging = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!AefNames.TryParse<AefTargetMode>(mode, out var targetMode) ||
                !AefNames.TryParse<AefContentCapture>(capture, out var contentCapture))
                throw EvalPortInput("invalid target mode or content capture");
            AefSubject? declaredSubject = null;
            if (subject is not null || kind is not null)
            {
                if (string.IsNullOrEmpty(subject) || !AefNames.TryParse<AefSubjectKind>(kind ?? "", out var subjectKind))
                    throw EvalPortInput("subject-ref and valid subject-kind must be supplied together");
                declaredSubject = new() { Ref = subject, Kind = subjectKind.Value };
            }
            if (noSeal && key is not null) throw EvalPortInput("a signing key requires sealing");
            output = Path.GetFullPath(output);
            var parent = EvalPortDestination(output);
            var bytes = CompanionRead(input, AefLimits.MaxJsonBytes, ct);
            using var signer = key is null ? null : AefSigningKey.FromPkcs8Pem(new UTF8Encoding(false, true).GetString(CompanionRead(key, 64 * 1024, ct)));
            var candidate = Path.Combine(parent, ".ae-evalport-" + Guid.NewGuid().ToString("N"));
            if (Path.Exists(candidate)) throw EvalPortInput("staging path exists");
            Directory.CreateDirectory(candidate); staging = candidate;
            var converted = new AefEvalPortImporter().Import(bytes, staging, new()
            {
                TargetMode = targetMode.Value, Subject = declaredSubject, SuiteVersion = version,
                ContentCapture = contentCapture.Value, Seal = !noSeal, Signer = signer,
            }, ct);
            ct.ThrowIfCancellationRequested();
            CompanionOrdinaryParents(parent);
            Directory.Move(staging, output); staging = null;
            return Converted(converted with { Directory = output }, asJson, stdout);
        }
        catch (Exception e) when (IsInputError(e) || e is NotSupportedException or AefReadException)
        { stderr.WriteLine(e.Message); return ExitCodes.UsageError; }
        finally { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static Command CreateExportEvalPort()
    {
        var input = new Argument<string>("run-dir");
        var output = new Argument<string>("out-file");
        var hash = new Option<string?>("--run-hash");
        var suite = new Option<string?>("--suite-id");
        var overlays = new Option<bool>("--ignore-overlays") { Description = "Explicitly export base evidence without applying overlays." };
        var policy = PolicyOption(); var json = JsonOption();
        var cmd = new Command("export-evalport", "Export intact sealed base evidence to a new ResultSet file; signature trust is not transferred.");
        cmd.Add(input); cmd.Add(output);
        foreach (var option in new Option[] { hash, suite, overlays, policy, json }) cmd.Add(option);
        cmd.SetAction((ParseResult p, CancellationToken ct) => Task.FromResult(RunExportEvalPort(
            p.GetValue(input)!, p.GetValue(output)!, p.GetValue(hash), p.GetValue(suite), p.GetValue(overlays),
            p.GetValue(policy), p.GetValue(json), Console.Out, Console.Error, ct)));
        return cmd;
    }

    internal static int RunExportEvalPort(string input, string output, string? hash, string? suite, bool ignoreOverlays,
        string? policy, bool asJson, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        string? staging = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            input = Path.GetFullPath(input); output = Path.GetFullPath(output);
            var relative = Path.GetRelativePath(input, output);
            if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw EvalPortInput("output must be outside the selected evidence directory");
            var parent = EvalPortDestination(output);
            var export = new AefEvalPortExporter().Export(input, new()
            { ExpectedRunHash = hash, SuiteId = suite, IgnoreOverlays = ignoreOverlays, Policy = CompanionPolicy(policy, ct) }, ct);
            var bytes = Encoding.UTF8.GetBytes(export.ResultSet.ToJson().ToJsonString());
            var candidate = Path.Combine(parent, ".ae-evalport-" + Guid.NewGuid().ToString("N"));
            // Own the temporary file before writing so any partial write can be cleaned up.
            using (var file = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { staging = candidate; file.Write(bytes); file.Flush(true); }
            ct.ThrowIfCancellationRequested();
            CompanionOrdinaryParents(parent);
            File.Move(staging, output); staging = null;
            if (asJson) stdout.WriteLine(new JsonObject
            {
                ["file"] = output, ["runHash"] = export.RunHash, ["runId"] = export.ResultSet.RunId,
                ["notes"] = new JsonArray([.. export.Notes.Select(n => (JsonNode?)n)]),
            }.ToJsonString(s_json));
            else
            {
                stdout.WriteLine($"EvalPort ResultSet written: {output}");
                foreach (var note in export.Notes) stdout.WriteLine($"  - {note}");
            }
            return ExitCodes.Success;
        }
        catch (Exception e) when (IsInputError(e) || e is NotSupportedException or AefReadException)
        { stderr.WriteLine(e.Message); return ExitCodes.UsageError; }
        finally { if (staging is not null) File.Delete(staging); }
    }

    private static string EvalPortDestination(string output)
    {
        var parent = Path.GetDirectoryName(output)!;
        CompanionOrdinaryParents(parent);
        if (Path.Exists(output)) throw EvalPortInput("output already exists");
        return parent;
    }

    private static AefInputException EvalPortInput(string actual) => new(
        $"Expected: valid explicit conversion options and a new output path. Actual: {actual}. Suggestions: check options and choose a new destination with an existing parent. Because: conversion must preserve existing evidence.");
}
