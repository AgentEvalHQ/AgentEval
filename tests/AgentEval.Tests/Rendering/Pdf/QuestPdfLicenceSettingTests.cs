// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using AgentEval.Compliance.EuAiAct.Reporting.Pdf;
using AgentEval.Compliance.Gdpr.Reporting.Pdf;
using AgentEval.Evals.Agentic.Reporting.Pdf;
using AgentEval.Rendering.Pdf;
using QuestPDF.Infrastructure;
using Xunit;

namespace AgentEval.Tests.Rendering.Pdf;

/// <summary>
/// Tests in this collection change the process-wide <c>QuestPDF.Settings.License</c> for a moment
/// (to <c>null</c> or <c>Professional</c>), so they must not run while other tests render PDFs.
/// </summary>
[CollectionDefinition(QuestPdfLicenceCollection.Name, DisableParallelization = true)]
public sealed class QuestPdfLicenceCollection
{
    public const string Name = "QuestPdfLicence";
}

/// <summary>
/// The four QuestPDF renderers declare the Community licence in their static constructors. They must
/// do so only when no licence type is set yet (<c>QuestPDF.Settings.License ??= LicenseType.Community</c>):
/// <c>QuestPDF.Settings.License</c> is process-wide, and an unconditional assignment replaced a licence
/// the host application had already set, for example Professional. THIRD-PARTY-NOTICES.md describes this.
/// </summary>
[Collection(QuestPdfLicenceCollection.Name)]
public class QuestPdfLicenceSettingTests
{
    public static TheoryData<Type> Renderers => new()
    {
        typeof(PdfEvalResultRenderer),
        typeof(GDPRPdfRenderer),
        typeof(EuAiActPdfRenderer),
        typeof(AgenticPdfRenderer),
    };

    [Fact]
    public void PdfEvalResultRenderer_StaticConstructor_KeepsTheLicenceTheHostAlreadySet()
    {
        // Fails on the old unconditional assignment, which replaced Professional with Community.
        var saved = QuestPDF.Settings.License;
        try
        {
            QuestPDF.Settings.License = LicenseType.Professional;

            RunStaticConstructorInFreshLoadContext(typeof(PdfEvalResultRenderer));

            Assert.Equal(LicenseType.Professional, QuestPDF.Settings.License);
        }
        finally
        {
            QuestPDF.Settings.License = saved;
        }
    }

    [Fact]
    public void PdfEvalResultRenderer_StaticConstructor_SetsCommunity_WhenNoLicenceIsSet()
    {
        // Wiring check for the test above: proves the fresh static constructor really ran against the
        // same QuestPDF.Settings this test reads, so the Professional case cannot pass vacuously.
        var saved = QuestPDF.Settings.License;
        try
        {
            QuestPDF.Settings.License = null;

            RunStaticConstructorInFreshLoadContext(typeof(PdfEvalResultRenderer));

            Assert.Equal(LicenseType.Community, QuestPDF.Settings.License);
        }
        finally
        {
            QuestPDF.Settings.License = saved;
        }
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public void StaticConstructor_ReadsTheLicenceBeforeSettingIt(Type rendererType)
    {
        // The GDPR, EU AI Act and agentic renderers live in assemblies with [ModuleInitializer]
        // benchmark registrations, so they cannot be loaded a second time the way the tests above do.
        // Instead, check what each static constructor calls on QuestPDF.Settings: the old code only
        // called set_License; `??=` reads the current value first.
        var typeInitializer = rendererType.TypeInitializer;
        Assert.NotNull(typeInitializer);

        var settingsCalls = CalledMethods(typeInitializer)
            .Where(m => m.DeclaringType == typeof(QuestPDF.Settings))
            .Select(m => m.Name)
            .ToArray();

        Assert.Equal(new[] { "get_License", "set_License" }, settingsCalls);
    }

    /// <summary>
    /// Runs <paramref name="rendererType"/>'s static constructor again by loading a second copy of its
    /// assembly into a new <see cref="AssemblyLoadContext"/>. Only the renderer's own assembly is loaded
    /// there; its references, QuestPDF included, resolve to the default context, so the static constructor
    /// writes the same process-wide <c>QuestPDF.Settings</c> the test reads. Use this only for an assembly
    /// with no <c>[ModuleInitializer]</c> (AgentEval.Rendering.Pdf has none), because loading the assembly
    /// again would run its module initializers again.
    /// </summary>
    private static void RunStaticConstructorInFreshLoadContext(Type rendererType)
    {
        var context = new AssemblyLoadContext("questpdf-licence-test", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(rendererType.Assembly.Location);
            var freshType = assembly.GetType(rendererType.FullName!, throwOnError: true)!;
            Assert.NotSame(rendererType, freshType);

            RuntimeHelpers.RunClassConstructor(freshType.TypeHandle);
        }
        finally
        {
            context.Unload();
        }
    }

    private static readonly Dictionary<short, OpCode> s_opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(op => op.Value)
        .ToDictionary(g => g.Key, g => g.First());

    /// <summary>Methods and constructors called by <paramref name="method"/>'s IL, in order.</summary>
    private static List<MethodBase> CalledMethods(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);

        var called = new List<MethodBase>();
        var i = 0;
        while (i < il.Length)
        {
            OpCode op;
            if (il[i] == 0xFE)
            {
                op = s_opCodes[unchecked((short)(0xFE00 | il[i + 1]))];
                i += 2;
            }
            else
            {
                op = s_opCodes[il[i]];
                i += 1;
            }

            if (op.OperandType == OperandType.InlineMethod)
            {
                var resolved = method.Module.ResolveMethod(BitConverter.ToInt32(il, i));
                if (resolved is not null)
                    called.Add(resolved);
            }

            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };
        }

        return called;
    }
}
