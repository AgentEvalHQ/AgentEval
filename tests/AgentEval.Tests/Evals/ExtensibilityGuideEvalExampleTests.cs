// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// The <c>ResponseLengthEval</c> example that opens docs/extensibility.md, copied here verbatim so the guide's first
/// example is known to compile and to do what the text says. Change both together.
/// </summary>
public class ExtensibilityGuideEvalExampleTests
{
    /// <summary>Passes when the response length is within [minLength, maxLength].</summary>
    public sealed class ResponseLengthEval(int minLength = 50, int maxLength = 500)
        : AtomicCodeEval("response_length", "Response length", "quality.format", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input)
        {
            // No response is not a short response: say nothing could be measured, never score it as a 0 fail.
            if (input.Response is null)
            {
                return NotApplicable("There is no response to measure.");
            }

            var length = input.Response.Length;
            var passed = length >= minLength && length <= maxLength;
            var score = passed ? 1.0
                : length < minLength ? (double)length / minLength
                : Math.Max(0.0, 1.0 - (double)(length - maxLength) / maxLength);

            return Build(score, passed, severity: passed ? "none" : "low",
                dimensions: new Dictionary<string, double> { ["length"] = length });
        }
    }

    [Fact]
    public async Task AResponseInRange_Passes()
    {
        var result = await new ResponseLengthEval().EvaluateAsync(new EvalInput("Summarise the ticket", Response: new string('x', 120)));

        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(1.0, result.Score.Value);
        Assert.Equal(120, result.Details.Dimensions!["length"]);
    }

    [Fact]
    public async Task AShortResponse_FailsWithAPartialScore()
    {
        var result = await new ResponseLengthEval().EvaluateAsync(new EvalInput("q", Response: new string('x', 25)));

        Assert.Equal("fail", result.Score.Label);
        Assert.Equal(0.5, result.Score.Value);
        Assert.Equal("low", result.Score.Severity);
    }

    [Fact]
    public async Task NoResponse_IsNotMeasured_NotAZeroFail()
    {
        var result = await new ResponseLengthEval().EvaluateAsync(new EvalInput("q"));

        Assert.Equal("inapplicable", result.Score.Label);
        Assert.Equal(MeasurementState.NotApplicable, result.Score.Measurement);
        Assert.False(result.Score.Passed);
        Assert.Equal("There is no response to measure.", result.Details.Summary);
    }

    [Fact]
    public void TheGuide_ShowsExactlyTheClassCompiledHere()
    {
        // The example above is a copy; this keeps the copy and the page from drifting apart.
        var root = RepoRoot();
        const string NewLine = "\n";
        var guide = File.ReadAllText(Path.Combine(root, "docs", "extensibility.md")).ReplaceLineEndings(NewLine);
        var source = File.ReadAllText(Path.Combine(root, "tests", "AgentEval.Tests", "Evals", "ExtensibilityGuideEvalExampleTests.cs"))
            .ReplaceLineEndings(NewLine);

        // The class block, from its summary to the closing brace after Build(...), with the class indentation removed.
        var start = source.IndexOf("    /// <summary>Passes when the response length", StringComparison.Ordinal);
        var classEnd = NewLine + "    }" + NewLine;
        var end = source.IndexOf(classEnd, source.IndexOf("return Build(score", start, StringComparison.Ordinal), StringComparison.Ordinal)
                  + classEnd.Length - 1;
        var example = string.Join(NewLine, source[start..end].Split(NewLine).Select(line => line.Length >= 4 ? line[4..] : line.TrimStart()));

        Assert.Contains(example, guide, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "extensibility.md")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("docs/extensibility.md was not found above the test directory.");
    }

    [Fact]
    public async Task ItIsAdmittedWithTheFloorTheGuideShows_AndTheRunnerBuilds()
    {
        var runner = await new AgentEvalBuilder()
            .AddEval(new ResponseLengthEval(), ChanceFloor.NotDerivable("no draw model: any length can be written"))
            .BuildAsync(CancellationToken.None);

        Assert.NotNull(runner);
    }
}
