// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Every GraphQL operation the Mission Control web app sends, taken from its source, validates against the server
/// as <c>agenteval mc serve</c> and Docker run it: Production settings. Two pages were broken this way while every
/// test was green, because the tests wrote their own queries (MC 01 P1-2, P1-3):
/// <list type="bullet">
///   <item>the evaluator list declared <c>$costTier: CostTier</c>; the schema's type is <c>EvaluatorCostTier</c>;</item>
///   <item>the scenario tree nests <c>details</c> four levels deep, past Hot Chocolate's coordinate-cycle rule, which
///         runs outside Development only.</item>
/// </list>
/// A validation failure is a response with no <c>data</c>; a resolver finding nothing (dummy ids) still returns data.
/// </summary>
public sealed class SpaQueriesValidateTests : IClassFixture<SeededMissionControlFactory>
{
    private static readonly Regex Constant = new(
        @"const\s+(?<name>[A-Z][A-Z0-9_]*)\s*=\s*(?:/\*\s*GraphQL\s*\*/\s*)?`(?<body>[^`]*)`",
        RegexOptions.Compiled);

    private static readonly Regex Interpolation = new(@"\$\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}", RegexOptions.Compiled);

    private static readonly Regex Operation = new(@"^\s*(query|mutation|subscription)\b|^\s*\{", RegexOptions.Compiled);

    private static readonly Regex RequiredVariable = new(@"\$(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<type>[A-Za-z_][A-Za-z0-9_]*)!", RegexOptions.Compiled);

    private readonly SeededMissionControlFactory _factory;

    public SpaQueriesValidateTests(SeededMissionControlFactory factory) => _factory = factory;

    /// <summary>(page, operation name, query text) for every GraphQL operation in the SPA source.</summary>
    public static TheoryData<string, string, string> SpaOperations()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var file in Directory.EnumerateFiles(SpaSourceRoot(), "*.ts*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var source = File.ReadAllText(file);
            var constants = Constant.Matches(source).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["body"].Value, StringComparer.Ordinal);
            foreach (var (name, body) in constants)
            {
                var text = Expand(body, constants, depth: 0);
                if (!Operation.IsMatch(text))
                {
                    continue;   // a fragment of fields, used inside an operation
                }

                data.Add(Path.GetFileName(file), name, text);
            }
        }

        return data;
    }

    [Fact]
    public void TheSpaSourceWasFound_AndHoldsOperations()
    {
        // Without this the theory below could pass over nothing.
        Assert.True(SpaOperations().Count >= 10, $"Expected the SPA's GraphQL operations under {SpaSourceRoot()}.");
    }

    [Theory]
    [MemberData(nameof(SpaOperations))]
    public async Task EveryOperation_Validates_UnderProductionSettings(string page, string constant, string query)
    {
        using var client = _factory.WithWebHostBuilder(b => b.UseEnvironment("Production")).CreateClient();
        var variables = RequiredVariable.Matches(query).ToDictionary(
            m => m.Groups["name"].Value,
            m => (object)(m.Groups["type"].Value switch
            {
                "Int" => 1,
                "Float" => 1.0,
                "Boolean" => false,
                "SubjectKind" => "AGENT",
                "EvaluatorCostTier" => "LOW",
                _ => "does-not-exist",
            }));

        var response = await client.PostAsJsonAsync("/graphql", new { query, variables });
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(
            doc.RootElement.TryGetProperty("data", out _),
            $"{page} {constant} was rejected by the server (HTTP {(int)response.StatusCode}): {body}");

        // A document error (validation, a rule such as the cycle limit) has no path; an error from a resolver run on the
        // placeholder variables has one. Only the first kind means the page's query is wrong.
        if (doc.RootElement.TryGetProperty("errors", out var errors))
        {
            Assert.All(errors.EnumerateArray(), e => Assert.True(
                e.TryGetProperty("path", out _), $"{page} {constant} has a document error: {e}"));
        }
    }

    private static string Expand(string body, IReadOnlyDictionary<string, string> constants, int depth)
    {
        Assert.True(depth < 8, "GraphQL fragment constants nest too deep to expand.");
        return Interpolation.Replace(body, m =>
        {
            var name = m.Groups["name"].Value;
            Assert.True(constants.ContainsKey(name), $"The query interpolates ${{{name}}}, which is not a constant in the same file.");
            return Expand(constants[name], constants, depth + 1);
        });
    }

    private static string SpaSourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AgentEval.MissionControl.Spa", "src");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("src/AgentEval.MissionControl.Spa/src was not found above the test directory.");
    }
}

#endif
