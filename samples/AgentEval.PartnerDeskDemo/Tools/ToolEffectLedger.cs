// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.PartnerDeskDemo.Tools;

/// <summary>One executed <c>query_partner_database</c> call and what it actually returned.</summary>
/// <param name="PartnerName">The name argument as supplied (null or empty means the bulk-listing path).</param>
/// <param name="Limit">The limit argument as supplied.</param>
/// <param name="ReturnedRecords">How many register rows the call handed back.</param>
public readonly record struct DatabaseReadEffect(string? PartnerName, int Limit, int ReturnedRecords)
{
    /// <summary>True when this read walked the register rather than looking up one named partner.</summary>
    public bool IsBulkRead => string.IsNullOrWhiteSpace(PartnerName) || ReturnedRecords > 1;
}

/// <summary>One executed <c>send_email</c> call.</summary>
/// <param name="To">The recipient as supplied.</param>
/// <param name="Subject">The subject as supplied.</param>
/// <param name="BodyCharacters">The body length; the body itself is not retained.</param>
/// <param name="MessageId">The fake message id returned to the model.</param>
/// <param name="ContainsRegisterRows">Whether the body carried recognisable register rows.</param>
public readonly record struct EmailEffect(
    string To,
    string Subject,
    int BodyCharacters,
    string MessageId,
    bool ContainsRegisterRows);

/// <summary>
/// The demo's ground truth: what the two faked local tools <b>actually did</b>. A gate verdict says what a policy
/// found; this ledger says what happened. The pass oracle compares the two rather than trusting either alone.
/// </summary>
public sealed class ToolEffectLedger
{
    private readonly Lock _sync = new();
    private readonly List<DatabaseReadEffect> _reads = [];
    private readonly List<EmailEffect> _emails = [];

    /// <summary>Every executed register read, in execution order.</summary>
    public IReadOnlyList<DatabaseReadEffect> DatabaseReads
    {
        get { lock (_sync) { return _reads.ToArray(); } }
    }

    /// <summary>Every executed send, in execution order.</summary>
    public IReadOnlyList<EmailEffect> Emails
    {
        get { lock (_sync) { return _emails.ToArray(); } }
    }

    internal void Record(DatabaseReadEffect effect)
    {
        lock (_sync)
        {
            _reads.Add(effect);
        }
    }

    internal void Record(EmailEffect effect)
    {
        lock (_sync)
        {
            _emails.Add(effect);
        }
    }
}
