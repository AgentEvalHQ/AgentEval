// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.DataLoaders;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.DataLoaders;

/// <summary>
/// Additional tests covering gaps identified during review:
/// - IsTrulyStreaming property values (G6)
/// - DefaultDatasetLoaderFactory via DI patterns (G2 fix verification)
/// - DatasetLoaderFactory.LoadAsync/LoadStreamingAsync convenience methods (G8)
/// - CSV ground_truth JSON blob and passing_score column parsing (G9)
/// - JSONL "documents" and "tools" alias parity (G1 fix verification)
/// </summary>
public class DataLoaderGapCoverageTests : IDisposable
{
    private readonly string _testDir;

    public DataLoaderGapCoverageTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"AgentEval_GapTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    #region G6: IsTrulyStreaming property values

    [Fact]
    public void JsonlLoader_IsTrulyStreaming_ReturnsTrue()
    {
        var loader = new JsonlDatasetLoader();
        Assert.True(loader.IsTrulyStreaming);
    }

    [Fact]
    public void CsvLoader_IsTrulyStreaming_ReturnsTrue()
    {
        var loader = new CsvDatasetLoader();
        Assert.True(loader.IsTrulyStreaming);
    }

    [Fact]
    public void JsonLoader_IsTrulyStreaming_ReturnsFalse()
    {
        var loader = new JsonDatasetLoader();
        Assert.False(loader.IsTrulyStreaming);
    }

    [Fact]
    public void YamlLoader_IsTrulyStreaming_ReturnsFalse()
    {
        var loader = new YamlDatasetLoader();
        Assert.False(loader.IsTrulyStreaming);
    }

    [Theory]
    [InlineData(".jsonl", new[] { ".jsonl", ".ndjson" })]
    [InlineData(".json", new[] { ".json" })]
    [InlineData(".csv", new[] { ".csv", ".tsv" })]
    [InlineData(".yaml", new[] { ".yaml", ".yml" })]
    public void Loader_SupportedExtensions_AreCorrect(string extension, string[] expectedExtensions)
    {
        var loader = DatasetLoaderFactory.CreateFromExtension(extension);
        Assert.Equal(expectedExtensions, loader.SupportedExtensions);
    }

    #endregion

    #region G2 Fix: DefaultDatasetLoaderFactory.Create() format symmetry

    [Theory]
    [InlineData("ndjson", "jsonl")]
    [InlineData("tsv", "csv")]
    [InlineData("yml", "yaml")]
    public void Factory_Create_HandlesAllFormatAliases(string format, string expectedFormat)
    {
        var factory = new DefaultDatasetLoaderFactory();
        var loader = factory.Create(format);
        Assert.Equal(expectedFormat, loader.Format);
    }

    [Fact]
    public void DefaultDatasetLoaderFactory_CreateFromExtension_AllSevenExtensions()
    {
        var factory = new DefaultDatasetLoaderFactory();
        var extensions = new[] { ".jsonl", ".ndjson", ".json", ".csv", ".tsv", ".yaml", ".yml" };
        foreach (var ext in extensions)
        {
            var loader = factory.CreateFromExtension(ext);
            Assert.NotNull(loader);
        }
    }

    [Fact]
    public void DefaultDatasetLoaderFactory_Register_AllowsCustomLoader()
    {
        var factory = new DefaultDatasetLoaderFactory();
        factory.Register(".parquet", () => new JsonlDatasetLoader());
        var loader = factory.CreateFromExtension(".parquet");
        Assert.NotNull(loader);
    }

    #endregion

    #region G8: DatasetLoaderFactory.LoadAsync/LoadStreamingAsync convenience methods

    [Fact]
    public async Task Factory_LoadAsync_AutoDetectsJsonl()
    {
        var filePath = Path.Combine(_testDir, "test.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"id": "t1", "input": "Hello", "expected": "World"}"""
        });

