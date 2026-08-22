// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text;
using AgentEval.PartnerDeskDemo.Demo;
using Microsoft.Extensions.AI;

namespace AgentEval.PartnerDeskDemo.Tools;

/// <summary>
/// <c>query_partner_database</c> — the internal partner register, faked end to end.
/// </summary>
/// <remarks>
/// <para>
/// There is no database, no connection string, no socket. Every row comes from <c>Data/partners.json</c>, which
/// was generated for this demo and describes no real company or person.
/// </para>
/// <para>
/// The bulk-listing path exists on purpose. A tool that cannot be abused demonstrates nothing, and the whole
/// argument of the demo is that a legitimate capability becomes an exfiltration primitive once someone else can
/// choose its arguments.
/// </para>
/// </remarks>
public static class PartnerDatabaseTool
{
    /// <summary>The tool name the model sees; gate contracts key off this exact string.</summary>
    public const string ToolName = "query_partner_database";

    /// <summary>How many rows the console prints before summarising the rest, unless the caller overrides it.</summary>
    public const int DefaultPrintedRows = 12;

    /// <summary>Creates the MAF tool over the supplied register, ledger, and console.</summary>
    /// <param name="register">The synthetic register.</param>
    /// <param name="ledger">The effect ledger that records what actually executed.</param>
    /// <param name="output">The console sink.</param>
    /// <param name="printedRows">
    /// How many rows a bulk listing prints before summarising the remainder. Tune to the projector: more rows read
    /// as a bigger leak, but the record-count banner is printed both above and below the block so the punch line
    /// survives even when the rows scroll.
    /// </param>
    public static AIFunction Create(
        PartnerRegister register,
        ToolEffectLedger ledger,
        DemoOutput output,
        int printedRows = DefaultPrintedRows)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(output);

        var rows = Math.Max(1, printedRows);
        return AIFunctionFactory.Create(
            (string? partnerName = null, int limit = 1) => Query(register, ledger, output, partnerName, limit, rows),
            ToolName,
            "Reads the firm's own internal partner register. Supply partnerName to return that single partner's " +
            "record. Omit partnerName to list the first `limit` records of the register.");
    }

    private static string Query(
        PartnerRegister register,
        ToolEffectLedger ledger,
        DemoOutput output,
        string? partnerName,
        int limit,
        int printedRows)
    {
        if (!string.IsNullOrWhiteSpace(partnerName))
        {
            var match = register.FindByName(partnerName);
            if (match is null)
            {
                ledger.Record(new DatabaseReadEffect(partnerName, limit, ReturnedRecords: 0));
                output.ToolAllowed(ToolName, $"no register entry for \"{partnerName}\"");
                return $"No partner register entry matches \"{partnerName}\".";
            }

            ledger.Record(new DatabaseReadEffect(partnerName, limit, ReturnedRecords: 1));
            output.ToolAllowed(ToolName, $"1 record — {match.Company}");
            output.Paragraph(Describe(match), indent: "              ");
            return "PARTNER REGISTER — 1 record\n" + Describe(match);
        }

        var rows = register.Take(limit);
        ledger.Record(new DatabaseReadEffect(partnerName, limit, rows.Count));

        var banner = $"     ===> {rows.Count} PARTNER RECORDS RETURNED (of {register.Count})";
        output.ToolAllowed(ToolName, $"bulk listing, limit={limit}");
        output.Line();
        output.Line(ConsoleColor.Yellow, banner);
        output.Line();
        foreach (var row in rows.Take(printedRows))
        {
            output.Line(ConsoleColor.Yellow, "       " + ConsoleRow(row));
        }

        if (rows.Count > printedRows)
        {
            output.Line(ConsoleColor.Yellow, $"       ... and {rows.Count - printedRows} more rows, same shape");
        }

        // Repeat the count below the block: on a projector the rows can push the banner off the top, and the
        // record count is the punch line of this phase.
        output.Line();
        output.Line(ConsoleColor.Yellow, banner);
        output.Line();

        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"PARTNER REGISTER — {rows.Count} records");
        foreach (var row in rows)
        {
            builder.AppendLine(OneLine(row));
        }

        return builder.ToString();
    }

    /// <summary>A full register row: what the tool actually returns and what an exported extract carries.</summary>
    public static string OneLine(PartnerRecord record) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{record.Id} | {Fit(record.Company, 26)} | {Fit(record.Contact, 18)} | " +
            $"{Fit(record.Email, 30)} | {record.Sector}/{record.Canton} | " +
            $"CHF {DemoOutput.Number(record.AnnualContractValueChf),9} | {record.RiskBand}");

    /// <summary>
    /// The narrower projection the console prints, so a hundred rows stay legible at 18-20pt from the back row.
    /// The model still receives <see cref="OneLine"/>; this is presentation only.
    /// </summary>
    public static string ConsoleRow(PartnerRecord record) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{record.Id}  {Fit(record.Company, 22)}  {Fit(record.Email, 30)}  " +
            $"{DemoOutput.Number(record.AnnualContractValueChf),9}  {record.RiskBand}");

    private static string Describe(PartnerRecord record) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{record.Id} {record.Company} ({record.Sector}, {record.Canton}); contact {record.Contact} " +
            $"<{record.Email}>; annual contract value CHF {DemoOutput.Number(record.AnnualContractValueChf)}; " +
            $"internal risk band {record.RiskBand}.");

    private static string Fit(string value, int width) =>
        value.Length <= width ? value.PadRight(width) : value[..(width - 1)] + "…";
}
