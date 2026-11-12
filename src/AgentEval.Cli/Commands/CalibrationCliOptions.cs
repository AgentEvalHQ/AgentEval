// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentEval.Cli.Commands;

/// <summary>Calibration input selection; loading or recording labels never asserts human review.</summary>
internal sealed class CalibrationCliOptions
{
    private readonly JudgePromptCliOptions _prompts;
    private readonly Option<FileInfo?> _dataset = new("--dataset")
        { Description = "One family calibration JSONL dataset (at most 16 MiB); requires --out. Omit for embedded fixtures." };
    private readonly Option<bool> _surrogates = new("--allow-surrogate-rubrics")
        { Description = "With a prompt pack, explicitly allow synthetic compliance rubrics; their agreement does not calibrate production scenarios." };
    private readonly bool _compliance;

    internal CalibrationCliOptions(Command command, JudgePromptCliOptions prompts, bool compliance)
    {
        _prompts = prompts;
        command.Add(_dataset);
        _compliance = compliance;
        if (compliance) command.Add(_surrogates);
    }

    internal Selection Read(ParseResult parse) => new(parse.GetValue(_dataset), _prompts.Read(parse), _compliance && parse.GetValue(_surrogates));

    internal sealed record Selection(FileInfo? Dataset, JudgePromptCliOptions.Selection Prompts, bool AllowSurrogateRubrics = false)
    {
        internal void ValidateOutput(string? output)
        {
            if (output is null && (Dataset is not null || Prompts.Pack is not null))
                throw new ArgumentException("--dataset and --judge-prompt-pack require --out so a different calibration instrument cannot overwrite the default baseline report.");
        }
    }

    internal static async Task<(T Dataset, CalibrationDatasetIdentity Identity)> LoadAsync<T>(FileInfo file,
        Func<string, Stream, CancellationToken, Task<T>> load, CancellationToken ct)
    {
        await using var source = file.OpenRead();
        if (source.Length > 16 * 1024 * 1024)
            throw new ArgumentException("Calibration datasets must be at most 16 MiB.");
        var bytes = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(bytes, ct);
        if (source.ReadByte() != -1) throw new IOException("Calibration dataset changed while being read.");
        ValidateRows(bytes);
        var identity = new CalibrationDatasetIdentity("supplied-jsonl", Digest(bytes));
        using var snapshot = new MemoryStream(bytes, writable: false);
        var dataset = await load("supplied-" + identity.Digest[^12..], snapshot, ct);
        return (dataset, identity);
    }

    internal static CalibrationDatasetIdentity EmbeddedIdentity<T>(T datasets) =>
        new("embedded-fixtures", Digest(JsonSerializer.SerializeToUtf8Bytes(datasets)));

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateRows(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), new UTF8Encoding(false, true));
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (++count > 10000) throw new ArgumentException("Calibration datasets must contain at most 10,000 cases.");
            try
            {
                using var document = JsonDocument.Parse(line, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
                    throw new ArgumentException("Each calibration row must be an object with unique property names.");
                foreach (var field in new[] { "scenarioId", "input", "agentResponse", "expectedVerdict", "rationale" })
                    if (!row.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
                        || (field is "scenarioId" or "expectedVerdict" && string.IsNullOrWhiteSpace(value.GetString())))
                        throw new ArgumentException($"Calibration case {count} requires a string '{field}'.");
                var hasEvaluator = row.TryGetProperty("evaluatorKey", out var evaluator);
                var hasArticle = row.TryGetProperty("articleControlId", out var article);
                var key = hasEvaluator ? evaluator : article;
                if (hasEvaluator == hasArticle || key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()))
                    throw new ArgumentException($"Calibration case {count} requires exactly one evaluatorKey or articleControlId.");
                if (!row.TryGetProperty("expectedScoreMin", out var min) || !row.TryGetProperty("expectedScoreMax", out var max)
                    || min.ValueKind != JsonValueKind.Number || max.ValueKind != JsonValueKind.Number
                    || !min.TryGetDouble(out var low) || !max.TryGetDouble(out var high)
                    || !double.IsFinite(low) || !double.IsFinite(high) || low < 0 || high > 1 || low > high)
                    throw new ArgumentException($"Calibration case {count} requires finite expected scores with 0 <= min <= max <= 1.");
            }
            catch (JsonException ex) { throw new ArgumentException($"Calibration case {count} is not valid JSON.", ex); }
        }
        if (count == 0) throw new ArgumentException("Calibration datasets must contain at least one labelled case.");
    }
}

internal sealed record CalibrationDatasetIdentity(string Source, string Digest)
{
    internal void AppendMarkdownHeader(StringBuilder sb)
    {
        sb.AppendLine($"Dataset source: {Source}");
        sb.AppendLine();
        sb.AppendLine($"Dataset digest: `{Digest}`");
        sb.AppendLine();
    }
}
