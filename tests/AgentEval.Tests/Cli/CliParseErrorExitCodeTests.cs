// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Cli;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// The exit code of a command line System.CommandLine cannot parse, through the real CLI entry point
/// (<c>Program.cs</c>), not a copy of its root command.
/// </summary>
/// <remarks>
/// <para>
/// System.CommandLine's parse-error action returns 1, which is <see cref="ExitCodes.TestFailure"/>: measured with the
/// built CLI, <c>agenteval eval --runs abc</c>, <c>agenteval eval</c> (no <c>--dataset</c>) and
/// <c>agenteval nosuchcommand</c> all exited 1, so CI could not tell a typo from a failing evaluation. Every usage-error
/// test here fails on that behaviour (it returned 1) and passes now that <c>Program.cs</c> returns
/// <see cref="ExitCodes.UsageError"/> (2). The <c>--help</c> and <c>--version</c> tests passed before too: they pin
/// that the new mapping does not catch them.
/// </para>
/// <para>
/// Each test runs <c>Program.cs</c> in-process through the assembly's entry point and redirects the console, hence the
/// <c>ConsoleTests</c> collection. None of these command lines reaches a provider: a parse error stops before any
/// action runs, and the <c>bench</c> cases stop at their own argument checks.
/// </para>
/// </remarks>
[Collection("ConsoleTests")]
public class CliParseErrorExitCodeTests
{
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
    {
        var entryPoint = typeof(ExitCodes).Assembly.EntryPoint
            ?? throw new InvalidOperationException("AgentEval.Cli has no entry point.");

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            // Off the test's synchronization context: a synchronous entry point blocks on the async top-level body.
            var returned = await Task.Run(() => entryPoint.Invoke(null, new object?[] { args }));
            int exitCode;
            if (returned is Task<int> pending)
                exitCode = await pending;
            else if (returned is int code)
                exitCode = code;
            else
                throw new InvalidOperationException($"Unexpected entry-point return value: {returned?.GetType().FullName ?? "null"}.");
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            throw new InvalidOperationException(
                $"The CLI threw instead of exiting: {inner.GetType().Name}: {inner.Message}", inner);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    // ── Parse errors exit 2 ─────────────────────────────────────────────────────

    [Fact]
    public async Task BadValue_ExitsUsageError_AndNamesTheValue()
    {
        var (exitCode, _, stderr) = await RunCliAsync("eval", "--dataset", "data.yaml", "--runs", "abc");

        Assert.Equal(ExitCodes.UsageError, exitCode);
        // System.CommandLine's own message, which may be localised; the offending token is in every language.
        Assert.Contains("abc", stderr);
    }

    [Fact]
    public async Task MissingRequiredOption_ExitsUsageError_AndStillPrintsTheErrorAndTheHelp()
    {
        var (exitCode, stdout, stderr) = await RunCliAsync("eval");

        Assert.Equal(ExitCodes.UsageError, exitCode);
        // Only the exit code changed: System.CommandLine's error still goes to stderr and the command's help to stdout.
        Assert.Contains("--dataset", stderr);
        Assert.Contains("--success-threshold", stdout);
    }

    [Fact]
    public async Task UnknownCommand_ExitsUsageError_AndNamesTheCommand()
    {
        var (exitCode, _, stderr) = await RunCliAsync("nosuchcommand");

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("nosuchcommand", stderr);
    }

    [Fact]
    public async Task UnknownOption_ExitsUsageError()
    {
        var (exitCode, _, stderr) = await RunCliAsync("eval", "--dataset", "data.yaml", "--no-such-option");

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("--no-such-option", stderr);
    }

    [Fact]
    public async Task NoCommand_ExitsUsageError()
    {
        var (exitCode, _, _) = await RunCliAsync();

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task LogFileWithoutAValue_ExitsUsageError_InsteadOfThrowing()
    {
        // Before: --log-file was read before the parse errors were looked at, and reading an option whose own value
        // failed to parse throws InvalidOperationException, so this crashed with an unhandled exception.
        var (exitCode, _, stderr) = await RunCliAsync("eval", "--log-file");

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("--log-file", stderr);
    }

    [Fact]
    public async Task ParseError_DoesNotCreateTheLogFile()
    {
        // Before: --log-file was opened (and truncated) before the parse error was reported.
        var logPath = Path.Combine(Path.GetTempPath(), "agenteval-parse-error-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var (exitCode, _, _) = await RunCliAsync("eval", "--log-file", logPath, "--runs", "abc");

            Assert.Equal(ExitCodes.UsageError, exitCode);
            Assert.False(File.Exists(logPath), "A command line that did not parse must not create the --log-file.");
        }
        finally
        {
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }

    // ── Required options a command checks itself also exit 2 ────────────────────

    [Fact]
    public async Task BenchWithoutSubject_ExitsUsageError()
    {
        // --subject is checked by the action rather than by System.CommandLine; before, this returned 1.
        var (exitCode, _, stderr) = await RunCliAsync("bench", "gdpr");

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("--subject", stderr);
    }

    [Fact]
    public async Task BenchWithoutAFamily_ExitsUsageError()
    {
        var (exitCode, _, stderr) = await RunCliAsync("bench");

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("bench --list", stderr);
    }

    // ── --help and --version are not parse errors ───────────────────────────────

    [Fact]
    public async Task Help_ExitsSuccess()
    {
        var (exitCode, stdout, _) = await RunCliAsync("--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("eval", stdout);
        Assert.Contains("bench", stdout);
    }

    [Fact]
    public async Task SubcommandHelp_WithItsRequiredOptionMissing_ExitsSuccess()
    {
        // --help clears the parse errors: `eval --help` without --dataset is a request for help, not a usage error.
        var (exitCode, stdout, _) = await RunCliAsync("eval", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("--dataset", stdout);
    }

    [Fact]
    public async Task Version_ExitsSuccess()
    {
        var (exitCode, _, _) = await RunCliAsync("--version");

        Assert.Equal(ExitCodes.Success, exitCode);
    }
}
