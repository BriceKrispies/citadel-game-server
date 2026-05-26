using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Citadel.RepoAnalyzers;

/// <summary>Small shared syntax helpers for the architectural-invariant analyzers.</summary>
internal static class AnalyzerSyntax
{
    /// <summary>
    /// The leftmost identifier of a member-access / invocation chain — e.g. <c>inbound</c> in
    /// <c>inbound.Envelope.PlayerId</c>. Used to attribute provenance to a named root.
    /// </summary>
    public static string? RootIdentifier(ExpressionSyntax expression)
    {
        var current = expression;
        while (true)
        {
            switch (current)
            {
                case IdentifierNameSyntax id:
                    return id.Identifier.Text;
                case MemberAccessExpressionSyntax member:
                    current = member.Expression;
                    break;
                case InvocationExpressionSyntax invocation:
                    current = invocation.Expression;
                    break;
                case ParenthesizedExpressionSyntax paren:
                    current = paren.Expression;
                    break;
                default:
                    return null;
            }
        }
    }
}
