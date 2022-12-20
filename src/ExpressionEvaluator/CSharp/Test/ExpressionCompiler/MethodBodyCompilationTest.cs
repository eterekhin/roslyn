// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using Microsoft.CodeAnalysis.CodeGen;
using Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator.UnitTests;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.ExpressionEvaluator;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.DiaSymReader;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator;

public class MethodBodyCompilationTest : ExpressionCompilerTestBase
{
    private const string emptyMethodBody = @"
            {
                   
            }";

    private const string source = @"
        public class MyClass 
        {{
            public void Test() {0}
        }}";
    
    private const string sourceInt = @"
        public class MyClass 
        {{
            public int Test() {0}
        }}";
    
    private const string sourceWithArgInt = @"
        public class MyClass 
        {{
            public int Test(int arg0) {0}
        }}";
    
    private const string sourceWithFieldInt = @"
        public class MyClass 
        {{
            public int field0;
            public int Test() {0}
        }}";

    private const string sourceWithFieldArgLocalInt = @"
        public class MyClass 
        {{
            public int field0;
            public int Test(int arg0) {0}
        }}";
    
    private const string typeWithPerson = @"
        public class Person {{ public string Name {{get; set;}} }}
        public class MyClass 
        {{
            public void Test() {0}
        }}";
    
    private const string typeWithPersonString = @"
        public class Person {{ public string Name {{get; set;}} }}
        public class MyClass 
        {{
            public string Test() {0}
        }}";

    public const string asyncContext = @"
    public class MyClass 
    {{
        public int field0;
        public async System.Threading.Tasks.Task<int> Test(int arg0) {0}
    }}";
    
    public const string asyncContext2 = @"
    public class MyClass 
    {{
        public int field0;
        public async System.Threading.Tasks.Task Test(int arg0) {0}
    }}";
    
    public const string enumerableGenerator = @"
    public class MyClass 
    {{
        public int field0;
        public System.Collections.Generic.IEnumerable<int> Test(int arg0) {0}
    }}";
    
    
    public const string staticField = @"
    public class Static {{ public static int field0; }} 
    public class MyClass 
    {{
        public int field0;
        public int Test(int arg0) {0}
    }}";

    public const string WithLambdaBoundParameter = @"
    public static class Tracker {{    public static void Track(){{ }} }}
    public class MyClass {{
        public void Test(int p) {0}
    }}
"; 
    
    public const string WithLambdaAndBoundMembers = @"
    public class MyClass
    {{
        private int f0 = 1;
        private static int sf0 = 1;
        public void Test(int a0) {0}
    }}
";
    
    public const string ClassContextUse = @"
";
    
    private CSharpCompileResult ExecuteTest(string source, string methodBody)
    {
        var replacedSource = string.Format(source, methodBody);
        var compilation0 = CreateCompilation(replacedSource, options: TestOptions.DebugDll);
        CSharpCompileResult result = null;
        WithRuntimeInstance(compilation0, runtime =>
        {
            ImmutableArray<MetadataBlock> blocks;
            Guid moduleVersionId;
            ISymUnmanagedReader symReader;
            int methodToken;
            int localSignatureToken;
            int methodVersion = 1;
            int ilOffset = -1;
            GetContextState(runtime, "MyClass.Test", out blocks, out moduleVersionId, out symReader, out methodToken, out localSignatureToken);
            compilation0 = blocks.ToCompilation(moduleVersionId, MakeAssemblyReferencesKind.AllAssemblies, optimizationLevel: OptimizationLevel.Debug);
            var methodHandle = (MethodDefinitionHandle)MetadataTokens.Handle(methodToken);
            var currentFrame = compilation0.GetMethod(moduleVersionId, methodHandle);
            RoslynDebug.AssertNotNull(currentFrame);
            var localSignatureHandle = (localSignatureToken != 0) ? (StandaloneSignatureHandle)MetadataTokens.Handle(localSignatureToken) : default;

            var symbolProvider = new CSharpEESymbolProvider(compilation0.SourceAssembly, (PEModuleSymbol)currentFrame.ContainingModule, currentFrame);

            var metadataDecoder = new MetadataDecoder((PEModuleSymbol)currentFrame.ContainingModule, currentFrame);
            var localInfo = metadataDecoder.GetLocalInfo(localSignatureHandle);
            var typedSymReader = (ISymUnmanagedReader3?)symReader;
            MethodDebugInfo<TypeSymbol, LocalSymbol> debugInfo = MethodDebugInfo<TypeSymbol, LocalSymbol>.ReadMethodDebugInfo(typedSymReader, symbolProvider, methodToken, methodVersion, ilOffset, isVisualBasicMethod: false);
            
            var evaluationContext = EvaluationContext.CreateMethodContext(compilation0, typedSymReader, moduleVersionId, methodToken, methodVersion, ilOffset, localSignatureToken); result = evaluationContext.CompileMethodBody(methodBody);
            // var blockSyntax = (BlockSyntax)((GlobalStatementSyntax)((CompilationUnitSyntax)SyntaxFactory.ParseSyntaxTree(methodBody).GetRoot()).Members[0]).Statement;
            // var d = new DiagnosticBag();
            // result = CompileExpression(compilation0, blockSyntax, currentFrame, debugInfo, d);
        });

        File.WriteAllBytes(@"C:\Users\user\Documents\a.dll", result.Assembly);
        return result;
    }
    
