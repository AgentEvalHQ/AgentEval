using System;
using System.IO;
using System.Linq;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Asserts that the corpus bytes COMPILED INTO the assembly are the corpus bytes in the repository.
/// </summary>
/// <remarks>
/// <para>
/// This closes a seam that every other check steps over. <c>Metadata_DescribesTheCorpusItShipsWith</c>
/// compares <see cref="TypedMemEvalCorpus.Sha256"/> against the sidecar's <c>corpus_sha256</c> — and
/// BOTH of those are read out of the same assembly's embedded resources. An assembly that embedded a
/// stale corpus together with its matching stale sidecar agrees with itself and passes. Until this
/// file existed, no test in the suite read a corpus from disk at all, so nothing anywhere compared
/// what ships against what is committed.
/// </para>
/// <para>
/// That is the gate self-examination rule in its plainest form: the artifact under test was supplying
/// both sides of its own comparison. The repository file is the one input to this decision that the
/// build cannot influence, which is exactly why the assertion is written against it.
/// </para>
/// <para>
/// What it catches: a glob that stops matching, a moved or renamed data file, a stale obj/ artifact
/// surviving an incremental build, a regenerated corpus that never made it into a rebuild. Every one
/// of those ships silently today and every sha we publish still reads as correct, because every sha
/// we publish is computed from the same stale bytes.
/// </para>
/// <para>
/// The packaging half of the chain — that the pushed .nupkg carries these bytes for EVERY target
/// framework — is verified in the release workflow before the push, because only there does the
/// artifact a consumer downloads exist. See "Verify the packed corpora" in release.yml.
/// </para>
/// </remarks>
public class TypedMemEvalPackagedCorpusTests
{
    public static TheoryData<TypedMemEvalVertical> AllVerticals()
    {
        var data = new TheoryData<TypedMemEvalVertical>();
        foreach (var vertical in Enum.GetValues<TypedMemEvalVertical>())
        {
            data.Add(vertical);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void EmbeddedCorpus_IsTheCorpusCommittedToTheRepository(TypedMemEvalVertical vertical)
    {
        var corpusId = TypedMemEvalVerticals.For(vertical).CorpusId;
        var onDisk = ReadRepositoryFile($"{corpusId}.json");

        // Same normalisation the shipped hash uses, deliberately reusing the production helper: a
        // check that normalised differently would measure normalisation rather than content.
        Assert.Equal(
            TypedMemEvalCorpus.ComputeSha256(onDisk),            // DevSkim: ignore DS197836
            TypedMemEvalCorpus.Sha256(vertical));                // DevSkim: ignore DS197836
    }

    [Theory]
    [MemberData(nameof(AllVerticals))]
    public void EmbeddedSidecar_IsTheSidecarCommittedToTheRepository(TypedMemEvalVertical vertical)
    {
        // The sidecar carries the probe records consumers read — arm pass counts, coverage, the
        // reference deployment. A stale one ships numbers describing a corpus nobody has, and it is
        // deliberately outside the corpus hash, so nothing else would notice.
        var corpusId = TypedMemEvalVerticals.For(vertical).CorpusId;
        var onDisk = ReadRepositoryFile($"{corpusId}.meta.json");

        Assert.Equal(
            TypedMemEvalCorpus.ComputeSha256(onDisk),            // DevSkim: ignore DS197836
            TypedMemEvalCorpus.ComputeSha256(                    // DevSkim: ignore DS197836
                TypedMemEvalCorpus.ReadMetadataJson(vertical)));
    }

    /// <summary>Reads a data file from the working tree, located by file name rather than by folder.</summary>
    /// <remarks>
    /// Searching for the name instead of rebuilding <c>Data/typedmemeval/{vertical}/…</c> keeps the
    /// folder convention written down in exactly one place — the csproj glob. A test that restated it
    /// would keep passing after a reorganisation that the glob had already stopped matching.
    /// </remarks>
    private static string ReadRepositoryFile(string fileName)
    {
        var root = LocateRepositoryRoot();
        var data = Path.Combine(root, "src", "AgentEval.Memory", "Data", "typedmemeval");
        Assert.True(Directory.Exists(data), $"TypedMemEval data directory not found at {data}.");

        var matches = Directory.GetFiles(data, fileName, SearchOption.AllDirectories);
        Assert.True(
            matches.Length == 1,
            $"Expected exactly one '{fileName}' under {data}, found {matches.Length}: "
            + string.Join(", ", matches.Select(Path.GetFileName)));

        return File.ReadAllText(matches[0]);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "AgentEval.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("AgentEval.sln not located above the test binary.");
    }
}
