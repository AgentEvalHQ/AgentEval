// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The demo console's own controls, unit by unit: the device-code mask that keeps the sign-in code out of console.log
/// (review M3 of the final gate: it had no test), and the ASCII markers a redirected screen gets instead of the symbols
/// (review L2: <c>✓ ✗ –</c> printed as <c>?</c> on Windows), with console.log always UTF-8.
/// </summary>
public sealed class DemoConsoleTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("agenteval-m365-demo-console-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder another process still holds is left for the OS to clean.
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The device-code mask
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void MaskDeviceCode_MsalsExactSentence_MasksTheCodeAndKeepsThePage()
    {
        const string msal = "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD1234 to authenticate.";

        var masked = DemoConsole.MaskDeviceCode(msal);

        Assert.Equal("To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code [redacted] to authenticate.", masked);
    }

    [Fact]
    public void MaskDeviceCode_TwoCodesOnOneLine_MasksBoth()
    {
        const string line = "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD1234 to authenticate. " +
                            "If that page asks again, enter the code EFGH5678.";

        var masked = DemoConsole.MaskDeviceCode(line);

        Assert.DoesNotContain("ABCD1234", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("EFGH5678", masked, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(masked, "enter the code [redacted]"));
        Assert.Contains("https://microsoft.com/devicelogin", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void MaskDeviceCode_TwoBareCodesOnADeviceLoginLine_TheFallbackMasksBoth()
    {
        // No "enter the code" phrase (another wording or language): the code shape is masked on a line that names devicelogin.
        const string line = "Codes QZ7RK2MXW and 7HJK2LMN9P for https://microsoft.com/devicelogin";

        var masked = DemoConsole.MaskDeviceCode(line);

        Assert.Equal("Codes [redacted] and [redacted] for https://microsoft.com/devicelogin", masked);
    }

    [Fact]
    public void MaskDeviceCode_ALineWithoutASignIn_IsUnchanged()
    {
        const string line = "  ✓ readiness: auth        token acquired from the host's token provider; request-id 7f3a2b9c1d4e5f60";

        Assert.Equal(line, DemoConsole.MaskDeviceCode(line));
    }

    [Fact]
    public void SignInPrompt_TheScreenShowsTheCode_ConsoleLogMasksIt()
    {
        using var screen = new StringWriter();
        var log = new FileInfo(Path.Combine(_root.FullName, "console.log"));
        using (var console = new DemoConsole(screen, TextWriter.Null, DemoVerbosity.Quiet, colors: false))
        {
            console.OpenLog(log);
            using var writer = new SignInPromptWriter(console);
            writer.WriteLine("  Microsoft 365 (Work IQ) sign-in required:");
            writer.WriteLine("    " + PromptingTokenProvider.MsalMessage);
        }

        // Shown in full at the quietest screen: the person must read the page and the code.
        Assert.Contains(PromptingTokenProvider.MsalMessage, screen.ToString(), StringComparison.Ordinal);
        var logged = File.ReadAllText(log.FullName);
        Assert.DoesNotContain(PromptingTokenProvider.UserCode, logged, StringComparison.Ordinal);
        Assert.Contains("https://microsoft.com/devicelogin and enter the code [redacted] to authenticate.", logged, StringComparison.Ordinal);
        Assert.Contains("sign-in required:", logged, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // ASCII markers on a screen that cannot show the symbols
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ToAsciiScreen_StatusSymbolsAtTheLineStart_BecomeMarkers()
    {
        const string table = "  ✓ readiness: config      PASS\n  ✗ readiness: auth        FAIL\n  – readiness: discovery   SKIPPED";

        var ascii = DemoConsole.ToAsciiScreen(table);

        Assert.Equal("  [PASS] readiness: config      PASS\n  [FAIL] readiness: auth        FAIL\n  [SKIP] readiness: discovery   SKIPPED", ascii);
    }

    [Fact]
    public void ToAsciiScreen_OtherSymbols_BecomeTheirAsciiStandIns_AndNothingNonAsciiOfTheDemosOwnRemains()
    {
        const string text = "ERROR — the turn did not complete; HTTP 400 · AADSTS90002 → check tenantId… ×3 – done ←";

        var ascii = DemoConsole.ToAsciiScreen(text);

        Assert.Equal("ERROR - the turn did not complete; HTTP 400 | AADSTS90002 -> check tenantId... x3 - done <-", ascii);
        Assert.All(DemoConsole.Symbols, symbol => Assert.DoesNotContain(symbol.ToString(), DemoConsole.ToAsciiScreen(DemoConsole.Symbols), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, 65001, true)]      // redirected: ASCII whatever the encoding
    [InlineData(false, 65001, false)]    // an interactive UTF-8 console shows the symbols
    [InlineData(false, 20127, true)]     // US-ASCII cannot encode them
    [InlineData(false, 28591, true)]     // Latin-1 cannot encode them
    public void NeedsAsciiScreen_RedirectedOrAnEncodingWithoutTheSymbols(bool redirected, int codePage, bool expected) =>
        Assert.Equal(expected, DemoConsole.NeedsAsciiScreen(redirected, Encoding.GetEncoding(codePage)));

    [Fact]
    public void AsciiScreen_TheScreenGetsMarkers_ConsoleLogKeepsTheUtf8Symbols()
    {
        using var screen = new StringWriter();
        var log = new FileInfo(Path.Combine(_root.FullName, "console.log"));
        using (var console = new DemoConsole(screen, TextWriter.Null, DemoVerbosity.Normal, colors: false, asciiScreen: true))
        {
            console.OpenLog(log);
            console.Line(DemoVerbosity.Normal, "  ✓ readiness: config      config valid — tenant 1111");
        }

        Assert.Equal("  [PASS] readiness: config      config valid - tenant 1111" + Environment.NewLine, screen.ToString());
        var bytes = File.ReadAllBytes(log.FullName);
        Assert.Equal("  ✓ readiness: config      config valid — tenant 1111\n", new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
