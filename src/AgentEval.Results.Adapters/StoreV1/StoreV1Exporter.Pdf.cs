// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.StoreV1;

internal sealed record StoreV1CompliancePdf(string DocumentId, byte[] Bytes);

public static partial class StoreV1Exporter
{
    private static void AddCompliancePdfs(AefRunWriter writer, IReadOnlyList<StoreV1CompliancePdf> pdfs, AefContentCapture capture)
    {
        if (capture == AefContentCapture.Off) return;
        for (var i = 0; i < pdfs.Count; i++) writer.AddEvidence(new()
        {
            EvidenceId = "E-compliance-pdf-" + (i + 1), Kind = AefEvidenceKind.Document,
            Link = AefEvidenceLink.ToBlob(writer.PutBlob(pdfs[i].Bytes)),
            Description = "Captured source PDF for the referenced compliance document; ingest custody begins at conversion.",
            Ext = new JsonObject { [AefCompliancePdfReader.ExtensionName] = new JsonObject { ["pdfFor"] = pdfs[i].DocumentId } },
        });
    }
}
