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
        // `testCases:` is the wrapper the loader's own documentation shows; the YAML loader used to reject it as a
        // file of the wrong shape.
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

    [Theory]
    [InlineData(".yaml", "- input: Hi\n  expected_output: A\n  expectedOutput: B\n")]
    [InlineData(".json", """[{"input": "Hi", "expected_output": "A", "expectedOutput": "B"}]""")]
    [InlineData(".json", """[{"input": "Hi", "expected_output": null, "expectedOutput": "B"}]""")]
    public async Task TwoSpellingsOfOneField_AreRejected(string extension, string content)
    {
        // One of the two would be lost, and which one would depend on the order of the keys (a null first one hid
        // the second, and the case passed any answer).
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(extension, content));

        Assert.Contains("same field in two spellings", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".yaml", "- input: Hi\n  expectedOutput: A\n  expected: B\n")]
    [InlineData(".json", """[{"input": "Hi", "expectedOutput": "A", "expected": "B"}]""")]
    public async Task Aliases_KeepTheirOrder_WhateverTheSpelling(string extension, string content)
    {
        // `expected` comes before `expected_output`, as it always did, whichever is written first.
        Assert.Equal("B", Assert.Single(await LoadAsync(extension, content)).ExpectedOutput);
    }

    [Theory]
    [InlineData(".yaml", "- input: What is 2+2?\n  expected: 4\n  passingScore: 90\n")]
    [InlineData(".yaml", "- input: What is 2+2?\n  expected: !!int 4\n  passingScore: !!int 90\n")]
    [InlineData(".json", """[{"input": "What is 2+2?", "expected": 4, "passingScore": 90}]""")]
    [InlineData(".json", """[{"input": "What is 2+2?", "expected": 4, "passingScore": "90"}]""")]
    public async Task ANumber_IsReadAsWritten(string extension, string content)
    {
        // JSON dropped `"expected": 4` (not a string), so the case passed any answer.
        var c = Assert.Single(await LoadAsync(extension, content));

        Assert.Equal("4", c.ExpectedOutput);
        Assert.Equal(90, c.PassingScore);
    }

    [Theory]
    [InlineData(".json", """[{"input": "Hi", "expected": ["a", "b"]}]""", "'expected' must be a single value")]
    [InlineData(".json", """[{"input": "Hi", "passingScore": 85.5}]""", "'passingScore' must be a whole number")]
    [InlineData(".jsonl", "{\"input\": \"Hi\"}\n{\"input\": \"Hi\", \"passingScore\": 85.5}\n", "Line 2")]
    [InlineData(".json", """[{"input": "Hi"}, "not a case"]""", "Test case 1")]
    [InlineData(".csv", "input,passing_score\nHi,high\n", "row 2")]
    public async Task AValueOfTheWrongShape_IsRejectedWithWhereItIs(string extension, string content, string where)
    {
        // Each of these used to be ignored (the field silently absent) or skipped, or threw a bare FormatException.
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(extension, content));

        Assert.Contains(where, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".yaml", "- input: Hi\n  expected: Paris\n  groundTruth: The capital of France is Paris.\n")]
    [InlineData(".json", """[{"input": "Hi", "expected": "Paris", "groundTruth": "The capital of France is Paris."}]""")]
    public async Task ATextGroundTruth_IsKeptAsMetadata_NotReadAsAToolCall(string extension, string content)
    {
        // `dataset init` wrote this through 0.43. The ground-truth field is an expected tool call; text there is a
        // reference answer by another name, so it is kept as metadata (as the JSON loader always did) and the file
        // still loads.
        var c = Assert.Single(await LoadAsync(extension, content));

        Assert.Null(c.GroundTruth);
        Assert.Equal("The capital of France is Paris.", c.Metadata["groundTruth"]);
    }

    [Fact]
    public async Task Yaml_MergeKeys_ApplyTheSharedDefaults()
    {
        var c = Assert.Single(await LoadAsync(".yaml", """
            defaults: &d
              expected_tools: [search]
              passing_score: 90
            test_cases:
              - <<: *d
                input: Find flights
            """));

        Assert.Equal(["search"], c.ExpectedTools!);
        Assert.Equal(90, c.PassingScore);
        Assert.Empty(c.Metadata);
    }

    [Fact]
    public async Task Yaml_AnEmptyWrapperKey_FallsThroughToTheNext()
    {
        var c = Assert.Single(await LoadAsync(".yaml", "test_cases:\ndata:\n  - input: Hi\n"));

        Assert.Equal("Hi", c.Input);
    }

    [Fact]
    public async Task Jsonl_ALineThatIsNotAnObject_IsRejectedWithItsLineNumber()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAsync(".jsonl", "{\"input\": \"Hi\"}\n[1, 2]\n"));

        Assert.Contains("Line 2", ex.Message, StringComparison.Ordinal);
    }
}
