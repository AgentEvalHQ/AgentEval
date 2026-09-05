// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galaxus.RecommendationAgent.Evals.Calibration;

/// <summary>One cut point's derivation, as it is written down.</summary>
/// <param name="Threshold">The constant this describes.</param>
/// <param name="PreCalibrationValue">The one-value-for-two-spaces constant this replaces.</param>
/// <param name="FitRows">Rows in the fit-slice population.</param>
/// <param name="HeldOutRows">Rows in the held-out population.</param>
/// <param name="TargetAdmitRate">
/// α — the operating point. Read off the CONCEPT fit slice at <paramref name="PreCalibrationValue"/>
/// and then held fixed for both spaces. This is the number that makes the derivation a transport
/// rather than a fresh choice.
/// </param>
/// <param name="DerivedValue">Rule 1's answer in THIS space.</param>
/// <param name="DerivedFitAdmitRate">What <paramref name="DerivedValue"/> actually admits on the fit slice.</param>
/// <param name="DerivedHeldOutAdmitRate">What it admits on the held-out slice. The generalisation check.</param>
/// <param name="PreCalibrationFitAdmitRate">What the old constant admits on the fit slice, in THIS space.</param>
/// <param name="PreCalibrationHeldOutAdmitRate">What the old constant admits on the held-out slice, in THIS space.</param>
/// <param name="NullDerivedValue">
/// Rule 2's answer in this space, or NaN where rule 2 has nothing to say. Rule 2 is a CHANCE-tail
/// cut and it applies only to the two cuts that ask "is this related at all" — the dense floor and
/// the attribution floor. A tray-routing line is a policy about which of two trays a related item
/// goes in, and chance does not have an opinion about that, so inventing a budget for it would be
/// choosing a number and calling it derived.
/// </param>
/// <param name="NullAdmitRateAtDerived">
/// What rule 1's derived value admits from the CHANCE population — the fraction of arbitrary
/// catalogue products that clear it. Reported for all four cuts, including the two rule 2 skips: it
/// is a diagnostic, never a derivation.
/// </param>
/// <param name="NullRows">Rows in the chance population.</param>
/// <param name="FitPercentiles">The fit distribution's shape, for anyone re-deriving this by hand.</param>
public sealed record CutDerivation(
    string Threshold,
    double PreCalibrationValue,
    int FitRows,
    int HeldOutRows,
    double TargetAdmitRate,
    double DerivedValue,
    double DerivedFitAdmitRate,
    double DerivedHeldOutAdmitRate,
    double PreCalibrationFitAdmitRate,
    double PreCalibrationHeldOutAdmitRate,
    double NullDerivedValue,
    double NullAdmitRateAtDerived,
    int NullRows,
    IReadOnlyDictionary<string, double> FitPercentiles);

/// <summary>
/// The whole derivation for one embedding space, written to disk so the OTHER space's run can read
/// the operating point instead of re-choosing it, and so the numbers in the report have a file
/// behind them.
/// </summary>
/// <param name="Space">Which space produced it.</param>
/// <param name="SpaceReason">Why that space resolved — including a fallback, when there was one.</param>
/// <param name="GeneratedUtc">When.</param>
/// <param name="FitPersonas">The fit slice, as ids.</param>
/// <param name="HeldOutPersonas">The held-out slice, as ids.</param>
/// <param name="AbstainingPersonas">Customers that contribute no rows because the §F.8 gate fires first.</param>
/// <param name="QueriesWithNoDenseLeg">Queries whose vector was unavailable or all-zero, so the floor never saw them.</param>
/// <param name="Cuts">One entry per cut point.</param>
public sealed record CalibrationRecord(
    string Space,
    string SpaceReason,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<string> FitPersonas,
    IReadOnlyList<string> HeldOutPersonas,
    IReadOnlyList<string> AbstainingPersonas,
    IReadOnlyList<string> QueriesWithNoDenseLeg,
    IReadOnlyList<CutDerivation> Cuts)
{
    /// <summary>The concept space's file name — the one the transport rule reads its α from.</summary>
    public const string ConceptFileName = "calibration.concept.json";

    /// <summary>The real space's file name.</summary>
    public const string RealVectorsFileName = "calibration.real-vectors.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>
    /// Where the records live: beside the code that derives them, inside the repository, NOT under
    /// the gitignored <c>.agenteval</c> snapshot store.
    /// </summary>
    /// <remarks>
    /// A derived threshold that ships in the product needs its provenance to ship with it. A record
    /// written to an ignored folder is a number whose derivation exists only on the machine that
    /// made it, which is the same as a number somebody chose.
    /// </remarks>
    public static string StorageLocation { get; } = FindStorePath();

    /// <summary>Writes this record, overwriting the previous one for the same space.</summary>
    /// <param name="fileName">One of the two file-name constants.</param>
    public void Save(string fileName)
    {
        Directory.CreateDirectory(StorageLocation);
        File.WriteAllText(Path.Combine(StorageLocation, fileName), JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Reads a stored record, or null when it has never been produced.</summary>
    /// <param name="fileName">One of the two file-name constants.</param>
    public static CalibrationRecord? Load(string fileName)
    {
        var path = Path.Combine(StorageLocation, fileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<CalibrationRecord>(File.ReadAllText(path), Json) : null;
    }

    /// <summary>The α this record recorded for one cut point, or NaN when it holds no such cut.</summary>
    /// <param name="threshold">The cut name.</param>
    public double TargetFor(string threshold) =>
        Cuts.FirstOrDefault(c => string.Equals(c.Threshold, threshold, StringComparison.Ordinal))?.TargetAdmitRate ?? double.NaN;

    private static string FindStorePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0)
                return Path.Combine(
                    dir.FullName, "samples", "Galaxus.RecommendationAgent.Evals", "Calibration", "derived");
            dir = dir.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "Calibration", "derived");
    }
}
