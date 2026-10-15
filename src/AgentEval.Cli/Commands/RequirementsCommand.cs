// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.CommandLine;
using System.Text.Json;
using AgentEval.Results;
using AgentEval.Results.Requirements;
using AgentEval.Results.Signatures;

namespace AgentEval.Cli.Commands;

/// <summary>External requirement catalogues and their reproducible evidence status.</summary>
internal static class RequirementsCommand
{
    internal static Command Create()
    {
        var command = new Command("requirements", "Validate external requirement catalogues and inspect evidence status.");
        var file = new Argument<string>("catalogue") { Description = "A requirement catalogue (.json, .yaml or .yml)." };
        var json = new Option<bool>("--json") { Description = "Print a structured validation result." };
        var validate = new Command("validate", "Validate catalogue schema and semantics without running an agent or modifying evidence.");
        validate.Add(file);
        validate.Add(json);
        validate.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunValidate(
            p.GetValue(file)!, p.GetValue(json), Console.Out, Console.Error)));
        command.Add(validate);
        var status = new Command("status", "Compute reproducible requirement status from explicitly selected sealed evidence.");
        var statusFile = new Argument<string>("catalogue") { Description = "External JSON/YAML catalogue." };
        var subject = new Option<string>("--subject") { Required = true, Description = "Exact subject reference." };
        var version = new Option<string>("--version") { Required = true, Description = "Exact subject version." };
        var at = new Option<string>("--at") { Required = true, Description = "Explicit RFC 3339 UTC view time, ending in Z." };
        var evidence = new Option<string[]>("--evidence") { Description = "Repeat HASH=DIRECTORY for selected sealed runs (maximum 256).", Arity = ArgumentArity.OneOrMore };
        var policy = new Option<string?>("--policy") { Description = "Optional AEF trust-policy JSON with exact requirements:waive:namespace:name permissions." };
        var statusJson = new Option<bool>("--json") { Description = "Print status, diagnostics and reproducibility inputs as JSON." };
        status.Add(statusFile); status.Add(subject); status.Add(version); status.Add(at);
        status.Add(evidence); status.Add(policy); status.Add(statusJson);
        status.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunStatus(p.GetValue(statusFile)!,
            p.GetValue(subject)!, p.GetValue(version)!, p.GetValue(at)!, p.GetValue(evidence) ?? [],
            p.GetValue(policy), p.GetValue(statusJson), Console.Out, Console.Error)));
        command.Add(status);
        return command;
    }

    internal static int RunStatus(string file, string subject, string version, string at, string[] selections,
        string? policyFile, bool json, TextWriter output, TextWriter error)
    {
        try
        {
            var catalogue = RequirementCatalogueFile.Read(file);
            var time = AefTime.Parse(at);
            var policy = policyFile is null ? null : TrustPolicy.Parse(RequirementCatalogueFile.ReadBytes(policyFile));
            if (selections.Length > 256) throw new RequirementStatusException("Expected: at most 256 runs. Actual: selection exceeds limit. Suggestions: use a bounded explicit selection. Because: evidence loading must be bounded.");
            var reader = new RequirementEvidenceReader();
            var evidence = selections.Select(value =>
            {
                if (value.Length < 66 || value[64] != '=') throw new RequirementStatusException("Expected: HASH=DIRECTORY. Actual: malformed evidence selection. Suggestions: provide the lowercase 64-character sealed hash and directory. Because: directory discovery is never implicit.");
                return reader.Read(new(value[65..], value[..64]), time, policy);
            }).ToArray();
            var report = new RequirementStatusResolver().Resolve(catalogue, new(subject, version), evidence,
                time, RequirementEvidenceReader.PolicyDigest(policy));
            if (json) output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            else
            {
                output.WriteLine($"Catalogue {report.CatalogueDigest}; policy {report.PolicyDigest}; at {report.At}; {subject}@{version}");
                foreach (var requirement in report.Requirements)
                {
                    output.WriteLine($"{requirement.Id}: {requirement.Status} (sealed: {requirement.BaseStatus}; effective: {requirement.EffectiveStatus})");
                    foreach (var diagnostic in requirement.Diagnostics) output.WriteLine($"  {diagnostic}");
                }
            }
            return report.Requirements.All(r => r.Status is "met" or "waived") ? ExitCodes.Success : ExitCodes.TestFailure;
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (json) output.WriteLine(JsonSerializer.Serialize(new { error = true, diagnostics = new[] { e.Message } }));
            else error.WriteLine($"Requirement status failed: {e.Message}");
            return ExitCodes.UsageError;
        }
    }

    internal static int RunValidate(string file, bool json, TextWriter output, TextWriter error)
    {
        try
        {
            var catalogue = RequirementCatalogueFile.Read(file);
            if (json) output.WriteLine(JsonSerializer.Serialize(new
            {
                valid = true, schemaVersion = catalogue.SchemaVersion, catalogueDigest = catalogue.Digest,
                requirements = catalogue.Requirements.Select(r => r.Id).ToArray(), diagnostics = Array.Empty<string>(),
            }));
            else output.WriteLine($"Valid requirement catalogue {catalogue.SchemaVersion}: {catalogue.Requirements.Count} requirements; SHA-256 {catalogue.Digest}.");
            return ExitCodes.Success;
        }
        catch (Exception e) when (e is RequirementCatalogueException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (json) output.WriteLine(JsonSerializer.Serialize(new { valid = false, diagnostics = new[] { e.Message } }));
            else error.WriteLine($"Catalogue validation failed: {e.Message}");
            return ExitCodes.UsageError;
        }
    }
}
