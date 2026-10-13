// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.DataLoaders;

namespace AgentEval.Tests.DataLoaders;

public sealed class DatasetSnapshotTests
{
    public static IEnumerable<object[]> Formats()
    {
        yield return [".json", "[{\"id\":\"  original/α  \",\"input\":\"question\",\"evaluationCriteria\":[\"correct\"]}]"];
        yield return [".jsonl", "{\"id\":\"  original/α  \",\"input\":\"question\",\"evaluationCriteria\":[\"correct\"]}\n"];
        yield return [".ndjson", "{\"id\":\"  original/α  \",\"input\":\"question\",\"evaluationCriteria\":[\"correct\"]}\n"];
        yield return [".yaml", "- id: '  original/α  '\n  input: question\n  evaluationCriteria: [correct]\n"];
        yield return [".yml", "- id: '  original/α  '\n  input: question\n  evaluationCriteria: [correct]\n"];
        yield return [".csv", "id,input,evaluationCriteria\n\"  original/α  \",question,correct\n"];
        yield return [".tsv", "id\tinput\tevaluationCriteria\n\"  original/α  \"\tquestion\tcorrect\n"];
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task LoadSnapshotAsync_BuiltInFormat_PreservesIdAndCommonFields(string extension, string content)
    {
        var item = Assert.Single(await DatasetLoaderFactory.LoadSnapshotAsync(Encoding.UTF8.GetBytes(content), extension, true));
        Assert.Equal("  original/α  ", item.Id);
        Assert.Equal("question", item.Input);
        Assert.Equal("correct", Assert.Single(item.EvaluationCriteria!));
    }

    [Theory]
    [InlineData(".json", "[{\"input\":\"question\"}]")]
    [InlineData(".jsonl", "{\"input\":\"question\"}")]
    [InlineData(".yaml", "- input: question")]
    [InlineData(".csv", "input\nquestion\n")]
    public async Task LoadSnapshotAsync_MissingId_RefusesInsteadOfSynthesizing(string extension, string content)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => DatasetLoaderFactory.LoadSnapshotAsync(
            Encoding.UTF8.GetBytes(content), extension, true));
        var permissive = await DatasetLoaderFactory.LoadSnapshotAsync(Encoding.UTF8.GetBytes(content), extension);
        Assert.False(string.IsNullOrEmpty(Assert.Single(permissive).Id));
    }

    [Theory]
    [InlineData(".json", "[{\"id\":\"same\",\"input\":\"one\"},{\"id\":\"same\",\"input\":\"two\"}]")]
    [InlineData(".jsonl", "{\"id\":\"same\",\"input\":\"one\"}\n{\"id\":\"same\",\"input\":\"two\"}")]
    [InlineData(".yaml", "- id: same\n  input: one\n- id: same\n  input: two")]
    [InlineData(".csv", "id,input\nsame,one\nsame,two\n")]
    public async Task LoadSnapshotAsync_DuplicateIds_Refuses(string extension, string content)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => DatasetLoaderFactory.LoadSnapshotAsync(
            Encoding.UTF8.GetBytes(content), extension, true));
    }

    [Theory]
    [InlineData("id,input\ncase,\n")]
    [InlineData("id,id,input\na,b,question\n")]
    [InlineData("id,input\n\"unfinished,question\n")]
    [InlineData("id,input\nab\"cd\",question\n")]
    [InlineData("id,input\n\"ab\"cd,question\n")]
    public async Task LoadSnapshotAsync_AmbiguousCsv_RefusesRatherThanDroppingOrRewriting(string content)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => DatasetLoaderFactory.LoadSnapshotAsync(
            Encoding.UTF8.GetBytes(content), ".csv", true));
    }

    [Fact]
    public async Task LoadSnapshotAsync_BomAndCaseInsensitiveExtension_LoadsWithoutAFile()
    {
        var bytes = Encoding.UTF8.GetBytes("\uFEFF[{\"id\":\"case\",\"input\":\"question\"}]");
        var item = Assert.Single(await DatasetLoaderFactory.LoadSnapshotAsync(bytes, ".JSON", true));
        Array.Fill(bytes, (byte)0);
        Assert.Equal("case", item.Id);
        Assert.Equal("question", item.Input);
    }

    [Fact]
    public async Task LoadSnapshotAsync_InvalidUtf8_RefusesInsteadOfReplacingBytes()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => DatasetLoaderFactory.LoadSnapshotAsync(
            new byte[] { 0xff, 0xfe }, ".json", true));
    }

    [Fact]
    public async Task LoadSnapshotAsync_Cancelled_DoesNotParse()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatasetLoaderFactory.LoadSnapshotAsync(
            Encoding.UTF8.GetBytes("invalid"), ".json", true, new CancellationToken(true)));
    }
}
