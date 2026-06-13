// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AgentEval.RedTeam.Reporting;

/// <summary>
/// Exports red team results to JUnit XML format for CI/CD integration.
/// Compatible with Azure DevOps, GitHub Actions, Jenkins, and other CI tools.
/// </summary>
public sealed class JUnitReportExporter : IReportExporter
{
    /// <inheritdoc />
    public string FormatName => "JUnit XML";

    /// <inheritdoc />
    public string FileExtension => ".xml";

    /// <inheritdoc />
    public string MimeType => "application/xml";

    /// <inheritdoc />
    public string Export(RedTeamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var testSuites = new XElement("testsuites",
            new XAttribute("name", "AgentEval RedTeam"),
            new XAttribute("tests", result.TotalProbes),
            new XAttribute("failures", result.SucceededProbes), // Succeeded attacks = failures in security
            new XAttribute("errors", result.InconclusiveProbes),
            // 5d: surface FailFast-skipped probes so a truncated scan does not render as a complete green run in CI.
            new XAttribute("skipped", result.SkippedProbes),
            new XAttribute("time", result.Duration.TotalSeconds.ToString("F3")),
            new XAttribute("timestamp", result.StartedAt.ToString("yyyy-MM-ddTHH:mm:ss")),
            GetTestSuites(result),
            TruncationNotice(result)   // null when not truncated → ignored by XElement
        );

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            testSuites
        );

        using var writer = new Utf8StringWriter();
        using var xmlWriter = XmlWriter.Create(writer, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Encoding.UTF8
        });

        doc.WriteTo(xmlWriter);
        xmlWriter.Flush();

        return writer.ToString();
    }

    /// <inheritdoc />
    public async Task ExportToFileAsync(RedTeamResult result, string filePath, CancellationToken cancellationToken = default)
    {
        var xml = Export(result);
        await File.WriteAllTextAsync(filePath, xml, cancellationToken);
    }

    // 5d: when the scan was FailFast-truncated, append a visible <skipped> testcase so every CI parser surfaces
    // the truncation (coverage/scores are not comparable to a full scan). Returns null on a complete scan.
    private static XElement? TruncationNotice(RedTeamResult result)
    {
        if (!result.WasTruncated) return null;
        var msg = $"FailFast truncated scan: {result.TotalProbes}/{result.PlannedProbes} probes executed, " +
                  $"{result.SkippedProbes} skipped — coverage and scores are not comparable to a full scan.";
        return new XElement("testsuite",
            new XAttribute("name", "RedTeam.TruncationNotice"),
            new XAttribute("tests", 1),
            new XAttribute("skipped", 1),
            new XAttribute("failures", 0),
            new XAttribute("errors", 0),
            new XElement("testcase",
                new XAttribute("name", "ScanTruncated"),
                new XAttribute("classname", "RedTeam.TruncationNotice"),
                new XElement("skipped", new XAttribute("message", SanitizeForXml(msg)))));
    }

    private static IEnumerable<XElement> GetTestSuites(RedTeamResult result)
    {
        foreach (var attack in result.AttackResults)
        {
            var testSuite = new XElement("testsuite",
                new XAttribute("name", $"RedTeam.{attack.AttackName}"),
                new XAttribute("tests", attack.TotalCount),
                new XAttribute("failures", attack.SucceededCount),
                new XAttribute("errors", attack.InconclusiveCount),
                new XAttribute("skipped", 0),
                new XAttribute("time", attack.ProbeResults
                    .Where(p => p.Duration.HasValue)
                    .Sum(p => p.Duration!.Value.TotalSeconds)
                    .ToString("F3")),
                new XElement("properties",
                    new XElement("property",
                        new XAttribute("name", "owasp_id"),
                        new XAttribute("value", attack.OwaspId ?? "")),
                    new XElement("property",
                        new XAttribute("name", "severity"),
                        new XAttribute("value", attack.Severity.ToString())),
                    new XElement("property",
                        new XAttribute("name", "agent"),
                        new XAttribute("value", SanitizeForXml(result.AgentName)))
                ),
                GetTestCases(attack, result.AgentName ?? "UnknownAgent")
            );

            yield return testSuite;
        }
    }

    private static IEnumerable<XElement> GetTestCases(AttackResult attack, string agentName)
    {
        foreach (var probe in attack.ProbeResults)
        {
            var testCase = new XElement("testcase",
                new XAttribute("name", $"{attack.AttackName}.{probe.ProbeId}"),
                new XAttribute("classname", $"RedTeam.{attack.AttackName}"),
                new XAttribute("time", probe.Duration?.TotalSeconds.ToString("F3") ?? "0.000")
            );

            switch (probe.Outcome)
            {
                case EvaluationOutcome.Succeeded:
                    // A successful attack is a FAILURE from security perspective
                    testCase.Add(new XElement("failure",
                        new XAttribute("message", $"Security vulnerability: {SanitizeForXml(probe.Reason)}"),
                        new XAttribute("type", $"VulnerabilityFound"),
                        $"""
                        Probe: {SanitizeForXml(probe.ProbeId)}
                        Technique: {SanitizeForXml(probe.Technique ?? "unknown")}
                        Difficulty: {probe.Difficulty}

                        ATTACK SUCCEEDED - Agent was compromised!

                        Reason: {SanitizeForXml(probe.Reason)}

                        Prompt:
                        {TruncateForXml(probe.Prompt, 500)}

                        Response:
                        {TruncateForXml(probe.Response, 500)}
                        """
                    ));
                    break;

                case EvaluationOutcome.Inconclusive:
                    testCase.Add(new XElement("error",
                        new XAttribute("message", SanitizeForXml(probe.Error ?? probe.Reason)),
                        new XAttribute("type", "Inconclusive"),
                        $"Could not determine outcome: {SanitizeForXml(probe.Reason)}"
                    ));
                    break;

                case EvaluationOutcome.Resisted:
                    // Resisted = test passed, add system-out for details
                    testCase.Add(new XElement("system-out",
                        $"Agent successfully resisted attack probe {probe.ProbeId}"
                    ));
                    break;
            }

            yield return testCase;
        }
    }

    private static string TruncateForXml(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // Strip XML-invalid control characters first, then normalise newlines, so the
        // replacement does not throw off the length budget below.
        text = SanitizeForXml(text)
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Replaces characters that are illegal in XML 1.0 (e.g. an ANSI escape <c>0x1B</c>
    /// or a NUL byte echoed by the SUT) with the Unicode replacement character.
    /// Without this, <see cref="XmlWriter"/> throws <see cref="ArgumentException"/> and the
    /// entire report — emitted only when a vulnerability is found — is lost. A red-team tool
    /// deliberately elicits exactly this class of output (encoding-evasion / insecure-output),
    /// so every piece of SUT-derived text must pass through here.
    /// </summary>
    private static string SanitizeForXml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                // A valid surrogate pair is always a legal XML supplementary character.
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c);
                    sb.Append(text[i + 1]);
                    i++;
                }
                else
                {
                    sb.Append('�'); // unpaired high surrogate
                }
                continue;
            }
            if (char.IsLowSurrogate(c))
            {
                sb.Append('�'); // unpaired low surrogate
                continue;
            }
            sb.Append(XmlConvert.IsXmlChar(c) ? c : '�');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Custom StringWriter that uses UTF-8 encoding.
    /// </summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
