// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Result of <see cref="AmountArgumentParser.TryGetAmount"/> — whether a monetary argument was usable.</summary>
internal enum AmountParseResult
{
    /// <summary>The argument is not present (or null) — no monetary component.</summary>
    Absent,

    /// <summary>The argument parsed to a usable decimal.</summary>
    Parsed,

    /// <summary>The argument is present but could not be parsed — the caller must fail closed.</summary>
    Unparseable,
}

/// <summary>
/// Shared, culture-invariant parser for a tool-call argument that carries a monetary amount — a value that may
/// arrive as a native <see cref="decimal"/>/<see cref="double"/>/<see cref="int"/>/… or (very commonly, over MCP /
/// JSON-based tool plumbing) a <see cref="JsonElement"/> or a numeric <see cref="string"/>. Extracted so
/// <see cref="RunBudgetGate"/>'s built-in monetary cap and the dedicated <see cref="MonetaryLimitGate"/> parse
/// amounts identically — one parser, no drift between the two gates that both need to fail closed on the same
/// ambiguous input.
/// </summary>
internal static class AmountArgumentParser
{
    /// <summary>Extracts a monetary amount from <paramref name="argName"/> in <paramref name="args"/>.</summary>
    public static AmountParseResult TryGetAmount(IReadOnlyDictionary<string, object?>? args, string argName, out decimal amount)
    {
        amount = 0m;
        if (args is null || !args.TryGetValue(argName, out var raw) || raw is null)
        {
            return AmountParseResult.Absent;
        }

        switch (raw)
        {
            case decimal d: amount = d; return AmountParseResult.Parsed;
            case double db: return TryFromDouble(db, out amount) ? AmountParseResult.Parsed : AmountParseResult.Unparseable;
            case float f: return TryFromDouble(f, out amount) ? AmountParseResult.Parsed : AmountParseResult.Unparseable;
            case int i: amount = i; return AmountParseResult.Parsed;
            case long l: amount = l; return AmountParseResult.Parsed;
            case JsonElement je: return TryFromJsonElement(je, out amount) ? AmountParseResult.Parsed : AmountParseResult.Unparseable;
            case string s:
                return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out amount) ? AmountParseResult.Parsed : AmountParseResult.Unparseable;
            default:
                try
                {
                    amount = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                    return AmountParseResult.Parsed;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return AmountParseResult.Unparseable;   // present but not a usable amount ⇒ fail closed
                }
        }
    }

    private static bool TryFromJsonElement(JsonElement je, out decimal amount)
    {
        amount = 0m;
        return je.ValueKind switch
        {
            JsonValueKind.Number => je.TryGetDecimal(out amount),
            JsonValueKind.String => decimal.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out amount),
            _ => false,
        };
    }

    private static bool TryFromDouble(double d, out decimal amount)
    {
        amount = 0m;
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return false;
        }

        try
        {
            amount = (decimal)d;   // can overflow for very large magnitudes
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
