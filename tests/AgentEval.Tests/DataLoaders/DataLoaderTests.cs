// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.DataLoaders;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.DataLoaders;

public class DataLoaderTests : IDisposable
{
    private readonly string _testDir;

    public DataLoaderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"AgentEval_DataLoaderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    #region Factory Tests

    [Theory]
    [InlineData(".jsonl", "jsonl")]
    [InlineData(".ndjson", "jsonl")]
    [InlineData(".json", "json")]
    [InlineData(".csv", "csv")]
    [InlineData(".tsv", "csv")]
    [InlineData(".yaml", "yaml")]
    [InlineData(".yml", "yaml")]
    public void Factory_CreateFromExtension_ReturnsCorrectLoader(string extension, string expectedFormat)
    {
        var loader = DatasetLoaderFactory.CreateFromExtension(extension);
        Assert.Equal(expectedFormat, loader.Format);
    }

    [Theory]
    [InlineData("jsonl")]
    [InlineData("json")]
    [InlineData("csv")]
    [InlineData("yaml")]
    public void Factory_Create_ReturnsCorrectLoader(string format)
    {
        var loader = DatasetLoaderFactory.Create(format);
        Assert.Equal(format, loader.Format);
    }

    [Fact]
    public void Factory_CreateFromExtension_ThrowsForUnknown()
    {
        Assert.Throws<ArgumentException>(() => DatasetLoaderFactory.CreateFromExtension(".xyz"));
    }

    [Fact]
    public void Factory_Create_ThrowsForUnknown()
    {
        Assert.Throws<ArgumentException>(() => DatasetLoaderFactory.Create("unknown"));
    }

    [Fact]
    public void Factory_Register_AllowsCustomLoader()
    {
        DatasetLoaderFactory.Register(".custom", () => new JsonlDatasetLoader());
        var loader = DatasetLoaderFactory.CreateFromExtension(".custom");
        Assert.NotNull(loader);
    }

    #endregion

    #region JSONL Loader Tests

