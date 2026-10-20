// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentEval.Samples;

/// <summary>
/// The free offline check of the whole catalogue (<c>dotnet run -- --offline-check</c>): every sample runs in its own
/// process with no provider configured, non-interactive and with stdin closed, and its exit code is compared with
/// <c>offline-check-expected.txt</c>. A sample that crashes (exit 3), hangs past the per-sample timeout, or exits with
/// any code its row does not name fails the check, and so does a sample with no row or a row with no sample.
/// </summary>
/// <remarks>
/// <para>
/// The table is keyed by the sample's stable id, its class name (<c>--list-samples</c> prints them), never by the
/// flattened menu number, which shifts whenever a sample is added. The menu label (<c>C2</c>) is printed beside it for
/// people, and is not part of the key.
/// </para>
/// <para>
/// The check strips every provider variable from the child environment before it starts a sample, so a run in a
/// shell that holds real keys is still free; the child refuses to run if one gets through anyway.
/// </para>
/// </remarks>
internal static class OfflineSampleCheck
{
    /// <summary>The expected-codes table, next to the binary (copied there by the project).</summary>
    public const string ExpectationsFileName = "offline-check-expected.txt";

    /// <summary>Set in the child environment; a sample run that sees it refuses to start if a provider is configured.</summary>
    public const string ChildMarkerVariable = "AGENTEVAL_SAMPLES_OFFLINE_CHECK";