    [Fact]
    public void BaseTest()
    {
        ExecuteTest(source, emptyMethodBody);
    }
    
    
    [Fact]
    public void TestWithLocals()
    {
        ExecuteTest(source, @"{ var someLocal = 1; }");
    }
    
    
    [Fact]
    public void TestWithLocals2()
    {
        ExecuteTest(sourceInt, @"{ var someLocal = 1; return someLocal; }");
    }
    
    [Fact]
    public void TestWithArg()
    {
        ExecuteTest(sourceWithArgInt, @"{ return arg0; }");
    }
    
    
    [Fact]
    public void TestAccessField()
    {
        ExecuteTest(sourceWithFieldInt, @"{ return field0; }");
    }
    
    [Fact]
    public void TestAccessFieldArgLocal()
    {
        ExecuteTest(sourceWithFieldArgLocalInt, @"{ int local0 = 1; return local0 + field0 + arg0; }");
    }
    
    
    [Fact]
    public void TestWithLocalMethod()
    {
        ExecuteTest(source, @"{ int SomeLocalMethod() { return 1; }; SomeLocalMethod(); }");
    }
    
    [Fact]
    public void TestWithLocalMethodClosure()
    {
        ExecuteTest(sourceWithFieldArgLocalInt, @"{ int SomeLocalMethod() { return arg0; } return SomeLocalMethod(); }");
    }
    
    [Fact]
    public void TestWithLocalMethodClosure2()
    {
        ExecuteTest(sourceWithFieldArgLocalInt, @"{ int local0 = 0; int SomeLocalMethod() { return arg0 + field0 + local0; } return SomeLocalMethod(); }");
    }
    
    [Fact]
    public void TestWithLambdaClosure()
    {
        ExecuteTest(sourceWithFieldArgLocalInt, @"{ int local0 = 0; System.Func<int> func = () => { return field0 + arg0 + local0; }; return func(); }");
    }
    
    [Fact]
    public void TestLambdaAndLocalFunctionClosure()
    {
        ExecuteTest(sourceWithFieldArgLocalInt, @"{ int local0 = 0; int SomeLocalMethod() { return arg0 + field0 + local0; } System.Func<int> func = () => { return field0 + arg0 + local0; }; return func() + SomeLocalMethod(); }");
    }

    [Fact]
    public void TestPatternMatching()
    {
        ExecuteTest(typeWithPerson, @"{ var person = new Person () { Name = ""My name"" }; if (person is { Name : ""My name"" } p) { System.Console.WriteLine(p.Name); } }");
    }
    
    [Fact]
    public void TestPatternMatching2()
    {
        ExecuteTest(typeWithPersonString, @"{ var person = new Person () { Name = ""My name"" }; if (person is { Name : ""My name"" } p) { System.Console.WriteLine(p.Name); }  return person.Name; }");
    }

    [Fact]
    public void TestAsyncMethodWithClosureWithScopes()
    {
        ExecuteTest(asyncContext, @"{ 
                int local0;
                { int local1 = 1; await System.Threading.Tasks.Task.Delay(1000); local0 = local1; }
                { int local1 = 2; await System.Threading.Tasks.Task.Delay(1000); local0 = local1;}
                return local0 + field0 + arg0;  }");
    }
    
    [Fact]
    public void TestAsyncTask()
    {
        ExecuteTest(asyncContext2, @"{ 
        int local0 = 0;
        int LocalFunction(int localArg0) { int local1 = 0; local0++; local1++; field0++; var f = () => local0 + local1 + localArg0 + field0; return f(); }
        System.Action closure = () => { int local1 = 0; local0++; local1++; field0++; };
        var s = LocalFunction(2);
        await System.Threading.Tasks.Task.CompletedTask;
        var s1 = LocalFunction(3);
        await System.Threading.Tasks.Task.CompletedTask;
        System.Console.WriteLine(s + s1);
        closure.Invoke();
 }");
    }

    [Fact]
    public void StaticFieldAccess()
    {
        ExecuteTest(staticField, @"{ 
            return Static.field0 + field0;
 }");
    }
    
