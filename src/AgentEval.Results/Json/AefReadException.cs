// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Json;

/// <summary>
/// Bytes a reader refuses before any schema sees them: an <see cref="AefEncodingException"/> (not an I-JSON text,
/// §2.1, §2.2) or an <see cref="AefLimitException"/> (beyond a limit of [ENC-17]). <see cref="Code"/> is the problem code
/// a verifier reports for it (§3.9).
/// </summary>
public abstract class AefReadException : Exception
{
    private protected AefReadException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The problem code: <c>encoding</c> or <c>limit</c>.</summary>
    public string Code { get; }
}

/// <summary>
/// Not an I-JSON text ([ENC-1]–[ENC-7]): not UTF-8, a byte-order mark, a syntax error, a member named twice, an unpaired
/// surrogate, a number that is not a finite binary64 value, a top-level value that is not an object, or NDJSON framing
/// that breaks [ENC-5] or [ENC-7]. Reported as <c>encoding</c>.
/// </summary>
public sealed class AefEncodingException(string message) : AefReadException("encoding", message);

/// <summary>Beyond a limit of [ENC-17] (nesting depth, size, number of lines). Reported as <c>limit</c> ([ENC-18]).</summary>
public sealed class AefLimitException(string message) : AefReadException("limit", message);
