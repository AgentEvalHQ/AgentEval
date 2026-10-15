// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results;
using AgentEval.Results.Interop;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

internal static partial class AefCommand
{
    private static Command CreateCompanion()
    {
        var group = new Command("companion", "Bind an explicit artifact digest to sealed evidence; no execution or approval claim.");
        foreach (var create in new[] { true, false })
        {
            var run = new Argument<string>("run-dir");
            var digest = new Option<string>("--artifact-sha256") { Required = true, Description = "Explicit lowercase SHA-256 of the selected artifact." };
            var document = new Option<string>(create ? "--output" : "--document") { Required = true,
                Description = create ? "New companion directory outside the run; parent must exist." : "Companion JSON file." };
            var name = new Option<string>("--name") { DefaultValueFactory = _ => "_", Description = "Artifact display label." };
            var key = new Option<string?>("--key") { Description = "Optional PKCS#8 P-256 key; signs the new companion." };
            var envelope = new Option<string?>("--envelope") { Description = "Explicit companion DSSE envelope; no automatic discovery." };
            var requiredSigner = new Option<string?>("--require-signer") { Description = "Require this exact policy identity to sign the companion." };
            var policy = PolicyOption(); var json = JsonOption();
            var cmd = new Command(create ? "create" : "verify", create ? "Create an unsigned or signed companion bundle." : "Verify binding and report signature trust independently.");
            cmd.Add(run); cmd.Add(digest); cmd.Add(document); cmd.Add(policy); cmd.Add(json);
            if (create) { cmd.Add(name); cmd.Add(key); }
            else { cmd.Add(envelope); cmd.Add(requiredSigner); }
            cmd.SetAction((ParseResult p, CancellationToken ct) => Task.FromResult(create
                ? RunCompanionCreate(p.GetValue(run)!, p.GetValue(digest)!, p.GetValue(name)!, p.GetValue(document)!,
                    p.GetValue(key), p.GetValue(policy), p.GetValue(json), Console.Out, Console.Error, ct)
                : RunCompanionVerify(p.GetValue(run)!, p.GetValue(digest)!, p.GetValue(document)!, p.GetValue(envelope),
                    p.GetValue(policy), p.GetValue(requiredSigner), p.GetValue(json), Console.Out, Console.Error, ct)));
            group.Add(cmd);
        }
        return group;
    }

