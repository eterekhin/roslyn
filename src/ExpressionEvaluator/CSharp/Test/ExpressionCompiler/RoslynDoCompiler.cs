using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ConsoleApp1;

public class RoslynDoCompiler
{
    private static CSharpCompilationOptions myCompilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        .WithOptimizationLevel(OptimizationLevel.Debug)
        .WithMetadataImportOptions(MetadataImportOptions.All)
        .WithReferencesSupersedeLowerVersions(true)
        .WithTopLevelBinderFlags(
            BinderFlags.SuppressObsoleteChecks |
            BinderFlags.UnsafeRegion |
            BinderFlags.UncheckedRegion |
            BinderFlags.IgnoreAccessibility |
            BinderFlags.SuppressObsoleteChecks |
            BinderFlags.AllowAwaitInUnsafeContext |
            BinderFlags.IgnoreCorLibraryDuplicatedTypes)
        .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);

    public static byte[] Compile()
    {
        var text = File.ReadAllText(@"C:\Users\win\Documents\roslyn\src\ExpressionEvaluator\CSharp\Test\ExpressionCompiler\DebuggerDisplayAttributeTests.cs");
        var tree = CSharpSyntaxTree.ParseText(text);

        var options2 = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: true);


        var references = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.Location).ToArray();
        var metadataReferences = references.Select(x => MetadataReference.CreateFromFile(x)).ToArray();

            var compilation = CSharpCompilation.Create("generated assembly", options: myCompilationOptions, references: metadataReferences).AddSyntaxTrees(tree);
        var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var diagnostics = result.Diagnostics;
        }

        ms.Seek(0, SeekOrigin.Begin);
        return ms.ToArray();
    }
}
