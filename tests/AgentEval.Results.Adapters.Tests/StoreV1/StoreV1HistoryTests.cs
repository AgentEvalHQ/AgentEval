// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.StoreV1;

public sealed class StoreV1HistoryTests : IDisposable
{
    private readonly string _output = AefTestRuns.TempPath("history-export");
    public void Dispose() => AefTestRuns.Delete(_output);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_HistoryNotesAndDuplicates_PreservesAssociatedRecordsInOrder(bool semantic)
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var entry = new HistoryEntry(source.RunId, DateTimeOffset.Parse("2026-10-23T12:30:00+02:00"), "ANNOTATED", 0.123,
            "Recorded note with a newline\nand quotes: \"kept\"");
        await source.Store.AppendHistoryEntryAsync(StoreV1Fixture.Subject, entry);
        await source.Store.AppendHistoryEntryAsync(StoreV1Fixture.Subject, entry);
        await source.Store.AppendHistoryEntryAsync(StoreV1Fixture.Subject, entry with { RunId = "another-run", Notes = "not this execution" });
        var conversion = semantic
            ? await StoreV1Exporter.ExportAsync(source.Store, source.RunId, _output)
            : StoreV1Exporter.ExportWorkspaceRun(source.Root, source.RunDirectory, _output);
        Directory.Delete(source.Root, recursive: true); // The converted artifact owns the recorded facts.
        var run = new AefRunEvidenceReader().Read(conversion.Directory);
        var bytes = run.CapturedContent("E-history");
        Assert.True(bytes.HasValue, "The selected run's recorded history must survive conversion.");
        using var json = JsonDocument.Parse(bytes!.Value);
        var rows = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length); // Automatic completion entry, then two explicit entries; no deduplication.
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.GetProperty("sourceOrdinal").GetInt32()));
        foreach (var row in rows.Skip(1))
        {
            var recorded = row.GetProperty("entry");
            Assert.Equal(source.RunId, recorded.GetProperty("runId").GetString());
            Assert.Equal(entry.Timestamp, recorded.GetProperty("timestamp").GetDateTimeOffset());
            Assert.Equal(entry.Score, recorded.GetProperty("score").GetDouble());
            Assert.Equal(entry.Verdict, recorded.GetProperty("verdict").GetString());
            Assert.Equal(entry.Notes, recorded.GetProperty("notes").GetString());
        }
        Assert.DoesNotContain("not this execution", json.RootElement.GetRawText());
    }

    [Fact]
    public async Task Export_CaptureOff_DoesNotWriteHistoryContent()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        await source.Store.AppendHistoryEntryAsync(StoreV1Fixture.Subject,
            new(source.RunId, DateTimeOffset.UtcNow, "PASS", 1, "HISTORY_NOTE_MUST_NOT_BE_CAPTURED"));
        StoreV1Exporter.ExportWorkspaceRun(source.Root, source.RunDirectory, _output, new() { ContentCapture = AefContentCapture.Off });
        var run = new AefRunEvidenceReader().Read(_output);
        var marker = run.Header.GetProperty("ext").GetProperty(StoreV1Exporter.ExtName).GetProperty("history");
        Assert.False(marker.GetProperty("captured").GetBoolean());
        Assert.Equal(2, marker.GetProperty("entries").GetInt32());
        Assert.Null(run.CapturedContent("E-history"));
        Assert.DoesNotContain("HISTORY_NOTE_MUST_NOT_BE_CAPTURED", string.Join("\n",
            Directory.EnumerateFiles(_output, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task Export_HistoryMissingRequiredScore_RefusesConstructorDefaults()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var history = new FileSystemLayout(source.Root).HistoryFile(StoreV1Fixture.Subject);
        await File.AppendAllTextAsync(history, "{\"runId\":\"missing-score\",\"timestamp\":\"2026-10-23T10:00:00Z\",\"verdict\":\"PASS\"}\n");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(source.Root, source.RunDirectory, _output));
    }
    [Theory]
    [InlineData("runId")]
    [InlineData("timestamp")]
    [InlineData("verdict")]
    [InlineData("score")]
    [InlineData("notes-type")]
    [InlineData("timestamp-without-offset")]
    public async Task Export_InvalidHistoryFacts_RefusesIncompleteIndex(string mutation)
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var raw = JsonNode.Parse("""{"runId":"recorded","timestamp":"2026-10-23T10:00:00Z","verdict":"PASS","score":0.1,"notes":null}""")!;
        if (mutation == "notes-type") raw["notes"] = 7;
        else if (mutation == "timestamp-without-offset") raw["timestamp"] = "2026-10-23T10:00:00";
        else raw.AsObject().Remove(mutation);
        await File.AppendAllTextAsync(new FileSystemLayout(source.Root).HistoryFile(StoreV1Fixture.Subject), raw.ToJsonString() + "\n");
        Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(source.Root, source.RunDirectory, _output));
    }

    [Fact]
    public async Task Export_HistoryPathIsDirectory_RefusesNonFileInput()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var path = new FileSystemLayout(source.Root).HistoryFile(StoreV1Fixture.Subject);
        File.Delete(path);
        Directory.CreateDirectory(path);
        var error = Assert.Throws<InvalidDataException>(() => StoreV1Exporter.ExportWorkspaceRun(source.Root, source.RunDirectory, _output));
        Assert.Contains("ordinary file", error.Message);
    }

    [Fact]
    public async Task ReadHistory_InsufficientBudget_RefusesBeforeAllocatingInput()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        var path = new FileSystemLayout(source.Root).HistoryFile(StoreV1Fixture.Subject);
        Assert.Throws<InvalidDataException>(() => StoreV1History.ReadFile(path, CancellationToken.None, new(1)));
    }

    [Fact]
    public async Task Export_HistoryFileAbsent_DoesNotInventEntries()
    {
        using var source = await StoreV1Fixture.CreateAsync();
        File.Delete(new FileSystemLayout(source.Root).HistoryFile(StoreV1Fixture.Subject));
        StoreV1Exporter.ExportWorkspaceRun(source.Root, source.RunDirectory, _output);
        Assert.Null(new AefRunEvidenceReader().Read(_output).CapturedContent("E-history"));
    }

}