    internal static int RunCompanionCreate(string directory, string digest, string name, string output,
        string? key, string? policyPath, bool asJson, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        string? staging = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            directory = Path.GetFullPath(directory); output = Path.GetFullPath(output);
            var relative = Path.GetRelativePath(directory, output);
            if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw CompanionInput("output must be outside the selected run");
            var parent = Path.GetDirectoryName(output)!;
            CompanionOrdinaryParents(parent);
            if (Path.Exists(output)) throw CompanionInput("output already exists");
            IArtifactCompanionService service = new ArtifactCompanionService();
            var companion = service.Create(directory, name, digest, CompanionPolicy(policyPath, ct), ct);
            using var signer = key is null ? null : AefSigningKey.FromPkcs8Pem(new UTF8Encoding(false, true).GetString(CompanionRead(key, 64 * 1024, ct)));
            var signed = signer is null ? null : service.Sign(companion, signer);
            var candidate = Path.Combine(parent, ".ae-companion-" + Guid.NewGuid().ToString("N"));
            if (Path.Exists(candidate)) throw CompanionInput("staging directory already exists");
            Directory.CreateDirectory(candidate); staging = candidate;
            CompanionWrite(Path.Combine(staging, "artifact-evidence.json"), companion.ToBytes());
            if (signed is not null) CompanionWrite(Path.Combine(staging, "artifact-evidence.dsse.json"), signed);
            ct.ThrowIfCancellationRequested();
            CompanionOrdinaryParents(parent);
            Directory.Move(staging, output); staging = null;
            var result = new JsonObject { ["binding"] = "producer-asserted", ["output"] = output,
                ["artifactSha256"] = digest, ["runHash"] = companion.RunHash, ["signed"] = signed is not null };
            if (asJson) stdout.WriteLine(result.ToJsonString(s_json));
            else stdout.WriteLine($"Producer-asserted artifact binding created; {(signed is null ? "unsigned" : "signed (trust not evaluated)")}: {output}");
            return 0;
        }
        catch (Exception e) when (IsInputError(e) || e is NotSupportedException)
        {
            stderr.WriteLine(e.Message); return 2;
        }
        finally { if (staging is not null) Directory.Delete(staging, recursive: true); }
    }

    internal static int RunCompanionVerify(string directory, string digest, string document, string? envelope,
        string? policyPath, string? requiredSigner, bool asJson, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (requiredSigner is not null && (string.IsNullOrWhiteSpace(requiredSigner) || policyPath is null))
                throw CompanionInput("--require-signer needs a nonempty identity and --policy");
            var companion = ArtifactCompanion.Parse(CompanionRead(document, ArtifactCompanion.MaxDocumentBytes, ct));
            var report = new ArtifactCompanionService().Verify(companion, digest, Path.GetFullPath(directory),
                envelope is null ? null : CompanionRead(envelope, ArtifactCompanion.MaxEnvelopeBytes, ct), CompanionPolicy(policyPath, ct), ct);
            var identities = report.Signature?.VerifiesFor ?? [];
            var signerSatisfied = requiredSigner is null || identities.Contains(requiredSigner, StringComparer.Ordinal);
            var envelopeValid = report.Signature is null || (report.Signature.EnvelopeResult is null &&
                report.Signature.Signatures.All(s => s.Result is DsseSignatureResult.Verified or DsseSignatureResult.UntrustedKey));
            var result = new JsonObject
            {
                ["binding"] = "producer-asserted", ["intactBinding"] = report.IntactBinding,
                ["artifactMatches"] = report.ArtifactMatches, ["sealMatches"] = report.SealMatches,
                ["runHashMatches"] = report.RunHashMatches, ["evidenceOutcome"] = report.Evidence.Outcome.ToString().ToLowerInvariant(),
                ["withheld"] = report.Evidence.Withheld, ["policySupplied"] = report.PolicySupplied,
                ["signatureStatus"] = report.Signature is null ? "unsigned" : report.Signature.EnvelopeResult?.ToString() ?? (!envelopeValid ? "invalid" : identities.Count > 0 ? "verified" : "untrusted"),
                ["signatureChecks"] = new JsonArray((report.Signature?.Signatures ?? []).Select(s => (JsonNode?)new JsonObject
                { ["keyId"] = s.KeyId, ["result"] = s.Result.ToString(), ["identity"] = s.Identity }).ToArray()),
                ["verifiedSigners"] = new JsonArray(identities.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["requiredSignerSatisfied"] = signerSatisfied,
            };
            if (asJson) stdout.WriteLine(result.ToJsonString(s_json));
            else stdout.WriteLine($"Producer-asserted binding: {(report.IntactBinding ? "intact" : "mismatch or invalid evidence")}; signature: {result["signatureStatus"]}; required signer satisfied: {signerSatisfied}.");
            return report.IntactBinding && envelopeValid && signerSatisfied ? 0 : 1;
        }
        catch (Exception e) when (IsInputError(e)) { stderr.WriteLine(e.Message); return 2; }
    }

    private static TrustPolicy? CompanionPolicy(string? path, CancellationToken ct) =>
        path is null ? null : TrustPolicy.Parse(CompanionRead(path, 1024 * 1024, ct));

    private static void CompanionWrite(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(flushToDisk: true);
    }

    private static byte[] CompanionRead(string path, int cap, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); path = Path.GetFullPath(path);
        CompanionOrdinaryParents(Path.GetDirectoryName(path)!);
        if (AefFolder.KindOf(path) != AefEntryKind.File) throw CompanionInput("input must be an ordinary file");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = input.Read(chunk, 0, Math.Min(chunk.Length, cap + 1 - (int)buffer.Length));
            if (read == 0) return buffer.ToArray();
            buffer.Write(chunk, 0, read);
            if (buffer.Length > cap) throw CompanionInput("input exceeds its byte limit");
        }
    }

    private static void CompanionOrdinaryParents(string directory)
    {
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
            if (AefFolder.KindOf(current) != AefEntryKind.Folder) throw CompanionInput("path ancestors must be ordinary existing directories");
    }

    private static AefInputException CompanionInput(string actual) => new(
        $"Expected: bounded companion files outside sealed evidence. Actual: {actual}. Suggestions: select ordinary files and a new output directory. Because: evidence must remain unchanged.");
}
