using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentEval.Memory.External.TypedMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Guards the ordering verticals against milestone names that a model already knows facts about.
/// </summary>
/// <remarks>
/// <para>
/// V2 caught the reference model answering <c>"Which came first, the Fenn commissioning or the
/// Yarrow move?"</c> — with no haystack at all — as <c>"Yarrow moved its shipbuilding operations to
/// Scotstoun, Glasgow in the early 1900s, while the Fenn commissioning was during World War II"</c>.
/// Nine of Temporal's twelve milestone names were real entities. The generator's comment asserted
/// the opposite and had never been measured.
/// </para>
/// <para>
/// V2 CANNOT BE THE GUARD HERE, which is why this test exists alongside it. V2 scores a leak only
/// when the leak AGREES with gold: five temporal questions show world-knowledge reasoning and V2
/// flagged two, because <c>tme-tem-013</c> happened to be ordered the way history was. Ordered the
/// other way, the model would have been confidently wrong ten times out of ten and the question
/// would have passed. A probe that can only see half a defect cannot certify its repair.
/// </para>
/// <para>
/// So the guard is deterministic instead: the colliding names are recorded in
/// <c>tools/name-collision-audit.json</c>, measured once by <c>tools/audit_name_collisions.py</c>,
/// and this asserts none of them reaches a question or an answer in the two verticals whose
/// questions ask about ORDER. Scoped there deliberately — a real "Meridian Tools" does not tell a
/// model which vendor the user chose, so the other 61 collisions are declared in that file rather
/// than rewritten at the cost of nine corpus shas and every control the consuming project holds.
/// </para>
/// </remarks>
public class TypedMemEvalNameCollisionTests
{
    public static TheoryData<TypedMemEvalVertical> EveryVertical()
    {
        var data = new TheoryData<TypedMemEvalVertical>();
        foreach (var vertical in System.Enum.GetValues<TypedMemEvalVertical>())
        {
            data.Add(vertical);
        }

        return data;
    }

    /// <summary>
    /// The declared-but-unremediated count, ratcheted so the exemption cannot quietly grow.
    /// </summary>
    /// <remarks>
    /// The audit measured 75 real-world referents across 163 names and remediated 14 — the ones in
    /// ORDERING banks, where the harm model says a referent carrying a date tells a model which
    /// milestone came first. The other 61 are recorded as assessed low-harm: vendors, places
    /// someone lived, products chosen, first names in padding, none of which answers its question.
    /// <para>
    /// That exemption is REASONED, so this guard does not re-open it. What it does is stop the set
    /// GROWING: a new bank that quietly adds real-world names would otherwise inherit the
    /// exemption's silence without ever being assessed.
    /// </para>
    /// </remarks>
    private const int DeclaredUnremediated = 61;

    [Fact]
    public void TheDeclaredExemption_DoesNotGrow()
    {
        var audit = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(LocateRepositoryRoot(), "tools", "name-collision-audit.json")))
            .RootElement;
        var declared = audit.GetProperty("declared_not_remediated").GetProperty("count").GetInt32();

        Assert.True(
            declared <= DeclaredUnremediated,
            $"The audit now declares {declared} unremediated real-world names against "
            + $"{DeclaredUnremediated} when this ratchet was set. The exemption is a reasoned "
            + "assessment of a MEASURED set, not a standing allowance — a larger set has not been "
            + "assessed. Re-run tools/audit_name_collisions.py and judge the additions, or "
            + "remediate them.");
    }

    [Theory]
    [MemberData(nameof(EveryVertical))]
    public void OrderingQuestions_NameNoRealWorldEntity(TypedMemEvalVertical vertical)
    {
        // WIDENED TO EVERY VERTICAL, at the consuming project's request. It scanned Temporal and
        // Conjunction because those are the ordering verticals the audit's harm model points at —
        // but a remediated name reappearing ANYWHERE is a bank regression, and the cost of checking
        // the other seven is nil. This is about the 14 names already judged harmful; the 61
        // declared low-harm are held by TheDeclaredExemption_DoesNotGrow instead, because
        // re-flagging a reasoned exemption would be overriding an assessment rather than gating it.
        var colliding = RemediatedNames();
        Assert.True(colliding.Length >= 14,
            $"The audit record lists {colliding.Length} remediated names; it described 14 when this "
            + "guard was written. A shrunken list would make the assertion below vacuous.");

        var corpus = JsonDocument.Parse(TypedMemEvalCorpus.ReadJson(vertical)).RootElement;
        var offenders = (
            from entry in corpus.EnumerateArray()
            let text = entry.GetProperty("question").GetString() + " "
                       + entry.GetProperty("answer").GetString()
            from name in colliding
            // Word boundaries: Bitemporal's "Calderwick" is a different, declared, place and must
            // not be read as the river "Calder".
            where System.Text.RegularExpressions.Regex.IsMatch(
                text, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b")
            select $"{entry.GetProperty("question_id").GetString()}: {name}").ToArray();

        Assert.True(offenders.Length == 0,
            $"{vertical} names real-world entities in its questions or answers, which lets a model "
            + "order the events without the corpus: " + string.Join(", ", offenders));
    }

    [Fact]
    public void AuditRecord_CoversEveryOrderingBank()
    {
        // The record is the guard's only source of truth, so it must actually describe the banks
        // this repository ships. A record that had quietly lost a bank would leave that bank
        // unguarded while every assertion above still passed.
        var record = AuditRecord();
        var banks = record.GetProperty("remediated_banks").EnumerateArray()
            .Select(b => b.GetString()).ToArray();
        Assert.Contains("temporal:MILESTONES", banks);
        Assert.Contains("temporal:FILLER_MILESTONES", banks);
        Assert.Contains("conjunction:MILESTONES", banks);

        // The 61 left in place are a DECISION, not an oversight, and the record has to say so.
        var declared = record.GetProperty("declared_not_remediated");
        Assert.True(declared.GetProperty("count").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(declared.GetProperty("reason").GetString()));
    }

    private static string[] RemediatedNames() =>
        AuditRecord().GetProperty("remediated_names").EnumerateArray()
            .Select(n => n.GetString()!).ToArray();

    private static JsonElement AuditRecord()
    {
        var path = Path.Combine(LocateRepositoryRoot(), "tools", "name-collision-audit.json");
        Assert.True(File.Exists(path), $"Name-collision audit record not found at {path}.");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
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
