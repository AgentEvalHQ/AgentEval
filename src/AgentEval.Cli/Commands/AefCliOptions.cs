// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Results.Signatures;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

/// <summary>Shared native evidence controls, scoped to a single CLI invocation.</summary>
internal sealed class AefCliOptions
{
    private static readonly AsyncLocal<Settings?> Active = new();
    private readonly Dictionary<Command, Binding> _commands = [];
    internal static AefCliProvenance? Provenance => Active.Value?.Provenance;
    internal static bool Seal => Active.Value?.Seal ?? true;
    internal static IAefSigner? Signer => Active.Value?.Signer;
    internal static AefContentCapture ContentCapture => Active.Value?.ContentCapture ?? AefContentCapture.Off;
    internal static AefContentCapture? RequestedContentCapture => Active.Value?.RequestedContentCapture;

    internal AefCliOptions(Command root) => Register(root);

    private void Register(Command command, string? parent = null)
    {
        var verb = parent is null ? command is RootCommand ? "" : command.Name
            : parent.Length == 0 ? command.Name : parent + " " + command.Name;
        if (command.Options.Any(o => o.Name == "--aef"))
        {
            var runner = IsRunner(command);
            var key = new Option<string?>("--aef-key") { Description = "Sign AEF evidence with an unencrypted PKCS#8 ECDSA P-256 key" };
            var unsealed = new Option<bool>("--aef-unsealed") { Description = runner
                ? "Not supported for runner jobs, which require sealed evidence"
                : "Leave closed AEF evidence unsealed for a host to seal later" };
            command.Options.Add(key);
            command.Options.Add(unsealed);
            var content = new Option<string?>("--aef-content") { Description = runner
                ? "Assert on|off matches the saved plan's contentCapture; defaults to the plan and never overrides it"
                : "Capture observed content in AEF evidence blobs: on|off (default: off)" };
            command.Options.Add(content);
            _commands.Add(command, new(key, unsealed, content, verb));
        }
        foreach (var child in command.Subcommands) Register(child, verb);
    }

    internal async Task<int> InvokeAsync(ParseResult parsed, Func<Task<int>> invoke)
    {
        if (parsed.Action is System.CommandLine.Help.HelpAction) return await invoke();
        if (!_commands.TryGetValue(parsed.CommandResult.Command, out var binding)) return await invoke();
        var keyPath = parsed.GetValue(binding.Key);
        var unsealed = parsed.GetValue(binding.Unsealed);
        var content = parsed.GetValue(binding.Content);
        if (content is not null and not "on" and not "off")
        {
            Console.Error.WriteLine("Error: --aef-content must be on or off.");
            return ExitCodes.UsageError;
        }
        var noAef = parsed.CommandResult.Command.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--no-aef");
        if (IsRunner(parsed.CommandResult.Command) && (unsealed || (noAef is not null && parsed.GetValue(noAef))))
        {
            Console.Error.WriteLine("Error: runner jobs require sealed AEF evidence; --no-aef and --aef-unsealed are not supported.");
            return ExitCodes.UsageError;
        }
        if ((keyPath is not null || unsealed || content == "on") && noAef is not null && parsed.GetValue(noAef))
        {
            Console.Error.WriteLine("Error: --no-aef cannot be combined with --aef-key, --aef-unsealed or --aef-content on.");
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
        using var usage = AgentEval.Cli.Infrastructure.CliUsageMeter.Begin();
        var previous = Active.Value;
        try
        {
            Active.Value = new(!unsealed, signer, content == "on" ? AefContentCapture.On : AefContentCapture.Off,
                content is null ? null : content == "on" ? AefContentCapture.On : AefContentCapture.Off,
                IsRunner(parsed.CommandResult.Command) ? null : await AgentEval.Cli.Infrastructure.CliGitProvenance.CaptureAsync(binding.Verb, Environment.CurrentDirectory));
            return await invoke();
        }
        finally
        {
            Active.Value = previous;
            signer?.Dispose();
        }
    }

    // The evidence is already durable. A closed/broken diagnostic stream cannot undo publication.
    internal static void ReportSaved(string directory, string? hash, string? subjectVersion, string indent = "")
    {
        try
        {
            Console.Error.WriteLine($"{indent}AEF run: {directory} ({(hash is null ? "unsealed" : $"run hash {hash[..12]}...")})");
            NoteMissingSubjectVersion(subjectVersion);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Do not report a write failure, retry publication or change the evaluation's exit code.
        }
    }

    internal static void NoteMissingSubjectVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            Console.Error.WriteLine("  AEF note: no exact subject version was recorded; this run cannot satisfy a checkpoint lane. Record an exact subject version when producing checkpoint evidence.");
    }

    private sealed record Binding(Option<string?> Key, Option<bool> Unsealed, Option<string?> Content, string Verb);
    private static bool IsRunner(Command command) => command.Name == "run" && command.Options.Any(o => o.Name == "--plan");
    private sealed record Settings(bool Seal, IAefSigner? Signer, AefContentCapture ContentCapture, AefContentCapture? RequestedContentCapture, AefCliProvenance? Provenance);
}
