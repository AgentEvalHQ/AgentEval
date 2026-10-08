// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Checkpoints;

/// <summary>
/// One run folder of a <see cref="AefRunStore"/>: its <c>runId</c> (from run.json), its run hash ([SEAL-4]), and, read
/// when asked and then kept, the run verifier's report (§4.5) and the run's documents.
/// </summary>
public sealed class AefStoredRun
{
    private readonly TrustPolicy? _policy;
    private AefRunFolder? _folder;
    private AefRunHash? _runHash;
    private AefRunVerification? _verification;
    private AefRunDocuments? _documents;

    internal AefStoredRun(string directory, string relativePath, string runId, JsonObject run, TrustPolicy? policy)
    {
        Directory = directory;
        RelativePath = relativePath;
        RunId = runId;
        Run = run;
        _policy = policy;
    }

    /// <summary>The run folder.</summary>
    public string Directory { get; }

    /// <summary>The folder's path under the store's root, <c>/</c>-separated (empty for the root itself).</summary>
    public string RelativePath { get; }

    /// <summary>run.json's <c>runId</c>.</summary>
    public string RunId { get; }

    /// <summary>run.json as read: an I-JSON object, valid against the reader schema or not.</summary>
    public JsonObject Run { get; }

    /// <summary>
    /// The run's run hash ([SEAL-4]): its seal's <c>predicate.runHash</c> when seal.json is valid against the reader seal
    /// schema, else recomputed from its files.
    /// </summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public AefRunHash RunHash => _runHash ??= SealVerifier.RunHashOf(Folder);

    /// <summary>The run verifier's report, under the store's trust policy ([OVL-10]: an authorized redaction withholds a blob).</summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public AefRunVerification Verification => _verification ??= AefRunVerifier.Verify(Folder, new AefVerifyOptions { Policy = _policy });

    /// <summary>
    /// Intact (§4.5): the seal verifies and the run keeps the rules across files. A blob withheld by a redaction the trust
    /// policy authorizes leaves it intact; an unsealed run never is.
    /// </summary>
    public bool Intact => Verification.Outcome == AefOutcome.Intact;

    /// <summary>The run's documents as a verifier reads them (§3.9).</summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public AefRunDocuments Documents => _documents ??= AefRunDocuments.Read(Folder);

    /// <summary>
    /// When the run closed ([LANE-9]): run.json's <c>endedAt</c>, as written (the seal's <c>closedAt</c> is not read);
    /// null when there is none, or it is not a time ([ENC-8]).
    /// </summary>
    public string? ClosedAt => AefNode.Time(Run["endedAt"]) is null ? null : AefNode.String(Run["endedAt"]);

    private AefRunFolder Folder => _folder ??= AefRunFolder.Open(Directory);
}

/// <summary>
/// A folder of runs, found by their run.json ([RUN-1]: a run's identity is in run.json, never in its path), and a way to
/// find a run by its <c>runId</c> and run hash ([CKP-8], [STRM-4]). A folder that holds a run.json is a run and is not
/// searched further (a run.json under its <c>ext/</c> is a file of that run); links are never followed ([RUN-3]). A
/// run.json that is not an I-JSON object with a string <c>runId</c> names no run.
/// </summary>
/// <remarks>
/// Several folders may hold one <c>runId</c>: the same run copied (with the same run hash), or different runs that reuse
/// it ([RUN-13]). <see cref="Find"/> takes the folders with the run hash asked for, and the run is intact when any of them
/// is, whatever the order the folders are listed in. Run hashes and verifications are computed when first needed, once.
/// </remarks>
public sealed class AefRunStore
{
    private readonly Dictionary<string, List<AefStoredRun>> _byRunId;

    private AefRunStore(string root, IReadOnlyList<AefStoredRun> runs)
    {
        Root = root;
        Runs = runs;
        _byRunId = runs.GroupBy(r => r.RunId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    /// <summary>The folder of runs.</summary>
    public string Root { get; }

    /// <summary>Every run found, by the bytes of its folder's path under <see cref="Root"/>.</summary>
    public IReadOnlyList<AefStoredRun> Runs { get; }

    /// <summary>Finds the runs under <paramref name="root"/>.</summary>
    /// <param name="root">The folder of runs (or one run folder).</param>
    /// <param name="policy">The caller's trust policy, for redactions it authorizes ([OVL-10]), or null.</param>
    /// <exception cref="DirectoryNotFoundException">No such folder.</exception>
    /// <exception cref="IOException">The folder cannot be listed.</exception>
    public static AefRunStore Open(string root, TrustPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!System.IO.Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"{root}: not a folder.");
        }

        var runs = new List<AefStoredRun>();
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((root, ""));
        while (pending.TryPop(out var current))
        {
            var header = Path.Combine(current.Full, "run.json");
            if (File.Exists(header) && AefFolder.KindOf(header) == AefEntryKind.File)
            {
                if (ReadHeader(header) is { } run && AefNode.String(run["runId"]) is { } runId)
                {
                    runs.Add(new AefStoredRun(current.Full, current.Relative, runId, run, policy));
                }

                continue;   // a run folder holds one run
            }

            foreach (var sub in System.IO.Directory.EnumerateDirectories(current.Full))
            {
                if (AefFolder.KindOf(sub) == AefEntryKind.Folder)
                {
                    var name = Path.GetFileName(sub);
                    pending.Push((sub, current.Relative.Length == 0 ? name : $"{current.Relative}/{name}"));
                }
            }
        }

        return new AefRunStore(root, [.. runs.OrderBy(r => r.RelativePath, AefProblemOrder.Utf8)]);
    }

    /// <summary>Whether any folder's run.json has <paramref name="runId"/>.</summary>
    public bool Has(string runId) => runId is not null && _byRunId.ContainsKey(runId);

    /// <summary>The folders whose run.json has <paramref name="runId"/>, in path order.</summary>
    public IReadOnlyList<AefStoredRun> WithRunId(string runId) =>
        runId is not null && _byRunId.TryGetValue(runId, out var runs) ? runs : [];

    /// <summary>
    /// The run with <paramref name="runId"/> and run hash <paramref name="runHash"/> ([CKP-8]: found), or null when no
    /// folder holds it. When several folders do, an intact one is returned if there is one; otherwise the first in path
    /// order, which is not intact (it is found, and the verifier reports it as not intact).
    /// </summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public AefStoredRun? Find(string runId, string runHash)
    {
        var copies = WithRunId(runId).Where(r => string.Equals(r.RunHash.Value, runHash, StringComparison.Ordinal)).ToList();
        return copies.FirstOrDefault(r => r.Intact) ?? copies.FirstOrDefault();
    }

    /// <summary>The intact run with <paramref name="runId"/> and run hash <paramref name="runHash"/>, or null ([STRM-4]).</summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public AefStoredRun? FindIntact(string runId, string runHash) => Find(runId, runHash) is { Intact: true } run ? run : null;

    private static JsonObject? ReadHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > AefLimits.MaxJsonBytes)
            {
                return null;   // beyond [ENC-17]: not read ([ENC-18]), so it names no run here
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return AefJsonReader.ParseDocument(bytes);
        }
        catch (AefReadException)
        {
            return null;
        }
    }
}
