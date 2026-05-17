// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles.Building;

namespace AgentEval.Compliance.Gdpr.DomainPacks.ChildrensService;

/// <summary>
/// Loads the children's-service-domain scenario extensions from embedded YAML resources
/// and groups them by <c>control_id</c> for use with
/// <see cref="Composition.CompositeExtensions.WithExtraScenarios"/>.
/// </summary>
public static class ChildrensServiceScenarios
{
    /// <summary>
    /// Loads the children's-service-domain scenario extensions and groups them by control_id.
    /// </summary>
    /// <param name="scenarioBuilder">
    /// The <see cref="ScenarioToAtomicEval"/> used to convert each scenario spec into an
    /// <see cref="IEval"/>.
    /// </param>
    /// <returns>
    /// A dictionary mapping GDPR control IDs to the list of extra
    /// <see cref="EvalComponent"/> instances to append.
    /// </returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<EvalComponent>> Load(
        ScenarioToAtomicEval scenarioBuilder)
    {
        ArgumentNullException.ThrowIfNull(scenarioBuilder);
        return DomainPackLoader.Load(
            typeof(ChildrensServiceScenarios).Assembly,
            resourceFilter: n => n.Contains(".DomainPacks.ChildrensService.Yaml.", StringComparison.Ordinal),
            scenarioBuilder);
    }
}
