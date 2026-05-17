// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Articles.Models;
using AgentEval.Compliance.Gdpr.Articles.Validation;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

public class AllArticleYamlsValidateTest
{
    [Fact]
    public async Task All_GDPR_Article_Yamls_Validate()
    {
        var assembly = typeof(ArticleScenarioYamlLoader).Assembly;
        var loader = new ArticleScenarioYamlLoader();
        var validator = new ArticleYamlValidator();

        var specs = await loader.LoadAllFromAssemblyAsync(assembly, includeTestFixtures: false);
        Assert.True(specs.Count >= 21, $"expected at least 21 article YAMLs embedded in the assembly, got {specs.Count}");

        var failures = new List<string>();
        foreach (var spec in specs)
        {
            var result = validator.Validate(spec);
            if (!result.IsValid)
                failures.Add($"{spec.Metadata.Article}: {result.ErrorsJoined}");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
