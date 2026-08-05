// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.RegularExpressions;
using AgentEval.MAF.Gatekeeper;

namespace AgentEval.Samples;

/// <summary>
/// Offline Phase-7 design fixture for dangerous-tool contracts. These sample-local gates deliberately implement
/// narrow mock grammars; they exercise gate verdicts, fail-closed argument handling, and useful API shapes, not safety
/// for arbitrary SQL dialects, browsers, cloud CLIs, or package managers.
/// </summary>
public static class GatekeeperMockedDangerousTools
{
    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("13");
        Console.WriteLine("\n=== Gatekeeper — Mocked Dangerous-Tool Contracts ===\n");
        Console.WriteLine(
            "   Scope: offline contract fixture only. No database, browser, cloud account, or package manager is contacted.\n");

        var component = new MockDangerousToolHost();
        var allowed = 0;
        var blocked = 0;

        await AttemptAsync(
            new MockSqlReadOnlyGate(["customers", "orders"]),
            component,
            "sql_query",
            [("statement", "SELECT id, name FROM customers WHERE id = 7")],
            "SQL: allow one narrow read",
            result => Tally(result, ref allowed, ref blocked));
        await AttemptAsync(
            new MockSqlReadOnlyGate(["customers", "orders"]),
            component,
            "sql_query",
            [("statement", "SELECT * FROM customers; DELETE FROM customers")],
            "SQL: block stacked write",
            result => Tally(result, ref allowed, ref blocked));

        await AttemptAsync(
            new MockBrowserNavigationGate(["docs.example.test"]),
            component,
            "browser_navigate",
            [("url", "https://docs.example.test/guides/gatekeeper")],
            "Browser: allow exact HTTPS host",
            result => Tally(result, ref allowed, ref blocked));
        await AttemptAsync(
            new MockBrowserNavigationGate(["docs.example.test"]),
            component,
            "browser_navigate",
            [("url", "https://docs.example.test.evil.invalid/collect")],
            "Browser: block suffix-confusion host",
            result => Tally(result, ref allowed, ref blocked));

        await AttemptAsync(
            new MockCloudOperationGate([("storage", "list")]),
            component,
            "cloud_operation",
            [("service", "storage"), ("operation", "list")],
            "Cloud: allow structured operation",
            result => Tally(result, ref allowed, ref blocked));
        await AttemptAsync(
            new MockCloudOperationGate([("storage", "list")]),
            component,
            "cloud_operation",
            [("service", "compute"), ("operation", "delete")],
            "Cloud: block unapproved operation",
            result => Tally(result, ref allowed, ref blocked));

        await AttemptAsync(
            new MockPackageRestoreGate(["AgentEval", "Microsoft.Extensions.AI"]),
            component,
            "package_restore",
            [("package", "AgentEval"), ("version", "1.0.0")],
            "Package: allow approved restore",
            result => Tally(result, ref allowed, ref blocked));
        await AttemptAsync(
            new MockPackageRestoreGate(["AgentEval", "Microsoft.Extensions.AI"]),
            component,
            "package_restore",
            [("package", "AgentEval"), ("version", "latest")],
            "Package: block floating version",
            result => Tally(result, ref allowed, ref blocked));
        await AttemptAsync(
            new MockPackageRestoreGate(["AgentEval", "Microsoft.Extensions.AI"]),
            component,
            "package_restore",
            [("package", "unreviewed.postinstall.runner"), ("version", "latest")],
            "Package: block unapproved package",
            result => Tally(result, ref allowed, ref blocked));

        Console.WriteLine(
            $"\n   Result: {allowed} allowed and executed by the mock host; {blocked} blocked before execution.");
        Console.WriteLine($"   Mock executions observed: {component.ExecutionCount}");
        if (allowed != 4 || blocked != 5 || component.ExecutionCount != 4)
        {
            throw new InvalidOperationException("The mocked contract fixture produced an unexpected verdict count.");
        }

