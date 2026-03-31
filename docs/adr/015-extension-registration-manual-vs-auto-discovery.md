# ADR-015: Extension Registration — Manual vs. Auto-Discovery

**Status:** Accepted  
**Date:** 2026-02-25  
**Decision Makers:** AgentEval Contributors  
**Related:** [ADR-006](006-service-based-architecture-di.md), [Extensibility Guide](../extensibility.md)

---

## Context

AgentEval's extensibility strategy (see strategy doc) proposes that third-party NuGet packages should be able to add metrics, exporters, dataset loaders, red team attacks, and plugins to AgentEval. The core design question is:

> **How should extension packages register their services with the AgentEval host?**

Two schools of thought compete:

| Approach | Mechanism | Example Frameworks |
|---|---|---|
| **Manual Registration** | Consumer explicitly calls `services.AddFoo()` or `builder.AddFoo()` | Entity Framework, MediatR, Serilog, FluentValidation |
| **Auto-Discovery** | Host scans assembly attributes at startup and invokes registration automatically | MEF, ASP.NET Razor Pages, NServiceBus, some plugin hosts |

This ADR evaluates both approaches and decides which AgentEval should adopt as primary.

---

## The Battle

### 🥊 Round 1: Developer Experience

#### Manual Registration — "Explicit is better than implicit"

```csharp
services.AddAgentEval();
services.AddAgentEvalHealthcareMetrics(o => o.StrictMode = true);
services.AddAgentEvalPowerBIExporter();
```

**Pros:**
- Developer sees exactly what's loaded in `Program.cs` — one line per extension
- IntelliSense discoverability: type `services.AddAgentEval` and see all available extensions
- Configuration is co-located with registration — natural parameter passing
- Familiar to every .NET developer (ASP.NET, EF, MediatR all use this pattern)

**Cons:**
- Each extension requires a line of code — boilerplate for large extension sets
- Developer must know the extension method name
- Can't activate extensions via config file or environment variable

#### Auto-Discovery — "Just add the NuGet"

```csharp
services.AddAgentEval(o => o.DiscoverExtensions = true);
// That's it — all referenced AgentEval.* packages auto-register
```

**Pros:**
- Zero-code activation — add NuGet reference, done
- CI/CD friendly — swap extensions by changing `.csproj` PackageReferences
- Lower barrier for non-developers (config-driven scenarios)

**Cons:**
- "Spooky action at a distance" — behaviour changes by adding a package reference
- Hard to debug: "why is this metric running?" requires checking all loaded assemblies
- Assembly scanning at startup adds latency and complexity
- Passing configuration to discovered extensions is awkward (requires convention)
- Conflicts: two extensions registering the same metric name — who wins?

**Verdict:** Manual Registration wins on transparency and debuggability. Auto-Discovery wins on convenience for simple cases.

---

### 🥊 Round 2: Configuration & Parameterization

#### Manual Registration

```csharp
services.AddAgentEvalHealthcareMetrics(options =>
{
    options.StrictMode = true;
    options.ComplianceStandard = "HIPAA";
    options.LicenseKey = config["Healthcare:LicenseKey"];
});
```

- ✅ Strongly-typed options with IntelliSense
- ✅ Works with `IConfiguration` binding (`config.GetSection("Healthcare").Bind(options)`)
- ✅ Validation at registration time — fail fast

#### Auto-Discovery

```csharp
services.AddAgentEval(o =>
{
    o.DiscoverExtensions = true;
    o.Configuration = config; // Pass IConfiguration for extensions to pull from
});
```

Extensions must fish their config out of a generic `IConfiguration`:

```csharp
public void ConfigureServices(IServiceCollection services, IConfiguration? config)
{
    var section = config?.GetSection("AgentEval:Healthcare");
    var strict = section?.GetValue<bool>("StrictMode") ?? false;
    // No compile-time safety, no IntelliSense
}
```

- ⚠️ Loosely-typed — typos in config keys cause silent failures
- ⚠️ No IntelliSense for config shape
- ⚠️ Extension must document its expected config keys

**Verdict:** Manual Registration wins decisively. Strongly-typed options with IntelliSense is objectively better DX.

---

### 🥊 Round 3: Debuggability & Diagnostics

#### Manual Registration

- Stack trace goes directly from `Program.cs` → extension's `AddFoo()` → DI registration
- Breakpoint on the extension method shows exactly what's being registered
- If an extension isn't loaded, the answer is obvious: its `Add*()` call is missing

#### Auto-Discovery

- Stack trace goes through reflection: `AddAgentEval()` → assembly scan → `Activator.CreateInstance()` → `ConfigureServices()`
- Need to log all discovered extensions and their versions (we proposed `ExtensionDiscoveryResult`)
- If an extension isn't loaded: is it because the assembly wasn't found? The attribute is missing? The version check failed? The assembly name doesn't match the `AgentEval*` convention?
- Debugging requires knowing the scan algorithm, name conventions, and version constraints

