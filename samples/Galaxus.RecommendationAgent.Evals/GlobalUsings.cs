// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo
//
// Global usings for the Galaxus.RecommendationAgent.Evals project.
// Makes the demo project's Config/catalogue/domain types and the AgentEval harness types
// available project-wide without per-file using directives.
//
// AgentEval.Assertions and AgentEval.MAF stay PER-FILE, exactly as in
// AgentEval.TravelDemo.Evals: the assertion entry point is `result.ToolUsage!.Should()`, and
// keeping the using at the call site is what makes it obvious in a diff which files can throw
// an AgentEvalAssertionException.

global using AgentEval.Comparison;                          // StochasticRunner, StochasticOptions, IAgentFactory
global using AgentEval.Core;                                // EvaluationOptions, IEvaluableAgent, AgentResponse
global using AgentEval.Models;                              // TestCase, TestResult, ToolUsageReport, ToolCallRecord

global using Galaxus.RecommendationAgent;                   // Config, GalaxusDemoPrompts, ConsoleLogRecorder
global using Galaxus.RecommendationAgent.Catalog;           // Catalogue, UserProfiles, CustomerProfile, Personas
global using Galaxus.RecommendationAgent.Domain;            // Product, User, Purchase, PurchaseIntent, EvidenceRef

global using Galaxus.RecommendationAgent.Evals;             // EvalPrinter, EvalResultStore, GalaxusEvalPrompt
global using Galaxus.RecommendationAgent.Evals.Cases;       // IntegrityCase, IntegrityCases, CoveragePersona
global using Galaxus.RecommendationAgent.Evals.Controls;    // Broken01..Broken05, injection probes
global using Galaxus.RecommendationAgent.Evals.Graders;     // PresentedCall, CatalogueIntegrityGrader, …
global using Galaxus.RecommendationAgent.Evals.Loop;        // IDiscoveryLoopArm, QueryVocabulary, telemetry
