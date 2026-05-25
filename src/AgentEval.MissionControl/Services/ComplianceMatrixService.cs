// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;

namespace AgentEval.MissionControl.Services;

/// <summary>
/// Builds the <see cref="ComplianceMatrix"/> for a regulation by walking all
/// stored evidence, grouping by (subject, control), and keeping the most recent
/// per cell. Plan-08 MC1.4.4.
/// </summary>
/// <remarks>
/// <para>
/// The on-disk compliance evidence layout (<c>compliance/{regulation}/{subject}/{ts}/evidence.json</c>)
/// keys evidence by subject NAME only — there's no kind in the path. To populate
/// <see cref="ComplianceMatrixSubject.Kind"/>, the service joins against
/// <see cref="IOutputStoreReader.ListSubjectsAsync"/>.
/// </para>
/// <para>
/// Audit-chain validity (<see cref="ComplianceMatrix.AllChainsValid"/>) currently
/// reports <c>true</c> when every loaded evidence's <c>SourceRun.ManifestHash</c>
/// matches its source run's content hash. Plan-07 §7 documents the SQLite generated-column
/// approach for Mode C; here in Mode A we compute it on the fly.
/// </para>
/// </remarks>
public sealed class ComplianceMatrixService
{
    private readonly IOutputStoreReader _store;

    public ComplianceMatrixService(IOutputStoreReader store)
    {
        _store = store;
    }

