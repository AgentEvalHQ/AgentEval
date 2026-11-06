// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using Xunit;

namespace AgentEval.Tests.Microsoft365.TestHelpers;

/// <summary>The fixture loader resolves folder-relative, dotted and full manifest names to the same embedded bytes.</summary>
public sealed class FixtureLoaderTests
{
    [Theory]
    [InlineData("envelopes/send-task-submitted.json", "AgentEval.Tests.Microsoft365.Fixtures.envelopes.send-task-submitted.json")]
    [InlineData("envelopes\\send-task-submitted.json", "AgentEval.Tests.Microsoft365.Fixtures.envelopes.send-task-submitted.json")]
    [InlineData("envelopes.send-task-submitted.json", "AgentEval.Tests.Microsoft365.Fixtures.envelopes.send-task-submitted.json")]
    [InlineData("AgentEval.Tests.Microsoft365.Fixtures.sse.stream-crlf.txt", "AgentEval.Tests.Microsoft365.Fixtures.sse.stream-crlf.txt")]
    [InlineData("normalization-fixtures.json", "AgentEval.Tests.Microsoft365.Fixtures.normalization-fixtures.json")]
    public void ResourceName_AnyForm_MapsToTheManifestName(string name, string expected)
    {
        Assert.Equal(expected, FixtureLoader.ResourceName(name));
    }

    [Fact]
    public void Text_SlashAndDottedForms_ReadTheSameFixture()
    {
        var bySlash = FixtureLoader.Text("envelopes/send-task-submitted.json");
        var byDot = FixtureLoader.Text("envelopes.send-task-submitted.json");

        Assert.Equal(bySlash, byDot);
        Assert.Contains("\"jsonrpc\": \"2.0\"", bySlash, StringComparison.Ordinal);
        Assert.Contains("TASK_STATE_SUBMITTED", bySlash, StringComparison.Ordinal);
    }

    [Fact]
    public void Bytes_Always_AreTheUtf8OfText()
    {
        var bytes = FixtureLoader.Bytes("sse/stream-happy-path.txt");

        Assert.Equal(bytes, Encoding.UTF8.GetBytes(FixtureLoader.Text("sse/stream-happy-path.txt")));
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
    }

    [Fact]
    public void Names_Always_ListEveryFolderInRelativeForm()
    {
        Assert.Contains("envelopes/send-task-submitted.json", FixtureLoader.Names);
        Assert.Contains("sse/stream-happy-path.txt", FixtureLoader.Names);
        Assert.Contains("cards/default-card-two-interfaces.json", FixtureLoader.Names);
        Assert.Contains("normalization-fixtures.json", FixtureLoader.Names);
        Assert.True(FixtureLoader.Names.Count >= 40);
        Assert.All(FixtureLoader.Names, n => Assert.True(FixtureLoader.Exists(n), n));
    }

    [Theory]
    [InlineData("envelopes/send-task-submitted.json", true)]
    [InlineData("cards/card-v03-only.json", true)]
    [InlineData("envelopes/does-not-exist.json", false)]
    [InlineData("{\"jsonrpc\":\"2.0\"}", false)]
    [InlineData("", false)]
    public void Exists_Name_SaysWhetherItIsEmbedded(string name, bool expected)
    {
        Assert.Equal(expected, FixtureLoader.Exists(name));
    }

    [Fact]
    public void Open_MissingFixture_ThrowsListingTheKnownNames()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => FixtureLoader.Open("envelopes/does-not-exist.json"));

        Assert.Contains("envelopes/does-not-exist.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("envelopes/send-task-submitted.json", ex.Message, StringComparison.Ordinal);
    }
}
