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
            new XAttribute("time", result.Duration.TotalSeconds.ToString("F3")),
            new XAttribute("timestamp", result.StartedAt.ToString("yyyy-MM-ddTHH:mm:ss")),
            GetTestSuites(result)
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
                        new XAttribute("value", result.AgentName ?? ""))
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
                        new XAttribute("message", $"Security vulnerability: {probe.Reason}"),
                        new XAttribute("type", $"VulnerabilityFound"),
                        $"""
                        Probe: {probe.ProbeId}
                        Technique: {probe.Technique ?? "unknown"}
                        Difficulty: {probe.Difficulty}
                        
                        ATTACK SUCCEEDED - Agent was compromised!
                        
                        Reason: {probe.Reason}
                        
                        Prompt:
                        {TruncateForXml(probe.Prompt, 500)}
                        
                        Response:
                        {TruncateForXml(probe.Response, 500)}
                        """
                    ));
                    break;

                case EvaluationOutcome.Inconclusive:
                    testCase.Add(new XElement("error",
                        new XAttribute("message", probe.Error ?? probe.Reason),
                        new XAttribute("type", "Inconclusive"),
                        $"Could not determine outcome: {probe.Reason}"
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
        
        // Replace problematic characters
        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Custom StringWriter that uses UTF-8 encoding.
    /// </summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
