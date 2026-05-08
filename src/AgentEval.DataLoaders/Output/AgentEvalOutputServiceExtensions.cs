// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Output;

/// <summary>Extension methods for registering output store services.</summary>
public static class AgentEvalOutputServiceExtensions
{
    /// <summary>
    /// Registers <see cref="IOutputStore"/> in the service collection based on the provided options.
    /// </summary>
    public static IServiceCollection AddAgentEvalOutputStore(
        this IServiceCollection services,
        Action<OutputStoreOptions>? configure = null)
    {
        var opts = new OutputStoreOptions();
        configure?.Invoke(opts);

        services.AddSingleton(opts);
        services.AddSingleton<IOutputStore>(sp =>
        {
            var mode = opts.OutputStore;
            if (mode == OutputStoreMode.Null) return new NullOutputStore();

            var path = opts.OutputStorePath ?? ResolveDefaultPath();
            if (mode == OutputStoreMode.Auto && !File.Exists(Path.Combine(path, "solution.json")))
                return new NullOutputStore();

            return new FileSystemOutputStore(path);
        });

        return services;
    }

    /// <summary>
    /// Registers a <see cref="SubjectIdentity"/> built from the given builder action.
    /// </summary>
    public static IServiceCollection RegisterEvalSubject(
        this IServiceCollection services,
        Action<SubjectRegistrationBuilder> build)
    {
        var builder = new SubjectRegistrationBuilder();
        build(builder);
        services.AddSingleton(builder.Build());
        return services;
    }

    private static string ResolveDefaultPath()
    {
        var root = WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory())
                ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, ".agenteval");
    }
}

/// <summary>Fluent builder for constructing a <see cref="SubjectIdentity"/> registration.</summary>
public sealed class SubjectRegistrationBuilder
{
    private SubjectKind? _kind;
    private string? _name;
    private string? _version;
    private string? _sourceProject;
    private string? _sourcePath;
    private string? _modelId;
    private string? _framework;
    private List<string>? _tags;

    /// <summary>Identifies the subject as an agent of type <typeparamref name="T"/>.</summary>
    public SubjectRegistrationBuilder Agent<T>(string name)
    {
        _kind = SubjectKind.Agent;
        _name = name;
        _sourcePath = typeof(T).FullName;
        return this;
    }

    /// <summary>Identifies the subject as a workflow of type <typeparamref name="T"/>.</summary>
    public SubjectRegistrationBuilder Workflow<T>(string name)
    {
        _kind = SubjectKind.Workflow;
        _name = name;
        _sourcePath = typeof(T).FullName;
        return this;
    }

    /// <summary>Sets the version string for the subject.</summary>
    public SubjectRegistrationBuilder Version(string v) { _version = v; return this; }

    /// <summary>Sets the source project reference for the subject.</summary>
    public SubjectRegistrationBuilder SourceProject(string p) { _sourceProject = p; return this; }

    /// <summary>Sets the model identifier for the subject.</summary>
    public SubjectRegistrationBuilder ModelId(string id) { _modelId = id; return this; }

    /// <summary>Sets the framework label for the subject.</summary>
    public SubjectRegistrationBuilder Framework(string f) { _framework = f; return this; }

    /// <summary>Appends one or more tags to the subject registration.</summary>
    public SubjectRegistrationBuilder Tags(params string[] tags)
    {
        _tags ??= new List<string>();
        _tags.AddRange(tags);
        return this;
    }

    internal SubjectIdentity Build()
    {
        if (_kind is null || _name is null)
            throw new InvalidOperationException("Specify .Agent<T>(name) or .Workflow<T>(name) before Build().");
        return new SubjectIdentity(_kind.Value, _name, _sourceProject, _sourcePath, _version, _modelId, _framework, _tags);
    }
}
