// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using A2A;
using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Tests.Microsoft365.Agent;
using Microsoft.Identity.Client;
using Xunit;

namespace AgentEval.Tests.Docs;

/// <summary>
/// <c>docs/microsoft365.md</c> is the one home of the Microsoft 365 failure catalogue. Its Troubleshooting table must list
/// every Entra code <see cref="WorkIqAadstsCatalog"/> knows and every issue code <see cref="WorkIqIssueClassifier"/>
/// emits for a failure the provider can register, and no code the provider never registers. The in-code example on that
/// page and in the package README must be the compiled <see cref="Microsoft365InCodeSnippet"/> region, so it can only
/// name members that exist.
/// </summary>
public sealed class Microsoft365DocsTests
{
    private const string Page = "docs/microsoft365.md";
    private const string SnippetName = "m365-in-code";

    private static readonly Regex s_httpStatusCode = new(@"^http\.\d+$", RegexOptions.CultureInvariant);

    [Fact]
    public void TroubleshootingTable_ListsEveryCatalogedAadstsCode()
    {
        var table = TroubleshootingTable();

        var missing = WorkIqAadstsCatalog.Entries.Keys
            .Where(code => !table.Contains($"`{code}`", StringComparison.Ordinal))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(WorkIqAadstsCatalog.Entries);
        Assert.True(missing.Count == 0, $"{Page} Troubleshooting has no row for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void TroubleshootingTable_ListsEveryIssueCodeTheClassifierEmits()
    {
        var table = TroubleshootingTable();

        var codes = ClassifierCodes().Select(DocumentedForm).Distinct(StringComparer.Ordinal).ToList();
        var missing = codes
            .Where(code => !table.Contains($"`{code}`", StringComparison.Ordinal))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        Assert.Contains("turn.failed", codes);
        Assert.Contains("auth.sign_in_not_completed", codes);
        Assert.True(missing.Count == 0, $"{Page} Troubleshooting has no row for: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The classifier also names <c>task.*</c> and <c>budget.recovery_exhausted</c>, but only for failures a turn builds
    /// (a terminal task state, a local budget). The turn registers <c>turn.&lt;outcome&gt;</c> for them, so the table
    /// leaves them out.
    /// </summary>
    [Fact]
    public void TurnOnlyFailures_AreRegisteredWithTheTurnCode()
    {
        var request = new WorkIqTurnRequest("prompt", "case-1", "turn-1");

        var failed = TurnResults.NotCompleted(request, WorkIqTurnOutcome.Failed);
        var terminal = failed with { Failure = failed.Failure! with { RemoteTaskState = "TASK_STATE_FAILED" } };
        Assert.Equal("task.failed", WorkIqIssueClassifier.ForFailure(terminal.Failure!).Code);
        Assert.Equal("turn.failed", WorkIqIssueClassifier.ForTurnOutcome(terminal).Code);

        var indeterminate = TurnResults.NotCompleted(request, WorkIqTurnOutcome.Indeterminate);
        var exhausted = indeterminate with { Failure = indeterminate.Failure! with { LocalBudget = WorkIqLocalBudget.RecoveryBudget } };
        Assert.Equal("budget.recovery_exhausted", WorkIqIssueClassifier.ForFailure(exhausted.Failure!).Code);
        Assert.Equal("turn.indeterminate", WorkIqIssueClassifier.ForTurnOutcome(exhausted).Code);
    }

    [Theory]
    [InlineData("task.failed")]
    [InlineData("task.rejected")]
    [InlineData("task.canceled")]
    [InlineData("remote.error")]
    [InlineData("budget.recovery_exhausted")]
    [InlineData("turn.not_completed")]
    [InlineData("auth.unauthorized")]
    [InlineData("auth.forbidden")]
    [InlineData("auth.untrusted_destination")]
    [InlineData("auth.msal_service_error")]
    [InlineData("auth.msal_client_error")]
    [InlineData("stream.failed")]
    public void TroubleshootingTable_DoesNotListACodeTheProviderNeverRegisters(string code)
    {
        Assert.DoesNotContain($"`{code}`", TroubleshootingTable(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docs/microsoft365.md")]
    [InlineData("src/AgentEval.Microsoft365/README.md")]
    [InlineData(".github/skills/m365-eval/SKILL.md")]
    public void InCodeExample_IsTheCompiledSnippet(string doc)
    {
        var root = RepoRoot();
        var lines = File.ReadAllLines(Path.Combine(root, doc));
        var marker = Array.FindIndex(lines, l => l.Trim() == $"<!-- snippet: {SnippetName} -->");
        Assert.True(
            marker >= 0 && marker + 1 < lines.Length && lines[marker + 1].Trim() == "```csharp",
            $"{doc} has no `<!-- snippet: {SnippetName} -->` marker on the line before a ```csharp block.");

        var block = lines.Skip(marker + 2).TakeWhile(l => l.TrimEnd() != "```").ToList();
        var docUsings = block.Where(IsUsing).Select(UsingNamespace).ToList();
        var docBody = Trim(block.Where(l => !IsUsing(l)).Select(l => l.TrimEnd()).ToList());

        var source = File.ReadAllLines(Path.Combine(root, "tests", "AgentEval.Tests", "Docs", "Microsoft365DocsTests.cs"));
        var begin = Array.FindIndex(source, l => l.Trim() == $"// begin-snippet: {SnippetName}");
        Assert.True(begin >= 0, $"No `// begin-snippet: {SnippetName}` region in Microsoft365DocsTests.cs.");
        var end = Array.FindIndex(source, begin + 1, l => l.Trim() == "// end-snippet");
        Assert.True(end > begin, $"The `{SnippetName}` region has no `// end-snippet`.");
        var region = Trim(Dedent(source[(begin + 1)..end]));

        Assert.Equal(string.Join("\n", region), string.Join("\n", docBody));
        Assert.NotEmpty(docUsings);
        var sourceUsings = source.Where(IsUsing).Select(UsingNamespace).ToList();
        Assert.All(docUsings, ns => Assert.Contains(ns, sourceUsings));
    }

    /// <summary>
    /// The codes the classifier gives every failure a client call, a doctor stage or the auth layer can end with, every
    /// turn outcome, and every exception a doctor stage can catch. A remote error without an HTTP status or a JSON-RPC
    /// code, and a local budget, exist only inside a turn (<see cref="TurnOnlyFailures_AreRegisteredWithTheTurnCode"/>).
    /// </summary>
    private static List<string> ClassifierCodes()
    {
        var codes = new List<string>();

        foreach (var status in new[] { 302, 400, 401, 403, 404, 409, 429, 500, 502, 503 })
        {
            codes.Add(WorkIqIssueClassifier.ForHttpStatus(status).Code);
        }

        foreach (var rpc in Enum.GetValues<A2AErrorCode>())
        {
            codes.Add(WorkIqIssueClassifier.ForRpcError((int)rpc).Code);
        }

        codes.Add(WorkIqIssueClassifier.ForRpcError(-1).Code);

        var operations = new[] { WorkIqOperation.Discover, WorkIqOperation.Send, WorkIqOperation.GetTask, WorkIqOperation.CancelTask, WorkIqOperation.Local };
        foreach (var category in Enum.GetValues<WorkIqFailureCategory>().Where(c => c != WorkIqFailureCategory.RemoteError))
        {
            foreach (var operation in operations)
            {
                codes.Add(WorkIqIssueClassifier.ForFailure(new WorkIqFailure(category, "safe message", OutcomeCertainty.Known, operation)).Code);
            }
        }

        foreach (var tokenFailure in Enum.GetValues<WorkIqTokenFailure>())
        {
            var failure = new WorkIqFailure(WorkIqFailureCategory.Unauthorized, "AADSTS65001: consent missing", OutcomeCertainty.Known, WorkIqOperation.Send) { TokenFailure = tokenFailure };
            codes.Add(WorkIqIssueClassifier.ForFailure(failure).Code);
        }

        var request = new WorkIqTurnRequest("prompt", "case-1", "turn-1");
        foreach (var outcome in Enum.GetValues<WorkIqTurnOutcome>().Where(o => o != WorkIqTurnOutcome.Completed))
        {
            codes.Add(WorkIqIssueClassifier.ForTurnOutcome(TurnResults.NotCompleted(request, outcome)).Code);
        }

        var exceptions = new List<Exception>
        {
            new WorkIqTurnNotCompletedException(TurnResults.NotCompleted(request, WorkIqTurnOutcome.Rejected)),
            new MsalServiceException("invalid_grant", "AADSTS65001: The user or administrator has not consented to use the application."),
            new MsalClientException("some_new_code", "something local went wrong"),
            new HttpRequestException("Response status code does not indicate success", null, HttpStatusCode.BadGateway),
            new HttpRequestException("ssl", new AuthenticationException("The remote certificate is invalid")),
            new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)),
            new TaskCanceledException("timed out", new TimeoutException()),
            new TimeoutException("timed out"),
            new OperationCanceledException(new CancellationToken(canceled: true)),
            new OperationCanceledException("no token"),
            new JsonException("bad json"),
            new A2AException("sdk said no", A2AErrorCode.InternalError),
            new AuthenticationException("tls"),
            new SocketException((int)SocketError.ConnectionReset),
            new IOException("unexpected EOF"),
            new IOException("reset", new SocketException((int)SocketError.ConnectionReset)),
            new NotSupportedException("not classified"),
        };
        exceptions.AddRange(Enum.GetValues<HttpRequestError>().Select(error => new HttpRequestException(error, "x")));

        foreach (var exception in exceptions)
        {
            codes.Add(WorkIqIssueClassifier.FromException(exception, WorkIqTraceStage.Send).Code);
        }

        codes.Add(WorkIqIssueClassifier.FromException(new InvalidOperationException("bad field"), WorkIqTraceStage.Config).Code);
        return codes;
    }

    /// <summary>The form the table uses for a code with a variable part: <c>http.&lt;status&gt;</c> and <c>exception.&lt;type&gt;</c>.</summary>
    private static string DocumentedForm(string code)
    {
        if (code.StartsWith("exception.", StringComparison.Ordinal))
        {
            return "exception.<type>";
        }

        return s_httpStatusCode.IsMatch(code) && code is not ("http.401" or "http.403" or "http.404" or "http.429")
            ? "http.<status>"
            : code;
    }

    /// <summary>The rows of the table under <c>## Troubleshooting</c> in <see cref="Page"/>.</summary>
    private static string TroubleshootingTable()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "microsoft365.md"));
        var start = Array.FindIndex(lines, l => l.TrimEnd() == "## Troubleshooting");
        Assert.True(start >= 0, $"{Page} has no `## Troubleshooting` section.");
        var rows = lines.Skip(start + 1)
            .TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal))
            .Where(l => l.StartsWith('|'))
            .ToList();
        Assert.NotEmpty(rows);
        return string.Join("\n", rows);
    }

