// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace AgentEval.Trust;

/// <summary>Renders a <see cref="TrustScoreBreakdown"/> as readable text (Phase 3, P3-10) — the CLI/report adapter.</summary>
public static class TrustScoreRenderer
{
    /// <summary>One-line header + per-signal breakdown (included signals first, by descending contribution).</summary>
    public static string Render(TrustScoreBreakdown breakdown)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        var sb = new StringBuilder();

        var score = breakdown.Result.Score is { } s ? s.ToString("F0", CultureInfo.InvariantCulture) + "/100" : "n/a";
        sb.Append("Trust: ").Append(score).Append("  [").Append(breakdown.Band).Append(']').AppendLine();
        sb.AppendLine(breakdown.Result.Explanation);

        foreach (var contribution in breakdown.Contributions.OrderByDescending(c => c.Included).ThenByDescending(c => c.WeightedContribution))
        {
            sb.Append("  ").Append(contribution.Included ? '•' : '×').Append(' ').Append(contribution.Name)
              .Append(": score=").Append(contribution.Score.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" weight=").Append(contribution.Weight.ToString("F2", CultureInfo.InvariantCulture));

            if (contribution.Included)
            {
                sb.Append(" → +").Append(contribution.WeightedContribution.ToString("F1", CultureInfo.InvariantCulture)).Append(" pts");
            }
            else
            {
                sb.Append(" (excluded: ").Append(contribution.ExclusionReason).Append(')');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
