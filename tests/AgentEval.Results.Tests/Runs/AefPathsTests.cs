using AgentEval.Results.Runs;

namespace AgentEval.Results.Tests.Runs;

/// <summary>[RUN-3]: the paths of a run folder.</summary>
public class AefPathsTests
{
    private static string[] Bad(params string[] paths) => [.. AefPaths.Check(paths).Select(p => p.Path)];

    [Theory]
    [InlineData("run.json")]
    [InlineData("blobs/sha256/ab/abcdef")]
    [InlineData("ext/a-b_c.d/e.txt")]
    [InlineData("ext/COM10")]
    [InlineData("ext/CONx")]
    [InlineData("ext/LPT0")]
    [InlineData("ext/console.txt")]
    [InlineData("ext/a.b.c")]
    public void AValidPath(string path)
    {
        Assert.True(AefPaths.IsValid(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/run.json")]
    [InlineData("ext/")]
    [InlineData("ext//a")]
    [InlineData("ext/./a")]
    [InlineData("ext/../run.json")]
    [InlineData(".DS_Store")]
    [InlineData("ext/a.")]
    [InlineData("ext/a b")]
    [InlineData("ext\\a")]
    [InlineData("ext/café")]
    [InlineData("ext/a:b")]
    [InlineData("ext/CON")]
    [InlineData("ext/con.txt")]
    [InlineData("ext/Aux.tar.gz")]
    [InlineData("ext/lpt9.log")]
    [InlineData("COM1/x")]
    public void AnInvalidPath(string path)
    {
        Assert.False(AefPaths.IsValid(path));
        Assert.Equal(new[] { path }, Bad(path));
    }

    [Fact]
    public void ThePathLengthCountsUtf8Bytes_255IsFine_256IsAPathProblem()
    {
        var ok = "ext/" + new string('a', 251);
        var tooLong = ok + "b";

        Assert.True(AefPaths.IsValid(ok));
        Assert.False(AefPaths.IsValid(tooLong));
    }

    [Fact]
    public void TwoPathsThatDifferOnlyInCase_TheLaterInByteOrderIsReported()
    {
        Assert.Equal(new[] { "ext/a.txt" }, Bad("ext/a.txt", "ext/A.txt"));
    }

    [Fact]
    public void TwoFoldersThatDifferOnlyInCase_EveryPathInTheLaterOneIsReported()
    {
        Assert.Equal(new[] { "ext/data/y", "ext/data/z" }, Bad("ext/Data/x", "ext/data/y", "ext/data/z", "ext/other"));
    }

    [Fact]
    public void AFileAndAFolderThatDifferOnlyInCase_Clash()
    {
        Assert.Equal(new[] { "ext/data/y" }, Bad("ext/Data", "ext/data/y"));
    }

    [Fact]
    public void ThreeWays_AllButTheFirstAreReported()
    {
        Assert.Equal(new[] { "ext/aB", "ext/ab" }, Bad("ext/ab", "ext/AB", "ext/aB"));
    }

    [Fact]
    public void ProblemsAreInByteOrder_EachPathOnce()
    {
        Assert.Equal(new[] { ".x", "Z/.y", "a/CON" }, Bad("a/CON", "Z/.y", ".x", "a/CON", "ok"));
    }
}