        Console.WriteLine(
            "\n   Design conclusion: structured cloud/package APIs are substantially safer to contract than raw CLI text. " +
            "Production SQL and browser predicates still need dialect/runtime-specific corpora.");
        Console.WriteLine("\n=== Gatekeeper — Mocked Dangerous-Tool Contracts Complete ===");
    }

    private static async Task AttemptAsync(
        IToolGate gate,
        MockDangerousToolHost component,
        string functionName,
        (string Name, object? Value)[] arguments,
        string label,
        Action<bool> tally)
    {
        var call = new GatedToolCall(
            functionName,
            arguments.ToDictionary(item => item.Name, item => item.Value),
            "phase-7-mock-agent",
            Iteration: 0,
            FunctionCallIndex: 0,
            FunctionCount: 1,
            IsStreaming: false,
            Messages: null);
        var verdict = await gate.InspectAsync(call);
        var allowed = verdict.Action == ToolGateAction.Allow;
        if (allowed)
        {
            component.Execute(functionName);
        }

        tally(allowed);
        Console.WriteLine(
            $"   {(allowed ? "✅ ALLOW" : "🛑 BLOCK")}  {label} " +
            $"({(allowed ? "mock body executed" : verdict.Reason)})");
    }

    private static void Tally(bool allowed, ref int allowedCount, ref int blockedCount)
    {
        if (allowed)
        {
            allowedCount++;
        }
        else
        {
            blockedCount++;
        }
    }

    private static bool TryGetRequiredString(
        GatedToolCall call,
        string argument,
        int maxLength,
        out string value)
    {
        value = string.Empty;
        if (call.Arguments is null ||
            !call.Arguments.TryGetValue(argument, out var raw) ||
            raw is not string text)
        {
            return false;
        }

        text = text.Trim();
        if (text.Length is 0 || text.Length > maxLength || text.Any(char.IsControl))
        {
            return false;
        }

        value = text;
        return true;
    }

    private sealed class MockDangerousToolHost
    {
        public int ExecutionCount { get; private set; }

        public void Execute(string functionName)
        {
            _ = functionName;
            ExecutionCount++;
        }
    }

    private sealed class MockSqlReadOnlyGate(IEnumerable<string> allowedTables) : IToolGate
    {
        private static readonly Regex TableReferencePattern = new(
            @"\b(?:FROM|JOIN)\s+(?<table>[A-Za-z_][A-Za-z0-9_.]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        private static readonly Regex ForbiddenTokenPattern = new(
            @"\b(?:ALTER|CALL|COPY|CREATE|DELETE|DROP|EXEC|EXECUTE|GRANT|INSERT|INTO|MERGE|REVOKE|TRUNCATE|UNION|UPDATE|WITH)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        private readonly HashSet<string> _allowedTables =
            new(allowedTables, StringComparer.OrdinalIgnoreCase);

        public string PolicyName => "sample.mock-sql-read-only";

        public GateCost Cost => GateCost.Bounded;

        public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(call.FunctionName, "sql_query", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName));
            }

            if (!TryGetRequiredString(call, "statement", 4096, out var statement))
            {
                return Block("missing, malformed, or oversized SQL statement");
            }

            try
            {
                if (!statement.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) ||
                    statement.Contains(';', StringComparison.Ordinal) ||
                    statement.Contains("--", StringComparison.Ordinal) ||
                    statement.Contains("/*", StringComparison.Ordinal) ||
                    statement.Contains("*/", StringComparison.Ordinal) ||
                    ForbiddenTokenPattern.IsMatch(statement))
                {
                    return Block("statement is outside the sample's single-SELECT subset");
                }

                var tables = TableReferencePattern.Matches(statement)
                    .Select(match => match.Groups["table"].Value)
                    .ToArray();
                if (tables.Length == 0 || tables.Any(table => !_allowedTables.Contains(table)))
                {
                    return Block("statement references no table or a table outside the sample allow-list");
                }

                return ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName));
            }
            catch (RegexMatchTimeoutException)
            {
                return Block("SQL inspection exceeded its bounded time budget");
            }
        }

        private ValueTask<ToolGateVerdict> Block(string reason) =>
            ValueTask.FromResult(ToolGateVerdict.Block(PolicyName, reason));
    }

    private sealed class MockBrowserNavigationGate(IEnumerable<string> allowedHosts) : IToolGate
    {
        private readonly HashSet<string> _allowedHosts =
            new(allowedHosts.Select(host => host.Trim().ToLowerInvariant()), StringComparer.Ordinal);

        public string PolicyName => "sample.mock-browser-navigation";

        public GateCost Cost => GateCost.Bounded;

        public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(call.FunctionName, "browser_navigate", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName));
            }

            if (!TryGetRequiredString(call, "url", 2048, out var text) ||
                !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !uri.IsDefaultPort)
            {
                return Block("URL must be an absolute default-port HTTPS URI without user information");
            }

            string host;
            try
            {
                host = uri.IdnHost.ToLowerInvariant();
            }
            catch (UriFormatException)
            {
                return Block("URL host is malformed");
            }

            return _allowedHosts.Contains(host)
                ? ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName))
                : Block("URL host is outside the sample allow-list");
        }

        private ValueTask<ToolGateVerdict> Block(string reason) =>
            ValueTask.FromResult(ToolGateVerdict.Block(PolicyName, reason));
    }

    private sealed class MockCloudOperationGate(IEnumerable<(string Service, string Operation)> allowed) : IToolGate
    {
        private readonly HashSet<string> _allowed = new(
            allowed.Select(item => Key(item.Service, item.Operation)),
            StringComparer.OrdinalIgnoreCase);

        public string PolicyName => "sample.mock-cloud-operation";

        public GateCost Cost => GateCost.PureCode;

        public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(call.FunctionName, "cloud_operation", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName));
            }

            if (!TryGetRequiredString(call, "service", 128, out var service) ||
                !TryGetRequiredString(call, "operation", 128, out var operation))
            {
                return Block("service and operation must be bounded strings");
            }

            return _allowed.Contains(Key(service, operation))
                ? ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName))
                : Block("structured cloud operation is outside the sample allow-list");
        }

        private static string Key(string service, string operation) => service + "\0" + operation;

        private ValueTask<ToolGateVerdict> Block(string reason) =>
            ValueTask.FromResult(ToolGateVerdict.Block(PolicyName, reason));
    }

    private sealed class MockPackageRestoreGate(IEnumerable<string> allowedPackages) : IToolGate
    {
        private readonly HashSet<string> _allowedPackages =
            new(allowedPackages, StringComparer.OrdinalIgnoreCase);

        public string PolicyName => "sample.mock-package-restore";

        public GateCost Cost => GateCost.PureCode;

        public ToolGatePolicy MinimumPolicy => ToolGatePolicy.ReplaceResult;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(call.FunctionName, "package_restore", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName));
            }

            if (!TryGetRequiredString(call, "package", 256, out var package) ||
                !TryGetRequiredString(call, "version", 128, out var version))
            {
                return Block("package and version must be bounded strings");
            }

            return _allowedPackages.Contains(package) && IsPinnedVersion(version)
                ? ValueTask.FromResult(ToolGateVerdict.Allow(PolicyName))
                : Block("package is outside the sample allow-list or its version is not pinned");
        }

        private static bool IsPinnedVersion(string version) =>
            version.Split('.', StringSplitOptions.None) is [var major, var minor, var patch] &&
            new[] { major, minor, patch }.All(
                part => part.Length > 0 && part.All(char.IsAsciiDigit));

        private ValueTask<ToolGateVerdict> Block(string reason) =>
            ValueTask.FromResult(ToolGateVerdict.Block(PolicyName, reason));
    }
}
