// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net.Http.Json;
using System.Text.Json;
using AgentEval.MissionControl.GraphQL;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Phase-5 Task 5.2 — pins the contract that <c>SubjectIdentity.QualifiedId</c>
/// is NOT bound as a GraphQL field. Pass-2 hid it from JSON via
/// <c>[JsonIgnore]</c>, but Hot Chocolate's default convention auto-binds
/// public properties — making <c>{ subjects { identity { qualifiedId } } }</c>
/// work and locking the field into the v1 GraphQL contract. The property is
/// now <c>internal</c>, which the default Hot Chocolate convention does not
/// discover. This introspection-level test catches any future regression where
/// somebody (a) makes the property public again, or (b) adds a Hot Chocolate
/// convention that surfaces internal members.
/// </summary>
public class SubjectIdentityGraphQLIntrospectionTests
    : IClassFixture<WebApplicationFactory<Query>>
{
    private readonly WebApplicationFactory<Query> _factory;

    public SubjectIdentityGraphQLIntrospectionTests(WebApplicationFactory<Query> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SubjectIdentity_GraphQL_DoesNotExposeQualifiedId()
    {
        using var client = _factory.CreateClient();
        var request = new
        {
            query = "{ __type(name: \"SubjectIdentity\") { fields { name } } }"
        };

        var response = await client.PostAsJsonAsync("/graphql", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        // Two flavours to catch — Hot Chocolate lowercases the property name
        // when binding (PascalCase → camelCase). Defence-in-depth: reject either.
        Assert.DoesNotContain("\"name\":\"qualifiedId\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"name\":\"QualifiedId\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubjectIdentity_GraphQL_AliasedIntrospection_DoesNotLeakField()
    {
        // Defence-in-depth: probe SubjectIdentity through TWO aliased introspection
        // paths in one request. A future Hot Chocolate convention change that re-binds
        // internal properties (or someone making the property public again) would
        // surface `qualifiedId` in both branches — we structurally walk every field
        // name in both branches and reject any case-insensitive match.
        using var client = _factory.CreateClient();
        var request = new
        {
            query = "{ __type(name: \"SubjectIdentity\") { fields { name } } "
                  + "validateQualifiedId: __type(name: \"SubjectIdentity\") { fields { name } } }"
        };

        var response = await client.PostAsJsonAsync("/graphql", request);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        foreach (var prop in new[] { "__type", "validateQualifiedId" })
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
            if (!data.TryGetProperty(prop, out var type)) continue;
            if (type.ValueKind == JsonValueKind.Null) continue;
            if (!type.TryGetProperty("fields", out var fields)) continue;
            foreach (var field in fields.EnumerateArray())
            {
                var fieldName = field.GetProperty("name").GetString();
                Assert.False(
                    string.Equals(fieldName, "qualifiedId", StringComparison.OrdinalIgnoreCase),
                    $"GraphQL surface unexpectedly exposes a `{fieldName}` field on SubjectIdentity. " +
                    "Phase-5 Task 5.2 made this property internal — investigate the regression.");
            }
        }
    }
}

#endif
