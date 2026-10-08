// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentEval.Results;

/// <summary>
/// An AEF duration ([ENC-9], contracts/aef/1/spec/02-encoding.md): ISO 8601 days, hours and minutes, the one grammar of a
/// checkpoint lane's freshness and a run plan's timeout. P, then optionally &lt;n&gt;D, then optionally T and &lt;n&gt;H,
/// &lt;n&gt;M or both in that order; each n one to five digits; at least one part, and no T without one (P14D, PT36H,
/// PT90M, P1DT12H30M).
/// </summary>
public static class AefDuration
{
    private static readonly Regex Pattern = new(
        "^P(?=[0-9]|T[0-9])(?:([0-9]{1,5})D)?(?:T(?=[0-9])(?:([0-9]{1,5})H)?(?:([0-9]{1,5})M)?)?\\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The duration in seconds.</summary>
    /// <exception cref="FormatException">Anything that is not one (PT30S, P1W, PT1M1H, P1DT, six digits).</exception>
    public static long Seconds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Pattern.Match(text);
        if (!match.Success)
        {
            throw new FormatException($"'{text}' is not a duration of days, hours and minutes (P14D, PT36H, PT90M, P1DT12H30M), each at most five digits.");
        }

        long Part(int group) => match.Groups[group].Success ? long.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        return (Part(1) * 86_400) + (Part(2) * 3_600) + (Part(3) * 60);
    }
}
