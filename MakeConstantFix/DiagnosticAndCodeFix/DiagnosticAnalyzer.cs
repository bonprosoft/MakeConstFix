using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DiagnosticAndCodeFix
{
    // TODO: Consider implementing other interfaces that implement IDiagnosticAnalyzer instead of or in addition to ISymbolAnalyzer

    [DiagnosticAnalyzer]
    [ExportDiagnosticAnalyzer(DiagnosticId, LanguageNames.CSharp)]
    public class DiagnosticAnalyzer : ISyntaxNodeAnalyzer<SyntaxKind>
    {
        public const string DiagnosticId = "MakeConst";
        internal const string Description = "Make Constant";
        internal const string MessageFormat = "定数化できます";
        internal const string Category = "Usage";

        internal static DiagnosticDescriptor Rule = new DiagnosticDescriptor(DiagnosticId, Description,
             MessageFormat, Category, DiagnosticSeverity.Warning, true);

        public ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(Rule); } }

        public ImmutableArray<SyntaxKind> SyntaxKindsOfInterest
        {
            get
            {
                return ImmutableArray.Create(SyntaxKind.LocalDeclarationStatement);
            }
        }

        public void AnalyzeNode(SyntaxNode node, SemanticModel semanticModel,
            Action<Diagnostic> addDiagnostic, AnalyzerOptions options, 
            CancellationToken cancellationToken)
        {

            // SyntaxKindsOfInterestにてLocalDeclarationStatementSyntaxのみに限定しているため、キャスト可能
            var localDeclaration = (LocalDeclarationStatementSyntax)node;

            // すでにconstキーワードが設定されているものは除外する
            if (localDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword))
                return;

            // 変数の型を取得する
            var variableTypeName = localDeclaration.Declaration.Type;
            var variableType = semanticModel.GetTypeInfo(variableTypeName).ConvertedType;

            // 複数の変数が同一文で宣言されている可能性がある
            foreach (var variable in localDeclaration.Declaration.Variables)
            {
                // int x,y = 0; は定数化不可能 -> Initializerを確認する
                var initializer = variable.Initializer;
                if (initializer == null)
                    return;

                // int x = somefunc(); は定数化不可能 -> コンパイル時定数であるか確認する
                var constantValue = semanticModel.GetConstantValue(initializer.Value);
                if (!constantValue.HasValue)
                    return;

                // 初期化子の値が宣言文との型と互換性があるか、確認する
                var conversion = semanticModel.ClassifyConversion(initializer.Value, variableType);
                if (!conversion.Exists || conversion.IsUserDefined)
                    return;

                // 初期化子がstringである場合
                if (constantValue.Value is string)
                {
                    // 変数の型がstringでない場合は対象外(const object s = "abc"は定数化できない)
                    if (variableType.SpecialType != SpecialType.System_String)
                        return;
                }
                else if (variableType.IsReferenceType && constantValue.Value != null)
                {
                    // それ以外の参照型は定数化できない
                    return;
                }
            }

            // ローカル宣言に対してデータフロー解析を行う
            var dataFlowAnalysis = semanticModel.AnalyzeDataFlow(localDeclaration);

            foreach (var variable in localDeclaration.Declaration.Variables)
            {
                // 宣言された各変数がほかの箇所で書き換えられている場合は、定数化できない
                var variableSymbol = semanticModel.GetDeclaredSymbol(variable);
                if (dataFlowAnalysis.WrittenOutside.Contains(variableSymbol))
                    return;
            }

            // 定数化できることを通知する
            addDiagnostic(Diagnostic.Create(Rule, node.GetLocation()));

        }
    }
}
