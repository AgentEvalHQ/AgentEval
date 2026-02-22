// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Output;

/// <summary>
/// Formats RedTeam results for console output with rich formatting.
/// </summary>
public class RedTeamOutputFormatter
{
    private readonly RedTeamOutputOptions _options;
    private readonly RedTeamConsoleTheme _theme;
    private readonly TextWriter _writer;
    private readonly int _width;

    /// <summary>
    /// Creates a new output formatter.
    /// </summary>
    public RedTeamOutputFormatter(
        RedTeamOutputOptions? options = null,
        TextWriter? writer = null)
    {
        _options = options ?? RedTeamOutputOptions.Default;
        _theme = new RedTeamConsoleTheme(_options.UseColors);
        _writer = writer ?? Console.Out;
        _width = DetermineWidth();
    }

    private int DetermineWidth()
    {
        if (_options.MaxWidth > 0)
            return _options.MaxWidth;

        try
        {
            return Console.IsOutputRedirected ? 80 : Math.Max(60, Console.WindowWidth);
        }
        catch
        {
            return 80; // Fallback for non-interactive environments
        }
    }

    /// <summary>
    /// Print the full RedTeam result with configured formatting.
    /// </summary>
    public void Print(RedTeamResult result)
    {
        if (_options.Verbosity == VerbosityLevel.Minimal)
        {
            PrintMinimal(result);
            return;
        }

        PrintHeader(result);

        if (_options.Verbosity >= VerbosityLevel.Summary)
            PrintAttackSummary(result);

        if (_options.Verbosity >= VerbosityLevel.Detailed)
            PrintFailedProbes(result);

        if (_options.Verbosity == VerbosityLevel.Full && _options.ShowSensitiveContent)
            PrintAllProbes(result);

        PrintFooter(result);
    }

    private void PrintMinimal(RedTeamResult result)
    {
        var icon = _options.UseEmoji
            ? (result.Passed ? "🛡️" : "⚠️")
            : (result.Passed ? "[PASS]" : "[FAIL]");

        var scoreColor = result.Passed ? _theme.Success : _theme.Failure;
        WriteLine($"RedTeam: {scoreColor}{result.OverallScore:F1}%{_theme.Reset} {icon} {result.Verdict} " +
            $"({result.TotalProbes} probes, {result.ResistedProbes} resisted)");
    }

    private void PrintHeader(RedTeamResult result)
    {
        var border = new string('═', _width - 2);
        var title = " RedTeam Security Assessment ";
        var padding = Math.Max(0, (_width - title.Length - 2) / 2);

        WriteLine($"╔{border}╗");
        WriteLine($"║{new string(' ', padding)}{_theme.Bold}{title}{_theme.Reset}{new string(' ', Math.Max(0, _width - padding - title.Length - 2))}║");
        WriteLine($"╠{border}╣");

        // Score with color based on threshold
        var scoreColor = result.OverallScore >= 85 ? _theme.Success
            : result.OverallScore >= 70 ? _theme.Warning
            : _theme.Failure;

        var icon = _options.UseEmoji
            ? (result.Passed ? "🛡️ " : "⚠️ ")
            : "";

        WriteLine($"║  {icon}Overall Score: {scoreColor}{result.OverallScore:F1}%{_theme.Reset}");
        WriteLine($"║  Verdict: {_theme.StatusIcon(result.Passed, _options.UseEmoji)} {result.Verdict}");
        WriteLine($"║  Duration: {result.Duration.TotalSeconds:F1}s | Agent: {result.AgentName}");
        WriteLine($"║  Probes: {result.TotalProbes} total, {result.ResistedProbes} resisted, {result.SucceededProbes} compromised");
        WriteLine($"╠{border}╣");
    }

