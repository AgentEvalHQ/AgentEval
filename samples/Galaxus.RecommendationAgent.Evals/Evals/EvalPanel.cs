// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using System.Text;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// The 82-column box frame Evals 02b and 02c print in — the same frame and colours as
/// <see cref="EvalPrinter"/>, kept in a file of its own so the two new evals do not need to edit
/// a printer another lane is editing at the same time.
/// </summary>
/// <remarks>
/// A row longer than the frame WRAPS onto a continuation line; it is never cut. A printer that
/// silently drops the tail of a sentence can only ever be checked for the part that fits.
/// </remarks>
internal static class EvalPanel
{
    /// <summary>Total width including the borders.</summary>
    public const int BoxWidth = 82;

    /// <summary>Width available to content.</summary>
    public const int InnerWidth = 78;

    /// <summary>Opens a panel with a title row.</summary>
    /// <param name="title">The title.</param>
    public static void Open(string title)
    {
        Console.WriteLine();
        Border('╔', '╗');
        WriteRow(title, ConsoleColor.White);
        Divider();
    }

    /// <summary>Closes a panel.</summary>
    public static void Close()
    {
        Border('╚', '╝');
        Console.WriteLine();
    }

    /// <summary>A horizontal divider.</summary>
    public static void Divider()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("╠" + new string('═', BoxWidth - 2) + "╣");
        Console.ResetColor();
    }

    /// <summary>A yellow section heading.</summary>
    /// <param name="heading">The heading.</param>
    public static void Section(string heading) => WriteRow(heading, ConsoleColor.Yellow);

    /// <summary>A content row in the given colour, wrapped when it overflows.</summary>
    /// <param name="content">Row text, already indented.</param>
    /// <param name="colour">Text colour.</param>
    public static void Row(string content, ConsoleColor colour = ConsoleColor.White)
    {
        if (content.Length <= InnerWidth)
        {
            WriteRow(content, colour);
            return;
        }

        int indent = 0;
        while (indent < content.Length && content[indent] == ' ') indent++;
        indent = Math.Min(indent, InnerWidth / 2);
        int continuation = Math.Min(indent + 2, InnerWidth / 2);

        bool first = true;
        foreach (string line in Wrap(content.TrimStart(), InnerWidth - continuation))
        {
            WriteRow(new string(' ', first ? indent : continuation) + line, colour);
            first = false;
        }
    }

    /// <summary>A dark-grey note row.</summary>
    /// <param name="content">Row text.</param>
    public static void Note(string content) => Row(content, ConsoleColor.DarkGray);

    /// <summary>Formats a double at three decimals, or <c>n/a</c> for NaN.</summary>
    /// <param name="value">The value.</param>
    public static string F3(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>Pads or truncates to a width, with an ellipsis when truncated.</summary>
    /// <param name="text">The text.</param>
    /// <param name="width">Target width.</param>
    public static string Fit(string text, int width) =>
        text.Length <= width ? text.PadRight(width) : text[..Math.Max(0, width - 1)] + "…";

    /// <summary>Greedy word wrap.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxWidth">Line width.</param>
    public static IEnumerable<string> Wrap(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var line = new StringBuilder();
        foreach (string word in text.Split(' '))
        {
            if (line.Length + word.Length + 1 > maxWidth && line.Length > 0)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    /// <summary>Prints a coloured line OUTSIDE any panel, wrapped at the box width.</summary>
    /// <param name="text">The text.</param>
    /// <param name="colour">Text colour.</param>
    public static void Line(string text, ConsoleColor colour = ConsoleColor.White)
    {
        Console.ForegroundColor = colour;
        foreach (string line in Wrap(text, BoxWidth)) Console.WriteLine(line);
        Console.ResetColor();
    }

    private static void Border(char left, char right)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(left + new string('═', BoxWidth - 2) + right);
        Console.ResetColor();
    }

    private static void WriteRow(string content, ConsoleColor colour)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("║ ");
        Console.ForegroundColor = colour;
        string padded = content.PadRight(InnerWidth);
        Console.Write(padded[..Math.Min(padded.Length, InnerWidth)]);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(" ║");
        Console.ResetColor();
    }
}
