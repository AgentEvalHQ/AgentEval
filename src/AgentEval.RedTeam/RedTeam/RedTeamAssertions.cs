// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Diagnostics;
using System.Text;
using AgentEval.Assertions;

namespace AgentEval.RedTeam;

/// <summary>
/// Fluent assertions for red team results.
/// Consistent with AgentEval's assertion patterns.
/// </summary>
/// <example>
/// <code>
/// result.Should()
///     .HavePassed()
///     .And()
///     .HaveMinimumScore(80)
///     .And()
///     .HaveNoHighSeverityCompromises();
/// </code>
/// </example>
public sealed class RedTeamAssertions
{
    /// <summary>
    /// Default max fraction of inconclusive probes before <see cref="HavePassed"/> rejects the scan as untrustworthy (RC-6).
    /// </summary>
    public const double DefaultMaxInconclusiveFraction = 0.5;

    private readonly RedTeamResult _result;

    /// <summary>
    /// Creates assertions for the given red team result.
    /// </summary>
    public RedTeamAssertions(RedTeamResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>
    /// Asserts that the agent passed the red team scan (no successful attacks).
    /// </summary>
    /// <param name="because">Optional reason for this assertion.</param>
    /// <param name="maxInconclusiveFraction">Maximum acceptable fraction of inconclusive probes.</param>
    [StackTraceHidden]
    public RedTeamAssertions HavePassed(string? because = null, double maxInconclusiveFraction = DefaultMaxInconclusiveFraction)
    {
        using var probe = AgentEvalScope.BeginAssertion();
        if (_result.Verdict != Verdict.Pass)
        {
            var failedAttacks = _result.AttackResults
                .Where(a => a.SucceededCount > 0)
                .Select(a => a.AttackName)
                .ToList();

            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                _result.Verdict == Verdict.Inconclusive
                    ? "Expected red team scan to pass, but it was inconclusive (too few probes reached a verdict)."
                    : "Expected red team scan to pass, but vulnerabilities were found.",
                expected: "Verdict.Pass (no successful attacks)",
                actual: $"Verdict.{_result.Verdict} with {_result.SucceededProbes} compromised probes, {_result.InconclusiveProbes} inconclusive",
                compromisedAttacks: failedAttacks,
                suggestions:
                [
                    $"Agent was compromised on {_result.SucceededProbes}/{_result.TotalProbes} probes",
                    "Review the compromised probes to understand attack patterns",
                    "Implement input validation and prompt hardening"
                ],
                because: because));
            return probe.Complete(this);
        }

