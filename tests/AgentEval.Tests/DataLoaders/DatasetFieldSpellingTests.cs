// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.DataLoaders;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.DataLoaders;

/// <summary>
/// Every loader reads a field in any spelling: <c>expected_output</c>, <c>expectedOutput</c>, <c>ExpectedOutput</c>,
/// <c>expected-output</c>. They used to read snake_case only, so a camelCase dataset lost its expected outputs without a
/// word (JSON kept them as metadata, YAML dropped them), and a test case with no expected output passes any answer.
/// </summary>
public sealed class DatasetFieldSpellingTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "agenteval-fieldnames-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private async Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string extension, string content)
    {
        var path = Path.Combine(_dir.FullName, $"cases-{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(path, content);
        return await DatasetLoaderFactory.CreateFromExtension(extension).LoadAsync(path);
    }

    public static TheoryData<string, string> ExpectedOutputInEachFormat(string key) => new()
    {
        { ".yaml", $"- input: Hi\n  {key}: Bonjour\n" },
        { ".json", $$"""[{"input": "Hi", "{{key}}": "Bonjour"}]""" },
        { ".jsonl", $$"""{"input": "Hi", "{{key}}": "Bonjour"}""" },
        { ".csv", $"input,{key}\nHi,Bonjour\n" },
    };

    public static TheoryData<string, string> CamelCase => ExpectedOutputInEachFormat("expectedOutput");
    public static TheoryData<string, string> PascalCase => ExpectedOutputInEachFormat("ExpectedOutput");
    public static TheoryData<string, string> Kebab => ExpectedOutputInEachFormat("expected-output");
    public static TheoryData<string, string> SnakeCase => ExpectedOutputInEachFormat("expected_output");

    [Theory]
    [MemberData(nameof(CamelCase))]
    [MemberData(nameof(PascalCase))]
    [MemberData(nameof(Kebab))]
    [MemberData(nameof(SnakeCase))]
    public async Task ExpectedOutput_IsReadInAnySpelling_AndIsNotLeftInMetadata(string extension, string content)
    {
        var c = Assert.Single(await LoadAsync(extension, content));

        Assert.Equal("Hi", c.Input);
        Assert.Equal("Bonjour", c.ExpectedOutput);
        Assert.Empty(c.Metadata);
    }

    [Fact]
    public async Task Yaml_EveryMultiWordField_InCamelCase_InsideATestCasesWrapper()
    {
        // `testCases:` is the wrapper the loader's own documentation shows; it used to be ignored too.
        var c = Assert.Single(await LoadAsync(".yaml", """
            testCases:
              - id: book
                input: Book a flight
                expectedOutput: Booked
                expectedTools: [search, book]
                evaluationCriteria: [Names the flight]
                passingScore: 80
                groundTruth:
                  name: book_flight
                  arguments:
                    to: LHR
            """));

        AssertAllFieldsRead(c);
    }

    [Fact]
    public async Task Json_EveryMultiWordField_InCamelCase_InsideATestCasesWrapper()
    {
        var c = Assert.Single(await LoadAsync(".json", """
            {"testCases": [{
              "id": "book", "input": "Book a flight", "expectedOutput": "Booked",
              "expectedTools": ["search", "book"], "evaluationCriteria": ["Names the flight"], "passingScore": 80,
              "groundTruth": {"name": "book_flight", "arguments": {"to": "LHR"}}
            }]}
            """));

        AssertAllFieldsRead(c);
    }

    [Fact]
    public async Task Csv_MultiWordHeaders_InCamelCase()
    {
        var c = Assert.Single(await LoadAsync(".csv",
            "id,input,expectedOutput,expectedTools,evaluationCriteria,passingScore\n" +
            "book,Book a flight,Booked,search|book,Names the flight,80\n"));

        Assert.Equal("Booked", c.ExpectedOutput);
        Assert.Equal(["search", "book"], c.ExpectedTools!);
        Assert.Equal(["Names the flight"], c.EvaluationCriteria!);
        Assert.Equal(80, c.PassingScore);
        Assert.Empty(c.Metadata);
    }

    private static void AssertAllFieldsRead(DatasetTestCase c)
    {
        Assert.Equal("book", c.Id);
        Assert.Equal("Booked", c.ExpectedOutput);
        Assert.Equal(["search", "book"], c.ExpectedTools!);
        Assert.Equal(["Names the flight"], c.EvaluationCriteria!);
        Assert.Equal(80, c.PassingScore);
        Assert.Equal("book_flight", c.GroundTruth!.Name);
        Assert.Equal("LHR", c.GroundTruth.Arguments["to"]);
        Assert.Empty(c.Metadata);
    }

    [Fact]
    public async Task Yaml_KeepsAnUnknownKeyAsMetadata_AsJsonDoes()
    {
        // YAML dropped every key it did not know, so a misspelled field vanished; JSON kept it as metadata.
        var yaml = Assert.Single(await LoadAsync(".yaml", "- input: Hi\n  owner: team-a\n"));
        var json = Assert.Single(await LoadAsync(".json", """[{"input": "Hi", "owner": "team-a"}]"""));

        Assert.Equal("team-a", yaml.Metadata["owner"]);
        Assert.Equal("team-a", json.Metadata["owner"]);
    }

    [Fact]
    public async Task Yaml_ASingleStringWhereAListIsExpected_IsAOneItemList()
    {
        var c = Assert.Single(await LoadAsync(".yaml", "- input: Hi\n  context: Paris is the capital of France\n"));

        Assert.Equal(["Paris is the capital of France"], c.Context!);
    }

    [Fact]
    public async Task Yaml_ASyntaxError_NamesTheLine()
    {
        // Was swallowed and reported as "must be an array of test cases or object with ...".
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(".yaml", "- input: [unclosed\n"));

        Assert.Contains("Invalid YAML", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at line 2", ex.Message, StringComparison.Ordinal);   // the flow sequence is still open at the end
    }

    [Fact]
    public async Task Yaml_APassingScoreThatIsNotAWholeNumber_IsRejectedByName()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAsync(".yaml", "- input: Hi\n  passingScore: high\n"));

        Assert.Contains("'passingScore'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jsonl_ALineThatIsNotAnObject_IsRejectedWithItsLineNumber()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAsync(".jsonl", "{\"input\": \"Hi\"}\n[1, 2]\n"));

        Assert.Contains("Line 2", ex.Message, StringComparison.Ordinal);
    }
}