    [Fact]
    public async Task JsonlLoader_LoadAsync_ParsesBasicTestCases()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"id": "test1", "input": "What is 2+2?", "expected": "4"}""",
            """{"id": "test2", "input": "Hello", "expected": "Hi there"}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Equal(2, results.Count);
        Assert.Equal("test1", results[0].Id);
        Assert.Equal("What is 2+2?", results[0].Input);
        Assert.Equal("4", results[0].ExpectedOutput);
        Assert.Equal("test2", results[1].Id);
    }

    [Fact]
    public async Task JsonlLoader_LoadAsync_HandlesAlternativeFieldNames()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"question": "What is AI?", "answer": "Artificial Intelligence"}""",
            """{"prompt": "Translate hello", "expected_output": "Hola"}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Equal(2, results.Count);
        Assert.Equal("What is AI?", results[0].Input);
        Assert.Equal("Artificial Intelligence", results[0].ExpectedOutput);
        Assert.Equal("Translate hello", results[1].Input);
        Assert.Equal("Hola", results[1].ExpectedOutput);
    }

    [Fact]
    public async Task JsonlLoader_LoadAsync_ParsesContext()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"input": "What color is the sky?", "context": ["The sky is blue on clear days."]}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].Context);
        var context = results[0].Context!;
        Assert.Single(context);
        Assert.Equal("The sky is blue on clear days.", context[0]);
    }

    [Fact]
    public async Task JsonlLoader_LoadAsync_ParsesExpectedTools()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"input": "Get weather", "expected_tools": ["get_weather", "format_response"]}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].ExpectedTools);
        Assert.Equal(2, results[0].ExpectedTools!.Count);
        Assert.Equal("get_weather", results[0].ExpectedTools![0]);
    }

    [Fact]
    public async Task JsonlLoader_LoadAsync_ParsesGroundTruth()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"input": "Weather in Seattle", "ground_truth": {"name": "get_weather", "arguments": {"city": "Seattle"}}}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].GroundTruth);
        Assert.Equal("get_weather", results[0].GroundTruth!.Name);
        Assert.Equal("Seattle", results[0].GroundTruth!.Arguments["city"]);
    }

    [Fact]
    public async Task JsonlLoader_LoadAsync_ParsesBfclStyleFormat()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"input": "Search for pizza", "function": "web_search", "arguments": {"query": "pizza"}}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].GroundTruth);
        Assert.Equal("web_search", results[0].GroundTruth!.Name);
        Assert.Equal("pizza", results[0].GroundTruth!.Arguments["query"]);
    }

    [Fact]
    public async Task JsonlLoader_LoadAsync_CollectsMetadata()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"input": "Test", "difficulty": "hard", "source": "benchmark"}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("hard", results[0].Metadata["difficulty"]);
        Assert.Equal("benchmark", results[0].Metadata["source"]);
    }

    [Fact]
    public async Task JsonlLoader_LoadAsync_SkipsEmptyLines()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllText(filePath, """
            {"id": "test1", "input": "First"}

            {"id": "test2", "input": "Second"}

            """);

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task JsonlLoader_LoadAsync_ThrowsForInvalidJson()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"id": "test1", "input": "Valid"}""",
            """not valid json"""
        });

        var loader = new JsonlDatasetLoader();
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(filePath));
    }

    [Fact]
    public async Task JsonlLoader_LoadAsync_ThrowsForMissingFile()
    {
        var loader = new JsonlDatasetLoader();
        await Assert.ThrowsAsync<FileNotFoundException>(() => 
            loader.LoadAsync(Path.Combine(_testDir, "nonexistent.jsonl")));
    }

    [Fact]
    public async Task JsonlLoader_LoadStreamingAsync_YieldsItems()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"id": "test1", "input": "First"}""",
            """{"id": "test2", "input": "Second"}""",
            """{"id": "test3", "input": "Third"}"""
        });

        var loader = new JsonlDatasetLoader();
        var count = 0;
        await foreach (var item in loader.LoadStreamingAsync(filePath))
        {
            count++;
            Assert.StartsWith("test", item.Id);
        }

        Assert.Equal(3, count);
    }

    #endregion

    #region JSON Loader Tests

    [Fact]
    public async Task JsonLoader_LoadAsync_ParsesArray()
    {
        var filePath = Path.Combine(_testDir, "test.json");
        File.WriteAllText(filePath, """
            [
                {"id": "test1", "input": "Question 1", "expected": "Answer 1"},
                {"id": "test2", "input": "Question 2", "expected": "Answer 2"}
            ]
            """);

        var loader = new JsonDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Equal(2, results.Count);
        Assert.Equal("test1", results[0].Id);
        Assert.Equal("Question 1", results[0].Input);
    }

    [Fact]
    public async Task JsonLoader_LoadAsync_ParsesObjectWithDataProperty()
    {
        var filePath = Path.Combine(_testDir, "test.json");
        File.WriteAllText(filePath, """
            {
                "metadata": {"version": "1.0"},
                "data": [
                    {"id": "test1", "input": "Q1", "expected": "A1"}
                ]
            }
            """);

        var loader = new JsonDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("test1", results[0].Id);
    }

    [Fact]
    public async Task JsonLoader_LoadAsync_ParsesObjectWithTestCasesProperty()
    {
        var filePath = Path.Combine(_testDir, "test.json");
        File.WriteAllText(filePath, """
            {
                "testCases": [
                    {"id": "test1", "input": "Q1", "expected": "A1"}
                ]
            }
            """);

        var loader = new JsonDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
    }

    [Fact]
    public async Task JsonLoader_LoadAsync_ParsesObjectWithExamplesProperty()
    {
        var filePath = Path.Combine(_testDir, "test.json");
        File.WriteAllText(filePath, """
            {
                "examples": [
                    {"query": "What time is it?", "response": "The current time is..."}
                ]
            }
            """);

        var loader = new JsonDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("What time is it?", results[0].Input);
        Assert.Equal("The current time is...", results[0].ExpectedOutput);
    }

    [Fact]
    public async Task JsonLoader_LoadAsync_ThrowsForInvalidRoot()
    {
        var filePath = Path.Combine(_testDir, "test.json");
        File.WriteAllText(filePath, """
            {
                "wrongProperty": [{"id": "test1"}]
            }
            """);

        var loader = new JsonDatasetLoader();
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(filePath));
    }

    [Fact]
    public async Task JsonLoader_LoadAsync_ThrowsForMissingFile()
    {
        var loader = new JsonDatasetLoader();
        await Assert.ThrowsAsync<FileNotFoundException>(() => 
            loader.LoadAsync(Path.Combine(_testDir, "nonexistent.json")));
    }

    #endregion

    #region CSV Loader Tests

    [Fact]
    public async Task CsvLoader_LoadAsync_ParsesBasicCsv()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,expected",
            "test1,What is 2+2?,4",
            "test2,Hello,Hi there"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Equal(2, results.Count);
        Assert.Equal("test1", results[0].Id);
        Assert.Equal("What is 2+2?", results[0].Input);
        Assert.Equal("4", results[0].ExpectedOutput);
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_HandlesQuotedFields()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,expected",
            "test1,\"Hello, world\",\"Greeting with comma\""
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("Hello, world", results[0].Input);
        Assert.Equal("Greeting with comma", results[0].ExpectedOutput);
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_HandlesEscapedQuotes()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,expected",
            "test1,\"Say \"\"hello\"\"\",Response"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("Say \"hello\"", results[0].Input);
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_HandlesAlternativeColumnNames()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllLines(filePath, new[]
        {
            "question,answer",
            "What is AI?,Artificial Intelligence"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("What is AI?", results[0].Input);
        Assert.Equal("Artificial Intelligence", results[0].ExpectedOutput);
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_ParsesExpectedToolsPipeSeparated()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,expected_tools",
            "test1,Get weather,get_weather|format_response"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].ExpectedTools);
        Assert.Equal(2, results[0].ExpectedTools!.Count);
        Assert.Equal("get_weather", results[0].ExpectedTools![0]);
        Assert.Equal("format_response", results[0].ExpectedTools![1]);
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_CollectsExtraColumnsAsMetadata()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,difficulty,source",
            "test1,Question,hard,benchmark"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("hard", results[0].Metadata["difficulty"]);
        Assert.Equal("benchmark", results[0].Metadata["source"]);
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_SkipsEmptyRows()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllText(filePath, """
            id,input,expected
            test1,First,A1

            test2,Second,A2

            """);

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_SkipsRowsWithoutInput()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,expected",
            "test1,Valid input,Answer",
            "test2,,No input here"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("test1", results[0].Id);
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_ThrowsForMissingInputColumn()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,something,other",
            "test1,value,value"
        });

        var loader = new CsvDatasetLoader();
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(filePath));
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_ThrowsForEmptyFile()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllText(filePath, "");

        var loader = new CsvDatasetLoader();
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(filePath));
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_ThrowsForMissingFile()
    {
        var loader = new CsvDatasetLoader();
        await Assert.ThrowsAsync<FileNotFoundException>(() => 
            loader.LoadAsync(Path.Combine(_testDir, "nonexistent.csv")));
    }

    [Fact]
    public async Task CsvLoader_LoadAsync_HandlesTsvAutoDetection()
    {
        var filePath = Path.Combine(_testDir, "test.tsv");
        File.WriteAllLines(filePath, new[]
        {
            "id\tinput\texpected",
            "test1\tQuestion 1\tAnswer 1"
        });

        // TSV auto-detects from extension
        var loader = DatasetLoaderFactory.CreateFromExtension(".tsv");
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("Question 1", results[0].Input);
    }

    #endregion

    #region YAML Loader Tests

    [Fact]
    public async Task YamlLoader_LoadAsync_ParsesArrayFormat()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, """
            - id: test1
              input: What is 2+2?
              expected: "4"
            - id: test2
              input: Hello
              expected: Hi there
            """);

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Equal(2, results.Count);
        Assert.Equal("test1", results[0].Id);
        Assert.Equal("What is 2+2?", results[0].Input);
        Assert.Equal("4", results[0].ExpectedOutput);
        Assert.Equal("test2", results[1].Id);
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ParsesObjectWithTestCases()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, """
            metadata:
              version: 1.0
            test_cases:
              - id: test1
                input: What is AI?
                expected: Artificial Intelligence
            """);

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("test1", results[0].Id);
        Assert.Equal("What is AI?", results[0].Input);
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ParsesObjectWithData()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, "data:\n  - id: data1\n    input: From data\n    expected: Value 1\n");

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("data1", results[0].Id);
        Assert.Equal("From data", results[0].Input);
        Assert.Equal("Value 1", results[0].ExpectedOutput);
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ParsesObjectWithExamples()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, "examples:\n  - id: ex1\n    input: From examples\n    expected: Value 2\n");

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("ex1", results[0].Id);
        Assert.Equal("From examples", results[0].Input);
        Assert.Equal("Value 2", results[0].ExpectedOutput);
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ParsesObjectWithSamples()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, "samples:\n  - id: samp1\n    input: From samples\n    expected: Value 3\n");

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("samp1", results[0].Id);
        Assert.Equal("From samples", results[0].Input);
        Assert.Equal("Value 3", results[0].ExpectedOutput);
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_HandlesAlternativeFieldNames()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, """
            - question: What is ML?
              answer: Machine Learning
            - prompt: Explain AI
              response: AI stands for Artificial Intelligence
            """);

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Equal(2, results.Count);
        Assert.Equal("What is ML?", results[0].Input);
        Assert.Equal("Machine Learning", results[0].ExpectedOutput);
        Assert.Equal("Explain AI", results[1].Input);
        Assert.Equal("AI stands for Artificial Intelligence", results[1].ExpectedOutput);
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ParsesContextAndTools()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, """
            - id: rag_test
              input: What is the capital?
              context:
                - France is a country in Europe
                - Paris is the capital of France
              expected_tools:
                - search
                - lookup
            """);

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal(2, results[0].Context!.Count);
        Assert.Equal(2, results[0].ExpectedTools!.Count);
        Assert.Contains("search", results[0].ExpectedTools!);
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ParsesGroundTruth()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, """
            - id: func_test
              input: Book a flight to Paris
              ground_truth:
                name: book_flight
                arguments:
                  destination: Paris
            """);

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].GroundTruth);
        Assert.Equal("book_flight", results[0].GroundTruth!.Name);
        Assert.Equal("Paris", results[0].GroundTruth!.Arguments["destination"]);
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ParsesFunctionArgumentsGroundTruth()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, """
            - id: func_args_test
              input: Book a hotel
              function: book_hotel
              arguments:
                city: Madrid
                nights: 3
            """);

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].GroundTruth);
        Assert.Equal("book_hotel", results[0].GroundTruth!.Name);
        Assert.Equal("Madrid", results[0].GroundTruth!.Arguments["city"]);
        Assert.Equal("3", results[0].GroundTruth!.Arguments["nights"]?.ToString());
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ParsesMetadata()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, """
            - id: meta_test
              input: With metadata
              expected: ok
              metadata:
                priority: high
                tags:
                  - a
                  - b
            """);

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.True(results[0].Metadata.ContainsKey("priority"));
        Assert.Equal("high", results[0].Metadata["priority"]);
        Assert.True(results[0].Metadata.ContainsKey("tags"));
    }

    [Fact]
    public async Task YamlLoader_LoadStreamingAsync_YieldsItems()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, """
            - id: test1
              input: Question 1
            - id: test2
              input: Question 2
            """);

        var loader = new YamlDatasetLoader();
        var count = 0;
        await foreach (var item in loader.LoadStreamingAsync(filePath))
        {
            count++;
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ThrowsForMissingFile()
    {
        var loader = new YamlDatasetLoader();
        await Assert.ThrowsAsync<FileNotFoundException>(() => 
            loader.LoadAsync(Path.Combine(_testDir, "nonexistent.yaml")));
    }

    [Fact]
    public async Task YamlLoader_LoadAsync_ThrowsForInvalidFormat()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, "just a plain string");

        var loader = new YamlDatasetLoader();
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(filePath));
    }

    #endregion
}
