using AgentEval.Assertions;

namespace AgentEval.Memory.Assertions;

/// <summary>
/// Exception thrown when memory-related assertions fail.
/// Provides structured error information following AgentEval patterns.
/// </summary>
public class MemoryAssertionException : AgentEvalAssertionException
{
    public MemoryAssertionException(
        string message,
        string? expected = null,
        string? actual = null,
        IEnumerable<string>? suggestions = null,
        string? because = null)
        : base(BuildMessage(message, expected, actual, suggestions, because))
    {
        // Set inherited init properties from AgentEvalAssertionException
        Expected = expected;
        Actual = actual;
        Suggestions = suggestions?.ToArray() ?? Array.Empty<string>();
        Because = because;
    }

    private static string BuildMessage(
        string message,
        string? expected,
        string? actual,
        IEnumerable<string>? suggestions,
        string? because)
    {
        var parts = new List<string> { message };

        if (!string.IsNullOrEmpty(because))
        {
            parts.Add($"Because: {because}");
        }

        if (!string.IsNullOrEmpty(expected))
        {
            parts.Add($"Expected: {expected}");
        }

        if (!string.IsNullOrEmpty(actual))
        {
            parts.Add($"Actual: {actual}");
        }

        var suggestionsList = suggestions?.ToArray();
        if (suggestionsList?.Length > 0)
        {
            parts.Add("Suggestions:");
            parts.AddRange(suggestionsList.Select(s => $"  • {s}"));
        }

        return string.Join(Environment.NewLine, parts);
    }
}