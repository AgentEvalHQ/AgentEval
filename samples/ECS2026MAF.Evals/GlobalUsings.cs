// SPDX-License-Identifier: MIT
// Global usings for the ECS2026MAF.Evals project.
// Makes ECS2026MAF.Config, AgentEval and comparison types available project-wide
// without per-file using directives.

global using ECS2026MAF;                    // Config.IsConfigured, Config.Endpoint, etc.
global using ECS2026MAF.Evals;             // Eval01–05, EvalPrinter, EvalResultStore, etc.
global using AgentEval.Comparison;          // StochasticRunner, StochasticOptions, StochasticResult
global using AgentEval.Core;               // ChatClientEvaluator, EvaluationOptions
global using AgentEval.Models;             // TestCase, TestResult, WorkflowTestCase, etc.