    private static bool IsUsing(string line) => line.StartsWith("using ", StringComparison.Ordinal);

    private static string UsingNamespace(string line)
    {
        var start = "using ".Length;
        var end = line.IndexOf(';', StringComparison.Ordinal);
        return line[start..(end < 0 ? line.Length : end)].Trim();
    }

    private static List<string> Dedent(IReadOnlyList<string> lines)
    {
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return lines.Select(l => l.Trim().Length == 0 ? "" : l[indent..].TrimEnd()).ToList();
    }

    private static List<string> Trim(List<string> lines)
    {
        var first = lines.FindIndex(l => l.Length > 0);
        var last = lines.FindLastIndex(l => l.Length > 0);
        return first < 0 ? new List<string>() : lines.GetRange(first, last - first + 1);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not located.");
    }
}

/// <summary>
/// The in-code example of <c>docs/microsoft365.md</c> and the provider README, compiled with the tests so it can only use
/// real types and members. It is never run: it would sign in and send a prompt.
/// </summary>
internal static class Microsoft365InCodeSnippet
{
    public static async Task RunAsync()
    {
        // begin-snippet: m365-in-code
        var config = Microsoft365Config.Load(new FileInfo("workiq.json"));

        // No default: the agent acts as the signed-in user on real Microsoft 365 data.
        // Pass true only for a non-production tenant and identity.
        await using WorkIqEvaluableAgent agent = Microsoft365AgentFactory.BuildLive(
            config,
            iUnderstandLiveSideEffects: true,
            options: new WorkIqOptions { MaxTurns = 10 });

        try
        {
            AgentResponse reply = await agent.InvokeAsync("In one sentence, what can you help me with?");
            if (!Microsoft365Evidence.IsAnswerMeasured(reply))
            {
                Console.WriteLine("Not measured: several answer candidates, and none was selected.");
                return;
            }

            Console.WriteLine(reply.Text);
            Microsoft365TurnEvidence? evidence = reply.Evidence();
            Console.WriteLine($"citation markers: {evidence?.CitationMarkers.Describe("marker(s)")}");
            Console.WriteLine($"tool executions:  {evidence?.ToolExecutions.Describe("execution(s)")}");
        }
        catch (WorkIqTurnNotCompletedException ex)
        {
            // Only a completed turn becomes an AgentResponse. Nothing was approved or resent.
            Console.Error.WriteLine($"not completed: {ex.TurnResult.Outcome}");
        }
        // end-snippet
    }
}
