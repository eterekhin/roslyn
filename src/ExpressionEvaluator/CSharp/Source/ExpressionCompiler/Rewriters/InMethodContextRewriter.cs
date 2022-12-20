// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator;

internal class InMethodContextRewriter : BoundTreeRewriterWithStackGuardWithoutRecursionOnTheLeftOfBinaryOperator
{
    private readonly BoundNode _root;

    internal static BoundNode Rewrite(BoundNode node)
    {
        var rewriter = new InMethodContextRewriter(node);
        return rewriter.Visit(node);
    }

    public InMethodContextRewriter(BoundNode root)
    {
        _root = root;
    }

    public override BoundNode? VisitFieldAccess(BoundFieldAccess node)
    {
        var access = base.VisitFieldAccess(node);
        return access;
    }
}