**Verdict:** Manual Registration wins. Assembly scanning introduces a layer of indirection that complicates debugging.

---

### 🥊 Round 4: Safety & Predictability

#### Manual Registration

- ✅ No surprise services — you control exactly what enters the DI container
- ✅ No accidental activation — a stale package reference can't inject behaviour
- ✅ Order of registration is explicit and deterministic
- ✅ Version conflicts are visible at compile time (NuGet restore errors)

#### Auto-Discovery

- ⚠️ Adding a transitive dependency could pull in an AgentEval extension you didn't expect
- ⚠️ Removing a package reference silently removes functionality — no compiler warning
- ⚠️ Two extensions registering the same metric name: last-wins? first-wins? error?
- ⚠️ Assembly scanning in `net8.0` AOT/trimmed apps may not work (reflection-unfriendly)

**Verdict:** Manual Registration wins on safety. Auto-Discovery introduces non-obvious side effects.

---

### 🥊 Round 5: Ecosystem Growth & Adoption

#### Manual Registration

- Each extension needs to document its `Add*()` method and configuration options
- Discoverability via NuGet package search + README is the standard .NET pattern
- Extension packages can provide both `IServiceCollection` and `AgentEvalBuilder` extensions

#### Auto-Discovery

- Lower friction for first-time use — "just add the NuGet" is a powerful pitch
- Better for demo/prototype scenarios where you want everything loaded
- Better for CLI tools where there's no `Program.cs` to modify

**Verdict:** Auto-Discovery has a slight edge for zero-config scenarios. But for production use, Manual Registration's explicitness is preferred.

---

### 🥊 Round 6: .NET Ecosystem Precedent

| Framework | Approach | Notes |
|---|---|---|
| **ASP.NET Core** | Manual (`services.AddControllers()`, `app.UseRouting()`) | Explicit pipeline, gold standard |
| **Entity Framework** | Manual (`services.AddDbContext<T>()`) | Strongly-typed options |
| **MediatR** | Manual (`services.AddMediatR()`) with optional assembly scanning *inside* the call | Scanning opt-in, scoped to declared assemblies |
| **Serilog** | Manual (`Log.Logger = new LoggerConfiguration()...`) | Builder pattern |
| **FluentValidation** | Manual (`services.AddValidatorsFromAssembly()`) | Scanning opt-in |
| **NServiceBus** | Auto-Discovery of message handlers | Full plugin host; different domain |
| **MEF (System.Composition)** | Auto-Discovery via `[Export]`/`[Import]` | Older .NET pattern; not recommended for modern .NET |
| **Azure Functions** | Auto-Discovery of functions via attributes | Different execution model (serverless) |
| **xUnit/NUnit** | Auto-Discovery of test classes | Test runners scan by design |

**Pattern:** The modern .NET ecosystem overwhelmingly uses **manual registration with opt-in scanning**. Pure auto-discovery (MEF-style) is considered an anti-pattern in modern .NET DI.

**Verdict:** Manual Registration aligns with .NET ecosystem conventions.

---

## Scorecard

| Criterion | Manual Registration | Auto-Discovery |
|---|---|---|
| Developer Experience | ⭐⭐⭐⭐ | ⭐⭐⭐ |
| Configuration | ⭐⭐⭐⭐⭐ | ⭐⭐ |
| Debuggability | ⭐⭐⭐⭐⭐ | ⭐⭐ |
| Safety & Predictability | ⭐⭐⭐⭐⭐ | ⭐⭐ |
| Ecosystem Growth | ⭐⭐⭐ | ⭐⭐⭐⭐ |
| .NET Convention Alignment | ⭐⭐⭐⭐⭐ | ⭐⭐ |
| Zero-Config Experience | ⭐⭐ | ⭐⭐⭐⭐⭐ |
| AOT/Trimming Compatibility | ⭐⭐⭐⭐⭐ | ⭐ |
| **Total** | **34/40** | **21/40** |

---

## Decision

### ✅ Manual Registration is the PRIMARY mechanism

AgentEval will use **explicit `IServiceCollection` extension methods** as the primary and recommended registration pattern for extensions.

Every extension package MUST provide:
```csharp
public static class MyExtensionServiceCollectionExtensions
{
    public static IServiceCollection AddAgentEval{Name}(
        this IServiceCollection services,
        Action<{Name}Options>? configure = null)
    {
        // Register metrics, exporters, loaders, etc.
    }
}
```

