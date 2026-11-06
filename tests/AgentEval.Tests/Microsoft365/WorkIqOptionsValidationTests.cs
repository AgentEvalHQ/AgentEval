// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365;
using Xunit;

namespace AgentEval.Tests.Microsoft365;

/// <summary>
/// <see cref="WorkIqOptions.Validate"/> refuses values that would crash a turn or busy-poll the service: a duration a
/// timer or <see cref="HttpClient.Timeout"/> cannot wait (above 24 days, such as <see cref="TimeSpan.MaxValue"/> used as
/// "no limit"), and a zero poll interval while recovery is on. Zero stays "none" where the option allows it.
/// </summary>
public sealed class WorkIqOptionsValidationTests
{
    public static TheoryData<string, WorkIqOptions> TooLong => new()
    {
        { nameof(WorkIqOptions.SendTimeout), new WorkIqOptions { SendTimeout = TimeSpan.MaxValue } },
        { nameof(WorkIqOptions.TurnDeadline), new WorkIqOptions { TurnDeadline = TimeSpan.MaxValue } },
        { nameof(WorkIqOptions.RecoveryBudget), new WorkIqOptions { RecoveryBudget = TimeSpan.FromDays(50) } },
        { nameof(WorkIqOptions.RecoveryPollInterval), new WorkIqOptions { RecoveryPollInterval = TimeSpan.FromDays(25) } },
    };

    [Theory]
    [MemberData(nameof(TooLong))]
    public void Validate_ADurationAboveTheMaximum_IsRefusedByName(string option, WorkIqOptions options)
    {
        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(option, ex.Message, StringComparison.Ordinal);
        Assert.Contains("24-day maximum", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ZeroPollIntervalWhileRecoveryIsOn_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(new WorkIqOptions { RecoveryPollInterval = TimeSpan.Zero }.Validate);

        Assert.Contains("RecoveryPollInterval is 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ZeroMeansNone_AndTheMaximumItselfIsAccepted()
    {
        new WorkIqOptions { SendTimeout = TimeSpan.Zero, TurnDeadline = TimeSpan.Zero, RecoveryBudget = TimeSpan.Zero, RecoveryPollInterval = TimeSpan.Zero }.Validate();
        new WorkIqOptions
        {
            SendTimeout = WorkIqOptions.MaxDuration,
            TurnDeadline = WorkIqOptions.MaxDuration,
            RecoveryBudget = WorkIqOptions.MaxDuration,
            RecoveryPollInterval = TimeSpan.FromMilliseconds(10),
        }.Validate();
    }

    [Fact]
    public void Validate_NegativeValues_AreStillNamedTogether()
    {
        var ex = Assert.Throws<InvalidOperationException>(new WorkIqOptions { SendTimeout = TimeSpan.FromSeconds(-1), MaxTurns = -1 }.Validate);

        Assert.Contains("negative value(s): SendTimeout, MaxTurns", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ATurnDeadlineTheClockCannotAdd_IsRefusedBeforeAnyTurn()
    {
        // Before: TimeSpan.MaxValue passed validation and the turn threw ArgumentOutOfRangeException after discovery.
        var options = new WorkIqOptions { TurnDeadline = TimeSpan.MaxValue };

        Assert.Throws<InvalidOperationException>(() =>
            new WorkIqConversation(new Conversation.FakeWorkIqA2AClient(), Protocol.WorkIqClientHarness.DefaultConfig(), options, TimeProvider.System));
    }
}
