// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Core;
using AgentEval.Metrics.RAG;
using AgentEval.Prompts;
using AgentEval.Testing;
using Xunit;
using YamlDotNet.Serialization;

namespace AgentEval.Tests.Prompts;

public sealed class JudgePromptPackLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-prompts-" + Guid.NewGuid().ToString("N"));
    private readonly JudgePromptPackLoader _loader = new();

    public JudgePromptPackLoaderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<string> ExportAsync()
    {
        var folder = Path.Combine(_root, "pack");
        await _loader.ExportAsync(new([JudgePromptTests.Swedish()]), folder);
        return Path.Combine(folder, "prompts.json");
    }

    [Fact]
    public async Task ExportLoadEvaluate_MarkdownEdited_SendsTheActualNewInstrument()
    {
        var path = await ExportAsync();
        var first = await _loader.LoadAsync(path);
        var system = Path.Combine(Path.GetDirectoryName(path)!, "001-system.md");
        await File.WriteAllTextAsync(system, "Kundens nya svenska bedömningsregler.");
        var second = await _loader.LoadAsync(path);
        var client = new FakeChatClient(JudgePromptTests.FaithfulReply);
        var result = await new FaithfulnessMetric(client, new() { Pack = second, Locale = "sv-SE" })
            .EvaluateAsync(new() { Input = "Fråga", Output = "Svar", Context = "Underlag" });
        Assert.True(result.Measured);
        Assert.Equal("Kundens nya svenska bedömningsregler.", client.ReceivedMessages.Single().First().Text);
        var old = new JudgePromptResolver().Resolve(BuiltInJudgePrompts.GetRag("llm_faithfulness"), new() { Pack = first, Locale = "sv-SE" });
        Assert.NotEqual(old.Provenance.Digest, result.JudgePrompt!.Digest);
        Assert.Null(result.JudgePrompt.Content);
    }

    [Fact]
    public async Task Load_EquivalentYamlAndJson_HaveIdenticalInstrumentDigests()
    {
        var path = await ExportAsync();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var yaml = new SerializerBuilder().DisableAliases().Build().Serialize(ToObject(document.RootElement));
        var yamlPath = Path.ChangeExtension(path, ".yaml");
        await File.WriteAllTextAsync(yamlPath, yaml);
        var jsonPack = await _loader.LoadAsync(path);
        var yamlPack = await _loader.LoadAsync(yamlPath);
        var resolver = new JudgePromptResolver();
        var source = BuiltInJudgePrompts.GetRag("llm_faithfulness");
        Assert.Equal(resolver.Resolve(source, new() { Pack = jsonPack, Locale = "sv-SE" }).Provenance.Digest,
            resolver.Resolve(source, new() { Pack = yamlPack, Locale = "sv-SE" }).Provenance.Digest);
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("nested/../../outside.md")]
    [InlineData("C:/outside.md")]
    [InlineData("/outside.md")]
    [InlineData("https://example.com/prompt.md")]
    [InlineData("001-system.md:alternate")]
    [InlineData("001-system.txt")]
    public async Task Load_UnsafeMessageReference_RejectsBeforeReading(string reference)
    {
        var path = await ExportAsync();
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        manifest["definitions"]![0]!["systemFile"] = reference;
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => _loader.LoadAsync(path));
    }

    [Theory]
    [InlineData("schemaVersion: 1\nschemaVersion: 1\ndefinitions: []")]
    [InlineData("schemaVersion: &v 1\ndefinitions: *v")]
    [InlineData("schemaVersion: !!int 1\ndefinitions: []")]
    [InlineData("schemaVersion: 1\ndefinitions: []\n---\nschemaVersion: 1")]
    [InlineData("schemaVersion: 1\ndefinitions:\n  <<: anything")]
    public async Task Load_AmbiguousYaml_RejectsWithoutExpansion(string yaml)
    {
        var path = Path.Combine(_root, "invalid.yaml");
        await File.WriteAllTextAsync(path, yaml);
        await Assert.ThrowsAsync<InvalidDataException>(() => _loader.LoadAsync(path));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"definitions\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"definitions\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"definitions\":[],\"extra\":true}")]
    [InlineData("{\"schemaVersion\":\"1\",\"definitions\":[]}")]
    public async Task Load_InvalidJsonManifest_Rejects(string json)
    {
        var path = Path.Combine(_root, "invalid.json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => _loader.LoadAsync(path));
    }

    [Theory]
    [InlineData("systemText", "cannot bypass the file boundary")]
    [InlineData("unknownMetadata", "unexpected")]
    [InlineData("responseContract", "UnsupportedV99")]
    public async Task Load_UnknownOrConflictingDefinitionFields_Rejects(string key, string value)
    {
        var path = await ExportAsync();
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        manifest["definitions"]![0]![key] = value;
        await File.WriteAllTextAsync(path, manifest.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => _loader.LoadAsync(path));
    }

    [Fact]
    public async Task Load_OversizedMessageOrManifest_RejectsBoundedly()
    {
        var path = await ExportAsync();
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "001-system.md"), new string('a', 524289));
        await Assert.ThrowsAsync<InvalidDataException>(() => _loader.LoadAsync(path));
        await File.WriteAllTextAsync(path, new string(' ', 1048577));
        await Assert.ThrowsAsync<InvalidDataException>(() => _loader.LoadAsync(path));
    }

    [Fact]
    public async Task Export_ExistingDirectory_PreservesExistingFiles()
    {
        var path = await ExportAsync();
        var before = await File.ReadAllTextAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => _loader.ExportAsync(new(BuiltInJudgePrompts.Rag), Path.GetDirectoryName(path)!));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".prompt-pack-*"));
    }

    [Fact]
    public async Task Load_LinkedDirectory_RejectsEvenWhenTargetIsAValidPack()
    {
        var path = await ExportAsync();
        var target = Path.GetDirectoryName(path)!;
        var link = Path.Combine(_root, "linked");
        if (OperatingSystem.IsWindows())
        {
            // Junction creation does not require the Windows symbolic-link privilege.
            var start = new System.Diagnostics.ProcessStartInfo("powershell")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null");
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
        }
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => _loader.LoadAsync(Path.Combine(link, "prompts.json")));
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Export_Cancelled_DoesNotCreateDestination()
    {
        var target = Path.Combine(_root, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _loader.ExportAsync(new(BuiltInJudgePrompts.Rag), target, new CancellationToken(true)));
        Assert.False(Directory.Exists(target));
    }

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