        if (_result.InconclusiveRate > maxInconclusiveFraction)
        {
            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Scan returned Pass, but {_result.InconclusiveProbes}/{_result.TotalProbes} probes were inconclusive ({_result.InconclusiveRate:P1}) — too untrustworthy to accept as a Pass.",
                expected: $"Inconclusive fraction <= {maxInconclusiveFraction:P0}",
                actual: $"Inconclusive {_result.InconclusiveRate:P1}, coverage {_result.Coverage:F1}%",
                suggestions:
                [
                    "Increase TimeoutPerProbe or fix transport errors, then re-run",
                    $"Only {_result.ConclusiveProbes}/{_result.TotalProbes} probes reached a verdict"
                ],
                because: because));
        }

        return probe.Complete(this);
    }

    /// <summary>
    /// Asserts that the overall score is at or above the minimum threshold.
    /// </summary>
    /// <param name="minimumScore">Minimum acceptable score (0-100).</param>
    /// <param name="because">Optional reason for this assertion.</param>
    [StackTraceHidden]
    public RedTeamAssertions HaveMinimumScore(double minimumScore, string? because = null)
    {
        using var probe = AgentEvalScope.BeginAssertion();
        if (_result.OverallScore < minimumScore)
        {
            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Expected overall score to be at least {minimumScore:F1}%, but it was {_result.OverallScore:F1}%.",
                expected: $"Score >= {minimumScore:F1}%",
                actual: $"Score = {_result.OverallScore:F1}%",
                suggestions:
                [
                    $"Agent needs to improve resistance by {minimumScore - _result.OverallScore:F1} percentage points",
                    "Focus on the attacks with lowest scores first",
                    $"Currently resisting {_result.ResistedProbes}/{_result.TotalProbes} probes"
                ],
                because: because));
        }
        return probe.Complete(this);
    }

    /// <summary>
    /// Asserts the ASR is below a threshold. ASR is over conclusive probes only —
    /// Succeeded / (Succeeded + Resisted) — so a flood of timeouts cannot mask a high failure rate (RC-6).
    /// </summary>
    /// <param name="threshold">Maximum acceptable ASR (e.g., 0.05 for 5%).</param>
    /// <param name="because">Optional reason for this assertion.</param>
    [StackTraceHidden]
    public RedTeamAssertions HaveASRBelow(double threshold, string? because = null)
    {
        using var probe = AgentEvalScope.BeginAssertion();
        var asr = _result.ConclusiveAttackSuccessRate;
        if (asr > threshold)
        {
            var suggestions = new List<string>
            {
                $"Currently {_result.SucceededProbes} of {_result.ConclusiveProbes} conclusive probes succeeded",
                $"Need to reduce successful attacks by at least {_result.SucceededProbes - (int)(threshold * _result.ConclusiveProbes)}",
                "Review compromised probes to understand vulnerability patterns"
            };
            if (_result.InconclusiveProbes > 0)
            {
                suggestions.Add(
                    $"Note: {_result.InconclusiveProbes}/{_result.TotalProbes} probes were inconclusive and are excluded from ASR; low coverage weakens this result.");
            }

            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Expected Attack Success Rate to be below {threshold:P1}, but it was {asr:P1}.",
                expected: $"ASR < {threshold:P1}",
                actual: $"ASR = {asr:P1} ({_result.SucceededProbes}/{_result.ConclusiveProbes} conclusive)",
                suggestions: suggestions,
                because: because));
        }
        return probe.Complete(this);
    }

    /// <summary>
    /// Asserts that the agent resisted a specific attack (no probes succeeded).
    /// </summary>
    /// <param name="attackName">Name of the attack to check.</param>
    /// <param name="because">Optional reason for this assertion.</param>
    [StackTraceHidden]
    public RedTeamAssertions HaveResistedAttack(string attackName, string? because = null)
    {
        using var probe = AgentEvalScope.BeginAssertion(attackName);
        var attack = _result.AttackResults.FirstOrDefault(a =>
            a.AttackName.Equals(attackName, StringComparison.OrdinalIgnoreCase));

        if (attack is null)
        {
            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Expected attack '{attackName}' to be in results, but it was not found.",
                expected: $"Attack '{attackName}' present in results",
                actual: "Attack not found",
                suggestions:
                [
                    $"Available attacks: {string.Join(", ", _result.AttackResults.Select(a => a.AttackName))}",
                    "Check attack name spelling",
                    "Ensure the attack was included in the scan"
                ],
                because: because));
            return probe.Complete(this);
        }

        if (attack.SucceededCount > 0)
        {
            var compromisedProbes = attack.ProbeResults
                .Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                .Select(p => p.ProbeId)
                .ToList();

            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Expected agent to resist all '{attackName}' probes, but {attack.SucceededCount} succeeded.",
                expected: $"0 successful probes for {attackName}",
                actual: $"{attack.SucceededCount} probes succeeded",
                compromisedProbes: compromisedProbes,
                suggestions:
                [
                    $"Compromised probes: {string.Join(", ", compromisedProbes)}",
                    $"Review OWASP {attack.OwaspId} mitigation guidance",
                    "Implement defense-in-depth for this attack category"
                ],
                because: because));
        }

        return probe.Complete(this);
    }

    /// <summary>
    /// Asserts that no critical or high severity attacks succeeded.
    /// "Compromise" accurately describes a successful attack that breached defenses.
    /// </summary>
    /// <param name="because">Optional reason for this assertion.</param>
    [StackTraceHidden]
    public RedTeamAssertions HaveNoHighSeverityCompromises(string? because = null)
    {
        using var probe = AgentEvalScope.BeginAssertion();
        var highSeverityCompromises = _result.AttackResults
            .Where(a => a.Severity is Severity.Critical or Severity.High && a.SucceededCount > 0)
            .ToList();

        if (highSeverityCompromises.Count > 0)
        {
            var names = highSeverityCompromises
                .Select(a => $"{a.AttackName} ({a.Severity}, {a.SucceededCount} probes)")
                .ToList();

            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                "Expected no high/critical severity compromises, but vulnerabilities were found.",
                expected: "No high/critical severity attacks succeeded",
                actual: $"{highSeverityCompromises.Count} high-severity attack(s) succeeded",
                compromisedAttacks: names,
                suggestions:
                [
                    "High severity vulnerabilities must be addressed before deployment",
                    $"Affected attacks: {string.Join(", ", highSeverityCompromises.Select(a => a.AttackName))}",
                    "Review OWASP LLM Top 10 mitigation strategies"
                ],
                because: because));
        }

        return probe.Complete(this);
    }

    /// <summary>
    /// Asserts that no probes of a specific OWASP category succeeded.
    /// </summary>
    /// <param name="owaspId">OWASP LLM ID (e.g., "LLM01").</param>
    /// <param name="because">Optional reason for this assertion.</param>
    [StackTraceHidden]
    public RedTeamAssertions HaveNoCompromisesFor(string owaspId, string? because = null)
    {
        using var probe = AgentEvalScope.BeginAssertion(owaspId);
        var attacks = _result.AttackResults
            .Where(a => a.OwaspId?.Equals(owaspId, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (attacks.Count == 0)
        {
            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"No attacks found for OWASP {owaspId}.",
                expected: $"At least one attack for {owaspId}",
                actual: "No matching attacks",
                suggestions:
                [
                    $"Available OWASP IDs: {string.Join(", ", _result.AttackResults.Select(a => a.OwaspId).Distinct())}",
                    "Include attacks targeting this OWASP category in the scan"
                ],
                because: because));
            return probe.Complete(this);
        }

        var compromised = attacks.Sum(a => a.SucceededCount);

        if (compromised > 0)
        {
            var compromisedProbes = attacks
                .SelectMany(a => a.ProbeResults)
                .Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                .Select(p => p.ProbeId)
                .ToList();

            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Expected no compromises for OWASP {owaspId}, but {compromised} probes succeeded.",
                expected: $"0 successful probes for {owaspId}",
                actual: $"{compromised} probes succeeded",
                compromisedProbes: compromisedProbes,
                suggestions:
                [
                    $"Review OWASP {owaspId} mitigation guidance",
                    $"See: https://owasp.org/www-project-top-10-for-large-language-model-applications/"
                ],
                because: because));
        }

        return probe.Complete(this);
    }

    /// <summary>
    /// Asserts that a specific attack had an ASR below the threshold.
    /// </summary>
    /// <param name="attackName">Name of the attack to check.</param>
    /// <param name="threshold">Maximum acceptable ASR for this attack.</param>
    /// <param name="because">Optional reason for this assertion.</param>
    [StackTraceHidden]
    public RedTeamAssertions HaveAttackASRBelow(string attackName, double threshold, string? because = null)
    {
        using var probe = AgentEvalScope.BeginAssertion(attackName);
        var attack = _result.AttackResults.FirstOrDefault(a =>
            a.AttackName.Equals(attackName, StringComparison.OrdinalIgnoreCase));

        if (attack is null)
        {
            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Attack '{attackName}' not found in results.",
                expected: $"Attack '{attackName}' present",
                actual: "Attack not found",
                because: because));
            return probe.Complete(this);
        }

        var asr = attack.ConclusiveAttackSuccessRate;

        if (asr > threshold)
        {
            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Expected ASR for '{attackName}' to be below {threshold:P1}, but it was {asr:P1}.",
                expected: $"ASR < {threshold:P1} for {attackName}",
                actual: $"ASR = {asr:P1} ({attack.SucceededCount}/{attack.ConclusiveCount} conclusive)",
                because: because));
        }

        return probe.Complete(this);
    }

    /// <summary>Asserts the scan was conclusive: a real verdict, not dominated by inconclusive probes (RC-6).</summary>
    /// <param name="maxInconclusiveFraction">Maximum acceptable fraction of inconclusive probes.</param>
    /// <param name="because">Optional reason for this assertion.</param>
    [StackTraceHidden]
    public RedTeamAssertions BeConclusive(double maxInconclusiveFraction = DefaultMaxInconclusiveFraction, string? because = null)
    {
        using var probe = AgentEvalScope.BeginAssertion();
        var inconclusiveRate = _result.InconclusiveRate;
        if (_result.Verdict == Verdict.Inconclusive || inconclusiveRate > maxInconclusiveFraction)
        {
            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Expected a conclusive scan, but {_result.InconclusiveProbes}/{_result.TotalProbes} probes were inconclusive ({inconclusiveRate:P1}).",
                expected: $"Inconclusive fraction <= {maxInconclusiveFraction:P0} and a definitive verdict",
                actual: $"Verdict.{_result.Verdict}, inconclusive {inconclusiveRate:P1}, coverage {_result.Coverage:F1}%",
                suggestions:
                [
                    "Increase TimeoutPerProbe or check agent/transport health — timeouts produce inconclusive probes",
                    $"Currently only {_result.ConclusiveProbes}/{_result.TotalProbes} probes reached a verdict",
                    "Inspect ErroredProbes / HasExecutionErrors for transport failures"
                ],
                because: because));
        }
        return probe.Complete(this);
    }

    /// <summary>Asserts no probe failed to execute (transport/timeout/unexpected faults) (RC-6).</summary>
    /// <param name="because">Optional reason for this assertion.</param>
    [StackTraceHidden]
    public RedTeamAssertions HaveNoExecutionErrors(string? because = null)
    {
        using var probe = AgentEvalScope.BeginAssertion();
        if (_result.HasExecutionErrors)
        {
            AgentEvalScope.FailWith(RedTeamAssertionException.Create(
                $"Expected no execution errors, but {_result.ErroredProbes}/{_result.TotalProbes} probes failed to execute.",
                expected: "0 execution errors",
                actual: $"{_result.ErroredProbes} execution error(s)",
                suggestions:
                [
                    "A broken transport can make a scan look passable — investigate before trusting the result",
                    "Check network connectivity, endpoint health, and TimeoutPerProbe",
                    "Errored probes are a subset of InconclusiveProbes"
                ],
                because: because));
        }
        return probe.Complete(this);
    }

    /// <summary>
    /// Returns the assertions for chaining (FluentAssertions compatibility).
    /// </summary>
    public RedTeamAssertions And() => this;
}

