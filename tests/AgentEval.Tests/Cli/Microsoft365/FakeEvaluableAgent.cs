// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Tests.Cli.Microsoft365;

/// <summary>
/// A benign <see cref="IEvaluableAgent"/> with a fixed helpful reply, reusable across unlimited calls: the
/// <c>sutOverride</c> stand-in that drives the whole <c>--sut microsoft365</c> CLI path credential-free. Deliberately
/// independent of <c>WorkIqEvaluableAgent</c> (whose live turn is another lane's), so these tests prove the CLI seam
/// (arg parsing, validation, consent, run, reporting) and nothing about the provider's transport.
/// </summary>
internal sealed class FakeEvaluableAgent : IEvaluableAgent
{
    private readonly string _reply;
    private int _invocations;

    public FakeEvaluableAgent(string name = "m365-fake", string reply = "Hi")
    {
        Name = name;
        _reply = reply;
    }

    public string Name { get; }

    /// <summary>How many prompts reached the stand-in.</summary>
    public int Invocations => Volatile.Read(ref _invocations);

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocations);
        return Task.FromResult(new AgentResponse { Text = _reply, FinishReason = "Stop" });
    }
}
