// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

// QuestPDF community license — must be set before any document rendering.
// The static constructor of GDPRPdfRenderer also sets this so unit tests work,
// but setting it here ensures it is configured for any direct invocations too.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

Console.WriteLine("AgentEval.Compliance.Gdpr — run via `agenteval bench gdpr`.");
return 0;
