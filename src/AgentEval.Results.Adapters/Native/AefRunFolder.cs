// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;

namespace AgentEval.Results.Adapters.Native;

/// <summary>
/// A run folder this writer owns: <c>runsRoot/yyyy/MM/&lt;runId&gt;</c>, with <c>runId</c> =
/// <c>yyyyMMddTHHmmssZ-&lt;kind&gt;-&lt;16 hex&gt;</c>. Ownership is a lock file beside the folder, created with
/// <see cref="FileMode.CreateNew"/>, which only one process can do: two writers that draw the same id cannot share (and
/// then discard) one run; the loser draws again. Disposing releases the lock (it deletes itself on close).
/// </summary>
internal sealed class AefRunFolder : IDisposable
{
    private readonly FileStream _lock;

    private AefRunFolder(string runId, string directory, FileStream @lock)
    {
        RunId = runId;
        Directory = directory;
        _lock = @lock;
    }

    /// <summary>The run's id.</summary>
    public string RunId { get; }

    /// <summary>The run's folder, not yet created: <c>AefRunWriter.Create</c> creates it.</summary>
    public string Directory { get; }

    /// <summary>Reserves a fresh run folder for a run of <paramref name="kind"/> that started at <paramref name="startedAt"/>.</summary>
    public static AefRunFolder Reserve(string runsRoot, DateTimeOffset startedAt, string kind)
    {
        var utc = startedAt.UtcDateTime;
        var parent = Path.Combine(runsRoot, $"{utc:yyyy}", $"{utc:MM}");
        System.IO.Directory.CreateDirectory(parent);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var runId = $"{utc:yyyyMMddTHHmmssZ}-{kind}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
            var directory = Path.Combine(parent, runId);
            FileStream reservation;
            try
            {
                reservation = new FileStream(directory + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (File.Exists(directory + ".lock"))
            {
                continue;   // another writer holds this id
            }

            if (System.IO.Directory.Exists(directory))
            {
                reservation.Dispose();   // an earlier run has it
                continue;
            }

            return new AefRunFolder(runId, directory, reservation);
        }

        throw new IOException($"Could not reserve a fresh run folder under {parent}.");
    }

    /// <inheritdoc/>
    public void Dispose() => _lock.Dispose();
}
