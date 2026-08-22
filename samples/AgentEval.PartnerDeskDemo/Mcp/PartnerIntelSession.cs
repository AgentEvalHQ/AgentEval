// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AgentEval.PartnerDeskDemo.Mcp;

/// <summary>
/// The agent side of the PartnerIntel boundary: an MCP <b>client</b> connected over stdio to a PartnerIntel
/// server running in its own child process.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReportTool"/> is the SDK's own <see cref="McpClientTool"/>. It is an <see cref="AIFunction"/>, which
/// is precisely why Gatekeeper can see it: the MAF function-invocation seam invokes it in-process, and the bytes
/// it returns arrived over a real MCP session from a process this one does not control. That is the seam a
/// tool-result gate inspects.
/// </para>
/// </remarks>
public sealed class PartnerIntelSession : IAsyncDisposable
{
    private readonly McpClient _client;

    private PartnerIntelSession(McpClient client, McpClientTool reportTool, bool addendumEnabled)
    {
        _client = client;
        ReportTool = reportTool;
        AddendumEnabled = addendumEnabled;
    }

    /// <summary>The MCP tool the agent may call, as a MAF-invocable <see cref="AIFunction"/>.</summary>
    public AIFunction ReportTool { get; }

    /// <summary>Whether the connected server process was started with the poisoned addendum enabled.</summary>
    public bool AddendumEnabled { get; }

    /// <summary>Starts a PartnerIntel child process and completes the MCP initialization handshake.</summary>
    public static async Task<PartnerIntelSession> OpenAsync(bool evilMode, CancellationToken cancellationToken = default)
    {
        var (command, arguments) = ResolveServerLaunch();
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [EvilMode.EnvironmentVariable] = evilMode ? "1" : "0",
        };

        // Only forward the addendum-override path when it is actually set; a null-valued entry would push an empty
        // variable into the child on some platforms.
        var addendumFile = Environment.GetEnvironmentVariable(EvilMode.AddendumFileVariable);
        if (!string.IsNullOrWhiteSpace(addendumFile))
        {
            environment[EvilMode.AddendumFileVariable] = addendumFile;
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = PartnerIntelServer.ServerName,
            Command = command,
            Arguments = arguments,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });

        // Bound the initialization handshake so a wedged child cannot hang a caller that passed CancellationToken.None.
        // StdioClientTransport is a stateless factory (ConnectAsync hands process ownership to the session), so it
        // has nothing to dispose here; cleaning up a child spawned by a failed handshake is McpClient.CreateAsync's
        // responsibility.
        using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handshakeTimeout.Token);
        var client = await McpClient.CreateAsync(transport, cancellationToken: linked.Token).ConfigureAwait(false);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: linked.Token).ConfigureAwait(false);
            var report = tools.FirstOrDefault(tool =>
                string.Equals(tool.Name, PartnerIntelServer.ToolName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"The PartnerIntel MCP server did not advertise '{PartnerIntelServer.ToolName}'.");

            return new PartnerIntelSession(client, report, evilMode);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();

    /// <summary>
    /// Works out how to start a second copy of this program in MCP-server mode. Prefers the apphost that sits
    /// next to the loaded assembly (present both when the demo runs itself and when a test project has copied
    /// the demo's output alongside its own), and falls back to the shared-framework launcher.
    /// </summary>
    private static (string Command, IList<string> Arguments) ResolveServerLaunch()
    {
        var directory = AppContext.BaseDirectory;
        var appHostName = "AgentEval.PartnerDeskDemo" +
            (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty);
        var appHost = Path.Combine(directory, appHostName);
        if (File.Exists(appHost))
        {
            return (appHost, [Program.McpServerSwitch]);
        }

        var assembly = Path.Combine(directory, "AgentEval.PartnerDeskDemo.dll");
        if (File.Exists(assembly))
        {
            return ("dotnet", [assembly, Program.McpServerSwitch]);
        }

        throw new FileNotFoundException(
            "Could not locate the PartnerDesk demo binary to start the PartnerIntel MCP server. " +
            $"Looked for '{appHost}' and '{assembly}'.");
    }
}
