// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Auth;

namespace AgentEval.Microsoft365Demo.Tests.TestHelpers;

/// <summary>
/// The token source of the sign-in prompt tests, built by the demo's <c>TokenProviderFactory</c> seam with the demo's own
/// sign-in prompt writer: on the calls <see cref="PromptOn"/> selects it writes MSAL's device-code message to that writer
/// exactly as <see cref="WorkIqDeviceCodeTokenProvider"/> does (a blank line, the heading, the message, a blank line),
/// then hands out <see cref="FakeTokenProvider.Token"/>. No MSAL, no network.
/// </summary>
internal sealed class PromptingTokenProvider : IWorkIqTokenProvider
{
    /// <summary>The device code in the message: upper-case letters and digits, nine characters, like MSAL's.</summary>
    public const string UserCode = "QZ7RK2MXW";

    /// <summary>MSAL's device-code sentence, word for word, with <see cref="UserCode"/>.</summary>
    public const string MsalMessage =
        "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code " + UserCode + " to authenticate.";

    private readonly TextWriter _prompt;
    private int _calls;
    private int _prompts;

    /// <summary>Creates the provider.</summary>
    /// <param name="prompt">The writer the demo gave its token source.</param>
    public PromptingTokenProvider(TextWriter prompt)
    {
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
    }

    /// <summary>Which calls (1-based) prompt; the first one by default.</summary>
    public Func<int, bool> PromptOn { get; init; } = call => call == 1;

    /// <summary>The writer the demo gave this provider.</summary>
    public TextWriter Prompt => _prompt;

    /// <summary>How many tokens were requested.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>How many prompts were written.</summary>
    public int Prompts => Volatile.Read(ref _prompts);

    /// <inheritdoc/>
    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PromptOn(Interlocked.Increment(ref _calls)))
        {
            Interlocked.Increment(ref _prompts);
            _prompt.WriteLine();
            _prompt.WriteLine("  Microsoft 365 (Work IQ) sign-in required:");
            _prompt.WriteLine($"    {MsalMessage}");
            _prompt.WriteLine();
            _prompt.Flush();
        }

        return new ValueTask<string>(FakeTokenProvider.Token);
    }
}
