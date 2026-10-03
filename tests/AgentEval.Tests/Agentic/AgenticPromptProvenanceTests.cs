// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// The agentic reference prompt files are AgentEval's own text. Their headers used to say
// "Source: forked from Azure/azure-sdk-for-python (commit <TBD-foundry-sha> ...)" and to name
// Microsoft's licence. A comparison with every upstream version of the cited .prompty files found
// no shared passage longer than six words, and eight of the cited files never existed (those
// evaluators run in Microsoft's hosted safety service and have no public prompt). These tests keep
// that claim from coming back, for example through a header copied into a new prompt file.
//
// Neither test depends on the files being sent to the judge (they are not, today): they read the
// embedded resources, which is what ships in the package.

using System.Reflection;
using AgentEval.Evals.Agentic.Quality;
using Xunit;

namespace AgentEval.Tests.Agentic;

public class AgenticPromptProvenanceTests
{
    private static readonly Assembly s_agenticAssembly = typeof(CoherenceEval).Assembly;

    private const string PromptResourcePrefix = "AgentEval.Evals.Agentic.Resources.Prompts.";

    /// <summary>Text that claims a fork of upstream prompt text, or labels AgentEval's text with Microsoft's licence.</summary>
    private static readonly string[] s_forbiddenProvenance = new[]
    {
        "TBD-foundry-sha",
        "forked from",
        "forked and extended from",
        "github.com/Azure/azure-sdk-for-python/blob/main/LICENSE",
    };

    /// <summary>
    /// The 22 prompt files modelled on an Azure AI Evaluation SDK evaluator: 14 whose evaluator ships a
    /// <c>.prompty</c> file, and 8 whose evaluator runs in Microsoft's hosted safety service.
    /// </summary>
    private static readonly string[] s_upstreamModelled = new[]
    {
        "quality.coherence.v1.md",
        "quality.fluency.v1.md",
        "quality.groundedness.v1.md",
        "quality.relevance.v1.md",
        "quality.response-completeness.v1.md",
        "quality.similarity.v1.md",
        "system.intent-resolution.v1.md",
        "system.task-adherence.v1.md",
        "system.task-completion.v1.md",
        "process.tool-call-success.v1.md",
        "process.tool-efficiency.v1.md",
        "process.tool-input-accuracy.v1.md",
        "process.tool-output-utilization.v1.md",
        "process.tool-selection.v1.md",
        "safety.code-vulnerability.v1.md",
        "safety.hate-unfairness.v1.md",
        "safety.indirect-attack.v1.md",
        "safety.protected-material.v1.md",
        "safety.self-harm.v1.md",
        "safety.sexual.v1.md",
        "safety.ungrounded-attributes.v1.md",
        "safety.violence.v1.md",
    };

    /// <summary>AgentEval originals that carried a stray <c>Commit: &lt;TBD-foundry-sha&gt;</c> line.</summary>
    private static readonly string[] s_originalsWithStrayCommitLine = new[]
    {
        "safety.prohibited-actions.v1.md",
        "safety.sensitive-data-leakage.v1.md",
    };

    public static TheoryData<string> UpstreamModelledPrompts()
    {
        var data = new TheoryData<string>();
        foreach (var file in s_upstreamModelled)
            data.Add(file);
        return data;
    }

    [Fact]
    public void No_embedded_agentic_prompt_claims_a_fork_or_carries_the_commit_placeholder_or_Microsofts_licence()
    {
        var names = s_agenticAssembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(PromptResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Vacuity guard: every file that carried the claim must be among the files scanned, so a renamed
        // folder or a changed EmbeddedResource glob cannot make this pass by scanning nothing.
        foreach (var carrier in s_upstreamModelled.Concat(s_originalsWithStrayCommitLine))
            Assert.Contains(PromptResourcePrefix + carrier, names);

        var offenders = (
            from name in names
            let text = ReadResource(name)
            from phrase in s_forbiddenProvenance
            where text.Contains(phrase, StringComparison.OrdinalIgnoreCase)
            select $"{name[PromptResourcePrefix.Length..]}: \"{phrase}\"").ToList();

        Assert.True(
            offenders.Count == 0,
            "These agentic prompt files claim to be forks of upstream prompt text, carry the commit placeholder, " +
            "or name Microsoft's licence. None of them reproduces upstream text: state the lineage as " +
            "'original AgentEval prompt text, modelled on the evaluator concept of ...' and use AgentEval's licence." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [MemberData(nameof(UpstreamModelledPrompts))]
    public void A_prompt_modelled_on_an_upstream_evaluator_states_its_lineage_and_AgentEvals_licence(string file)
    {
        var header = LeadingComment(ReadResource(PromptResourcePrefix + file));

        Assert.Contains("Lineage: original AgentEval prompt text, modelled on the evaluator concept", header, StringComparison.Ordinal);
        Assert.Contains("Checked 2026-10-02: no upstream prompt text is reproduced.", header, StringComparison.Ordinal);
        Assert.Contains("License: MIT (https://github.com/AgentEvalHQ/AgentEval/blob/main/LICENSE)", header, StringComparison.Ordinal);
    }

    private static string ReadResource(string resourceName)
    {
        using var stream = s_agenticAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The leading <c>&lt;!-- ... --&gt;</c> block, which holds the provenance header.</summary>
    private static string LeadingComment(string text)
    {
        Assert.StartsWith("<!--", text, StringComparison.Ordinal);
        var end = text.IndexOf("-->", StringComparison.Ordinal);
        Assert.True(end > 0, "The prompt file's leading comment is not closed.");
        return text[..end];
    }
}