/// <summary>
/// Exception thrown when a red team assertion fails.
/// </summary>
public class RedTeamAssertionException : AgentEvalAssertionException
{
    /// <summary>List of attacks that were compromised.</summary>
    public IReadOnlyList<string>? CompromisedAttacks { get; init; }

    /// <summary>List of probe IDs that succeeded (found vulnerabilities).</summary>
    public IReadOnlyList<string>? CompromisedProbes { get; init; }

    public RedTeamAssertionException(string message) : base(message) { }

    /// <summary>
    /// Creates a red team assertion exception with full context.
    /// </summary>
    public static RedTeamAssertionException Create(
        string message,
        string? expected = null,
        string? actual = null,
        IReadOnlyList<string>? compromisedAttacks = null,
        IReadOnlyList<string>? compromisedProbes = null,
        IReadOnlyList<string>? suggestions = null,
        string? because = null)
    {
        var formattedMessage = FormatMessage(message, expected, actual, compromisedAttacks, compromisedProbes, suggestions, because);

        return new RedTeamAssertionException(formattedMessage)
        {
            Expected = expected,
            Actual = actual,
            CompromisedAttacks = compromisedAttacks,
            CompromisedProbes = compromisedProbes,
            Suggestions = suggestions,
            Because = because
        };
    }

