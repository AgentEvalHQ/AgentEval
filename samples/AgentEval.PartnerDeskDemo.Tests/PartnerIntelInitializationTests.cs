// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.IO.Pipelines;
using AgentEval.PartnerDeskDemo.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentEval.PartnerDeskDemo.Tests;

public sealed class PartnerIntelInitializationTests
{
    [Fact]
    public async Task OpenAsync_CallerCancelled_PreservesCancellationAndIdentifiesCreateStage()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PartnerIntelSession.OpenAsync(false, caller.Token));

        Assert.Equal("CreateAsync", error.Data["PartnerIntel.Stage"]);
        Assert.Equal(true, error.Data["PartnerIntel.CallerCancelled"]);
        Assert.Equal(false, error.Data["PartnerIntel.HandshakeBudgetCancelled"]);
        Assert.IsType<TimeSpan>(error.Data["PartnerIntel.CreateElapsed"]);
        Assert.False(error.Data.Contains("PartnerIntel.ListToolsElapsed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_ListFailure_IdentifiesStageAndCancellationOrigin(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requests = new Pipe();
        var replies = new Pipe();
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "initialization-control", Version = "1.0" },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) =>
                {
                    if (cancelCaller) caller.Cancel();
                    throw new McpException("ListTools failure control");
                }
            }
        };
        await using var serverTransport = new StreamServerTransport(requests.Reader.AsStream(), replies.Writer.AsStream());
        await using var server = McpServer.Create(serverTransport, options);
        var running = server.RunAsync(lifetime.Token);
        try
        {
            var transport = new StreamClientTransport(requests.Writer.AsStream(), replies.Reader.AsStream());
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                PartnerIntelSession.OpenAsync(transport, false, caller.Token));
            Assert.Equal("ListToolsAsync", error.Data["PartnerIntel.Stage"]);
            Assert.Equal(cancelCaller, error.Data["PartnerIntel.CallerCancelled"]);
            Assert.Equal(false, error.Data["PartnerIntel.HandshakeBudgetCancelled"]);
            Assert.IsType<TimeSpan>(error.Data["PartnerIntel.CreateElapsed"]);
            Assert.IsType<TimeSpan>(error.Data["PartnerIntel.ListToolsElapsed"]);
            if (cancelCaller) Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.IsAssignableFrom<McpException>(error);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await running; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
}
