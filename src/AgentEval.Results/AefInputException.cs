// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results;

/// <summary>
/// A recognized refusal of supplied AEF data or an operation on a run in an incompatible state.
/// Unlike ordinary argument or operation exceptions, this may be reported as an input error.
/// Failures in producer callbacks and verifier failures after writing are not input errors.
/// </summary>
public sealed class AefInputException : ArgumentException
{
    /// <summary>Describes the rule the supplied input violates.</summary>
    public AefInputException(string message) : base(message) { }

    /// <summary>Describes a refusal of a named argument.</summary>
    public AefInputException(string message, string? paramName) : base(message, paramName) { }

    /// <summary>Describes an input decoding failure and retains its cause.</summary>
    public AefInputException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Describes a refusal of a named argument and retains its cause.</summary>
    public AefInputException(string message, string? paramName, Exception innerException)
        : base(message, paramName, innerException) { }
}
