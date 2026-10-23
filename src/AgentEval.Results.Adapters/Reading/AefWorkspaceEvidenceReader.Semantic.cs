// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;
using AgentEval.Output;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Reading;

public sealed partial class AefWorkspaceEvidenceReader
{
    /// <inheritdoc />
    public async Task<AefWorkspaceEvidence> ReadAsync(IOutputStoreReader store, long maxBytes = 256L << 20, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maxBytes is < 1 or > (1L << 30)) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        ct.ThrowIfCancellationRequested();
        if (store.WorkspaceRoot is { } root) return Read(root, maxBytes, ct);
        if (!store.IsAvailable) return new([]);
        var budget = new AefReadBudget(maxBytes);
        var discovery = new StoreV1Exporter.SemanticAccountant(new AefWriteBudget(maxBytes, ct, budget.Charge));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var subjectCount = 0;
        // Some recent-run indexes contain completed runs only. Enumerate the
        // manifest inventory so partial executions do not disappear.
        await foreach (var subject in store.ListSubjectsAsync(ct: ct).ConfigureAwait(false))
        {
            discovery.Account(subject);
            if (++subjectCount > 1000) throw Bad("The semantic store exceeds 1000 subjects.");
            await foreach (var manifest in store.ListRunsAsync(subject.Identity, ct).ConfigureAwait(false))
            {
                discovery.Account(manifest);
                if (!ids.Add(manifest.Run.RunId)) throw Bad("The semantic store returned ambiguous run IDs; an exact filesystem source is required.");
                if (ids.Count > 1000) throw Bad("The semantic store exceeds 1000 runs.");
            }
        }
        var runs = new List<AefWorkspaceRun>();
        foreach (var runId in ids.OrderBy(id => id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var temporary = Directory.CreateTempSubdirectory("ae-semantic-read-");
            try
            {
                var conversion = await StoreV1Exporter.ExportSemanticBoundedAsync(store, runId,
                    Path.Combine(temporary.FullName, "run"), ct, budget).ConfigureAwait(false);
                // All output writes, including sealing, were already charged.
                // The snapshot is still independently bounded and verified.
                var evidence = new AefRunEvidenceReader().Read(conversion.Directory, 64L << 20, ct);
                var sourceId = "semantic-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
                runs.Add(new(sourceId, evidence, []));
            }
            finally { temporary.Delete(recursive: true); }
        }
        ct.ThrowIfCancellationRequested();
        return new(runs);
    }
}
