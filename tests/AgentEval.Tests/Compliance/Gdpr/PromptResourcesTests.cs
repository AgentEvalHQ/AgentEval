// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Compliance.Gdpr.Articles.Models;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

public class PromptResourcesTests
{
    private static readonly Assembly s_gdprAsm =
        typeof(ArticleSpec).Assembly;

    private static async Task<string> LoadEmbeddedAsync(string suffix)
    {
        var name = s_gdprAsm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        using var stream = s_gdprAsm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task JudgeSystemPrompt_LoadsFromEmbeddedResource_NonEmpty()
    {
        var text = await LoadEmbeddedAsync(".gdpr-judge-system.v1.md");

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("## Role", text, StringComparison.Ordinal);
        Assert.Contains("overall_score", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerCriterionPrompt_LoadsFromEmbeddedResource_NonEmpty()
    {
        var text = await LoadEmbeddedAsync(".per-criterion.v1.md");

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("met", text, StringComparison.Ordinal);
        Assert.Contains("confidence", text, StringComparison.Ordinal);
    }
}
