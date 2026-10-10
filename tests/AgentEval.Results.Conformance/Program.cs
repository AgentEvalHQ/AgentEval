using System.Text;

namespace AgentEval.Results.Conformance;

/// <summary>
/// The aef-dotnet driver: <c>aef-dotnet &lt;operation&gt; &lt;arguments&gt;</c>, the operations of the AEF conformance
/// command-line contract (spec 09 §9.3; <c>tools/aef_conformance.py</c> lists them per vector kind). Each writes one
/// JSON value to standard output and exits 0 when the operation ran; a usage or input error goes to standard error with
/// exit code 2.
/// </summary>
public static class Program
{
    /// <summary>An operation: its arguments (after the operation name) and the writer for its JSON result.</summary>
    internal delegate int Operation(string[] args, TextWriter stdout);

    /// <summary>
    /// The operations, by name. Each work package registers its own here (one line each) and keeps its code in its own
    /// <c>Ops/*.cs</c> file, so packages built in parallel do not edit the same code.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, Operation> Operations = new Dictionary<string, Operation>(StringComparer.Ordinal)
    {
        // WP1: document, decide, match, stream, paths, result-id
        ["document"] = Ops.DocumentOps.Document, ["paths"] = Ops.DocumentOps.Paths, ["result-id"] = Ops.DocumentOps.ResultId, ["decide"] = Ops.DecisionOps.Decide, ["match"] = Ops.ProtocolOps.Match, ["stream"] = Ops.ProtocolOps.Stream,
        // WP2: signature
        ["signature"] = Ops.SignatureOps.Signature,
        // WP3: run, seal, chain, view
        ["run"] = Ops.RunOps.Run, ["seal"] = Ops.RunOps.Seal, ["chain"] = Ops.RunOps.Chain, ["view"] = Ops.RunOps.View,
        // WP4: checkpoint, lanes, conform
        ["checkpoint"] = Ops.CheckpointOps.Checkpoint, ["lanes"] = Ops.CheckpointOps.Lanes, ["conform"] = Ops.CheckpointOps.Conform,
        // WP5a: summarize, seal-write, sign (the write side); write-samples (not a spec 09 operation: writer-crosscheck.sh)
        ["summarize"] = Ops.WriteSideOps.Summarize, ["seal-write"] = Ops.WriteSideOps.SealWrite, ["sign"] = Ops.WriteSideOps.Sign, ["write-samples"] = Ops.WriterOps.WriteSamples,
        // Round 4: produce (a Producer writes the run a scenario describes)
        ["produce"] = Ops.ProduceOps.Produce,
        // Round 7: job (a Runner runs a plan against a scripted target)
        ["job"] = Ops.JobOps.Job,
    };

    public static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        try
        {
            return Dispatch(args, stdout, Console.Error);
        }
        finally
        {
            stdout.Flush();
        }
    }

    /// <summary>
    /// Runs one operation; the in-process entry point the tests use. Exit codes ([CONF-3]): 0 when the operation ran; 2 for
    /// a usage or input error the operation recognised (a <see cref="UsageException"/>); 1 for any other failure, which
    /// nobody expected and is never reported as an input error. The message goes to standard error.
    /// </summary>
    public static int Dispatch(string[] args, TextWriter stdout, TextWriter stderr) => Dispatch(args, stdout, stderr, Operations);

    /// <summary><see cref="Dispatch(string[], TextWriter, TextWriter)"/> over the given operations (a test injects a failing one).</summary>
    internal static int Dispatch(string[] args, TextWriter stdout, TextWriter stderr, IReadOnlyDictionary<string, Operation> operations)
    {
        if (args.Length == 0 || !operations.TryGetValue(args[0], out var operation))
        {
            stderr.WriteLine(args.Length == 0
                ? "usage: aef-dotnet <operation> <arguments>"
                : $"unknown operation '{args[0]}' (known: {string.Join(", ", operations.Keys.Order(StringComparer.Ordinal))})");
            return 2;
        }

        try
        {
            return operation(args[1..], stdout);
        }
        catch (UsageException e)
        {
            stderr.WriteLine(e.Message);
            return 2;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            stderr.WriteLine($"aef-dotnet {args[0]}: an unexpected failure, not an input error: {e}");
            return 1;
        }
    }
}

/// <summary>
/// Wrong arguments or unreadable input for an operation, recognised as such: reported on standard error, exit code 2
/// ([CONF-3]). Never thrown for a failure nobody expected.
/// </summary>
public sealed class UsageException(string message) : Exception(message);
