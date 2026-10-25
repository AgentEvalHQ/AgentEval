// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>CLI-only compatibility boundary: transient report assembly unless legacy writes are explicitly enabled.</summary>
internal static class CliRunOutput
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static IOutputStore CreateStore(string root) => AefCliOptions.LegacyOutput
        ? new FileSystemOutputStore(root)
        : new InMemoryOutputStore();

    internal static Task SweepAsync(IOutputStore store, CancellationToken ct) => store is FileSystemOutputStore files
        ? files.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct)
        : Task.CompletedTask;

    internal static string ReportDirectory(IOutputStore store, string root, SubjectIdentity subject, string runId, string family)
    {
        if (store.WorkspaceRoot is not null) return store.ResolveRunDirectory(subject, runId);
        var path = Path.Combine(root, "reports", family, FileSystemLayout.Sanitize(subject.Name), runId);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static async Task WriteReportAsync<T>(string path, T report, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = File.Create(path);
        await JsonSerializer.SerializeAsync(output, report, ReportJson, ct);
    }

    internal static Task WriteComplianceReportAsync(string root, ComplianceEvidence report, CancellationToken ct) =>
        WriteReportAsync(new FileSystemLayout(root).ComplianceEvidenceFile(report.Regulation, report.Subject,
            report.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss")), report, ct);
}
