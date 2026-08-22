// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;

namespace AgentEval.PartnerDeskDemo.Demo;

/// <summary>
/// Stage-legible console output. Every line is kept under 100 characters so the demo stays readable projected at
/// 18–20pt, and colour is applied only when the sink really is the console.
/// </summary>
/// <remarks>
/// Console text is <b>presentation</b>, never evidence. The pass oracle reads <see cref="PhaseOutcome"/>, which is
/// assembled from the tool ledger, the model's proposed calls, and Gatekeeper's recorded verdicts.
/// </remarks>
public sealed class DemoOutput
{
    /// <summary>The widest line this demo prints.</summary>
    public const int Width = 96;

    private readonly TextWriter _writer;
    private readonly bool _colour;

    private DemoOutput(TextWriter writer, bool colour)
    {
        _writer = writer;
        _colour = colour;
    }

    /// <summary>Writes to the console, in colour.</summary>
    public static DemoOutput Console { get; } = new(System.Console.Out, colour: true);

    /// <summary>Discards everything — used by the deterministic tests.</summary>
    public static DemoOutput Silent { get; } = new(TextWriter.Null, colour: false);

    /// <summary>Writes one raw line.</summary>
    public void Line(string text = "") => _writer.WriteLine(text);

    /// <summary>Writes a horizontal rule.</summary>
    public void Rule(char c = '-') => _writer.WriteLine(new string(c, Width));

    /// <summary>Writes a line in the supplied colour.</summary>
    public void Line(ConsoleColor colour, string text)
    {
        if (!_colour)
        {
            _writer.WriteLine(text);
            return;
        }

        var previous = System.Console.ForegroundColor;
        System.Console.ForegroundColor = colour;
        _writer.WriteLine(text);
        System.Console.ForegroundColor = previous;
    }

    /// <summary>The per-phase header: which phase, whether the supplier is compromised, which gates are active.</summary>
    public void PhaseBanner(string phase, string title, bool evilMode, string gates)
    {
        Line();
        Rule('=');
        Line(ConsoleColor.White, $"PHASE {phase} — {title}");
        Line($"  PartnerIntel MCP : {(evilMode ? "COMPROMISED (addendum ON)" : "clean (addendum off)")}");
        Line($"  Gatekeeper       : {gates}");
        Rule('=');
    }

    /// <summary>One proposed tool call, printed the moment the model asks for it.</summary>
    public void ToolProposed(string toolName, string arguments)
    {
        var text = $"  -> {toolName}({arguments})";
        Line(ConsoleColor.DarkGray, Clip(text));
    }

    /// <summary>The outcome of a proposed call that ran.</summary>
    public void ToolAllowed(string toolName, string detail)
        => Line(ConsoleColor.Green, Clip($"     ALLOWED  {toolName} — {detail}"));

    /// <summary>The outcome of a proposed call an enforced gate stopped.</summary>
    public void ToolRefused(string toolName, string policy, string? reason)
    {
        Line(ConsoleColor.Red, Clip($"     REFUSED  {toolName} — {policy}"));
        if (!string.IsNullOrWhiteSpace(reason))
        {
            foreach (var line in Wrap(reason!, Width - 14))
            {
                Line(ConsoleColor.Red, $"              {line}");
            }
        }
    }

    /// <summary>The outcome of a tool result an enforced result gate withheld from model context.</summary>
    public void ResultRefused(string toolName, string policy, string? reason)
    {
        Line(ConsoleColor.Red, Clip($"     WITHHELD {toolName} result — {policy}"));
        if (!string.IsNullOrWhiteSpace(reason))
        {
            foreach (var line in Wrap(reason!, Width - 14))
            {
                Line(ConsoleColor.Red, $"              {line}");
            }
        }
    }

    /// <summary>Prints a labelled section heading.</summary>
    public void Section(string title)
    {
        Line();
        Line(ConsoleColor.White, title);
        Line(new string('-', Math.Min(Width, title.Length)));
    }

    /// <summary>Prints a paragraph, wrapped to the stage width and indented.</summary>
    public void Paragraph(string text, string indent = "  ")
    {
        foreach (var line in Wrap(text, Width - indent.Length))
        {
            _writer.WriteLine(indent + line);
        }
    }

    /// <summary>Formats a number with thousands separators.</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Clip(string text) => text.Length <= Width ? text : text[..(Width - 1)] + "…";

    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = new System.Text.StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    yield return line.ToString();
                    line.Clear();
                }

                if (line.Length > 0)
                {
                    line.Append(' ');
                }

                line.Append(word);
            }

            yield return line.ToString();
        }
    }
}
