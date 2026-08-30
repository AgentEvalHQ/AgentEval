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
    [Theory]
    [InlineData(TypedMemEvalVertical.Temporal)]
    [InlineData(TypedMemEvalVertical.Conjunction)]
    public void OrderingQuestions_NameNoRealWorldEntity(TypedMemEvalVertical vertical)
    {
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