        var results = await DatasetLoaderFactory.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("t1", results[0].Id);
        Assert.Equal("Hello", results[0].Input);
    }

    [Fact]
    public async Task Factory_LoadAsync_AutoDetectsCsv()
    {
        var filePath = Path.Combine(_testDir, "test.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,expected",
            "t1,Hello,World"
        });

        var results = await DatasetLoaderFactory.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("t1", results[0].Id);
    }

    [Fact]
    public async Task Factory_LoadAsync_ThrowsForUnknownExtension()
    {
        var filePath = Path.Combine(_testDir, "test.xyz");
        File.WriteAllText(filePath, "data");

        await Assert.ThrowsAsync<ArgumentException>(() => DatasetLoaderFactory.LoadAsync(filePath));
    }

    [Fact]
    public async Task Factory_LoadStreamingAsync_YieldsItems()
    {
        var filePath = Path.Combine(_testDir, "stream.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"id": "s1", "input": "First"}""",
            """{"id": "s2", "input": "Second"}"""
        });

        var count = 0;
        await foreach (var item in DatasetLoaderFactory.LoadStreamingAsync(filePath))
        {
            count++;
        }

        Assert.Equal(2, count);
    }

    #endregion

    #region G9: CSV ground_truth JSON blob and passing_score parsing

    [Fact]
    public async Task CsvLoader_ParsesGroundTruthJsonBlob()
    {
        var filePath = Path.Combine(_testDir, "gt.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,ground_truth",
            "test1,Search for pizza,\"{\"\"name\"\":\"\"web_search\"\",\"\"arguments\"\":{\"\"query\"\":\"\"pizza\"\"}}\""
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].GroundTruth);
        Assert.Equal("web_search", results[0].GroundTruth!.Name);
        Assert.Equal("pizza", results[0].GroundTruth!.Arguments["query"]);
    }

    [Fact]
    public async Task CsvLoader_GroundTruthInvalidJson_FallsBackToMetadata()
    {
        var filePath = Path.Combine(_testDir, "gt_invalid.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,ground_truth",
            "test1,Question,not-json-at-all"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Null(results[0].GroundTruth);
        Assert.Equal("not-json-at-all", results[0].Metadata["ground_truth"]);
    }

    [Fact]
    public async Task CsvLoader_ParsesPassingScore()
    {
        var filePath = Path.Combine(_testDir, "score.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,expected,passing_score",
            "test1,Question,Answer,85"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal(85, results[0].PassingScore);
    }

    [Fact]
    public async Task CsvLoader_ParsesEvaluationCriteriaPipeSeparated()
    {
        var filePath = Path.Combine(_testDir, "criteria.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,evaluation_criteria",
            "test1,Question,Must be concise|Must be accurate"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].EvaluationCriteria);
        Assert.Equal(2, results[0].EvaluationCriteria!.Count);
        Assert.Equal("Must be concise", results[0].EvaluationCriteria![0]);
        Assert.Equal("Must be accurate", results[0].EvaluationCriteria![1]);
    }

    [Fact]
    public async Task CsvLoader_ParsesTagsPipeSeparated()
    {
        var filePath = Path.Combine(_testDir, "tags.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,tags",
            "test1,Question,rag|quality"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].Tags);
        Assert.Equal(2, results[0].Tags!.Count);
        Assert.Equal("rag", results[0].Tags![0]);
    }

    [Fact]
    public async Task CsvLoader_DocumentsAlias_MapsToContext()
    {
        var filePath = Path.Combine(_testDir, "docs.csv");
        File.WriteAllLines(filePath, new[]
        {
            "id,input,documents",
            "test1,Question,Doc1|Doc2"
        });

        var loader = new CsvDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].Context);
        Assert.Equal(2, results[0].Context!.Count);
        Assert.Equal("Doc1", results[0].Context![0]);
    }

    #endregion

    #region G1 Fix: JSONL "documents" and "tools" alias parity

    [Fact]
    public async Task JsonlLoader_DocumentsAlias_MapsToContext()
    {
        var filePath = Path.Combine(_testDir, "docs.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"input": "Question", "documents": ["Doc1", "Doc2"]}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].Context);
        Assert.Equal(2, results[0].Context!.Count);
        Assert.Equal("Doc1", results[0].Context![0]);
    }

    [Fact]
    public async Task JsonlLoader_ToolsAlias_MapsToExpectedTools()
    {
        var filePath = Path.Combine(_testDir, "tools.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"input": "Get data", "tools": ["search", "lookup"]}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].ExpectedTools);
        Assert.Equal(2, results[0].ExpectedTools!.Count);
        Assert.Equal("search", results[0].ExpectedTools![0]);
    }

    [Fact]
    public async Task JsonlLoader_ParsesEvaluationCriteria()
    {
        var filePath = Path.Combine(_testDir, "criteria.jsonl");
        File.WriteAllLines(filePath, new[]
        {
            """{"input": "Question", "evaluation_criteria": ["Be concise", "Be accurate"], "tags": ["rag"], "passing_score": 90}"""
        });

        var loader = new JsonlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal(2, results[0].EvaluationCriteria!.Count);
        Assert.Single(results[0].Tags!);
        Assert.Equal(90, results[0].PassingScore);
    }

    #endregion

    #region YAML new properties: EvaluationCriteria, Tags, PassingScore

    [Fact]
    public async Task YamlLoader_ParsesEvaluationCriteriaTagsPassingScore()
    {
        var filePath = Path.Combine(_testDir, "props.yaml");
        File.WriteAllText(filePath, """
            - id: yaml-props
              input: What is 2+2?
              expected: "4"
              evaluation_criteria:
                - Must be correct
                - Must be concise
              tags:
                - math
                - basic
              passing_score: 90
            """);

        var loader = new YamlDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].EvaluationCriteria);
        Assert.Equal(2, results[0].EvaluationCriteria!.Count);
        Assert.Equal("Must be correct", results[0].EvaluationCriteria![0]);
        Assert.NotNull(results[0].Tags);
        Assert.Equal(2, results[0].Tags!.Count);
        Assert.Equal("math", results[0].Tags![0]);
        Assert.Equal(90, results[0].PassingScore);
    }

    #endregion

    #region JSON new properties: EvaluationCriteria, Tags, PassingScore

    [Fact]
    public async Task JsonLoader_ParsesEvaluationCriteriaTagsPassingScore()
    {
        var filePath = Path.Combine(_testDir, "props.json");
        File.WriteAllText(filePath, """
            [
                {
                    "id": "json-props",
                    "input": "What is 2+2?",
                    "expected": "4",
                    "evaluation_criteria": ["Must be correct", "Must be concise"],
                    "tags": ["math", "basic"],
                    "passing_score": 85
                }
            ]
            """);

        var loader = new JsonDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].EvaluationCriteria);
        Assert.Equal(2, results[0].EvaluationCriteria!.Count);
        Assert.Equal("Must be correct", results[0].EvaluationCriteria![0]);
        Assert.NotNull(results[0].Tags);
        Assert.Equal(2, results[0].Tags!.Count);
        Assert.Equal("math", results[0].Tags![0]);
        Assert.Equal(85, results[0].PassingScore);
    }

    [Fact]
    public async Task JsonLoader_DocumentsAndToolsAliases_MapCorrectly()
    {
        var filePath = Path.Combine(_testDir, "aliases.json");
        File.WriteAllText(filePath, """
            [
                {
                    "input": "Find data",
                    "documents": ["Doc1", "Doc2"],
                    "tools": ["search", "filter"]
                }
            ]
            """);

        var loader = new JsonDatasetLoader();
        var results = await loader.LoadAsync(filePath);

        Assert.Single(results);
        Assert.NotNull(results[0].Context);
        Assert.Equal(2, results[0].Context!.Count);
        Assert.Equal("Doc1", results[0].Context![0]);
        Assert.NotNull(results[0].ExpectedTools);
        Assert.Equal(2, results[0].ExpectedTools!.Count);
        Assert.Equal("search", results[0].ExpectedTools![0]);
    }

    #endregion

    #region DefaultDatasetLoaderFactory.Create primary formats

    [Theory]
    [InlineData("json", "json")]
    [InlineData("jsonl", "jsonl")]
    [InlineData("csv", "csv")]
    [InlineData("yaml", "yaml")]
    public void Factory_Create_PrimaryFormatsWork(string format, string expectedFormat)
    {
        var factory = new DefaultDatasetLoaderFactory();
        var loader = factory.Create(format);
        Assert.Equal(expectedFormat, loader.Format);
    }

    [Fact]
    public void Factory_Create_ThrowsForUnknownFormat()
    {
        var factory = new DefaultDatasetLoaderFactory();
        Assert.Throws<ArgumentException>(() => factory.Create("parquet"));
    }

    #endregion

    #region DatasetLoaderFactory convenience methods with other formats

    [Fact]
    public async Task Factory_LoadAsync_AutoDetectsYaml()
    {
        var filePath = Path.Combine(_testDir, "test.yaml");
        File.WriteAllText(filePath, "- id: y1\n  input: Hello\n  expected: World\n");

        var results = await DatasetLoaderFactory.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("y1", results[0].Id);
    }

    [Fact]
    public async Task Factory_LoadAsync_AutoDetectsJson()
    {
        var filePath = Path.Combine(_testDir, "test.json");
        File.WriteAllText(filePath, """[{"id": "j1", "input": "Hi", "expected": "Hello"}]""");

        var results = await DatasetLoaderFactory.LoadAsync(filePath);

        Assert.Single(results);
        Assert.Equal("j1", results[0].Id);
    }

    #endregion
}