    private void PrintAttackSummary(RedTeamResult result)
    {
        WriteLine($"║  {_theme.Bold}Attack Results:{_theme.Reset}");
        WriteLine($"║");

        // Column widths (constants for formatting)
        const int attackCol = 23;
        const int resistedCol = 12;
        const int rateCol = 8;
        const int severityCol = 10;

        WriteLine($"║  {"Attack".PadRight(attackCol)} {"Resisted".PadRight(resistedCol)} {"Rate".PadRight(rateCol)} {"Severity".PadRight(severityCol)}");
        WriteLine($"║  {new string('─', Math.Min(_width - 6, attackCol + resistedCol + rateCol + severityCol + 3))}");

        foreach (var attack in result.AttackResults)
        {
            var rate = attack.TotalCount > 0
                ? (double)attack.ResistedCount / attack.TotalCount
                : 1.0;
            var rateStr = $"{rate:P0}".PadRight(rateCol);
            var icon = _theme.RateIcon(rate, _options.UseEmoji);

            var severityStr = _theme.Colorize(attack.Severity.ToString(), attack.Severity);
            var displayName = attack.AttackDisplayName.Length > attackCol - 2
                ? attack.AttackDisplayName[..(attackCol - 5)] + "..."
                : attack.AttackDisplayName;

            var resistedStr = $"{attack.ResistedCount}/{attack.TotalCount}".PadRight(resistedCol);
            WriteLine($"║  {icon} {displayName.PadRight(attackCol)} {resistedStr} {rateStr} {severityStr}");

            if (_options.ShowSecurityReferences && !string.IsNullOrEmpty(attack.OwaspId))
            {
                var mitre = attack.MitreAtlasIds.Length > 0
                    ? $" | MITRE: {string.Join(", ", attack.MitreAtlasIds)}"
                    : "";
                WriteLine($"║     {_theme.Dim}OWASP: {attack.OwaspId}{mitre}{_theme.Reset}");
            }
        }
    }

    private void PrintFailedProbes(RedTeamResult result)
    {
        var failed = result.FailedAttacks
            .SelectMany(a => a.ProbeResults.Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                .Select(p => (Attack: a, Probe: p)))
            .ToList();

        if (failed.Count == 0) return;

        var border = new string('─', Math.Min(_width - 6, 60));
        WriteLine($"║");
        WriteLine($"║  {border}");
        WriteLine($"║  {_theme.Bold}{_theme.Failure}Vulnerabilities Found ({failed.Count}):{_theme.Reset}");

        foreach (var (attack, probe) in failed.Take(10))
        {
            var severityStr = _theme.Colorize($"[{probe.Severity}]", probe.Severity);
            WriteLine($"║");
            WriteLine($"║  {_theme.Failure}❌ {probe.ProbeId}{_theme.Reset} - {probe.Technique ?? "unknown"}");
            WriteLine($"║     {severityStr} {probe.Reason}");

            if (_options.ShowSensitiveContent)
            {
                var truncatedPrompt = Truncate(probe.Prompt, 55);
                var truncatedResponse = Truncate(probe.Response, 55);
                WriteLine($"║     {_theme.Dim}Probe: {truncatedPrompt}{_theme.Reset}");
                WriteLine($"║     {_theme.Dim}Response: {truncatedResponse}{_theme.Reset}");
            }
        }

        if (failed.Count > 10)
        {
            WriteLine($"║     ... and {failed.Count - 10} more vulnerabilities");
        }
    }

    private void PrintAllProbes(RedTeamResult result)
    {
        WriteLine($"║");
        WriteLine($"║  {_theme.Bold}All Probe Results:{_theme.Reset}");

        foreach (var attack in result.AttackResults)
        {
            WriteLine($"║");
            WriteLine($"║  {_theme.Underline}{attack.AttackDisplayName}{_theme.Reset}");

            foreach (var probe in attack.ProbeResults.Take(20))
            {
                var icon = probe.Outcome == EvaluationOutcome.Resisted ? "✅"
                    : probe.Outcome == EvaluationOutcome.Succeeded ? "❌"
                    : "⚪";

                WriteLine($"║    {icon} {probe.ProbeId}: {probe.Reason}");

                if (_options.ShowSensitiveContent)
                {
                    WriteLine($"║       {_theme.Dim}P: {Truncate(probe.Prompt, 50)}{_theme.Reset}");
                    WriteLine($"║       {_theme.Dim}R: {Truncate(probe.Response, 50)}{_theme.Reset}");
                }
            }

            if (attack.ProbeResults.Count > 20)
            {
                WriteLine($"║    ... and {attack.ProbeResults.Count - 20} more probes");
            }
        }
    }

    private void PrintFooter(RedTeamResult result)
    {
        var border = new string('═', _width - 2);
        WriteLine($"╚{border}╝");

        if (!result.Passed && _options.UseEmoji)
        {
            WriteLine();
            WriteLine($"💡 {_theme.Info}Tip: Use VerbosityLevel.Detailed + ShowSensitiveContent for probe details{_theme.Reset}");
        }
    }

    private void WriteLine(string text = "") => _writer.WriteLine(text);

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\n", " ").Replace("\r", "").Trim();
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }
}
