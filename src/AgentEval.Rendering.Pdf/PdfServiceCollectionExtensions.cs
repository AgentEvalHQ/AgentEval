// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentEval.Rendering.Pdf;

/// <summary>Explicit PDF provider registration; registration does not render or initialize an engine.</summary>
public static class PdfServiceCollectionExtensions
{
    /// <summary>Registers one named provider. Repeated identical registrations are harmless; conflicts are rejected.</summary>
    public static IServiceCollection AddAgentEvalPdfProvider<T>(this IServiceCollection services, string providerId)
        where T : class, IPdfReportRenderer
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var previous = services.Where(d => d.ServiceType == typeof(PdfProviderRegistration))
            .Select(d => (PdfProviderRegistration)d.ImplementationInstance!)
            .FirstOrDefault(r => StringComparer.OrdinalIgnoreCase.Equals(r.Id, providerId));
        if (previous is not null)
        {
            if (previous.Type == typeof(T)) return services;
            throw new InvalidOperationException($"Expected: a unique PDF provider ID. Actual: '{providerId}' is already registered as {previous.Type.Name}. Suggestions: use a different ID. Because: registration order must not select an engine silently.");
        }
        services.AddSingleton(new PdfProviderRegistration(providerId, typeof(T)));
        services.TryAddTransient<T>();
        services.TryAddScoped<IPdfRendererRegistry>(sp => new PdfRendererRegistry(
            sp.GetServices<PdfProviderRegistration>().ToArray(), sp));
        return services;
    }

    /// <summary>Creates a generic result adapter for the selected registered provider.</summary>
    public static Evals.IEvalResultRenderer ForEvalResults(this IPdfRendererRegistry registry, string? providerId = null)
        => new PdfEvalResultRenderer(registry, providerId);

}

internal sealed record PdfProviderRegistration(string Id, Type Type);

internal sealed class PdfRendererRegistry(PdfProviderRegistration[] registrations, IServiceProvider services) : IPdfRendererRegistry
{
    public IReadOnlyList<string> ProviderIds { get; } = Array.AsReadOnly(registrations.Select(r => r.Id).Order(StringComparer.Ordinal).ToArray());

    public IPdfReportRenderer Get(string? providerId = null)
    {
        PdfProviderRegistration? registration;
        if (providerId is null)
        {
            if (registrations.Length != 1)
                throw new InvalidOperationException($"Expected: one PDF provider or an explicit provider ID. Actual: {registrations.Length} providers ({string.Join(", ", ProviderIds)}). Suggestions: select a registered provider ID. Because: PDF has no implicit engine preference.");
            registration = registrations[0];
        }
        else
            registration = registrations.SingleOrDefault(r => StringComparer.OrdinalIgnoreCase.Equals(r.Id, providerId));
        if (registration is null)
            throw new InvalidOperationException($"Expected: registered PDF provider '{providerId}'. Actual: available providers are [{string.Join(", ", ProviderIds)}]. Suggestions: install and register the requested provider. Because: PDF providers are opt-in.");
        return (IPdfReportRenderer)services.GetRequiredService(registration.Type);
    }
}
