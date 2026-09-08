// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// ADR-031 §5.3 — <i>"If a VOID run can be promoted to a baseline, or renders like a FAIL, the
/// distinction is decorative."</i>
/// </summary>
/// <remarks>
/// <para>
/// A baseline is the bar every later run of a subject is measured against. Promoting a run that
/// measured nothing does not merely record a bad number: it makes every subsequent comparison
/// meaningless <b>while looking exactly like a healthy one</b>, which is the flattering direction.
/// </para>
/// <para>
/// ⚠ <b>Two of the three refusals are reachable today and one is not, and the difference is
/// asserted rather than announced</b> — see <see cref="TheVoidClauseIsAPreconditionNotALiveCheck"/>.
/// </para>
/// </remarks>
public class BaselinePromotionTests
{
    private static RunSummary Summary(string verdict = "PASS", int total = 3, int skipped = 0) =>
        new("1.0", "2026-09-06_00-00-00_deadbeef", verdict,
            new RunStats(total, total - skipped, 0, 0, skipped),
            new Dictionary<string, double> { ["score"] = 1.0 });

    // ── the positive control comes FIRST, because a guard that refuses everything is an outage ──

    [Fact]
    public void AHealthyRunIsPromotable()
    {
        Assert.Null(BaselinePromotion.RefusalFor(Summary()));
        Assert.True(BaselinePromotion.IsPromotable(Summary()));
        BaselinePromotion.EnsurePromotable(Summary());   // does not throw
    }

    [Theory]
    [InlineData("PASS")]
    [InlineData("FAIL")]
    [InlineData("WARN")]
    [InlineData("PENDING")]
    public void EveryVerdictTheSchemaAllowsIsPromotable(string verdict)
    {
        // ⚠ A FAILING run is promotable ON PURPOSE. A baseline records what the subject DID, and
        // refusing to pin a red run would make it impossible to show that a later run improved.
        // The refusals below are about runs that measured nothing, not runs that measured badly.
        Assert.Null(BaselinePromotion.RefusalFor(Summary(verdict)));
    }

    // ── the three refusals, decomposed because they are three different failures ─────────────

