// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;

namespace AgentEval.RedTeam.Baseline;

/// <summary>
/// Fluent assertions for RedTeam baseline comparisons.
/// </summary>
public class BaselineAssertions
{
    private readonly RedTeamComparison _comparison;
    private readonly List<string> _failures = [];

    /// <summary>
    /// Creates a new assertions instance for the comparison.
    /// </summary>
    public BaselineAssertions(RedTeamComparison comparison)
    {
        _comparison = comparison ?? throw new ArgumentNullException(nameof(comparison));
    }

    /// <summary>
    /// Asserts that there are no new vulnerabilities compared to baseline.
    /// </summary>
    public BaselineAssertions HaveNoNewVulnerabilities(string? because = null)
    {
        if (_comparison.NewVulnerabilities.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Expected no new vulnerabilities, but found {_comparison.NewVulnerabilities.Count}:");
            foreach (var vuln in _comparison.NewVulnerabilities.Take(5))
            {
                sb.AppendLine($"  - {vuln.ProbeId} ({vuln.AttackName}): {vuln.Reason}");
            }
            if (_comparison.NewVulnerabilities.Count > 5)
            {
                sb.AppendLine($"  ... and {_comparison.NewVulnerabilities.Count - 5} more");
            }
            if (because != null)
            {
                sb.AppendLine($"because {because}");
            }
            _failures.Add(sb.ToString());
        }
        return this;
    }

    /// <summary>
    /// Asserts that the overall score has not decreased by more than the specified amount.
    /// </summary>
    public BaselineAssertions HaveOverallScoreNotDecreasedBy(double maxDecrease, string? because = null)
    {
        if (_comparison.ScoreDelta < -maxDecrease)
        {
            var message = $"Score decreased by {Math.Abs(_comparison.ScoreDelta):F1}% " +
                $"(baseline: {_comparison.Baseline.OverallScore:F1}%, current: {_comparison.Current.OverallScore:F1}%, " +
                $"max allowed decrease: {maxDecrease}%)";
            if (because != null)
            {
                message += $" because {because}";
            }
            _failures.Add(message);
        }
        return this;
    }

    /// <summary>
    /// Asserts that this is not a regression.
    /// </summary>
    public BaselineAssertions NotBeRegression(string? because = null)
    {
        if (_comparison.IsRegression)
        {
            var message = $"Security regression detected: {_comparison.NewVulnerabilities.Count} new vulnerabilities, " +
                $"score delta: {_comparison.ScoreDelta:+0.0;-0.0}%";
            if (because != null)
            {
                message += $" because {because}";
            }
            _failures.Add(message);
        }
        return this;
    }

    /// <summary>
    /// Asserts that specific attacks have not regressed.
    /// </summary>
    public BaselineAssertions HaveNoRegressionForAttack(string attackName, string? because = null)
    {
        var attackComparison = _comparison.AttackComparisons
            .FirstOrDefault(a => a.AttackName == attackName || a.AttackDisplayName == attackName);

        if (attackComparison != null && attackComparison.NewFailures.Count > 0)
        {
            var message = $"Attack '{attackName}' has {attackComparison.NewFailures.Count} new failures: " +
                string.Join(", ", attackComparison.NewFailures.Take(3));
            if (attackComparison.NewFailures.Count > 3)
            {
                message += $" (+{attackComparison.NewFailures.Count - 3} more)";
            }
            if (because != null)
            {
                message += $" because {because}";
            }
            _failures.Add(message);
        }
        return this;
    }

    /// <summary>
    /// Combines this assertion with another for fluent chaining.
    /// </summary>
    public BaselineAssertions And() => this;

    /// <summary>
    /// Throws if any assertions failed. Called implicitly at end of chain.
    /// </summary>
    public void ThrowIfFailed()
    {
        if (_failures.Count > 0)
        {
            throw new RedTeamRegressionException(
                string.Join("\n\n", _failures),
                _comparison);
        }
    }
}

/// <summary>
/// Exception thrown when a baseline regression is detected.
/// </summary>
public class RedTeamRegressionException : Exception
{
    /// <summary>The comparison that caused the regression.</summary>
    public RedTeamComparison Comparison { get; }

    /// <summary>
    /// Creates a new regression exception.
    /// </summary>
    public RedTeamRegressionException(string message, RedTeamComparison comparison)
        : base(message)
    {
        Comparison = comparison;
    }
}

/// <summary>
/// Extension methods for baseline assertions.
/// </summary>
public static class BaselineComparisonExtensions
{
    /// <summary>
    /// Start making assertions about the comparison.
    /// </summary>
    public static BaselineAssertions Should(this RedTeamComparison comparison)
        => new(comparison);
}

/// <summary>
/// Extension methods for creating and working with baselines.
/// </summary>
public static class RedTeamResultBaselineExtensions
{
    /// <summary>
    /// Compares this result to a baseline.
    /// </summary>
    public static RedTeamComparison CompareToBaseline(this RedTeamResult result, RedTeamBaseline baseline)
    {
        var comparer = new RedTeamBaselineComparer();
        return comparer.Compare(result, baseline);
    }

    /// <summary>
    /// Converts this result to a baseline.
    /// </summary>
    public static RedTeamBaseline ToBaseline(this RedTeamResult result, string version, string? notes = null)
        => RedTeamBaseline.FromResult(result, version, notes);

    /// <summary>
    /// Saves this result as a baseline.
    /// </summary>
    public static async Task SaveAsBaselineAsync(
        this RedTeamResult result,
        string path,
        string version,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var baseline = result.ToBaseline(version, notes);
        await baseline.SaveAsync(path, cancellationToken);
    }
}
