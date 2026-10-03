// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Core;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The explicit mock target, <c>--sut mock</c>, and the refusal every evaluating <c>bench</c> command gives when no
/// target is named.
/// </summary>
/// <remarks>
/// <para>
/// Through 0.42, <c>bench owasp</c>, <c>mitre</c> and <c>nist</c> scanned a built-in agent that refuses everything,
/// <c>bench perf</c> measured an echo agent, and <c>bench gdpr</c> and <c>eu-ai-act</c> graded a built-in answer,
/// whenever no target was given. A warning was printed, but the run was stored in <c>.agenteval/</c> and shown in
/// Mission Control and <c>compare</c> like a measurement, and a stand-in that refuses everything passes a red-team
/// benchmark.
/// </para>
/// <para>
/// Now a command without a target fails with a usage error. The stand-ins run only when asked for by name. A mock
/// run says MOCK, exits <see cref="ExitCodes.GateIndeterminate"/> whatever it scores (it measured no agent), and is
/// never written to <c>.agenteval/</c>.
/// </para>
/// </remarks>
internal static class MockTarget
{
    /// <summary>The <c>--sut</c> value that selects the mock.</summary>
    public const string Sut = "mock";

    /// <summary>The <c>--sut</c> help line for the mock.</summary>
    public const string SutHelp =
        "mock = a built-in stand-in that measures nothing; the run is labelled MOCK and is not stored";

    /// <summary>The real targets of the red-team and perf commands, as named in their refusal.</summary>
    public const string AgentTargets =
        "--sut <target>, --endpoint <url> --model <name>, or --azure-from-env (the configured provider)";

    /// <summary>The real targets of <c>bench gdpr</c> and <c>bench eu-ai-act</c>, as named in their refusal.</summary>
    public const string ComplianceTargets =
        "--sut <target>, --azure-from-env (the configured provider), or the agent's real answer with --response/--response-file";

    /// <summary>True when <paramref name="sut"/> names the mock.</summary>
    public static bool IsRequested(string? sut) =>
        string.Equals(sut?.Trim(), Sut, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Separates <c>--sut mock</c> from a real <c>--sut</c> value, for the command-line wiring. The mock cannot be
    /// combined with another target: a run is either a measurement or a mock.
    /// </summary>
    public static (bool Mock, string? RealSut, string? Error) Parse(string? sut, bool anotherTargetNamed) =>
        !IsRequested(sut) ? (false, sut, null)
        : anotherTargetNamed ? (true, null, MockWithRealTargetMessage)
        : (true, null, null);

    private const string MockWithRealTargetMessage =
        "--sut " + Sut + " cannot be combined with a real target: a run is either a measurement or a mock.";

    /// <summary>
    /// The commands' own guard against a mock combined with a real target, for callers other than the command line.
    /// Prints why and returns <see cref="ExitCodes.UsageError"/>.
    /// </summary>
    public static int RefuseMockWithRealTarget()
    {
        Console.Error.WriteLine($"Error: {MockWithRealTargetMessage}");
        return ExitCodes.UsageError;
    }

    /// <summary>
    /// The judge of a mock run, resolved without reading the environment: a mock run never calls or bills a real
    /// judge and needs no provider. It exists only inside <c>--sut mock</c> runs, which are never stored.
    /// </summary>
    public static (IEvaluator? Judge, string JudgeModel, int ExitCode) JudgeResolution => (new Judge(), Sut, 0);

    /// <summary>
    /// The <c>--sut</c> option of a command whose real target is a supplied answer, so the mock is its only value.
    /// </summary>
    public static Option<string?> MockOnlySutOption() => new("--sut")
    {
        Description = $"Only '{Sut}' here ({SutHelp}). The real target of this command is the agent's answer (--response/--response-file).",
    };

    /// <summary>Prints why the command will not run, and returns <see cref="ExitCodes.UsageError"/>.</summary>
    public static int RefuseWithoutTarget(string command, string realTargets)
    {
        Console.Error.WriteLine(
            $"Error: {command} needs a real target: {realTargets}. " +
            $"To try the command without an agent, pass --sut {Sut}: a stand-in that measures nothing, " +
            "and the run is not stored.");
        return ExitCodes.UsageError;
    }

    /// <summary>Prints the banner that opens a mock run.</summary>
    public static void PrintBanner(string command, string standIn) =>
        Console.Error.WriteLine(
            $"MOCK RUN: {command} is running against {standIn}, not an agent. " +
            "The result measures nothing about any agent and is not stored in .agenteval/.");

    /// <summary>
    /// Prints the result line of a mock run and returns <see cref="ExitCodes.GateIndeterminate"/>: a mock run
    /// never passes or fails a gate.
    /// </summary>
    public static int Finish(string command, string result)
    {
        Console.WriteLine($"MOCK RESULT ({command}, stand-in, not stored): {result}");
        return ExitCodes.GateIndeterminate;
    }

    /// <summary>The mock run's judge: a fixed placeholder verdict, labelled as such in every criterion.</summary>
    internal sealed class Judge : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default)
            => Task.FromResult(new EvaluationResult
            {
                OverallScore = 75,
                Summary = "MOCK judge: a placeholder verdict, no model was asked.",
                CriteriaResults = criteria
                    .Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "MOCK: not graded." })
                    .ToList(),
            });
    }

    /// <summary>The red-team stand-in: refuses every request.</summary>
    internal sealed class RefusingAgent(string name) : IEvaluableAgent
    {
        public string Name { get; } = name;

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse
            {
                Text = "I cannot help with that request. Please rephrase or ask something else.",
            });
    }

    /// <summary>The perf stand-in: echoes the prompt after 50 ms.</summary>
    internal sealed class EchoingAgent(string name) : IEvaluableAgent
    {
        public string Name { get; } = name;

        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            await Task.Delay(50, cancellationToken);
            return new AgentResponse
            {
                Text = prompt,
                TokenUsage = new TokenUsage { PromptTokens = prompt.Length / 4, CompletionTokens = prompt.Length / 4 },
            };
        }
    }
}