    [Theory]
    [InlineData("VOID")]
    [InlineData("void")]
    [InlineData("  Void  ")]
    public void AVoidRunIsRefused(string verdict)
    {
        // Case-insensitive and trimmed: refusing MORE than the exact literal is the safe direction,
        // and "void" out of a hand-edited file is the same fact as "VOID" out of a writer.
        string? refusal = BaselinePromotion.RefusalFor(Summary(verdict));

        Assert.NotNull(refusal);
        Assert.Contains("VOID", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatMeasuredNothingIsRefused_EvenWhenItSaysPASS()
    {
        // The green-because-nothing-ran shape. A PASS over zero scenarios is an absence, and an
        // absence promoted to a baseline is a bar of zero that every later run clears.
        string? refusal = BaselinePromotion.RefusalFor(Summary(total: 0));

        Assert.NotNull(refusal);
        Assert.Contains("NOTHING", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWhoseScenariosWereAllSkippedIsRefused()
    {
        string? refusal = BaselinePromotion.RefusalFor(Summary(total: 4, skipped: 4));

        Assert.NotNull(refusal);
        Assert.Contains("SKIPPED", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithOneRealMeasurementAmongSkipsIsStillPromotable()
    {
        // The boundary, in the direction that matters: 3 of 4 skipped is thin, not empty, and the
        // rule is about there being NO measurement — not about there being few.
        Assert.Null(BaselinePromotion.RefusalFor(Summary(total: 4, skipped: 3)));
    }

    [Fact]
    public void TheThreeRefusalsAreThree_NotOneMessageWithThreeCauses()
    {
        // A pooled "this run is no good" would hide WHICH failure you have, and the three call for
        // different responses: fix the controls, run something, or stop skipping.
        string?[] refusals =
        [
            BaselinePromotion.RefusalFor(Summary("VOID")),
            BaselinePromotion.RefusalFor(Summary(total: 0)),
            BaselinePromotion.RefusalFor(Summary(total: 4, skipped: 4)),
        ];

        Assert.All(refusals, r => Assert.NotNull(r));
        Assert.Equal(3, refusals.Distinct(StringComparer.Ordinal).Count());
    }

    // ── the fact that stops this being oversold ──────────────────────────────────────────────

    [Fact]
    public void TheVoidClauseIsAPreconditionNotALiveCheck()
    {
        // 🔴 MEASURED, NOT ASSUMED. `summary.schema.json` constrains `verdict` to
        // PASS | FAIL | WARN | PENDING — VOID is NOT in it — so nothing in this repository can
        // currently write a VOID run summary that `doctor` would accept. Widening that enum is
        // ADR-031 S4 (gated on Q5) and it would spend ADR-030 §6.2's single budgeted schema change
        // (gated on Q4). The clause is therefore a PRECONDITION for S4, and saying so here stops
        // the next reader treating a green test as evidence that VOID runs are being caught.
        string schema = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "AgentEval.DataLoaders", "Output", "Schema", "v1", "summary.schema.json"));

        Assert.Contains("\"verdict\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("VOID", schema, StringComparison.Ordinal);

        // …and the two clauses that ARE reachable are reachable through the shipped shape.
        Assert.NotNull(BaselinePromotion.RefusalFor(Summary(total: 0)));
        Assert.NotNull(BaselinePromotion.RefusalFor(Summary(total: 2, skipped: 2)));
    }

    // ── every store, not just the one you happened to configure ──────────────────────────────

    [Fact]
    public async Task TheFileSystemStoreRefusesToWriteAnUnpromotableBaseline()
    {
        using var temp = TempWorkspace.Create("BaselineRefusalFs");
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "BaselineSubject");
        await store.EnsureSubjectAsync(subject);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveBaselineAsync(subject, Summary(total: 0)));

        // …and it refused BEFORE writing, so there is no half-written baseline on disk.
        Assert.Empty(Directory.GetFiles(temp.Path, "baseline.json", SearchOption.AllDirectories));

        // The positive control: the same store DOES write a healthy one.
        await store.SaveBaselineAsync(subject, Summary());
        Assert.Single(Directory.GetFiles(temp.Path, "baseline.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TheInMemoryStoreRefusesToo()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "BaselineSubject");
        await store.EnsureSubjectAsync(subject);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveBaselineAsync(subject, Summary("VOID")));
        await store.SaveBaselineAsync(subject, Summary());
    }

    [Fact]
    public async Task EvenTheNullStoreRefuses()
    {
        // It stores nothing, so nothing is at risk on disk — but a caller that promotes a VOID run
        // against the null store and gets no complaint learns the promotion was fine.
        var store = new NullOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "BaselineSubject");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveBaselineAsync(subject, Summary("VOID")));
        await store.SaveBaselineAsync(subject, Summary());
    }

    [Fact]
    public void EveryShippedOutputStoreCallsTheGuard()
    {
        // Asserted by source over the three implementations, and it asserts its own input: a scan
        // that found no files and a scan that found no offenders are indistinguishable.
        string dir = Path.Combine(RepositoryRoot(), "src", "AgentEval.DataLoaders", "Output");
        string[] stores =
        [
            Path.Combine(dir, "FileSystemOutputStore.cs"),
            Path.Combine(dir, "InMemoryOutputStore.cs"),
            Path.Combine(dir, "NullOutputStore.cs"),
        ];

        foreach (string store in stores)
        {
            Assert.True(File.Exists(store), $"{store} is not where this test expects it — the scan is asserting nothing.");

            string body = File.ReadAllText(store);
            Assert.Contains("SaveBaselineAsync", body, StringComparison.Ordinal);
            Assert.Contains("BaselinePromotion.EnsurePromotable(summary)", body, StringComparison.Ordinal);
        }

        // …and the set of implementations is the set this test knows about, so a fourth store added
        // tomorrow makes this fail rather than pass silently.
        string[] found = Directory
            .GetFiles(dir, "*OutputStore.cs")
            .Where(f => File.ReadAllText(f).Contains("public Task SaveBaselineAsync", StringComparison.Ordinal)
                     || File.ReadAllText(f).Contains("public async Task SaveBaselineAsync", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(stores.Order(StringComparer.Ordinal), found);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"AgentEval.sln was not found above {AppContext.BaseDirectory}.");
    }
}