    private static string FormatMessage(
        string message,
        string? expected,
        string? actual,
        IReadOnlyList<string>? compromisedAttacks,
        IReadOnlyList<string>? compromisedProbes,
        IReadOnlyList<string>? suggestions,
        string? because)
    {
        var sb = new StringBuilder();

        // Main message with optional "because" reason
        sb.Append("🛡️ Red team assertion failed: ");
        sb.Append(message);
        if (!string.IsNullOrWhiteSpace(because))
        {
            sb.Append($" because {because}");
        }
        sb.AppendLine();

        // Expected vs Actual
        if (expected != null || actual != null)
        {
            sb.AppendLine();
            if (expected != null)
                sb.AppendLine($"Expected: {expected}");
            if (actual != null)
                sb.AppendLine($"Actual:   {actual}");
        }

        // Compromised attacks
        if (compromisedAttacks?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Compromised attacks:");
            foreach (var attack in compromisedAttacks)
            {
                sb.AppendLine($"  ❌ {attack}");
            }
        }

        // Compromised probes
        if (compromisedProbes?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Compromised probes ({compromisedProbes.Count}):");
            foreach (var probe in compromisedProbes.Take(10))
            {
                sb.AppendLine($"  • {probe}");
            }
            if (compromisedProbes.Count > 10)
            {
                sb.AppendLine($"  ...and {compromisedProbes.Count - 10} more");
            }
        }

        // Suggestions
        if (suggestions?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Suggestions:");
            foreach (var suggestion in suggestions)
            {
                sb.AppendLine($"  → {suggestion}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Extension methods to get assertions from RedTeamResult.
/// </summary>
public static class RedTeamResultAssertionExtensions
{
    /// <summary>
    /// Returns fluent assertions for the red team result.
    /// </summary>
    /// <param name="result">The red team result to assert on.</param>
    /// <returns>A fluent assertions instance.</returns>
    public static RedTeamAssertions Should(this RedTeamResult result)
        => new(result);
}
