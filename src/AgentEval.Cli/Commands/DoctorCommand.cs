// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>Implements the <c>agenteval doctor</c> command.</summary>
public static class DoctorCommand
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Runs the doctor command, discovering workspace root from the current directory.</summary>
    public static Task<int> RunAsync() =>
        RunAsync(rootOverride: null);

    /// <summary>Runs the doctor command with an optional explicit root override (used in tests).</summary>
    internal static async Task<int> RunAsync(string? rootOverride)
    {
        // Defense-in-depth canonicalisation for operator-supplied paths.
        if (rootOverride is not null)
        {
            var canonical = WorkspaceRootValidator.CanonicaliseOrNull(rootOverride);
            if (canonical is null) return 1;
            rootOverride = canonical;
        }

        var workspaceRoot = rootOverride ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        if (workspaceRoot is null)
        {
            Console.Error.WriteLine("✖ Could not find a solution root (.sln, .slnx, or .git).");
            return 1;
        }

        var dir = Path.Combine(workspaceRoot, ".agenteval");
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"✖ .agenteval/ not found at {dir}. Run `agenteval init` first.");
            return 1;
        }

        int errors = 0;
        int warnings = 0;
        int ok = 0;

        // ─── Validate solution.json ─────────────────────────────────────────
        // T1.6 (v1.1): in addition to the spot-checks below (which surface friendly
        // operator messages), also run the canonical schema-validator against the
        // embedded solution.schema.json. Catches malformed enums + missing required
        // properties the spot-checks don't enumerate.
        var solutionFile = Path.Combine(dir, "solution.json");
        if (!File.Exists(solutionFile))
        {
            Console.Error.WriteLine("✖ solution.json is missing.");
            errors++;
        }
        else
        {
            try
            {
                var doc = JsonDocument.Parse(await File.ReadAllTextAsync(solutionFile));
                var root2 = doc.RootElement;

                bool valid = true;
                if (!root2.TryGetProperty("schemaVersion", out _))
                {
                    Console.Error.WriteLine("✖ solution.json: missing 'schemaVersion' property.");
                    errors++;
                    valid = false;
                }
                if (!root2.TryGetProperty("id", out var idProp) ||
                    !Guid.TryParse(idProp.GetString(), out var parsedId) ||
                    parsedId == Guid.Empty)
                {
                    Console.Error.WriteLine("✖ solution.json: 'id' is missing or empty GUID.");
                    errors++;
                    valid = false;
                }
                if (!root2.TryGetProperty("name", out _))
                {
                    Console.Error.WriteLine("✖ solution.json: missing 'name' property.");
                    errors++;
                    valid = false;
                }

                // Schema validation (T1.6)
                var (schemaOk, schemaErr) = SchemaValidator.ValidateFile(solutionFile, "solution.schema.json");
                if (!schemaOk)
                {
                    Console.Error.WriteLine($"✖ solution.json: {schemaErr}");
                    errors++;
                    valid = false;
                }

                if (valid)
                {
                    Console.WriteLine("✔ solution.json OK");
                    ok++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"✖ solution.json: parse error: {ex.Message}");
                errors++;
            }
        }

        // ─── Validate subjects ──────────────────────────────────────────────
        var layout = new FileSystemLayout(dir);

        foreach (var kind in new[] { SubjectKind.Agent, SubjectKind.Workflow })
        {
            var kindDir = layout.SubjectKindDir(kind);
            if (!Directory.Exists(kindDir)) continue;

            foreach (var subjectDirPath in Directory.GetDirectories(kindDir))
            {
                var subjectName = Path.GetFileName(subjectDirPath);
                var subjectFile = Path.Combine(subjectDirPath, "subject.json");

                if (!File.Exists(subjectFile))
                {
                    Console.WriteLine($"  ⚠ subject.json missing for {subjectDirPath}");
                    warnings++;
                    continue;
                }

                // T1.6: schema validation for subject.json
                var (subjectSchemaOk, subjectSchemaErr) = SchemaValidator.ValidateFile(subjectFile, "subject.schema.json");
                if (!subjectSchemaOk)
                {
                    Console.Error.WriteLine($"✖ {subjectFile}: {subjectSchemaErr}");
                    errors++;
                }

                // Parse identity for use with ContentHasher
                SubjectIdentity? identity = null;
                try
                {
                    var subjectDoc = await ReadJsonAsync<SubjectFileV1>(subjectFile);
                    if (subjectDoc is not null)
                    {
                        if (!Enum.TryParse<SubjectKind>(subjectDoc.Kind, ignoreCase: true, out var parsedKind))
                            parsedKind = kind;
                        identity = new SubjectIdentity(parsedKind, subjectDoc.Name);

                        // Plan T4.3 check #2: subject.json#name matches its folder location
                        var sanitizedName = FileSystemLayout.Sanitize(subjectDoc.Name);
                        if (!string.Equals(sanitizedName, subjectName, StringComparison.Ordinal))
                        {
                            Console.Error.WriteLine($"✖ Subject name mismatch: subject.json#name='{subjectDoc.Name}' (sanitized='{sanitizedName}') but folder is '{subjectName}'.");
                            errors++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"✖ subject.json parse error for {subjectName}: {ex.Message}");
                    errors++;
                }

                // ─── Validate runs ───────────────────────────────────────────
                var runsDir = Path.Combine(subjectDirPath, "runs");
                if (!Directory.Exists(runsDir)) continue;

                foreach (var runDirPath in Directory.GetDirectories(runsDir))
                {
                    var runId = Path.GetFileName(runDirPath);
                    var manifestFile = Path.Combine(runDirPath, "manifest.json");

                    if (!File.Exists(manifestFile))
                    {
                        Console.WriteLine($"  ⚠ Orphan run: {runId} (manifest.json missing in {runDirPath})");
                        warnings++;
                        continue;
                    }

                    // T1.6: schema validation for manifest.json
                    var (manSchemaOk, manSchemaErr) = SchemaValidator.ValidateFile(manifestFile, "manifest.schema.json");
                    if (!manSchemaOk)
                    {
                        Console.Error.WriteLine($"✖ {manifestFile}: {manSchemaErr}");
                        errors++;
                    }

                    // T1.6: schema validation for summary.json (when present — runs that
                    // didn't complete may not have one, which is a separate warning above).
                    var summaryFile = Path.Combine(runDirPath, "summary.json");
                    if (File.Exists(summaryFile))
                    {
                        var (sumSchemaOk, sumSchemaErr) = SchemaValidator.ValidateFile(summaryFile, "summary.schema.json");
                        if (!sumSchemaOk)
                        {
                            Console.Error.WriteLine($"✖ {summaryFile}: {sumSchemaErr}");
                            errors++;
                        }
                    }

                    // T1.6 (v1.1): no schema validation for scenarios/*.json — the file shape
                    // on disk is the `ScenarioResult` wrapper (id/name/input/output/passed/...),
                    // not an EvalResult tree. `eval-result.schema.json` describes the EvalResult
                    // tree that may LIVE INSIDE `ScenarioResult.Output`; per T2.6, the production
                    // writer's EvalResult serialization is intentionally looser than the schema,
                    // so this validation was rejecting legitimate fixture data. The wrapper shape
                    // is enforced by C# typing at write time. A future v1.2 task may introduce a
                    // dedicated `scenario-result.schema.json` and re-enable validation here.

                    if (identity is not null)
                    {
                        try
                        {
                            var manifest = await ReadJsonAsync<RunManifestDto>(manifestFile);
                            if (manifest?.ContentHash is not null)
                            {
                                var matches = await ContentHasher.VerifyAsync(layout, identity, runId, manifest.ContentHash, CancellationToken.None);
                                if (!matches)
                                {
                                    Console.Error.WriteLine($"✖ Hash mismatch in run {runId} (subject: {subjectName}).");
                                    // T1.7 (v1.1) BREAKING-change diagnostic: pre-v1.1 workspaces had
                                    // their contentHash computed against raw scenario/summary/trace bytes.
                                    // v1.1's CanonicalJsonProjector hashes the canonical JSON projection
                                    // (alphabetic property order, no insignificant whitespace, deterministic
                                    // number encoding). Operators upgrading from v0.8.1-beta will trip
                                    // this on every legacy run — surface the upgrade path inline so they
                                    // don't think this is a tamper alert.
                                    Console.Error.WriteLine(
                                        "  If this workspace was created before AgentEval v1.1, the hash " +
                                        "algorithm changed (canonical-JSON projection). Re-run " +
                                        "`agenteval bench …` to regenerate the run; the new hash will " +
                                        "match thereafter. See CHANGELOG v1.1 for details.");
                                    errors++;
                                }
                                else
                                {
                                    ok++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"✖ Run {runId} manifest error: {ex.Message}");
                            errors++;
                        }
                    }
                }
            }
        }

        // ─── Compliance evidence audit chain (plan T4.3 check #4) ──────────
        var complianceDir = Path.Combine(dir, "compliance");
        if (Directory.Exists(complianceDir))
        {
            // Build a runId -> manifest cache so we don't re-read the same manifest repeatedly.
            var manifestHashCache = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var regDir in Directory.GetDirectories(complianceDir))
            {
                foreach (var subjDir in Directory.GetDirectories(regDir))
                {
                    foreach (var tsDir in Directory.GetDirectories(subjDir))
                    {
                        var evidenceFile = Path.Combine(tsDir, "evidence.json");
                        if (!File.Exists(evidenceFile)) continue;

                        // T1.6: schema validation for evidence.json. The base evidence.schema.json
                        // is the universal contract; regulation-specific schemas (gdpr-evidence.schema.json,
                        // eu-ai-act-evidence.schema.json) are additional and live in their owning
                        // assemblies. The base schema catches the missing-sourceRun / wrong-enum / etc.
                        // class of corruption that the spot-checks below don't enumerate.
                        var (evSchemaOk, evSchemaErr) = SchemaValidator.ValidateFile(evidenceFile, "evidence.schema.json");
                        if (!evSchemaOk)
                        {
                            Console.Error.WriteLine($"✖ {evidenceFile}: {evSchemaErr}");
                            errors++;
                        }

                        try
                        {
                            var ev = await ReadJsonAsync<EvidenceDto>(evidenceFile);
                            if (ev?.SourceRun is null)
                            {
                                Console.Error.WriteLine($"✖ {evidenceFile}: missing sourceRun.");
                                errors++;
                                continue;
                            }

                            var runId = ev.SourceRun.RunId;
                            if (!manifestHashCache.TryGetValue(runId, out var actualHash))
                            {
                                actualHash = await FindManifestHashAsync(layout, runId);
                                manifestHashCache[runId] = actualHash;
                            }

                            if (actualHash is null)
                            {
                                Console.Error.WriteLine($"✖ {evidenceFile}: source run {runId} not found.");
                                errors++;
                            }
                            else if (!string.Equals(actualHash, ev.SourceRun.ManifestHash, StringComparison.Ordinal))
                            {
                                Console.Error.WriteLine($"✖ {evidenceFile}: hash mismatch — source run {runId} has {actualHash}, evidence references {ev.SourceRun.ManifestHash}.");
                                errors++;
                            }
                            else
                            {
                                ok++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"✖ {evidenceFile}: parse error: {ex.Message}");
                            errors++;
                        }
                    }
                }
            }
        }

        // ─── Legacy path scan ───────────────────────────────────────────────
        foreach (var finding in LegacyPathScanner.Scan(workspaceRoot))
        {
            Console.Error.WriteLine($"✖ Legacy path: {finding.Path} - {finding.Reason}");
            errors++;
        }

        // ─── Summary ─────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"Errors: {errors} | Warnings: {warnings} | OK: {ok}");

        return errors > 0 ? 2 : 0;
    }

    private static async Task<T?> ReadJsonAsync<T>(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, s_json);
    }

    private static async Task<string?> FindManifestHashAsync(FileSystemLayout layout, string runId)
    {
        foreach (var kind in new[] { SubjectKind.Agent, SubjectKind.Workflow })
        {
            var kindDir = layout.SubjectKindDir(kind);
            if (!Directory.Exists(kindDir)) continue;
            foreach (var subjectDir in Directory.GetDirectories(kindDir))
            {
                var manifestFile = Path.Combine(subjectDir, "runs", runId, "manifest.json");
                if (!File.Exists(manifestFile)) continue;
                try
                {
                    var dto = await ReadJsonAsync<RunManifestDto>(manifestFile);
                    return dto?.ContentHash;
                }
                catch
                {
                    return null;
                }
            }
        }
        return null;
    }

    // Minimal DTO for reading subject.json kind+name fields.
    private sealed record SubjectFileV1(string? Kind, string Name);

    // Minimal DTO for reading manifest.json ContentHash field.
    private sealed record RunManifestDto(string? ContentHash);

    // Minimal DTO for evidence.json sourceRun audit chain.
    private sealed record EvidenceDto(SourceRunDto? SourceRun);

    private sealed record SourceRunDto(string RunId, string ManifestHash);
}
