// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Results.Runs;

/// <summary>
/// A result line's id ([RES-4], contracts/aef/1/spec/03-run.md): deterministic, so the same run read twice gives the
/// same ids. <c>"r_"</c> and the first 32 hex characters (lower case) of the SHA-256 of the UTF-8 of
/// <c>runId ␟ caseId ␟ path ␟ trial</c>, ␟ being U+001F and <c>trial</c> the trial number in plain digits, or empty on
/// a line without one. Vectors: contracts/aef/1/conformance/result-ids.json.
/// </summary>
public static class AefResultId
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Computes a result id.</summary>
    /// <param name="runId">The run's runId.</param>
    /// <param name="caseId">The line's caseId.</param>
    /// <param name="path">The line's path.</param>
    /// <param name="trial">The line's trial (0-based), or null on a line without one.</param>
    /// <exception cref="ArgumentException">
    /// A control character (C0, DEL or C1: U+001F among them, which would make the join ambiguous) or an unpaired
    /// surrogate in one of the strings, or a negative trial.
    /// </exception>
    public static string Compute(string runId, string caseId, string path, long? trial = null)
    {
        Check(runId, nameof(runId));
        Check(caseId, nameof(caseId));
        Check(path, nameof(path));
        if (trial < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trial), trial, "A trial number is 0-based ([RES-8]).");
        }

        byte[] joined;
        try
        {
            joined = StrictUtf8.GetBytes($"{runId}\u001F{caseId}\u001F{path}\u001F{trial?.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (EncoderFallbackException e)
        {
            throw new ArgumentException("A string holds an unpaired surrogate: it has no UTF-8 form ([ENC-2]).", e);
        }

        return "r_" + Convert.ToHexString(SHA256.HashData(joined), 0, 16).ToLowerInvariant();
    }

    private static void Check(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text, name);
        if (text.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} holds a control character: a result id joins its parts with U+001F ([RES-4]).", name);
        }
    }
}
