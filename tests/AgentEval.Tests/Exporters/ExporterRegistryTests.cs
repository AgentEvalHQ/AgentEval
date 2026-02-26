// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using System.Linq;
using AgentEval.Core;
using AgentEval.Exporters;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Exporters;

public class ExporterRegistryTests
{
    [Fact]
    public void Register_ByFormatName_AddsExporter()
    {
        var registry = new ExporterRegistry();
        var exporter = new FakeExporter(ExportFormat.Json, ".json");

        registry.Register("custom", exporter);

        Assert.True(registry.Contains("custom"));
        Assert.Same(exporter, registry.Get("custom"));
    }

    [Fact]
    public void Register_ByExporter_UsesFormatName()
    {
        var registry = new ExporterRegistry();
        var exporter = new FakeExporter(ExportFormat.Json, ".json");

        registry.Register(exporter);

        Assert.True(registry.Contains("Json"));
    }

    [Fact]
    public void Register_DuplicateFormatName_Throws()
    {
        var registry = new ExporterRegistry();
        var exporter1 = new FakeExporter(ExportFormat.Json, ".json");
        var exporter2 = new FakeExporter(ExportFormat.Json, ".json");

        registry.Register("json", exporter1);

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register("json", exporter2));
    }

    [Fact]
    public void Register_NullFormatName_Throws()
    {
        var registry = new ExporterRegistry();
        var exporter = new FakeExporter(ExportFormat.Json, ".json");

        Assert.Throws<ArgumentException>(() =>
            registry.Register("", exporter));
    }

    [Fact]
    public void Register_NullExporter_Throws()
    {
        var registry = new ExporterRegistry();

        Assert.Throws<ArgumentNullException>(() =>
            registry.Register("json", null!));
    }

    [Fact]
    public void Get_ExistingFormat_ReturnsExporter()
    {
        var registry = new ExporterRegistry();
        var exporter = new FakeExporter(ExportFormat.Json, ".json");
        registry.Register("json", exporter);

        var result = registry.Get("json");

        Assert.Same(exporter, result);
    }

    [Fact]
    public void Get_CaseInsensitive_ReturnsExporter()
    {
        var registry = new ExporterRegistry();
        var exporter = new FakeExporter(ExportFormat.Json, ".json");
        registry.Register("json", exporter);

        Assert.Same(exporter, registry.Get("JSON"));
        Assert.Same(exporter, registry.Get("Json"));
    }

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        var registry = new ExporterRegistry();

        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public void GetRequired_NonExistent_Throws()
    {
        var registry = new ExporterRegistry();

        Assert.Throws<KeyNotFoundException>(() =>
            registry.GetRequired("nonexistent"));
    }

    [Fact]
    public void GetAll_ReturnsAllExporters()
    {
        var registry = new ExporterRegistry();
        var exporter1 = new FakeExporter(ExportFormat.Json, ".json");
        var exporter2 = new FakeExporter(ExportFormat.Csv, ".csv");
        registry.Register("json", exporter1);
        registry.Register("csv", exporter2);

        var all = registry.GetAll().ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void GetRegisteredFormats_ReturnsAllNames()
    {
        var registry = new ExporterRegistry();
        registry.Register("json", new FakeExporter(ExportFormat.Json, ".json"));
        registry.Register("csv", new FakeExporter(ExportFormat.Csv, ".csv"));

        var formats = registry.GetRegisteredFormats().ToList();

        Assert.Contains("json", formats);
        Assert.Contains("csv", formats);
    }

    [Fact]
    public void Remove_ExistingFormat_ReturnsTrue()
    {
        var registry = new ExporterRegistry();
        registry.Register("json", new FakeExporter(ExportFormat.Json, ".json"));

        Assert.True(registry.Remove("json"));
        Assert.False(registry.Contains("json"));
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        var registry = new ExporterRegistry();

        Assert.False(registry.Remove("nonexistent"));
    }

    [Fact]
    public void Clear_RemovesAllExporters()
    {
        var registry = new ExporterRegistry();
        registry.Register("json", new FakeExporter(ExportFormat.Json, ".json"));
        registry.Register("csv", new FakeExporter(ExportFormat.Csv, ".csv"));

        registry.Clear();

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Constructor_WithExporters_PrePopulates()
    {
        var exporters = new IResultExporter[]
        {
            new FakeExporter(ExportFormat.Json, ".json"),
            new FakeExporter(ExportFormat.Csv, ".csv"),
        };

        var registry = new ExporterRegistry(exporters);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.Contains("Json"));
        Assert.True(registry.Contains("Csv"));
    }

    [Fact]
    public void Count_ReturnsCorrectValue()
    {
        var registry = new ExporterRegistry();
        Assert.Equal(0, registry.Count);

        registry.Register("json", new FakeExporter(ExportFormat.Json, ".json"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Get_NullOrEmpty_ReturnsNull()
    {
        var registry = new ExporterRegistry();

        Assert.Null(registry.Get(null!));
        Assert.Null(registry.Get(""));
        Assert.Null(registry.Get("  "));
    }

    [Fact]
    public void Contains_NullOrEmpty_ReturnsFalse()
    {
        var registry = new ExporterRegistry();

        Assert.False(registry.Contains(null!));
        Assert.False(registry.Contains(""));
        Assert.False(registry.Contains("  "));
    }

    // Fake exporter for testing
    private class FakeExporter : IResultExporter
    {
        public FakeExporter(ExportFormat format, string fileExtension)
        {
            Format = format;
            FileExtension = fileExtension;
        }

        public ExportFormat Format { get; }
        public string FileExtension { get; }
        public string ContentType => "application/octet-stream";

        public Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
