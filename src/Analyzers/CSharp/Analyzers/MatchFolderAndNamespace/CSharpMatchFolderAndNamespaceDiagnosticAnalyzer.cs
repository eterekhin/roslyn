﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Analyzers.MatchFolderAndNamespace;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.LanguageServices;
using System.Collections.Immutable;

namespace Microsoft.CodeAnalysis.CSharp.Analyzers.MatchFolderAndNamespace
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    internal class CSharpMatchFolderAndNamespaceDiagnosticAnalyzer
        : AbstractMatchFolderAndNamespaceDiagnosticAnalyzer<SyntaxKind, BaseNamespaceDeclarationSyntax>
    {
        protected override ISyntaxFacts GetSyntaxFacts() => CSharpSyntaxFacts.Instance;

        protected override ImmutableArray<SyntaxKind> GetSyntaxKindsToAnalyze()
            => ImmutableArray.Create(SyntaxKind.NamespaceDeclaration, SyntaxKind.FileScopedNamespaceDeclaration);
    }
}
