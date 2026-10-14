// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net.Http.Json;
using System.Text.Json;
using AgentEval.MissionControl.GraphQL;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentEval.Tests.MissionControl;

public sealed class EvidenceSchemaTests
{
    [Fact]
    public async Task EvidenceSchema_ReachableTypesAndArguments_MatchReviewedContract()
    {
        using var factory = new WebApplicationFactory<Query>(); using var client = factory.CreateClient();
        var roots = new HashSet<string>(StringComparer.Ordinal)
        {
            "evidenceSources", "evidenceResults", "evidenceMetrics", "evidenceSpans", "evidenceFields",
            "evidenceSuite", "evidenceSuiteComparison", "evidenceResultDetail", "governanceStatus"
        };
        var pending = new Queue<string>(); pending.Enqueue("Query");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // Eight wrapper levels exceed all current list/nullability shapes; a deeper type fails explicitly below.
        var reference = "kind name";
        for (var i = 0; i < 8; i++) reference = "kind name ofType { " + reference + " }";
        while (pending.TryDequeue(out var name))
        {
            if (!visited.Add(name)) continue;
            using var response = await client.PostAsJsonAsync("/graphql", new
            {
                query = "{ __type(name:" + JsonSerializer.Serialize(name) + ") { kind name enumValues { name } fields(includeDeprecated:true) { name type { "
                    + reference + " } args { name defaultValue type { " + reference + " } } } } }"
            });
            var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body); Assert.False(json.RootElement.TryGetProperty("errors", out _), body);
            var type = json.RootElement.GetProperty("data").GetProperty("__type");
            Assert.NotEqual(JsonValueKind.Null, type.ValueKind);
            var kind = type.GetProperty("kind").GetString();
            var lines = new List<string>();
            if (kind == "ENUM")
                lines.AddRange(type.GetProperty("enumValues").EnumerateArray().Select(v => v.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
            if (kind == "OBJECT")
            {
                foreach (var field in type.GetProperty("fields").EnumerateArray().OrderBy(f => f.GetProperty("name").GetString(), StringComparer.Ordinal))
                {
                    var fieldName = field.GetProperty("name").GetString()!;
                    if (name == "Query" && !roots.Contains(fieldName)) continue;
                    var arguments = field.GetProperty("args").EnumerateArray().OrderBy(a => a.GetProperty("name").GetString(), StringComparer.Ordinal)
                        .Select(a => a.GetProperty("name").GetString() + ": " + Signature(a.GetProperty("type"), pending)
                            + (a.GetProperty("defaultValue").ValueKind == JsonValueKind.Null ? "" : " = " + a.GetProperty("defaultValue").GetString()));
                    var args = string.Join(", ", arguments);
                    lines.Add(fieldName + (args.Length == 0 ? "" : "(" + args + ")") + ": " + Signature(field.GetProperty("type"), pending));
                }
                if (name == "Query") Assert.Equal(roots.Count, lines.Count);
            }
            Assert.Contains(kind, new[] { "OBJECT", "ENUM", "SCALAR" });
            definitions.Add(name, kind + " " + name + (lines.Count == 0 ? "\n" : " {\n  " + string.Join("\n  ", lines) + "\n}\n"));
        }
        var actual = string.Join("\n", definitions.Values);
        if (Environment.GetEnvironmentVariable("AGENTEVAL_SCHEMA_CAPTURE") is { Length: > 0 } capture)
            await File.WriteAllTextAsync(capture, actual);
        using var stream = typeof(EvidenceSchemaTests).Assembly.GetManifestResourceStream("AgentEval.Tests.EvidenceSchema.txt");
        Assert.NotNull(stream); using var expected = new StreamReader(stream);
        Assert.Equal((await expected.ReadToEndAsync()).Replace("\r\n", "\n"), actual);
    }

    private static string Signature(JsonElement type, Queue<string> pending)
    {
        var kind = type.GetProperty("kind").GetString();
        if (kind == "NON_NULL") return Signature(type.GetProperty("ofType"), pending) + "!";
        if (kind == "LIST") return "[" + Signature(type.GetProperty("ofType"), pending) + "]";
        var name = type.GetProperty("name").GetString(); Assert.False(string.IsNullOrEmpty(name));
        pending.Enqueue(name!); return name!;
    }
}
#endif