    /// <summary>Lists all regulations that have at least one evidence record.</summary>
    public async Task<IReadOnlyList<ComplianceRegulationSummary>> ListRegulationsAsync(
        CancellationToken ct = default)
    {
        if (!_store.IsAvailable) return Array.Empty<ComplianceRegulationSummary>();

        var pointers = new List<ComplianceEvidencePointer>();
        await foreach (var p in _store.ListComplianceEvidenceAsync(null, null, ct))
            pointers.Add(p);

        return pointers
            .GroupBy(p => p.Regulation, StringComparer.Ordinal)
            .Select(g =>
            {
                var subjects = g.Select(p => p.SubjectName).Distinct(StringComparer.Ordinal).Count();
                // Sort by parsed timestamp where possible; fall back to lexical
                // ordinal compare only when parse fails. The fixed
                // `yyyy-MM-dd_HH-mm-ss` format IS lexicographically sortable,
                // but parsing first guards against any future format drift.
                var sorted = g
                    .OrderByDescending(p => TryParseTimestamp(p.Timestamp) ?? DateTimeOffset.MinValue)
                    .ThenByDescending(p => p.Timestamp, StringComparer.Ordinal)
                    .ToList();
                var latest = sorted.First();
                var lastTs = TryParseTimestamp(latest.Timestamp);
                return new ComplianceRegulationSummary(
                    Regulation: g.Key,
                    SubjectCount: subjects,
                    EvidenceCount: sorted.Count,
                    LastEvidenceAt: lastTs,
                    OverallStatus: latest.OverallStatus);
            })
            .OrderBy(s => s.Regulation, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Builds the subject × control matrix for a single regulation, taking the
    /// most recent evidence per subject as the authoritative state for that subject's
    /// row. Returns an empty matrix when no evidence exists.
    /// </summary>
    public async Task<ComplianceMatrix> BuildMatrixAsync(
        string regulation,
        CancellationToken ct = default)
    {
        if (!_store.IsAvailable)
            return EmptyMatrix(regulation);
        // Untrusted segment — flows into FileSystemLayout.ComplianceDir.
        // Reject anything that could escape the workspace; return an empty
        // matrix instead so the SPA renders a graceful "no evidence" state.
        if (!FileSystemLayout.IsSafePathSegment(regulation))
            return EmptyMatrix(regulation);

        // Step 1 — collect all evidence pointers for the regulation.
        var pointers = new List<ComplianceEvidencePointer>();
        await foreach (var p in _store.ListComplianceEvidenceAsync(regulation, null, ct))
            pointers.Add(p);

        if (pointers.Count == 0)
            return EmptyMatrix(regulation);

        // Step 2 — for each subject, find the most recent evidence.
        // Order by parsed timestamp where possible; fall back to lexical
        // ordinal sort when parsing fails so the rule degrades gracefully
        // if any evidence ever lands in an unexpected format.
        var latestPerSubject = pointers
            .GroupBy(p => p.SubjectName, StringComparer.Ordinal)
            .Select(g => g
                .OrderByDescending(p => TryParseTimestamp(p.Timestamp) ?? DateTimeOffset.MinValue)
                .ThenByDescending(p => p.Timestamp, StringComparer.Ordinal)
                .First())
            .ToList();

        // Step 3 — load each latest evidence to read its full Controls list.
        // 2026-05-24 (plan-08 portal-review A1): capture the per-evidence chain-check
        // outcome (valid / source-run-not-found / hash-mismatch) so we can propagate
        // it down to per-cell granularity in step 6. The aggregate `allChainsValid`
        // remains for the matrix-header badge.
        var loaded = new List<(ComplianceEvidencePointer Pointer, ComplianceEvidence Evidence, bool ChainValid, string? ChainBreakReason)>();
        var allChainsValid = true;
        foreach (var pointer in latestPerSubject)
        {
            // ComplianceEvidence is keyed in the store by SubjectIdentity, so we need the kind.
            var subjectIdentity = await ResolveSubjectIdentityAsync(pointer.SubjectName, ct);
            if (subjectIdentity is null) continue;

            var evidence = await _store.GetComplianceEvidenceAsync(
                regulation, subjectIdentity, pointer.Timestamp, ct);
            if (evidence is null) continue;

            // Verify audit chain: SourceRun.ManifestHash must match the run's content hash.
            var manifest = await _store.GetRunManifestAsync(evidence.SourceRun.RunId, ct);
            bool chainValid;
            string? chainBreakReason;
            if (manifest is null)
            {
                chainValid = false;
                chainBreakReason = "source-run-not-found";
                allChainsValid = false;
            }
            else if (!string.Equals(manifest.ContentHash, evidence.SourceRun.ManifestHash, StringComparison.Ordinal))
            {
                chainValid = false;
                chainBreakReason = "hash-mismatch";
                allChainsValid = false;
            }
            else
            {
                chainValid = true;
                chainBreakReason = null;
            }

            loaded.Add((pointer, evidence, chainValid, chainBreakReason));
        }

        if (loaded.Count == 0)
            return EmptyMatrix(regulation);

        // Step 4 — derive the column inventory from the union of controls.
        var controlMap = new Dictionary<string, string>(StringComparer.Ordinal); // id -> title
        foreach (var (_, ev, _, _) in loaded)
        {
            foreach (var c in ev.Controls)
            {
                if (!controlMap.ContainsKey(c.Id))
                    controlMap[c.Id] = c.Title;
            }
        }
        var controls = controlMap
            .Select(kv => new ComplianceMatrixControl(kv.Key, kv.Value))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        // Step 5 — derive subject rows from the latest evidence's Subject.Kind.
        var subjects = loaded
            .Select(t => new ComplianceMatrixSubject(t.Evidence.Subject.Name, t.Evidence.Subject.Kind))
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        // Step 6 — build cells. Per-cell ChainValid + ChainBreakReason propagated from step 3.
        var cells = new List<ComplianceMatrixCell>(loaded.Count * controls.Count);
        foreach (var (pointer, ev, chainValid, chainBreakReason) in loaded)
        {
            var ts = TryParseTimestamp(pointer.Timestamp) ?? ev.GeneratedAt;
            foreach (var control in ev.Controls)
            {
                cells.Add(new ComplianceMatrixCell(
                    SubjectName: ev.Subject.Name,
                    ControlId: control.Id,
                    Status: control.Status,
                    PassRate: control.PassRate,
                    LastEvidenceAt: ts,
                    LastEvidenceRunId: ev.SourceRun.RunId,
                    Timestamp: pointer.Timestamp,
                    RegressedFromBaseline: control.RegressedFromBaseline,
                    ChainValid: chainValid,
                    ChainBreakReason: chainBreakReason));
            }
        }

        var lastEvidenceAt = loaded.Max(t => TryParseTimestamp(t.Pointer.Timestamp) ?? t.Evidence.GeneratedAt);

        return new ComplianceMatrix(
            Regulation: regulation,
            Subjects: subjects,
            Controls: controls,
            Cells: cells,
            AllChainsValid: allChainsValid,
            LastEvidenceAt: lastEvidenceAt);
    }

    /// <summary>
    /// Resolves a subject name to its full <see cref="SubjectIdentity"/> by scanning
    /// the subjects store. Compliance evidence on disk is keyed by name only; we need
    /// the Kind for store lookups.
    /// </summary>
    private async Task<SubjectIdentity?> ResolveSubjectIdentityAsync(string name, CancellationToken ct)
    {
        await foreach (var info in _store.ListSubjectsAsync(null, ct))
        {
            if (string.Equals(info.Identity.Name, name, StringComparison.Ordinal))
                return info.Identity;
        }
        return null;
    }

    private static ComplianceMatrix EmptyMatrix(string regulation) => new(
        Regulation: regulation,
        Subjects: Array.Empty<ComplianceMatrixSubject>(),
        Controls: Array.Empty<ComplianceMatrixControl>(),
        Cells: Array.Empty<ComplianceMatrixCell>(),
        AllChainsValid: true,
        LastEvidenceAt: null);

    private static DateTimeOffset? TryParseTimestamp(string timestamp)
    {
        // The on-disk timestamp format is "yyyy-MM-dd_HH-mm-ss" (per FileSystemOutputStore).
        // Try that first, then fall back to ISO8601 for forward compatibility.
        if (DateTimeOffset.TryParseExact(timestamp, "yyyy-MM-dd_HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        if (DateTimeOffset.TryParse(timestamp, out var iso))
            return iso;
        return null;
    }
}
