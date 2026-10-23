// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.CommandLine;
using System.Text.RegularExpressions;
using AgentEval.Cli;
using AgentEval.Tests.Contracts;

namespace AgentEval.Tests.Cli.Aef;

// Drift guard, not proof of runtime publication. Real producer/reader tests establish that separately.
public sealed class AefWriterCensusTests
{
    private static readonly (string Command, string? LegacyOwner)[] BenchProducers =
    [
        ("gdpr", "BenchCommand.cs"), ("eu-ai-act", "BenchEuAiActCommand.cs"),
        ("agentic", "BenchAgenticCommand.cs"), ("owasp", "BenchOwaspCommand.cs"),
        ("mitre", "BenchMitreCommand.cs"), ("nist", "BenchNistCommand.cs"),
        ("longmemeval", "BenchLongMemEvalCommand.cs"), ("typedmemeval", "BenchTypedMemEvalCommand.cs"),
        ("memory", "BenchMemoryCommand.cs"), ("trace-fidelity", "BenchTraceFidelityCommand.cs"),
        ("workflow-trace-fidelity", "BenchWorkflowTraceFidelityCommand.cs"), ("perf", "BenchPerfCommand.cs"),
        ("autoaudit", null),
    ];

    [Fact]
    public void BenchCommandTree_ExecutionFamilies_AreAllClassified()
    {
        var bench = CliCommandTree.Build().Root.Subcommands.Single(c => c.Name == "bench");
        Assert.Equal(BenchProducers.Select(p => p.Command).OrderBy(x => x, StringComparer.Ordinal),
            bench.Subcommands.Select(c => c.Name).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void CliPersistenceOwners_AreAllAssignedToKnownProducerOrSharedHelper()
    {
        var cli = Path.Combine(AefSchemaSet.RepoRoot(), "src", "AgentEval.Cli");
        // The current persistence boundary is FileSystemOutputStore/IOutputStore, including shared stochastic trials.
        // New source ownership requires an explicit census decision; this deliberately does not inspect arbitrary IL.
        var owners = Directory.EnumerateFiles(cli, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(cli, path).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"\bnew\s+FileSystemOutputStore\b|\bIOutputStore\b"))
            .Select(path => Path.GetRelativePath(cli, path).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal);
        var expected = BenchProducers.Where(p => p.LegacyOwner is not null).Select(p => "Commands/" + p.LegacyOwner)
            .Append("Commands/StochasticRunner.cs").OrderBy(p => p, StringComparer.Ordinal);
        Assert.Equal(expected, owners);
    }

    [Fact]
    public void ExecutionCommands_DeclareEvidenceControls_IncludingNativeOnlyProducers()
    {
        var root = CliCommandTree.Build().Root;
        var families = root.Subcommands.Single(c => c.Name == "bench").Subcommands;
        var perf = families.Single(c => c.Name == "perf");
        Assert.Equal(new[] { "cost", "latency", "throughput" }, perf.Subcommands.Select(c => c.Name).OrderBy(x => x, StringComparer.Ordinal));
        var commands = families.Where(c => c.Name != "perf").Concat(perf.Subcommands)
            .Concat(root.Subcommands.Where(c => c.Name is "eval" or "redteam" or "run")).ToArray();
        Assert.Equal(BenchProducers.Length + 5, commands.Length);
        foreach (var command in commands)
        {
            Assert.Contains(command.Options, o => o.Name == "--aef");
            Assert.Contains(command.Options, o => o.Name == "--no-aef");
        }
    }
}
