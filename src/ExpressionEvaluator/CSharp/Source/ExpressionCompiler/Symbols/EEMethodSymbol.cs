// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CodeGen;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ExpressionEvaluator;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator
{
    internal delegate BoundExpression GenerateThisReference(SyntaxNode syntax);

    internal delegate BoundStatement GenerateMethodBody(
        EEMethodSymbol method,
        DiagnosticBag diagnostics,
        out ImmutableArray<LocalSymbol> declaredLocals,
        out ResultProperties properties);

    /// <summary>
    /// Synthesized expression evaluation method.
    /// </summary>
    internal sealed class EEMethodSymbol : MethodSymbol
    {
        // We only create a single EE method (per EE type) that represents an arbitrary expression,
        // whose lowering may produce synthesized members (lambdas, dynamic sites, etc).
        // We may thus assume that the method ordinal is always 0.
        //
        // Consider making the implementation more flexible in order to avoid this assumption.
        // In future we might need to compile multiple expression and then we'll need to assign 
        // a unique method ordinal to each of them to avoid duplicate synthesized member names.
        private const int _methodOrdinal = 0;

        internal readonly TypeMap TypeMap;
        internal readonly MethodSymbol SubstitutedSourceMethod;
        internal readonly ImmutableArray<LocalSymbol> Locals;
        internal readonly ImmutableArray<LocalSymbol> LocalsForBinding;

        private readonly EENamedTypeSymbol _container;
        private readonly string _name;
        private readonly ImmutableArray<Location> _locations;
        private readonly ImmutableArray<TypeParameterSymbol> _typeParameters;
        private readonly ImmutableArray<ParameterSymbol> _parameters;
        private readonly ParameterSymbol _thisParameter;
        private readonly ImmutableDictionary<string, DisplayClassVariable> _displayClassVariables;

        /// <summary>
        /// Invoked at most once to generate the method body.
        /// (If the compilation has no errors, it will be invoked
        /// exactly once, otherwise it may be skipped.)
        /// </summary>
        private readonly GenerateMethodBody _generateMethodBody;
        private TypeWithAnnotations _lazyReturnType;
        private ResultProperties _lazyResultProperties;

        // NOTE: This is only used for asserts, so it could be conditional on DEBUG.
        private readonly ImmutableArray<TypeParameterSymbol> _allTypeParameters;

        internal EEMethodSymbol(
            EENamedTypeSymbol container,
            string name,
            Location location,
            MethodSymbol sourceMethod,
            ImmutableArray<LocalSymbol> sourceLocals,
            ImmutableArray<LocalSymbol> sourceLocalsForBinding,
            ImmutableDictionary<string, DisplayClassVariable> sourceDisplayClassVariables,
            GenerateMethodBody generateMethodBody)
        {
            Debug.Assert(sourceMethod.IsDefinition);
            Debug.Assert(TypeSymbol.Equals((TypeSymbol)sourceMethod.ContainingSymbol, container.SubstitutedSourceType.OriginalDefinition, TypeCompareKind.ConsiderEverything2));
            Debug.Assert(sourceLocals.All(l => l.ContainingSymbol == sourceMethod));

            _container = container;
            _name = name;
            _locations = ImmutableArray.Create(location);

            // What we want is to map all original type parameters to the corresponding new type parameters
            // (since the old ones have the wrong owners).  Unfortunately, we have a circular dependency:
            //   1) Each new type parameter requires the entire map in order to be able to construct its constraint list.
            //   2) The map cannot be constructed until all new type parameters exist.
            // Our solution is to pass each new type parameter a lazy reference to the type map.  We then 
            // initialize the map as soon as the new type parameters are available - and before they are 
            // handed out - so that there is never a period where they can require the type map and find
            // it uninitialized.

            var sourceMethodTypeParameters = sourceMethod.TypeParameters;
            var allSourceTypeParameters = container.SourceTypeParameters.Concat(sourceMethodTypeParameters);

            sourceMethod = new EECompilationContextMethod(DeclaringCompilation, sourceMethod);

            sourceMethodTypeParameters = sourceMethod.TypeParameters;
            allSourceTypeParameters = allSourceTypeParameters.Concat(sourceMethodTypeParameters);

            var getTypeMap = new Func<TypeMap>(() => this.TypeMap);
            _typeParameters = sourceMethodTypeParameters.SelectAsArray(
                (tp, i, arg) => (TypeParameterSymbol)new EETypeParameterSymbol(this, tp, i, getTypeMap),
                (object)null);
            _allTypeParameters = container.TypeParameters.Concat(_typeParameters).Concat(_typeParameters);
            this.TypeMap = new TypeMap(allSourceTypeParameters, _allTypeParameters, allowAlpha: true);

            EENamedTypeSymbol.VerifyTypeParameters(this, _typeParameters);

            var substitutedSourceType = container.SubstitutedSourceType;
            this.SubstitutedSourceMethod = sourceMethod.AsMember(substitutedSourceType);
            if (sourceMethod.Arity > 0)
            {
                this.SubstitutedSourceMethod = this.SubstitutedSourceMethod.Construct(_typeParameters.As<TypeSymbol>());
            }
            TypeParameterChecker.Check(this.SubstitutedSourceMethod, _allTypeParameters);

            // Create a map from original parameter to target parameter.
            var parameterBuilder = ArrayBuilder<ParameterSymbol>.GetInstance();

            var substitutedSourceThisParameter = this.SubstitutedSourceMethod.ThisParameter;
            var substitutedSourceHasThisParameter = (object)substitutedSourceThisParameter != null;
            if (substitutedSourceHasThisParameter)
            {
                _thisParameter = MakeParameterSymbol(0, GeneratedNames.ThisProxyFieldName(), substitutedSourceThisParameter);
                Debug.Assert(TypeSymbol.Equals(_thisParameter.Type, this.SubstitutedSourceMethod.ContainingType, TypeCompareKind.ConsiderEverything2));
                parameterBuilder.Add(_thisParameter);
            }

            var ordinalOffset = (substitutedSourceHasThisParameter ? 1 : 0);
            foreach (var substitutedSourceParameter in this.SubstitutedSourceMethod.Parameters)
            {
                var ordinal = substitutedSourceParameter.Ordinal + ordinalOffset;
                Debug.Assert(ordinal == parameterBuilder.Count);
                var parameter = MakeParameterSymbol(ordinal, substitutedSourceParameter.Name, substitutedSourceParameter);
                parameterBuilder.Add(parameter);
            }

            _parameters = parameterBuilder.ToImmutableAndFree();

            var localsBuilder = ArrayBuilder<LocalSymbol>.GetInstance();
            var localsMap = PooledDictionary<LocalSymbol, LocalSymbol>.GetInstance();
            foreach (var sourceLocal in sourceLocals)
            {
                var local = sourceLocal.ToOtherMethod(this, this.TypeMap);
                localsMap.Add(sourceLocal, local);
                localsBuilder.Add(local);
            }
            this.Locals = localsBuilder.ToImmutableAndFree();
            localsBuilder = ArrayBuilder<LocalSymbol>.GetInstance();
            foreach (var sourceLocal in sourceLocalsForBinding)
            {
                LocalSymbol local;
                if (!localsMap.TryGetValue(sourceLocal, out local))
                {
                    local = sourceLocal.ToOtherMethod(this, this.TypeMap);
                    localsMap.Add(sourceLocal, local);
                }
                localsBuilder.Add(local);
            }
            this.LocalsForBinding = localsBuilder.ToImmutableAndFree();

            // Create a map from variable name to display class field.
            var displayClassVariables = PooledDictionary<string, DisplayClassVariable>.GetInstance();
            foreach (var pair in sourceDisplayClassVariables)
            {
                var variable = pair.Value;
                var oldDisplayClassInstance = variable.DisplayClassInstance;

                // Note: we don't call ToOtherMethod in the local case because doing so would produce
                // a new LocalSymbol that would not be ReferenceEquals to the one in this.LocalsForBinding.
                var oldDisplayClassInstanceFromLocal = oldDisplayClassInstance as DisplayClassInstanceFromLocal;
                var newDisplayClassInstance = (oldDisplayClassInstanceFromLocal == null)
                    ? oldDisplayClassInstance.ToOtherMethod(this, this.TypeMap)
                    : new DisplayClassInstanceFromLocal((EELocalSymbol)localsMap[oldDisplayClassInstanceFromLocal.Local]);

                variable = variable.SubstituteFields(newDisplayClassInstance, this.TypeMap);
                displayClassVariables.Add(pair.Key, variable);
            }

            _displayClassVariables = displayClassVariables.ToImmutableDictionary();
            displayClassVariables.Free();
            localsMap.Free();

            _generateMethodBody = generateMethodBody;
        }

        private ParameterSymbol MakeParameterSymbol(int ordinal, string name, ParameterSymbol sourceParameter)
        {
            return SynthesizedParameterSymbol.Create(this, sourceParameter.TypeWithAnnotations, ordinal, sourceParameter.RefKind, name, DeclarationScope.Unscoped, refCustomModifiers: sourceParameter.RefCustomModifiers);
        }

        internal override bool IsMetadataNewSlot(bool ignoreInterfaceImplementationChanges = false)
        {
            return false;
        }

        internal override bool IsMetadataVirtual(bool ignoreInterfaceImplementationChanges = false)
        {
            return false;
        }

        internal override bool IsMetadataFinal
        {
            get { return false; }
        }

        public override MethodKind MethodKind
        {
            get { return MethodKind.Ordinary; }
        }

        public override string Name
        {
            get { return _name; }
        }

        public override int Arity
        {
            get { return _typeParameters.Length; }
        }

        public override bool IsExtensionMethod
        {
            get { return false; }
        }

        internal override bool HasSpecialName
        {
            get { return true; }
        }

        internal override System.Reflection.MethodImplAttributes ImplementationAttributes
        {
            get { return default(System.Reflection.MethodImplAttributes); }
        }

        internal override bool HasDeclarativeSecurity
        {
            get { return false; }
        }

        public override DllImportData GetDllImportData()
        {
            return null;
        }

        public override bool AreLocalsZeroed
        {
            get { return true; }
        }

        internal override IEnumerable<Cci.SecurityAttribute> GetSecurityInformation()
        {
            throw ExceptionUtilities.Unreachable();
        }

        internal override MarshalPseudoCustomAttributeData ReturnValueMarshallingInformation
        {
            get { return null; }
        }

        internal override bool RequiresSecurityObject
        {
            get { return false; }
        }

        internal override bool TryGetThisParameter(out ParameterSymbol thisParameter)
        {
            thisParameter = null;
            return true;
        }

        public override bool HidesBaseMethodsByName
        {
            get { return false; }
        }

        public override bool IsVararg
        {
            get { return this.SubstitutedSourceMethod.IsVararg; }
        }

        public override RefKind RefKind
        {
            get { return this.SubstitutedSourceMethod.RefKind; }
        }

        public override bool ReturnsVoid
        {
            get { return this.ReturnType.SpecialType == SpecialType.System_Void; }
        }

        public override bool IsAsync
        {
            get { return false; }
        }

        public override TypeWithAnnotations ReturnTypeWithAnnotations
        {
            get
            {
                if ((object)_lazyReturnType == null)
                {
                    throw new InvalidOperationException();
                }
                return _lazyReturnType;
            }
        }

        public override FlowAnalysisAnnotations ReturnTypeFlowAnalysisAnnotations => FlowAnalysisAnnotations.None;

        public override ImmutableHashSet<string> ReturnNotNullIfParameterNotNull => ImmutableHashSet<string>.Empty;

        public override FlowAnalysisAnnotations FlowAnalysisAnnotations => FlowAnalysisAnnotations.None;

        public override ImmutableArray<TypeWithAnnotations> TypeArgumentsWithAnnotations
        {
            get { return GetTypeParametersAsTypeArguments(); }
        }

        public override ImmutableArray<TypeParameterSymbol> TypeParameters
        {
            get { return _typeParameters; }
        }

        public override ImmutableArray<ParameterSymbol> Parameters
        {
            get { return _parameters; }
        }

        public override ImmutableArray<MethodSymbol> ExplicitInterfaceImplementations
        {
            get { return ImmutableArray<MethodSymbol>.Empty; }
        }

        public override ImmutableArray<CustomModifier> RefCustomModifiers
        {
            get { return ImmutableArray<CustomModifier>.Empty; }
        }

        public override Symbol AssociatedSymbol
        {
            get { return null; }
        }

        internal override ImmutableArray<string> GetAppliedConditionalSymbols()
        {
            throw ExceptionUtilities.Unreachable();
        }

        internal override Cci.CallingConvention CallingConvention
        {
            get
            {
                Debug.Assert(this.IsStatic);
                var cc = Cci.CallingConvention.Default;
                if (this.IsVararg)
                {
                    cc |= Cci.CallingConvention.ExtraArguments;
                }
                if (this.IsGenericMethod)
                {
                    cc |= Cci.CallingConvention.Generic;
                }
                return cc;
            }
        }

        internal override bool GenerateDebugInfo
        {
            get { return false; }
        }

        public override Symbol ContainingSymbol
        {
            get { return _container; }
        }

        public override ImmutableArray<Location> Locations
        {
            get { return _locations; }
        }

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences
        {
            get { return GetDeclaringSyntaxReferenceHelper<CSharpSyntaxNode>(_locations); }
        }

        public override Accessibility DeclaredAccessibility
        {
            get { return Accessibility.Internal; }
        }

        public override bool IsStatic
        {
            get { return true; }
        }

        public override bool IsVirtual
        {
            get { return false; }
        }

        public override bool IsOverride
        {
            get { return false; }
        }

        public override bool IsAbstract
        {
            get { return false; }
        }

        public override bool IsSealed
        {
            get { return false; }
        }

        public override bool IsExtern
        {
            get { return false; }
        }

        internal override bool IsDeclaredReadOnly => false;

        internal override bool IsInitOnly => false;

        internal override ObsoleteAttributeData ObsoleteAttributeData
        {
            get { throw ExceptionUtilities.Unreachable(); }
        }

        internal sealed override UnmanagedCallersOnlyAttributeData GetUnmanagedCallersOnlyAttributeData(bool forceComplete) => throw ExceptionUtilities.Unreachable();

        internal override bool HasUnscopedRefAttribute => false;

        internal override bool UseUpdatedEscapeRules => false;

        internal ResultProperties ResultProperties
        {
            get { return _lazyResultProperties; }
        }

        internal override void GenerateMethodBody(TypeCompilationState compilationState, BindingDiagnosticBag diagnostics)
        {
            ImmutableArray<LocalSymbol> declaredLocalsArray;
            var body = _generateMethodBody(this, diagnostics.DiagnosticBag, out declaredLocalsArray, out _lazyResultProperties);
            if (body.HasErrors)
                throw new Exception("body has errors");
            var compilation = compilationState.Compilation;

            _lazyReturnType = TypeWithAnnotations.Create(CalculateReturnType(compilation, body));

            // Can't do this until the return type has been computed.
            TypeParameterChecker.Check(this, _allTypeParameters);

            if (diagnostics.HasAnyErrors())
            {
                return;
            }

            Binder.ProcessedFieldInitializers processedFieldInitializers = default;
            CompileMethod(this, 0, ref processedFieldInitializers, null, compilationState, (BoundBlock)body);
            return;
            
            DiagnosticsPass.IssueDiagnostics(compilation, body, diagnostics, this);
            if (diagnostics.HasAnyErrors())
            {
                return;
            }

            // Check for use-site diagnostics (e.g. missing types in the signature).
            UseSiteInfo<AssemblySymbol> useSiteInfo = default;
            this.CalculateUseSiteDiagnostic(ref useSiteInfo);
            if (useSiteInfo.DiagnosticInfo != null && useSiteInfo.DiagnosticInfo.Severity == DiagnosticSeverity.Error)
            {
                diagnostics.Add(useSiteInfo.DiagnosticInfo, this.Locations[0]);
                return;
            }

            try
            {
                var declaredLocals = PooledHashSet<LocalSymbol>.GetInstance();
                try
                {
                    // Rewrite local declaration statement.
                    body = (BoundStatement)LocalDeclarationRewriter.Rewrite(
                        compilation,
                        declaredLocals,
                        body,
                        declaredLocalsArray,
                        diagnostics.DiagnosticBag);

                    // Verify local declaration names.
                    foreach (var local in declaredLocals)
                    {
                        Debug.Assert(local.Locations.Length > 0);
                        var name = local.Name;
                        if (name.StartsWith("$", StringComparison.Ordinal))
                        {
                            diagnostics.Add(ErrorCode.ERR_UnexpectedCharacter, local.Locations[0], name[0]);
                            return;
                        }
                    }

                    // Rewrite references to placeholder "locals".
                    body = (BoundStatement)PlaceholderLocalRewriter.Rewrite(compilation, declaredLocals, body, diagnostics.DiagnosticBag);

                    if (diagnostics.HasAnyErrors())
                    {
                        return;
                    }
                }
                finally
                {
                    declaredLocals.Free();
                }

                var syntax = body.Syntax;
                var statementsBuilder = ArrayBuilder<BoundStatement>.GetInstance();
                statementsBuilder.Add(body);
                // Insert an implicit return statement if necessary.
                if (body.Kind != BoundKind.ReturnStatement)
                {
                    statementsBuilder.Add(new BoundReturnStatement(syntax, RefKind.None, expressionOpt: null, @checked: false));
                }

                var localsSet = PooledHashSet<LocalSymbol>.GetInstance();
                try
                {
                    var localsBuilder = ArrayBuilder<LocalSymbol>.GetInstance();
                    foreach (var local in this.LocalsForBinding)
                    {
                        Debug.Assert(!localsSet.Contains(local));
                        localsBuilder.Add(local);
                        localsSet.Add(local);
                    }
                    foreach (var local in this.Locals)
                    {
                        if (localsSet.Add(local))
                        {
                            localsBuilder.Add(local);
                        }
                    }

                    body = new BoundBlock(syntax, localsBuilder.ToImmutableAndFree(), statementsBuilder.ToImmutableAndFree()) { WasCompilerGenerated = true };

                    Debug.Assert(!diagnostics.HasAnyErrors());
                    Debug.Assert(!body.HasErrors);

                    bool sawLambdas;
                    bool sawLocalFunctions;
                    bool sawAwaitInExceptionHandler;
                    ImmutableArray<SourceSpan> dynamicAnalysisSpans = ImmutableArray<SourceSpan>.Empty;
                    body = LocalRewriter.Rewrite(
                        compilation: this.DeclaringCompilation,
                        method: this,
                        methodOrdinal: _methodOrdinal,
                        containingType: _container,
                        statement: body,
                        compilationState: compilationState,
                        previousSubmissionFields: null,
                        allowOmissionOfConditionalCalls: false,
                        instrumentForDynamicAnalysis: false,
                        debugDocumentProvider: null,
                        dynamicAnalysisSpans: ref dynamicAnalysisSpans,
                        diagnostics: diagnostics,
                        sawLambdas: out sawLambdas,
                        sawLocalFunctions: out sawLocalFunctions,
                        sawAwaitInExceptionHandler: out sawAwaitInExceptionHandler);

                    Debug.Assert(!sawAwaitInExceptionHandler);
                    Debug.Assert(dynamicAnalysisSpans.Length == 0);

                    if (body.HasErrors)
                    {
                        return;
                    }

                    // Variables may have been captured by lambdas in the original method
                    // or in the expression, and we need to preserve the existing values of
                    // those variables in the expression. This requires rewriting the variables
                    // in the expression based on the closure classes from both the original
                    // method and the expression, and generating a preamble that copies
                    // values into the expression closure classes.
                    //
                    // Consider the original method:
                    // static void M()
                    // {
                    //     int x, y, z;
                    //     ...
                    //     F(() => x + y);
                    // }
                    // and the expression in the EE: "F(() => x + z)".
                    //
                    // The expression is first rewritten using the closure class and local <1>
                    // from the original method: F(() => <1>.x + z)
                    // Then lambda rewriting introduces a new closure class that includes
                    // the locals <1> and z, and a corresponding local <2>: F(() => <2>.<1>.x + <2>.z)
                    // And a preamble is added to initialize the fields of <2>:
                    //     <2> = new <>c__DisplayClass0();
                    //     <2>.<1> = <1>;
                    //     <2>.z = z;

                    // Rewrite "this" and "base" references to parameter in this method.
                    // Rewrite variables within body to reference existing display classes.
                    body = (BoundStatement)CapturedVariableRewriter.Rewrite(
                        this.GenerateThisReference,
                        compilation.Conversions,
                        _displayClassVariables,
                        body,
                        diagnostics.DiagnosticBag);

                    if (body.HasErrors)
                    {
                        Debug.Assert(false, "Please add a test case capturing whatever caused this assert.");
                        return;
                    }

                    if (diagnostics.HasAnyErrors())
                    {
                        return;
                    }

                    if (sawLambdas || sawLocalFunctions)
                    {
                        var closureDebugInfoBuilder = ArrayBuilder<ClosureDebugInfo>.GetInstance();
                        var lambdaDebugInfoBuilder = ArrayBuilder<LambdaDebugInfo>.GetInstance();

                        body = ClosureConversion.Rewrite(
                            loweredBody: body,
                            thisType: this.SubstitutedSourceMethod.ContainingType,
                            thisParameter: _thisParameter,
                            method: this,
                            methodOrdinal: _methodOrdinal,
                            substitutedSourceMethod: this.SubstitutedSourceMethod.OriginalDefinition,
                            closureDebugInfoBuilder: closureDebugInfoBuilder,
                            lambdaDebugInfoBuilder: lambdaDebugInfoBuilder,
                            slotAllocatorOpt: null,
                            compilationState: compilationState,
                            diagnostics: diagnostics,
                            assignLocals: localsSet);

                        // we don't need this information:
                        closureDebugInfoBuilder.Free();
                        lambdaDebugInfoBuilder.Free();
                    }
                }
                finally
                {
                    localsSet.Free();
                }

                // Insert locals from the original method,
                // followed by any new locals.
                var block = (BoundBlock)body;
                var localBuilder = ArrayBuilder<LocalSymbol>.GetInstance();
                foreach (var local in this.Locals)
                {
                    Debug.Assert(!(local is EELocalSymbol) || (((EELocalSymbol)local).Ordinal == localBuilder.Count));
                    localBuilder.Add(local);
                }
                foreach (var local in block.Locals)
                {
                    if (local is EELocalSymbol oldLocal)
                    {
                        Debug.Assert(localBuilder[oldLocal.Ordinal] == oldLocal);
                        continue;
                    }
                    localBuilder.Add(local);
                }

                body = block.Update(localBuilder.ToImmutableAndFree(), block.LocalFunctions, block.Statements);
                TypeParameterChecker.Check(body, _allTypeParameters);
                compilationState.AddSynthesizedMethod(this, body);
            }
            catch (BoundTreeVisitor.CancelledByStackGuardException ex)
            {
                ex.AddAnError(diagnostics);
            }
        }

        private void CompileMethod(
            MethodSymbol methodSymbol,
            int methodOrdinal,
            ref Binder.ProcessedFieldInitializers processedInitializers,
            SynthesizedSubmissionFields previousSubmissionFields,
            TypeCompilationState compilationState,
            BoundBlock boundBlock)
        {
            var _cancellationToken = CancellationToken.None;
            var _compilation = compilationState.Compilation;
            var _moduleBeingBuiltOpt = compilationState.ModuleBuilderOpt;
            var _diagnostics = new BindingDiagnosticBag();
            var ReportNullableDiagnostics = false;
            var _emitTestCoverageData = false;
            var _emitMethodBodies = true;
            DebugDocumentProvider _debugDocumentProvider = null;
            _cancellationToken.ThrowIfCancellationRequested();
            SourceMemberMethodSymbol sourceMethod = methodSymbol as SourceMemberMethodSymbol;

            if (methodSymbol.IsAbstract || methodSymbol.ContainingType?.IsDelegateType() == true)
            {
                if ((object)sourceMethod != null)
                {
                    bool diagsWritten;
                    sourceMethod.SetDiagnostics(ImmutableArray<Diagnostic>.Empty, out diagsWritten);
                    if (diagsWritten && !methodSymbol.IsImplicitlyDeclared && _compilation.EventQueue != null)
                    {
                        _compilation.SymbolDeclaredEvent(methodSymbol);
                    }
                }

                return;
            }

            // get cached diagnostics if not building and we have 'em
            if (_moduleBeingBuiltOpt == null && (object)sourceMethod != null)
            {
                var cachedDiagnostics = sourceMethod.Diagnostics;

                if (!cachedDiagnostics.IsDefault)
                {
                    _diagnostics.AddRange(cachedDiagnostics);
                    return;
                }
            }

            ImportChain oldImportChain = compilationState.CurrentImportChain;

            // In order to avoid generating code for methods with errors, we create a diagnostic bag just for this method.
            var diagsForCurrentMethod = BindingDiagnosticBag.GetInstance(_diagnostics);

            try
            {
                // if synthesized method returns its body in lowered form
                if (methodSymbol.SynthesizesLoweredBoundBody)
                {
                    if (_moduleBeingBuiltOpt != null)
                    {
                        methodSymbol.GenerateMethodBody(compilationState, diagsForCurrentMethod);
                        _diagnostics.AddRange(diagsForCurrentMethod);
                    }

                    return;
                }

                // no need to emit the default ctor, we are not emitting those
                if (methodSymbol.IsDefaultValueTypeConstructor())
                {
                    return;
                }

                bool includeNonEmptyInitializersInBody = false;
                BoundBlock body;
                bool originalBodyNested = false;

                // initializers that have been analyzed but not yet lowered.
                BoundStatementList analyzedInitializers = null;
                MethodBodySemanticModel.InitialState forSemanticModel = default;
                ImportChain importChain = null;
                var hasTrailingExpression = false;

                if (methodSymbol.IsScriptConstructor)
                {
                    Debug.Assert(methodSymbol.IsImplicitlyDeclared);
                    body = new BoundBlock(methodSymbol.GetNonNullSyntaxNode(), ImmutableArray<LocalSymbol>.Empty, ImmutableArray<BoundStatement>.Empty) { WasCompilerGenerated = true };
                }
                else if (methodSymbol.IsScriptInitializer)
                {
                    Debug.Assert(methodSymbol.IsImplicitlyDeclared);

                    // rewrite top-level statements and script variable declarations to a list of statements and assignments, respectively:
                    var initializerStatements = InitializerRewriter.RewriteScriptInitializer(processedInitializers.BoundInitializers, (SynthesizedInteractiveInitializerMethod)methodSymbol, out hasTrailingExpression);

                    // the lowered script initializers should not be treated as initializers anymore but as a method body:
                    body = BoundBlock.SynthesizedNoLocals(initializerStatements.Syntax, initializerStatements.Statements);

                    if (ReportNullableDiagnostics)
                    {
                        NullableWalker.AnalyzeIfNeeded(
                            _compilation,
                            methodSymbol,
                            initializerStatements,
                            diagsForCurrentMethod.DiagnosticBag,
                            useConstructorExitWarnings: false,
                            initialNullableState: null,
                            getFinalNullableState: true,
                            baseOrThisInitializer: null,
                            out _);
                    }

                    var unusedDiagnostics = DiagnosticBag.GetInstance();
                    DefiniteAssignmentPass.Analyze(_compilation, methodSymbol, initializerStatements, unusedDiagnostics, out _, requireOutParamsAssigned: false);
                    DiagnosticsPass.IssueDiagnostics(_compilation, initializerStatements, BindingDiagnosticBag.Discarded, methodSymbol);
                    unusedDiagnostics.Free();
                }
                else
                {
                    var includeInitializersInBody = methodSymbol.IncludeFieldInitializersInBody();
                    // Do not emit initializers if we are invoking another constructor of this class.
                    includeNonEmptyInitializersInBody = includeInitializersInBody && !processedInitializers.BoundInitializers.IsDefaultOrEmpty;

                    if (includeNonEmptyInitializersInBody && processedInitializers.LoweredInitializers == null)
                    {
                        analyzedInitializers = InitializerRewriter.RewriteConstructor(processedInitializers.BoundInitializers, methodSymbol);
                        processedInitializers.HasErrors = processedInitializers.HasErrors || analyzedInitializers.HasAnyErrors;
                    }

                    body = boundBlock;
                    // body = BindMethodBody(
                    //     methodSymbol,
                    //     compilationState,
                    //     diagsForCurrentMethod,
                    //     includeInitializersInBody,
                    //     analyzedInitializers,
                    //     ReportNullableDiagnostics,
                    //     out importChain,
                    //     out originalBodyNested,
                    //     out bool prependedDefaultValueTypeConstructorInitializer,
                    //     out forSemanticModel);

                    // Debug.Assert(!prependedDefaultValueTypeConstructorInitializer || originalBodyNested);
                    // Debug.Assert(!prependedDefaultValueTypeConstructorInitializer || methodSymbol.ContainingType.IsStructType());

                    if (diagsForCurrentMethod.HasAnyErrors() && body != null)
                    {
                        body = (BoundBlock)body.WithHasErrors();
                    }

                    // lower initializers just once. the lowered tree will be reused when emitting all constructors
                    // with field initializers. Once lowered, these initializers will be stashed in processedInitializers.LoweredInitializers
                    // (see later in this method). Don't bother lowering _now_ if this particular ctor won't have the initializers
                    // appended to its body.
                    if (includeNonEmptyInitializersInBody && processedInitializers.LoweredInitializers == null)
                    {
                        if (body != null && ((methodSymbol.ContainingType.IsStructType() && !methodSymbol.IsImplicitConstructor) || methodSymbol is SynthesizedRecordConstructor || _emitTestCoverageData))
                        {
                            if (_emitTestCoverageData && methodSymbol.IsImplicitConstructor)
                            {
                                // Flow analysis over the initializers is necessary in order to find assignments to fields.
                                // Bodies of implicit constructors do not get flow analysis later, so the initializers
                                // are analyzed here.
                                DefiniteAssignmentPass.Analyze(_compilation, methodSymbol, analyzedInitializers, diagsForCurrentMethod.DiagnosticBag, out _, requireOutParamsAssigned: false);
                            }

                            // In order to get correct diagnostics, we need to analyze initializers and the body together.
                            int insertAt = 0;
                            var prependedDefaultValueTypeConstructorInitializer = /*TODO: is it correct?*/false;
                            if (originalBodyNested &&
                                prependedDefaultValueTypeConstructorInitializer)
                            {
                                insertAt = 1;
                            }
                            body = body.Update(body.Locals, body.LocalFunctions, body.Statements.Insert(insertAt, analyzedInitializers));
                            includeNonEmptyInitializersInBody = false;
                            analyzedInitializers = null;
                        }
                        else
                        {
                            // These analyses check for diagnostics in lambdas.
                            // Control flow analysis and implicit return insertion are unnecessary.
                            DefiniteAssignmentPass.Analyze(_compilation, methodSymbol, analyzedInitializers, diagsForCurrentMethod.DiagnosticBag, out _, requireOutParamsAssigned: false);
                            DiagnosticsPass.IssueDiagnostics(_compilation, analyzedInitializers, diagsForCurrentMethod, methodSymbol);
                        }
                    }
                }

#if DEBUG
                // If the method is a synthesized static or instance constructor, then debugImports will be null and we will use the value
                // from the first field initializer.
                if ((methodSymbol.MethodKind == MethodKind.Constructor || methodSymbol.MethodKind == MethodKind.StaticConstructor) &&
                    methodSymbol.IsImplicitlyDeclared && body == null)
                {
                    // There was no body to bind, so we didn't get anything from BindMethodBody.
                    Debug.Assert(importChain == null);
                }

                // Either there were no field initializers or we grabbed debug imports from the first one.
                Debug.Assert(processedInitializers.BoundInitializers.IsDefaultOrEmpty || processedInitializers.FirstImportChain != null);
#endif

                importChain = importChain ?? processedInitializers.FirstImportChain;

                // Associate these debug imports with all methods generated from this one.
                compilationState.CurrentImportChain = importChain;

                if (body != null)
                {
                    DiagnosticsPass.IssueDiagnostics(_compilation, body, diagsForCurrentMethod, methodSymbol);
                }

                BoundBlock flowAnalyzedBody = null;
                if (body != null)
                {
                    flowAnalyzedBody = FlowAnalysisPass.Rewrite(methodSymbol, body, compilationState, diagsForCurrentMethod, hasTrailingExpression: hasTrailingExpression, originalBodyNested: originalBodyNested);
                }

                var _hasDeclarationErrors = false;
                bool hasErrors = _hasDeclarationErrors || diagsForCurrentMethod.HasAnyErrors() || processedInitializers.HasErrors;

                // Record whether or not the bound tree for the lowered method body (including any initializers) contained any
                // errors (note: errors, not diagnostics).
                SetGlobalErrorIfTrue(hasErrors);

                var actualDiagnostics = diagsForCurrentMethod.ToReadOnly();
                if (sourceMethod != null)
                {
                    _compilation.RegisterPossibleUpcomingEventEnqueue();

                    try
                    {
                        bool diagsWritten;
                        actualDiagnostics = new ImmutableBindingDiagnostic<AssemblySymbol>(sourceMethod.SetDiagnostics(actualDiagnostics.Diagnostics, out diagsWritten), actualDiagnostics.Dependencies);

                        if (diagsWritten && !methodSymbol.IsImplicitlyDeclared && _compilation.EventQueue != null)
                        {
                            // If compilation has a caching semantic model provider, then cache the already-computed bound tree
                            // onto the semantic model and store it on the event.
                            SyntaxTreeSemanticModel semanticModelWithCachedBoundNodes = null;
                            if (body != null &&
                                forSemanticModel.Syntax is { } semanticModelSyntax &&
                                _compilation.SemanticModelProvider is CachingSemanticModelProvider cachingSemanticModelProvider)
                            {
                                var syntax = body.Syntax;
                                semanticModelWithCachedBoundNodes = (SyntaxTreeSemanticModel)cachingSemanticModelProvider.GetSemanticModel(syntax.SyntaxTree, _compilation);
                                semanticModelWithCachedBoundNodes.GetOrAddModel(semanticModelSyntax,
                                                            (rootSyntax) =>
                                                            {
                                                                Debug.Assert(rootSyntax == forSemanticModel.Syntax);
                                                                return MethodBodySemanticModel.Create(semanticModelWithCachedBoundNodes,
                                                                                                      methodSymbol,
                                                                                                      forSemanticModel);
                                                            });
                            }

                            _compilation.EventQueue.TryEnqueue(new SymbolDeclaredCompilationEvent(_compilation, methodSymbol.GetPublicSymbol(), semanticModelWithCachedBoundNodes));
                        }
                    }
                    finally
                    {
                        _compilation.UnregisterPossibleUpcomingEventEnqueue();
                    }
                }

                // Don't lower if we're not emitting or if there were errors.
                // Methods that had binding errors are considered too broken to be lowered reliably.
                if (_moduleBeingBuiltOpt == null || hasErrors)
                {
                    _diagnostics.AddRange(actualDiagnostics);
                    return;
                }

                // ############################
                // LOWERING AND EMIT
                // Any errors generated below here are considered Emit diagnostics
                // and will not be reported to callers Compilation.GetDiagnostics()

                ImmutableArray<SourceSpan> dynamicAnalysisSpans = ImmutableArray<SourceSpan>.Empty;
                bool hasBody = flowAnalyzedBody != null;
                VariableSlotAllocator lazyVariableSlotAllocator = null;
                StateMachineTypeSymbol stateMachineTypeOpt = null;
                var lambdaDebugInfoBuilder = ArrayBuilder<LambdaDebugInfo>.GetInstance();
                var closureDebugInfoBuilder = ArrayBuilder<ClosureDebugInfo>.GetInstance();
                var stateMachineStateDebugInfoBuilder = ArrayBuilder<StateMachineStateDebugInfo>.GetInstance();
                BoundStatement loweredBodyOpt = null;

                try
                {
                    if (hasBody)
                    {
                        loweredBodyOpt = MethodCompiler.LowerBodyOrInitializer(
                            methodSymbol,
                            methodOrdinal,
                            flowAnalyzedBody,
                            previousSubmissionFields,
                            compilationState,
                            _emitTestCoverageData,
                            _debugDocumentProvider,
                            ref dynamicAnalysisSpans,
                            diagsForCurrentMethod,
                            ref lazyVariableSlotAllocator,
                            lambdaDebugInfoBuilder,
                            closureDebugInfoBuilder,
                            stateMachineStateDebugInfoBuilder,
                            out stateMachineTypeOpt);

                        Debug.Assert(loweredBodyOpt != null);
                    }
                    else
                    {
                        loweredBodyOpt = null;
                    }

                    hasErrors = hasErrors || (hasBody && loweredBodyOpt.HasErrors) || diagsForCurrentMethod.HasAnyErrors();
                    SetGlobalErrorIfTrue(hasErrors);
                    CSharpSyntaxNode syntax = methodSymbol.GetNonNullSyntaxNode();
                    // don't emit if the resulting method would contain initializers with errors
                    if (!hasErrors && (hasBody || includeNonEmptyInitializersInBody))
                    {
                        Debug.Assert(!(methodSymbol.IsImplicitInstanceConstructor && methodSymbol.ParameterCount == 0) ||
                                     !methodSymbol.IsDefaultValueTypeConstructor());

                        // Fields must be initialized before constructor initializer (which is the first statement of the analyzed body, if specified),
                        // so that the initialization occurs before any method overridden by the declaring class can be invoked from the base constructor
                        // and access the fields.

                        ImmutableArray<BoundStatement> boundStatements;

                        if (methodSymbol.IsScriptConstructor)
                        {
                            boundStatements = MethodBodySynthesizer.ConstructScriptConstructorBody(loweredBodyOpt, methodSymbol, previousSubmissionFields, _compilation);
                        }
                        else
                        {
                            boundStatements = ImmutableArray<BoundStatement>.Empty;

                            if (analyzedInitializers != null)
                            {
                                // For dynamic analysis, field initializers are instrumented as part of constructors,
                                // and so are never instrumented here.
                                Debug.Assert(!_emitTestCoverageData);
                                StateMachineTypeSymbol initializerStateMachineTypeOpt;

                                BoundStatement lowered = MethodCompiler.LowerBodyOrInitializer(
                                    methodSymbol,
                                    methodOrdinal,
                                    analyzedInitializers,
                                    previousSubmissionFields,
                                    compilationState,
                                    _emitTestCoverageData,
                                    _debugDocumentProvider,
                                    ref dynamicAnalysisSpans,
                                    diagsForCurrentMethod,
                                    ref lazyVariableSlotAllocator,
                                    lambdaDebugInfoBuilder,
                                    closureDebugInfoBuilder,
                                    stateMachineStateDebugInfoBuilder,
                                    out initializerStateMachineTypeOpt);

                                processedInitializers.LoweredInitializers = lowered;

                                // initializers can't produce state machines
                                Debug.Assert((object)initializerStateMachineTypeOpt == null);
                                Debug.Assert(!hasErrors);
                                hasErrors = lowered.HasAnyErrors || diagsForCurrentMethod.HasAnyErrors();
                                SetGlobalErrorIfTrue(hasErrors);
                                if (hasErrors)
                                {
                                    _diagnostics.AddRange(diagsForCurrentMethod);
                                    return;
                                }

                                // Only do the cast if we haven't returned with some error diagnostics.
                                // Otherwise, `lowered` might have been a BoundBadStatement.
                                processedInitializers.LoweredInitializers = (BoundStatementList)lowered;
                            }

                            // initializers for global code have already been included in the body
                            if (includeNonEmptyInitializersInBody)
                            {
                                if (processedInitializers.LoweredInitializers.Kind == BoundKind.StatementList)
                                {
                                    BoundStatementList lowered = (BoundStatementList)processedInitializers.LoweredInitializers;
                                    boundStatements = boundStatements.Concat(lowered.Statements);
                                }
                                else
                                {
                                    boundStatements = boundStatements.Add(processedInitializers.LoweredInitializers);
                                }
                            }

                            if (hasBody)
                            {
                                boundStatements = boundStatements.Concat(loweredBodyOpt);
                            }

                            var factory = new SyntheticBoundNodeFactory(methodSymbol, syntax, compilationState, diagsForCurrentMethod);

                            hasErrors = diagsForCurrentMethod.HasAnyErrors();
                            SetGlobalErrorIfTrue(hasErrors);
                            if (hasErrors)
                            {
                                _diagnostics.AddRange(diagsForCurrentMethod);
                                return;
                            }
                        }
                        if (_emitMethodBodies && (!(methodSymbol is SynthesizedStaticConstructor cctor) || cctor.ShouldEmit(processedInitializers.BoundInitializers)))
                        {
                            var boundStatement = boundStatements.Single();
                            if (boundStatement.HasErrors)
                                throw new Exception("BoundStatement has errors");
                            compilationState.AddSynthesizedMethod(this, boundStatement);
                            return;
                            var boundBody = BoundStatementList.Synthesized(syntax, boundStatements);

                            // var emittedBody = MethodCompiler.GenerateMethodBody(
                            //     _moduleBeingBuiltOpt,
                            //     methodSymbol,
                            //     methodOrdinal,
                            //     boundBody,
                            //     lambdaDebugInfoBuilder.ToImmutable(),
                            //     closureDebugInfoBuilder.ToImmutable(),
                            //     stateMachineStateDebugInfoBuilder.ToImmutable(),
                            //     stateMachineTypeOpt,
                            //     lazyVariableSlotAllocator,
                            //     diagsForCurrentMethod,
                            //     _debugDocumentProvider,
                            //     importChain,
                            //     _emittingPdb,
                            //     _emitTestCoverageData,
                            //     dynamicAnalysisSpans,
                            //     entryPointOpt: null);
                            //
                            // _moduleBeingBuiltOpt.SetMethodBody(methodSymbol.PartialDefinitionPart ?? methodSymbol, emittedBody);
                        }
                    }

                    _diagnostics.AddRange(diagsForCurrentMethod);
                }
                finally
                {
                    lambdaDebugInfoBuilder.Free();
                    closureDebugInfoBuilder.Free();
                    stateMachineStateDebugInfoBuilder.Free();
                }
            }
            finally
            {
                diagsForCurrentMethod.Free();
                compilationState.CurrentImportChain = oldImportChain;
            }
        }

        private void SetGlobalErrorIfTrue(bool arg)
        {
            //NOTE: this is not a volatile write
            //      for correctness we need only single threaded consistency.
            //      Within a single task - if we have got an error it may not be safe to continue with some lowerings.
            //      It is ok if other tasks will see the change after some delay or does not observe at all.
            //      Such races are unavoidable and will just result in performing some work that is safe to do
            //      but may no longer be needed.
            //      The final Join of compiling tasks cannot happen without interlocked operations and that
            //      will ensure that any write of the flag is globally visible.
            if (arg)
            {
                _globalHasErrors = true;
            }
        }

        private bool _globalHasErrors;
        
        private BoundExpression GenerateThisReference(SyntaxNode syntax)
        {
            var thisProxy = CompilationContext.GetThisProxy(_displayClassVariables);
            if (thisProxy != null)
            {
                return thisProxy.ToBoundExpression(syntax);
            }
            if ((object)_thisParameter != null)
            {
                var typeNameKind = GeneratedNameParser.GetKind(_thisParameter.TypeWithAnnotations.Type.Name);
                if (typeNameKind != GeneratedNameKind.None && typeNameKind != GeneratedNameKind.AnonymousType)
                {
                    Debug.Assert(typeNameKind == GeneratedNameKind.LambdaDisplayClass ||
                        typeNameKind == GeneratedNameKind.StateMachineType,
                        $"Unexpected typeNameKind '{typeNameKind}'");
                    return null;
                }
                return new BoundParameter(syntax, _thisParameter);
            }
            return null;
        }

        private static TypeSymbol CalculateReturnType(CSharpCompilation compilation, BoundStatement bodyOpt)
        {
            // TODO: needed to pass method return type here
            // return compilation.GetSpecialType(SpecialType.System_Void);
            if (bodyOpt == null)
            {
                // If the method doesn't do anything, then it doesn't return anything.
                return compilation.GetSpecialType(SpecialType.System_Void);
            }

            // TODO: don't cover all scenarious
            BoundStatement getLastBoundStatement(BoundBlock body)
            {
                while (true)
                {
                    var lastStatement = body.Statements.LastOrDefault();
                    if (lastStatement is BoundBlock lastStatementAsBoundBlock)
                    {
                        body = lastStatementAsBoundBlock;
                        continue;
                    }

                    return lastStatement;
                    break;
                }
            }

            BoundStatement statement = getLastBoundStatement((BoundBlock)bodyOpt);

            switch (statement.Kind)
            {
                case BoundKind.ReturnStatement:
                    return ((BoundReturnStatement)statement).ExpressionOpt.Type;
                case BoundKind.ExpressionStatement:
                case BoundKind.LocalDeclaration:
                case BoundKind.MultipleLocalDeclarations:
                    return compilation.GetSpecialType(SpecialType.System_Void);
                default:
                    throw ExceptionUtilities.UnexpectedValue(statement.Kind);
            }
        }

        internal override int CalculateLocalSyntaxOffset(int localPosition, SyntaxTree localTree)
        {
            return localPosition;
        }

        internal override bool IsNullableAnalysisEnabled() => false;

        protected override bool HasSetsRequiredMembersImpl => throw ExceptionUtilities.Unreachable();
    }
}
