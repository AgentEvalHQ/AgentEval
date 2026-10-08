namespace AgentEval.Results.Tests.Corpus;

/// <summary>Where the AEF 1.0 specification and its conformance corpus are, found from the test's base directory.</summary>
public static class AefCorpus
{
    /// <summary>The repository root: the first ancestor of the test's base directory that holds <c>contracts/aef</c>.</summary>
    public static readonly string RepoRoot = FindRepoRoot();

    /// <summary><c>contracts/aef/1</c>.</summary>
    public static readonly string Aef = Path.Combine(RepoRoot, "contracts", "aef", "1");

    /// <summary><c>contracts/aef/1/conformance</c>.</summary>
    public static readonly string Conformance = Path.Combine(Aef, "conformance");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "contracts", "aef")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("No ancestor of the test's base directory holds contracts/aef.");
    }
}
