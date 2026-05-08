// SPDX-License-Identifier: MIT
// Global usings for the AgentEval.TravelDemo.Evals project.
// Makes AgentEval.TravelDemo.Config, AgentEval and comparison types available project-wide
// without per-file using directives.

global using AgentEval.TravelDemo;                    // Config.IsConfigured, Config.Endpoint, etc.
global using AgentEval.TravelDemo.Evals;             // Eval01–05, EvalPrinter, EvalResultStore, etc.
global using AgentEval.Comparison;          // StochasticRunner, StochasticOptions, StochasticResult
global using AgentEval.Core;               // ChatClientEvaluator, EvaluationOptions
global using AgentEval.Models;             // TestCase, TestResult, WorkflowTestCase, etc.