    /// <summary>The per-sample timeout when <c>--timeout</c> is not given.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    // Every variable that can select, credential or point at a model, a judge or a live agent, plus the samples' own
    // switches (--mock, --dry-run, --preset) that would change what a sample does. Matched as a name prefix.
    private static readonly Regex s_strippedVariables = new(
        "^(AZURE|AZUREOPENAI|OPENAI|FOUNDRY|MS_FOUNDRY|MAFARTCOMP|APPLICATIONINSIGHTS|OPENROUTER|TYPESAFE|JEV_|" +
        "COPILOTSTUDIO|BITDEER|AI_INFERENCE|ANTHROPIC|GEMINI|MISTRAL|OLLAMA|LONGMEMEVAL_|AGENTEVAL_SAMPLES_|AGENTEVAL_A2A_)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly int[] s_expectableCodes =
        [SampleOutcome.Completed, SampleOutcome.Failed, SampleOutcome.Warning, SampleOutcome.NotMeasured];

    /// <summary>One row of the table: a sample id, the exit code it must produce offline, and why.</summary>
    public sealed record Expectation(string Id, int ExitCode, string Note);

    /// <summary>One catalogue entry as the check sees it.</summary>
    /// <param name="Label">The menu label, e.g. <c>C2</c>; for people only.</param>
    /// <param name="Id">The stable id the table is keyed by.</param>
    public sealed record CatalogueEntry(string Label, string Id);

    /// <summary>
    /// The suffix of a row that runs its sample with <c>--mock</c> as well: <c>HelloWorld+mock 0</c>. Most samples stop at
    /// "no provider" offline, so their code past that point never ran in CI; the ones with a labelled mock walkthrough
    /// (Getting Started, Extensibility, Auto-Audit) run it for free this way. Such a row is extra: the sample still needs
    /// its plain row.
    /// </summary>
    public const string MockSuffix = "+mock";

    // AIConfig.MockVariable, which --mock sets in the child. Spelled out: the test project compiles this file without
    // AIConfig.
    private const string MockVariable = "AGENTEVAL_SAMPLES_MOCK";

    /// <summary>What one sample run produced.</summary>
    public sealed record RunResult(CatalogueEntry Sample, int? ExitCode, bool TimedOut, TimeSpan Elapsed, string LogFile);

    /// <summary>
    /// Parses the table: one row per line, <c>id code note…</c>; blank lines and lines starting with <c>#</c> are
    /// ignored. Throws <see cref="FormatException"/> naming the line for a malformed row, a code a sample may not be
    /// expected to produce (2 or 3), or a duplicate id.
    /// </summary>
    public static IReadOnlyList<Expectation> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var rows = new List<Expectation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var code))
                throw new FormatException($"{ExpectationsFileName} line {lineNumber}: expected '<id> <exit code> <note>', got '{line}'.");
            if (!s_expectableCodes.Contains(code))
                throw new FormatException(
                    $"{ExpectationsFileName} line {lineNumber}: {parts[0]} expects exit {code}; a sample may be expected to " +
                    "exit 0, 1, 10 or 11 only (2 is an unknown sample, 3 a crash).");
            if (!seen.Add(parts[0]))
                throw new FormatException($"{ExpectationsFileName} line {lineNumber}: {parts[0]} has a second row.");

            rows.Add(new Expectation(parts[0], code, parts.Length > 2 ? parts[2] : ""));
        }

        return rows;
    }

    /// <summary>
    /// The table's coverage of the catalogue: every sample must have a row, and every row a sample. Empty when they
    /// match one to one.
    /// </summary>
    public static IReadOnlyList<string> CoverageProblems(IReadOnlyList<CatalogueEntry> catalogue, IReadOnlyList<Expectation> table)
    {
        var problems = new List<string>();
        var duplicates = catalogue.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var duplicate in duplicates)
            problems.Add($"two catalogue entries share the id {duplicate.Key} ({string.Join(", ", duplicate.Select(c => c.Label))})");

        var rows = table.Where(e => !IsMockRow(e)).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in catalogue.Where(c => !rows.Contains(c.Id)))
            problems.Add($"{sample.Label} {sample.Id} has no row in {ExpectationsFileName}: add one with the exit code it gives offline");

        var ids = catalogue.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Where(e => !ids.Contains(SampleIdOf(e))))
            problems.Add($"{ExpectationsFileName} has a row for {row.Id}, which is not in the catalogue: remove it or fix the id");

        return problems;
    }

    /// <summary>True for a <see cref="MockSuffix"/> row.</summary>
    public static bool IsMockRow(Expectation row) => row.Id.EndsWith(MockSuffix, StringComparison.OrdinalIgnoreCase);

    // The sample a row runs: its id, without the mock suffix.
    private static string SampleIdOf(Expectation row) => IsMockRow(row) ? row.Id[..^MockSuffix.Length] : row.Id;

    /// <summary>The name of every variable in <paramref name="environment"/> the check strips. Names only, never values.</summary>
    public static IReadOnlyList<string> ProviderVariables(IEnumerable<string> environment) =>
        environment.Where(name => s_strippedVariables.IsMatch(name)).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The provider variables set in this process: the child-side guard.</summary>
    public static IReadOnlyList<string> ProviderVariablesInThisProcess() =>
        ProviderVariables(Environment.GetEnvironmentVariables().Keys.Cast<string>())
            .Where(name => !string.Equals(name, ChildMarkerVariable, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(name, "AGENTEVAL_SAMPLES_NONINTERACTIVE", StringComparison.OrdinalIgnoreCase)
                        // set by the child itself from a +mock row's --mock: canned replies, no provider
                        && !string.Equals(name, MockVariable, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>
    /// Runs the check: every sample in <paramref name="catalogue"/> (or only <paramref name="only"/>), one process each.
    /// Returns 0 when every sample exits as its row says and the table covers the catalogue, 1 otherwise.
    /// </summary>
    /// <param name="catalogue">The menu's samples, in menu order.</param>
    /// <param name="expectationsFile">The table.</param>
    /// <param name="host">The program that starts a sample: this binary's apphost, or <c>dotnet</c>.</param>
    /// <param name="hostArguments">Arguments before <c>--sample &lt;id&gt;</c> (the assembly path when the host is <c>dotnet</c>).</param>
    /// <param name="timeout">How long one sample may run before it counts as a hang and is killed.</param>
    /// <param name="logDirectory">Where each sample's output is written; also holds the samples' working directory.</param>
    /// <param name="only">Sample ids or menu labels to run; null runs them all. An id or label that names no sample is a
    /// usage error (exit 2), so a typo never passes as "0/0 exited as expected".</param>
    public static async Task<int> RunAsync(
        IReadOnlyList<CatalogueEntry> catalogue,
        string expectationsFile,
        string host,
        IReadOnlyList<string> hostArguments,
        TimeSpan timeout,
        string logDirectory,
        IReadOnlySet<string>? only = null)
    {
        Console.WriteLine($"\nOffline sample check — no provider, non-interactive, stdin closed, {timeout.TotalSeconds:F0}s per sample");
        Console.WriteLine($"   expected : {expectationsFile}");
        Console.WriteLine($"   logs     : {logDirectory}");

        IReadOnlyList<Expectation> table;
        try
        {
            table = Parse(await File.ReadAllTextAsync(expectationsFile));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"   ✘ the expected-codes table could not be read: {ex.Message}");
            return SampleOutcome.Failed;
        }

        var problems = CoverageProblems(catalogue, table).ToList();
        var expected = table.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var workingDirectory = Path.Combine(logDirectory, "cwd");
        Directory.CreateDirectory(workingDirectory);
        var stripped = ProviderVariables(Environment.GetEnvironmentVariables().Keys.Cast<string>());
        Console.WriteLine($"   stripped : {stripped.Count} provider or sample-switch variable(s) from every sample's environment\n");

        bool Names(CatalogueEntry c, string wanted) =>
            string.Equals(c.Id, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Label, wanted, StringComparison.OrdinalIgnoreCase);
        var unknown = only?.Where(o => !catalogue.Any(c => Names(c, o))).ToList() ?? [];
        if (unknown.Count > 0)
        {
            Console.WriteLine($"   ✘ --only names no sample: {string.Join(", ", unknown)} (see --list-samples)");
            return SampleOutcome.UsageError;
        }

        var selected = catalogue.Where(c => only is null || only.Any(o => Names(c, o))).ToList();
        if (selected.Count == 0)
        {
            Console.WriteLine("   ✘ no sample selected");
            return SampleOutcome.UsageError;
        }
        // Each selected sample plainly, then the ones with a +mock row again with --mock.
        var runs = selected.Select(c => (Sample: c, Mock: false))
            .Concat(selected.Where(c => expected.ContainsKey(c.Id + MockSuffix)).Select(c => (Sample: c, Mock: true)))
            .ToList();
        var mismatches = new List<(RunResult Run, string Why)>();
        foreach (var (entry, mock) in runs)
        {
            var sample = mock ? entry with { Label = entry.Label + MockSuffix, Id = entry.Id + MockSuffix } : entry;
            var run = await RunOneAsync(sample, host, hostArguments, timeout, logDirectory, workingDirectory, entry.Id, mock);
            var row = expected.GetValueOrDefault(sample.Id);
            var why = Judge(run, row);
            var mark = why is null ? "✔" : "✘";
            var got = run.TimedOut ? "TIMEOUT" : $"{run.ExitCode,2} {Describe(run.ExitCode)}";
            var want = row is null ? "no row" : $"expected {row.ExitCode}";
            Console.WriteLine($"   {mark} {sample.Label,-4} {sample.Id,-42} {got,-17} ({want})  {run.Elapsed.TotalSeconds,5:F1}s");
            if (why is not null)
                mismatches.Add((run, why));
        }

        Console.WriteLine();
        Console.WriteLine($"Offline sample check: {runs.Count - mismatches.Count}/{runs.Count} run(s) exited as expected " +
                          $"({selected.Count} sample(s), {runs.Count - selected.Count} also with --mock)" +
                          (problems.Count > 0 ? $"; {problems.Count} table problem(s)" : "") + ".");
        foreach (var problem in problems)
            Console.WriteLine($"   ✘ {problem}");
        foreach (var (run, why) in mismatches)
        {
            Console.WriteLine($"\n   ✘ {run.Sample.Label} {run.Sample.Id}: {why}");
            Console.WriteLine($"     log: {run.LogFile}");
            foreach (var line in Tail(run.LogFile, 12))
                Console.WriteLine($"     | {line}");
        }

        return mismatches.Count == 0 && problems.Count == 0 ? SampleOutcome.Completed : SampleOutcome.Failed;
    }

    /// <summary>Why <paramref name="run"/> does not meet <paramref name="row"/>; null when it does.</summary>
    public static string? Judge(RunResult run, Expectation? row)
    {
        if (run.TimedOut)
            return $"no exit within the timeout ({run.Elapsed.TotalSeconds:F0}s): a hang, killed";
        if (run.ExitCode == SampleOutcome.RuntimeError)
            return "the sample threw (exit 3)";
        if (row is null)
            return $"exit {run.ExitCode}, and the sample has no row in {ExpectationsFileName}";
        return run.ExitCode == row.ExitCode ? null : $"exit {run.ExitCode} ({Describe(run.ExitCode)}), expected {row.ExitCode} ({Describe(row.ExitCode)})";
    }

    private static string Describe(int? exitCode) => exitCode switch
    {
        SampleOutcome.Completed => "COMPLETED",
        SampleOutcome.Failed => "FAIL",
        SampleOutcome.UsageError => "UNKNOWN SAMPLE",
        SampleOutcome.RuntimeError => "ERROR",
        SampleOutcome.Warning => "WARN",
        SampleOutcome.NotMeasured => "NOT MEASURED",
        null => "no exit code",
        _ => "unexpected code",
    };

    private static async Task<RunResult> RunOneAsync(
        CatalogueEntry sample, string host, IReadOnlyList<string> hostArguments, TimeSpan timeout,
        string logDirectory, string workingDirectory, string sampleId, bool mock)
    {
        var logFile = Path.Combine(logDirectory, $"{sample.Label}-{sample.Id}.log");
        var start = new ProcessStartInfo
        {
            FileName = host,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in hostArguments)
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--sample");
        start.ArgumentList.Add(sampleId);
        if (mock)
            start.ArgumentList.Add("--mock");

        foreach (var name in ProviderVariables(start.Environment.Keys))
            start.Environment.Remove(name);
        start.Environment["AGENTEVAL_SAMPLES_NONINTERACTIVE"] = "1";
        start.Environment["AGENTEVAL_GATEKEEPER_FORCE_OFFLINE"] = "true";
        start.Environment[ChildMarkerVariable] = "1";

        var gate = new object();
        await using var log = new StreamWriter(logFile, append: false, new UTF8Encoding(false));
        void Append(string? line)
        {
            if (line is null) return;
            lock (gate) log.WriteLine(line);
        }

        var watch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.StandardInput.Close();   // stdin closed: a sample that waits for a key reads end-of-input at once
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = new CancellationTokenSource(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // It exited between the deadline and the kill, or a child could not be killed; the bounded wait below
                // still returns.
            }

            // Bounded: a grandchild that escaped the kill and still holds the output pipe must not hang the check
            // until the job's own time limit.
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(drain.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"   (the killed sample's output pipe did not close within 30 s; its log may be incomplete)");
            }
        }

        // WaitForExitAsync returns once the output pipes close, so the log is complete here.
        watch.Stop();
        lock (gate) log.Flush();
        return new RunResult(sample, timedOut ? null : process.ExitCode, timedOut, watch.Elapsed, logFile);
    }

    private static IEnumerable<string> Tail(string file, int lines)
    {
        try
        {
            return File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(lines).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }
}
