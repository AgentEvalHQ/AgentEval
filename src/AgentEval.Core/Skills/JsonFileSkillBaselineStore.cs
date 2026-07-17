// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Skills;

/// <summary>
/// File-system-backed <see cref="ISkillBaselineStore"/> — one JSON file per snapshot, NEVER overwritten,
/// under <c>{rootPath}/skills-baseline-{CapturedAt:yyyyMMddHHmmssfff}-{Id}.json</c>. Both the timestamp AND
/// the <see cref="SkillBaselineSnapshot.Id"/> are in the filename — timestamp alone (even to millisecond
/// precision) is not a safe uniqueness guarantee on a fast CI runner or in a tight test loop, which is the
/// exact reason <see cref="SkillBaselineSnapshot.Id"/> is a ULID/GUID and not itself timestamp-derived (see
/// its own remarks); the filename honors that same reasoning rather than silently reintroducing the
/// collision risk the Id field exists to avoid. <see cref="ListAsync"/> orders by
/// <see cref="SkillBaselineSnapshot.CapturedAt"/> (not filename) so the sort is correct even if a caller
/// ever renames files.
/// </summary>
public sealed class JsonFileSkillBaselineStore : ISkillBaselineStore
{
    private const string FilePrefix = "skills-baseline-";

    private readonly string _rootPath;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Creates a store rooted at <paramref name="rootPath"/> (default <c>.agenteval/skills-baselines</c>).</summary>
    public JsonFileSkillBaselineStore(string rootPath = ".agenteval/skills-baselines")
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        _rootPath = rootPath;
    }

    /// <inheritdoc/>
    public async Task<string> SaveAsync(SkillBaselineSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(_rootPath);
        var fileName = $"{FilePrefix}{snapshot.CapturedAt:yyyyMMddHHmmssfff}-{snapshot.Id}.json";
        var path = Path.Combine(_rootPath, fileName);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        return snapshot.Id;
    }

    /// <inheritdoc/>
    public async Task<SkillBaselineSnapshot?> LoadAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Directory.Exists(_rootPath))
        {
            return null;
        }

        foreach (var file in Directory.GetFiles(_rootPath, $"{FilePrefix}*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await TryReadAsync(file, ct).ConfigureAwait(false);
            if (snapshot?.Id == id)
            {
                return snapshot;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SkillBaselineSnapshot>> ListAsync(CancellationToken ct = default)
    {
        var results = new List<SkillBaselineSnapshot>();
        if (!Directory.Exists(_rootPath))
        {
            return results;
        }

        foreach (var file in Directory.GetFiles(_rootPath, $"{FilePrefix}*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await TryReadAsync(file, ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                results.Add(snapshot);
            }
        }

        return results.OrderBy(s => s.CapturedAt).ToList();
    }

    private static async Task<SkillBaselineSnapshot?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SkillBaselineSnapshot>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Tolerate a corrupt file rather than fail the whole load — same discipline as
            // JsonFileBaselineStore.TryDeserializeBaseline / JsonFileCalibrationReportStore.TryReadAsync.
            return null;
        }
    }
}
