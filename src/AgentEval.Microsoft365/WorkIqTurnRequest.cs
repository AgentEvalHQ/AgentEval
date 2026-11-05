// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365;

/// <summary>
/// One turn to submit through <see cref="IWorkIqConversation"/>: the user input plus the local identities that
/// make the turn traceable in evidence. <see cref="TurnId"/> is the stable client turn identity that also
/// appears in <see cref="Evidence.TurnIdentity.TurnId"/>; retries keep it and increment the attempt instead.
/// </summary>
public sealed record WorkIqTurnRequest
{
    private readonly string _input = "";
    private readonly string _caseId = "";
    private readonly string _turnId = "";

    /// <summary>Creates a turn request.</summary>
    /// <param name="input">The user input (may be empty, never null).</param>
    /// <param name="caseId">The local benchmark case id (non-blank).</param>
    /// <param name="turnId">The stable client turn id (non-blank).</param>
    /// <param name="metadata">Optional caller metadata recorded with the turn; never sent to the service.</param>
    public WorkIqTurnRequest(string input, string caseId, string turnId, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        Input = input;
        CaseId = caseId;
        TurnId = turnId;
        Metadata = metadata;
    }

    /// <summary>The user input sent as the turn's text part.</summary>
    public string Input
    {
        get => _input;
        init => _input = value ?? throw new ArgumentNullException(nameof(Input));
    }

    /// <summary>The local benchmark case id.</summary>
    public string CaseId
    {
        get => _caseId;
        init => _caseId = RequireNonBlank(value, nameof(CaseId));
    }

    /// <summary>The stable client turn id.</summary>
    public string TurnId
    {
        get => _turnId;
        init => _turnId = RequireNonBlank(value, nameof(TurnId));
    }

    /// <summary>Optional caller metadata recorded with the turn (for example the scenario name); never sent to the service.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    private static string RequireNonBlank(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}