    [Fact]
    public void TestAsyncMethod()
    {
        ExecuteTest(asyncContext, @"{ await System.Threading.Tasks.Task.Delay(1000); return 1;  }");
    }
    
    [Fact]
    public void TestAsyncMethodWithClosure()
    {
        ExecuteTest(asyncContext, @"{ int local0 = 1; await System.Threading.Tasks.Task.Delay(1000); return local0 + field0 + arg0;  }");
    }
    
    [Fact]
    public void TestEnumerableGenerator()
    {
        ExecuteTest(enumerableGenerator, @"{
            int local0 = 1;
            yield return local0;
            yield return field0;
            yield return arg0;
 }");
    }
    
    [Fact]
    public void TestEnumerableGeneratorWithLambdaAndLocalFunction()
    {
        ExecuteTest(enumerableGenerator, @"{
              int local0 = 1;
              yield return field0;
              yield return 1;
              yield return arg0;
              int LocalFunction() { local0++; arg0++; return field0++; }
              var lambda = () => { local0++; arg0++; return field0++; };
              lambda.Invoke();
              LocalFunction();
              yield return local0;
              yield return arg0;
              yield return field0;
 }");
    }

    [Fact]
    public void TestWithSeveralClosures()
    {
        ExecuteTest(WithLambdaBoundParameter, @"{
            Tracker.Track();
            new System.Action(() => System.Console.WriteLine(p)).Invoke();
        }");
    }

    [Fact]
    public void TestWithSeveralClosures2()
    {
        ExecuteTest(WithLambdaAndBoundMembers, @"{
             _ = ""0""; 
            int l0 = 1;
            int l1 = new System.Func<int>(() => l0 + a0 + f0 + sf0).Invoke();
            _ = ""0""; 
            int l2 = new System.Func<int>(() => l1 + l0 + a0 + f0 + sf0).Invoke();
            _ = ""0""; 
            int LocalFunction() => l2 + l1 + l0 + a0 + f0 + sf0;
            _ = ""0""; 
            int l3 = LocalFunction();
            int l4 = 0;
            if (l3 == 11)
            {
                l4 = 1;
            }
            else
            {
                l4 = 0;
            }
        }");
    }

    [Fact]
    public void MethodWithContextUse()
    {
        ExecuteTest(WithLambdaAndBoundMembers, @"{
             _ = ""0""; 
            int l0 = 1;
            int l1 = new System.Func<int>(() => l0 + a0 + f0 + sf0).Invoke();
            _ = ""0""; 
            int l2 = new System.Func<int>(() => l1 + l0 + a0 + f0 + sf0).Invoke();
            _ = ""0""; 
            int LocalFunction() => l2 + l1 + l0 + a0 + f0 + sf0;
            _ = ""0""; 
            int l3 = LocalFunction();
            int l4 = 0;
            if (l3 == 11)
            {
                l4 = 1;
            }
            else
            {
                l4 = 0;
            }
        }");
    }
   
    private const string TypeName = "<>x";
    private const string MethodName = "<>m0";
    private static CSharpCompileResult CompileExpression(CSharpCompilation Compilation, BlockSyntax methodBody, PEMethodSymbol currentMethod, MethodDebugInfo<TypeSymbol, LocalSymbol> methodDebugInfo, DiagnosticBag diagnostics)
    {
        var namespaceBinder = CompilationContext.CreateBinderChain(
            Compilation,
            currentMethod.ContainingNamespace,
            methodDebugInfo.ImportRecordGroups,
            methodDebugInfo.ContainingDocumentName is { } documentName ? FileIdentifier.Create(documentName) : null);
        
        var objectType = Compilation.GetSpecialType(SpecialType.System_Object);
        
        var synthesizedType = new EENamedTypeSymbol(
            Compilation.SourceModule.GlobalNamespace,
            objectType,
            // methodBody,
            currentMethod,
            TypeName,
            // MethodName,
            (symbol, container) =>
            {
                GenerateMethodBody generateMethodBody = delegate(EEMethodSymbol method, DiagnosticBag diagnostics, out ImmutableArray<LocalSymbol> locals, out ResultProperties properties)
                {
                    locals = ImmutableArray<LocalSymbol>.Empty;
                    properties = default;
                    var binder = ExtendBinderChain2(
                        methodBody,
                        method,
                        namespaceBinder,
                        out var declaredLocals);

                    var result = binder.BindEmbeddedBlock(methodBody, diagnostics);
                    if (result.HasErrors)
                        throw new InvalidOperationException("Has errors");
                    return result;
                };
              
                var method =  new EEMethodSymbol(
                    container,
                    MethodName,
                    methodBody.Location,
                    currentMethod,
                    ImmutableArray<LocalSymbol>.Empty, 
                    ImmutableArray<LocalSymbol>.Empty, 
                    ImmutableDictionary<string, DisplayClassVariable>.Empty, 
                    generateMethodBody, true);
                
                return ImmutableArray.Create<MethodSymbol>(method);
            }
          );

        var testData = new CompilationTestData();
        var moduleBuilder = CompilationContext.CreateModuleBuilder(
            Compilation,
            additionalTypes: ImmutableArray.Create((NamedTypeSymbol)synthesizedType),
            testData,
            diagnostics);
        
        Compilation.Compile(
            moduleBuilder,
            emittingPdb: false,
            diagnostics,
            filterOpt: null,
            CancellationToken.None);

        if (diagnostics.HasAnyErrors())
        {
            throw new Exception("Exception after compilation");
        }

        using var stream = new MemoryStream();
        var synthesizedMethod = CompilationContext.GetSynthesizedMethod(synthesizedType);

        Cci.PeWriter.WritePeToStream(
            new EmitContext(moduleBuilder, null, diagnostics, metadataOnly: false, includePrivateMembers: true),
            Compilation.MessageProvider,
            () => stream,
            getPortablePdbStreamOpt: null,
            nativePdbWriterOpt: null,
            pdbPathOpt: null,
            metadataOnly: false,
            isDeterministic: false,
            emitTestCoverageData: false,
            privateKeyOpt: null,
            CancellationToken.None);

        if (diagnostics.HasAnyErrors())
        {
            return null;
        }

        Debug.Assert(synthesizedMethod.ContainingType.MetadataName == TypeName);
        Debug.Assert(synthesizedMethod.MetadataName == MethodName);

        return new CSharpCompileResult(
            stream.ToArray(),
            synthesizedMethod,
            formatSpecifiers: new ReadOnlyCollection<string>(new List<string>()));
    }
    
