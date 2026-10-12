// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

/// <summary>Shared native evidence controls, scoped to a single CLI invocation.</summary>
internal sealed class AefCliOptions
{
    private static readonly AsyncLocal<Settings?> Active = new();
    private readonly Dictionary<Command, Binding> _commands = [];
    internal static bool Seal => Active.Value?.Seal ?? true;
    internal static IAefSigner? Signer => Active.Value?.Signer;

    internal AefCliOptions(Command root) => Register(root);

    private void Register(Command command)
    {
        if (command.Options.Any(o => o.Name == "--aef"))
        {
            var key = new Option<string?>("--aef-key") { Description = "Sign AEF evidence with an unencrypted PKCS#8 ECDSA P-256 key" };
            var unsealed = new Option<bool>("--aef-unsealed") { Description = "Leave closed AEF evidence unsealed for a host to seal later" };
            command.Options.Add(key);
            command.Options.Add(unsealed);
            _commands.Add(command, new(key, unsealed));
        }
        foreach (var child in command.Subcommands) Register(child);
    }

    internal async Task<int> InvokeAsync(ParseResult parsed, Func<Task<int>> invoke)
    {
        if (parsed.Action is System.CommandLine.Help.HelpAction) return await invoke();
        if (!_commands.TryGetValue(parsed.CommandResult.Command, out var binding)) return await invoke();
        var keyPath = parsed.GetValue(binding.Key);
        var unsealed = parsed.GetValue(binding.Unsealed);
        var noAef = parsed.CommandResult.Command.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--no-aef");
        if ((keyPath is not null || unsealed) && noAef is not null && parsed.GetValue(noAef))
        {
            Console.Error.WriteLine("Error: --no-aef cannot be combined with --aef-key or --aef-unsealed.");
            return ExitCodes.UsageError;
        }
        if (keyPath is not null && unsealed)
        {
            Console.Error.WriteLine("Error: --aef-key requires a seal; remove --aef-unsealed to sign the run.");
            return ExitCodes.UsageError;
        }

        EcdsaP256Signer? signer = null;
        try
        {
            if (keyPath is not null) signer = AefSigningKey.Load(keyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Do not echo the path or key material from a parser or filesystem diagnostic.
            Console.Error.WriteLine("Error: --aef-key must name a readable, unencrypted PKCS#8 ECDSA P-256 private key.");
            return ExitCodes.UsageError;
        }
        var previous = Active.Value;
        try
        {
            Active.Value = new(!unsealed, signer);
            return await invoke();
        }
        finally
        {
            Active.Value = previous;
            signer?.Dispose();
        }
    }

    private sealed record Binding(Option<string?> Key, Option<bool> Unsealed);
    private sealed record Settings(bool Seal, IAefSigner? Signer);
}
