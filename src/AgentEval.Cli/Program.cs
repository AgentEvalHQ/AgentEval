// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Cli.Infrastructure;

// The command tree (every command, option and argument) is built in CliCommandTree, where the CLI surface
// snapshot test can reach it; this file parses and runs it.
var (rootCmd, logFileOpt, captureFixtureOpt, aefRunCommand) = AgentEval.Cli.CliCommandTree.Build();

var nativeAefOptions = new AefCliOptions(rootCmd);
var parseResult = rootCmd.Parse(args);

// A parse error — an unknown command or option, a value that does not convert (`--runs abc`), a missing required
// option, no command at all — exits ExitCodes.UsageError (2). System.CommandLine's own ParseErrorAction prints the
// errors to stderr and the help to stdout, then returns 1, which this CLI's contract reserves for a failed evaluation
// (ExitCodes.TestFailure): CI could not tell a typo from a failing run. Its output is kept; only the code changes.
// --help and --version are not parse errors: their actions clear the errors and return 0.
// This runs before --log-file and --capture-fixture are read: reading an option whose own value failed to parse
// throws, and an invocation that never started must not create or truncate either file.
if (parseResult.Action is System.CommandLine.Invocation.ParseErrorAction)
{
    await parseResult.InvokeAsync();
    return AgentEval.Cli.ExitCodes.UsageError;
}

// A dry-run must not create even explicitly requested diagnostic files.
if (parseResult.Action is System.CommandLine.Help.HelpAction)
    return await parseResult.InvokeAsync();

if (parseResult.CommandResult.Command == aefRunCommand && AgentEval.Cli.Runner.RunCommand.IsDryRun(parseResult))
    return await nativeAefOptions.InvokeAsync(parseResult, () => parseResult.InvokeAsync());

return await nativeAefOptions.InvokeAsync(parseResult, async () =>
{
    using var logWriter = VerboseLog.Initialize(parseResult.GetValue(logFileOpt));
    using var fixtureWriter = FixtureCapture.Initialize(parseResult.GetValue(captureFixtureOpt));
    return await parseResult.InvokeAsync();
});

namespace AgentEval.Cli.Commands
{
    using AgentEval.Core;
    using AgentEval.Decisions;

    /// <summary>
    /// The <c>--decisions</c> switch of <c>bench gdpr calibrate</c> and <c>bench eu-ai-act calibrate</c>: grades the
    /// golden datasets with the decision model instead of the generative judge, and names that judge in the report.
    /// </summary>
    internal static class DecisionCalibration
    {
        /// <summary>
        /// Runs <paramref name="runCalibration"/> with no evaluator override when <paramref name="decisions"/> is
        /// off, and otherwise with a <see cref="DecisionJudge"/> and the identity its report header names it by.
        /// </summary>
        /// <param name="decisions">Whether <c>--decisions</c> was passed.</param>
        /// <param name="runCalibration">The calibration to run, given the evaluator override and its identity.</param>
        /// <param name="resolveOptions">The transport source; <see langword="null"/> reads the environment.</param>
        /// <param name="createClient">Builds the client from the options; <see langword="null"/> builds the HTTP client.</param>
        /// <returns>
        /// What <paramref name="runCalibration"/> returned, or <see cref="ExitCodes.RuntimeError"/> when
        /// <c>--decisions</c> was passed and no decision-model transport is configured.
        /// </returns>
        internal static async Task<int> RunAsync(
            bool decisions,
            Func<IEvaluator?, CalibrationJudgeIdentity?, Task<int>> runCalibration,
            Func<(SystemOneClientOptions? Options, string? Diagnostic)>? resolveOptions = null,
            Func<SystemOneClientOptions, IDecisionClient>? createClient = null)
        {
            ArgumentNullException.ThrowIfNull(runCalibration);
            if (!decisions)
                return await runCalibration(null, null);

            resolveOptions ??= static () => DecisionClientFactory.TryResolve();
            createClient ??= static options => new SystemOneDecisionClient(options);

            var (decisionOptions, decisionDiagnostic) = resolveOptions();
            if (decisionOptions is null)
            {
                Console.Error.WriteLine($"✖ --decisions needs a decision-model transport: {decisionDiagnostic} Set {DecisionClientFactory.RequiredVariables}.");
                return ExitCodes.RuntimeError;
            }

            var client = createClient(decisionOptions);
            try
            {
                var judge = new DecisionJudge(client, decisionOptions.Model);
                Console.Error.WriteLine($"✔ Decision-model judge configured — {decisionOptions.ProviderName}, model={decisionOptions.Model} (requested; the provider may serve a different build under this name).");
                return await runCalibration(judge, IdentityOf(decisionOptions));
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// What the calibration report names the decision judge: the provider, and the model requested from it. The
        /// provider may answer with a different build under that name (an alias such as <c>jev-latest</c> moves), so
        /// the model is labelled as requested. Never the key and never the endpoint.
        /// </summary>
        internal static CalibrationJudgeIdentity IdentityOf(SystemOneClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var provider = options.ProviderName switch
            {
                "typesafe" => "TypeSafe",
                "openrouter" => "OpenRouter",
                var other => other,
            };
            return new CalibrationJudgeIdentity(
                $"{provider} decision model (--decisions)",
                $"{options.Model} (requested; the provider may serve a different build under this name)");
        }
    }
}