And every extension package SHOULD also provide a builder extension:
```csharp
public static class MyExtensionBuilderExtensions
{
    public static AgentEvalBuilder Add{Name}(
        this AgentEvalBuilder builder,
        Action<{Name}Options>? configure = null)
    {
        // Register via builder API
    }
}
```

### ⚠️ Auto-Discovery is an OPTIONAL convenience layer

Auto-Discovery will be available but:
1. **Off by default** — `DiscoverExtensions = false`
2. **Opt-in only** — consumer must explicitly enable: `services.AddAgentEval(o => o.DiscoverExtensions = true)`
3. **Uses the same interfaces** — discovered extensions call the same `IServiceCollection` registration methods
4. **Not required** — extension packages MUST NOT depend on auto-discovery as their only registration path
5. **Logged** — all discovered extensions are logged at startup with name, version, and source assembly

### ❌ Auto-Discovery is NOT the recommended approach

Documentation, templates, and samples will default to manual registration. Auto-Discovery will be documented as an advanced/convenience feature.

### Naming Convention

Extension packages MUST follow `AgentEval.{Category}.{Name}` for auto-discovery to find them (assembly name filter). This is enforced by convention, not by code.

Categories: `Metrics`, `Exporters`, `DataLoaders`, `Plugins`, `Adapters`, `RedTeam`

---

## Consequences

### Positive

1. **Predictable** — developers know exactly what's loaded
2. **Debuggable** — clear stack traces, no reflection surprises
3. **Configurable** — strongly-typed options with IntelliSense
4. **Convention-aligned** — follows ASP.NET Core, EF, MediatR patterns
5. **AOT-friendly** — no assembly scanning by default
6. **Auto-Discovery still available** — zero-config convenience for demos and CLI

### Negative

1. **Slightly more boilerplate** — one `services.AddAgentEval{X}()` per extension
2. **Extension authors** must implement `IServiceCollection` extension methods (but templates help)
3. **No "just add NuGet" magic** by default — requires a code change

### Mitigations

- **`dotnet new agenteval-metric` template** scaffolds the extension method automatically
- **Documentation** will show copy-pasteable registration patterns
- **Auto-Discovery** remains available as opt-in for scenarios that benefit

---

## Implementation Impact

This decision affects the extensibility strategy (Phases 1–5) as follows:

| Phase | Impact |
|---|---|
| **Phase 1** (DI Foundation) | Unchanged — `TryAdd*` registrations proceed as planned |
| **Phase 2** (Registries) | Unchanged — registries are DI-populated regardless of discovery method |
| **Phase 3** (Auto-Discovery) | **Reduced priority** — becomes optional convenience, not critical path. Still implemented but docs emphasize manual registration. `DiscoverExtensions` defaults to `false`. |
| **Phase 4** (Documentation) | **Updated** — docs lead with manual registration; auto-discovery documented in an "Advanced" section |
| **Phase 5** (Templates) | **Enhanced** — templates generate `AddAgentEval{Name}()` extension method as first-class citizen |
| **Phase 6** (Future) | Config-file-based loading (6.1) deferred further; it primarily benefits auto-discovery scenarios |

---

## Alternatives Considered

### Alternative 1: Auto-Discovery Only (MEF-style)

- ❌ Against .NET conventions
- ❌ Debugging nightmare
- ❌ AOT-incompatible
- **Rejected**

### Alternative 2: Manual Registration Only (no scanning)

- ✅ Simplest, most predictable
- ❌ Loses the "just add NuGet" convenience for demos
- ❌ CLI/CI-first scenarios suffer
- **Rejected** — auto-discovery has value as opt-in

### Alternative 3: Manual Registration Primary + Auto-Discovery Opt-In (CHOSEN)

- ✅ Best of both worlds
- ✅ Follows MediatR/FluentValidation pattern
- ✅ Default is safe and predictable
- ✅ Opt-in scanning for advanced scenarios
- **Accepted**

---

## References

- [ASP.NET Core Dependency Injection](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection)
- [MediatR Registration](https://github.com/jbogard/MediatR/wiki#aspnet-core) — `services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))`
- [FluentValidation DI](https://docs.fluentvalidation.net/en/latest/di.html) — `services.AddValidatorsFromAssemblyContaining<T>()`
- [MEF Deprecation Guidance](https://learn.microsoft.com/en-us/dotnet/framework/mef/) — "Consider Microsoft.Extensions.DependencyInjection for new projects"
- [.NET AOT Compatibility](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) — reflection-based scanning is incompatible

---

## Review

**Reviewed by:** Strategy Review  
**Date:** 2026-02-25  
**Verdict:** Accepted — Manual Registration as primary aligns with .NET ecosystem conventions, provides the best developer experience, and maintains AgentEval's SOLID/CLEAN architecture principles. Auto-Discovery as opt-in preserves convenience without sacrificing safety.
