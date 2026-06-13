// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace AgentEval.RedTeam;

/// <summary>
/// Default implementation of <see cref="IAttackTypeRegistry"/>.
/// Pre-populated with the built-in attacks from <see cref="Attack.All"/> (13 as of Wave D).
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton in DI via <c>services.AddAgentEval()</c>.
/// Extension packages can register additional attack types via DI
/// (<c>services.AddSingleton&lt;IAttackType, CustomAttack&gt;()</c>).
/// </para>
/// </remarks>
public sealed class AttackTypeRegistry : IAttackTypeRegistry
{
    private readonly ConcurrentDictionary<string, IAttackType> _attacks
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a registry pre-populated with all built-in attacks.
    /// </summary>
    public AttackTypeRegistry()
    {
        foreach (var attack in Attack.All)
        {
            _attacks.TryAdd(attack.Name, attack);
        }
    }

    /// <summary>
    /// Creates a registry pre-populated with built-in attacks
    /// plus additional DI-registered attacks.
    /// </summary>
    /// <param name="additionalAttacks">Extra attack types to register (from DI).</param>
    public AttackTypeRegistry(IEnumerable<IAttackType> additionalAttacks) : this()
    {
        ArgumentNullException.ThrowIfNull(additionalAttacks);

        foreach (var attack in additionalAttacks)
        {
            // DI-registered attacks can override built-ins
            _attacks[attack.Name] = attack;
        }
    }

    /// <inheritdoc/>
    public void Register(IAttackType attackType)
    {
        ArgumentNullException.ThrowIfNull(attackType);
        _attacks[attackType.Name] = attackType;
    }

    /// <inheritdoc/>
    public IAttackType? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _attacks.TryGetValue(name, out var attack) ? attack : null;
    }

    /// <inheritdoc/>
    public IAttackType GetRequired(string name)
    {
        var attack = Get(name);
        if (attack == null)
        {
            var available = string.Join(", ", _attacks.Keys.Take(10));
            throw new KeyNotFoundException(
                $"Attack type '{name}' is not registered. Available: {available}" +
                (_attacks.Count > 10 ? $" (and {_attacks.Count - 10} more)" : ""));
        }
        return attack;
    }

    /// <inheritdoc/>
    public IEnumerable<IAttackType> GetAll() => _attacks.Values.ToList();

    /// <inheritdoc/>
    public IEnumerable<IAttackType> GetByOwaspId(string owaspId)
    {
        if (string.IsNullOrWhiteSpace(owaspId))
            return [];

        return _attacks.Values
            .Where(a => string.Equals(a.OwaspLlmId, owaspId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc/>
    public bool Contains(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && _attacks.ContainsKey(name);
    }

    /// <summary>
    /// Gets the number of registered attack types.
    /// </summary>
    public int Count => _attacks.Count;
}