    private static Binder ExtendBinderChain2(
            CSharpSyntaxNode syntax,
            // ImmutableArray<Alias> aliases,
            EEMethodSymbol method,
            Binder binder,
            // bool hasDisplayClassThis,
            // bool methodNotType,
            out ImmutableArray<LocalSymbol> declaredLocals)
        {
            var substitutedSourceMethod = CompilationContext.GetSubstitutedSourceMethod(method.SubstitutedSourceMethod, false);
            var substitutedSourceType = substitutedSourceMethod.ContainingType;

            var stack = ArrayBuilder<NamedTypeSymbol>.GetInstance();
            for (var type = substitutedSourceType; type is object; type = type.ContainingType)
            {
                stack.Add(type);
            }

            while (stack.Count > 0)
            {
                substitutedSourceType = stack.Pop();

                binder = new InContainerBinder(substitutedSourceType, binder);
                if (substitutedSourceType.Arity > 0)
                {
                    binder = new WithTypeArgumentsBinder(substitutedSourceType.TypeArgumentsWithAnnotationsNoUseSiteDiagnostics, binder);
                }
            }

            stack.Free();

            if (substitutedSourceMethod.Arity > 0)
            {
                binder = new WithTypeArgumentsBinder(substitutedSourceMethod.TypeArgumentsWithAnnotations, binder);
            }

            // // Method locals and parameters shadow pseudo-variables.
            // // That is why we place PlaceholderLocalBinder and ExecutableCodeBinder before EEMethodBinder.
            // if (methodNotType)
            // {
            //     var typeNameDecoder = new EETypeNameDecoder(binder.Compilation, (PEModuleSymbol)substitutedSourceMethod.ContainingModule);
            //     binder = new PlaceholderLocalBinder(
            //         syntax,
            //         aliases,
            //         method,
            //         typeNameDecoder,
            //         binder);
            // }

            binder = new EEMethodBinder(method, substitutedSourceMethod, binder, true);

            // if (methodNotType)
            // {
            //     binder = new SimpleLocalScopeBinder(method.LocalsForBinding, binder);
            // }

            Binder? actualRootBinder = null;
            SyntaxNode? declaredLocalsScopeDesignator = null;

            var executableBinder = new ExecutableCodeBinder(syntax, substitutedSourceMethod, binder,
                (rootBinder, declaredLocalsScopeDesignatorOpt) =>
                {
                    actualRootBinder = rootBinder;
                    declaredLocalsScopeDesignator = declaredLocalsScopeDesignatorOpt;
                });

            // We just need to trigger the process of building the binder map
            // so that the lambda above was executed.
            executableBinder.GetBinder(syntax);

            RoslynDebug.AssertNotNull(actualRootBinder);

            if (declaredLocalsScopeDesignator != null)
            {
                declaredLocals = actualRootBinder.GetDeclaredLocalsForScope(declaredLocalsScopeDesignator);
            }
            else
            {
                declaredLocals = ImmutableArray<LocalSymbol>.Empty;
            }

            return actualRootBinder;
        }
}
