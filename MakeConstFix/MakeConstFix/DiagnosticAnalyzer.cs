using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MakeConstFix
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class MakeConstFixAnalyzer : DiagnosticAnalyzer
    {
        public const string MakeConstDiagnosticId = "MakeConstFix";

        //// NOTE: リソースファイルを用いることで、多言語対応も可能
        //
        //private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AnalyzerTitle), Resources.ResourceManager, typeof(Resources));
        //private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AnalyzerMessageFormat), Resources.ResourceManager, typeof(Resources));
        //private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.AnalyzerDescription), Resources.ResourceManager, typeof(Resources));
        //private const string Category = "Naming";
        //private static DiagnosticDescriptor MakeConstRule = new DiagnosticDescriptor(DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, description: Description);

        public static readonly DiagnosticDescriptor MakeConstRule = new DiagnosticDescriptor(MakeConstDiagnosticId,
                                                                                     "定数化",
                                                                                     "定数化できます",
                                                                                     "Usage",
                                                                                     DiagnosticSeverity.Warning,
                                                                                     isEnabledByDefault: true);
        // この診断に関する情報（カテゴリや警告レベル）を宣言
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(MakeConstRule); } }

        public override void Initialize(AnalysisContext context)
        {
            // このアナライザーがVisual Studioから受け取る情報を宣言
            context.RegisterSyntaxNodeAction(AnalyzeNode, SyntaxKind.LocalDeclarationStatement);
        }

        private static bool CanBeMadeConst(LocalDeclarationStatementSyntax localDeclaration, SemanticModel semanticModel)
        {
            // すでにconstキーワードが修飾子に含まれるものは除外する
            if (localDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword))
            {
                return false;
            }

            // 複数の変数が同一ステートメントで宣言されている場合がある
            foreach (var variable in localDeclaration.Declaration.Variables)
            {
                // int x,y = 0; は定数化不可能 -> Initializerを確認する
                var initializer = variable.Initializer;
                if (initializer == null)
                {
                    return false;
                }

                // int x = somefunc(); は定数化不可能 -> コンパイル時定数であるか確認する
                var constantValue = semanticModel.GetConstantValue(initializer.Value);
                if (!constantValue.HasValue)
                {
                    return false;
                }

                var variableTypeName = localDeclaration.Declaration.Type;
                var variableType = semanticModel.GetTypeInfo(variableTypeName).ConvertedType;

                // 初期化子の値が宣言文との型と互換性があるか、確認する
                var conversion = semanticModel.ClassifyConversion(initializer.Value, variableType);
                if (!conversion.Exists || conversion.IsUserDefined)
                {
                    return false;
                }

                // 特別な場合を考える
                // * 初期化子がstringである場合 -> 型はSystem.Stringである必要がある
                // * 定数がnullの場合には、必ず型は参照型である必要がある
                if (constantValue.Value is string)
                {
                    if (variableType.SpecialType != SpecialType.System_String)
                    {
                        return false;
                    }
                }
                else if (variableType.IsReferenceType && constantValue.Value != null)
                {
                    return false;
                }
            }

            // ローカル宣言に対してデータフロー解析を行う
            var dataFlowAnalysis = semanticModel.AnalyzeDataFlow(localDeclaration);

            // 宣言された各変数がほかの箇所で書き換えられている場合は、定数化できない
            foreach (var variable in localDeclaration.Declaration.Variables)
            {
                var variableSymbol = semanticModel.GetDeclaredSymbol(variable);
                if (dataFlowAnalysis.WrittenOutside.Contains(variableSymbol))
                {
                    return false;
                }
            }

            return true;
        }

        private void AnalyzeNode(SyntaxNodeAnalysisContext context)
        {
            if (CanBeMadeConst((LocalDeclarationStatementSyntax)context.Node, context.SemanticModel))
            {
                // 定数化できることを通知する
                context.ReportDiagnostic(Diagnostic.Create(MakeConstRule, context.Node.GetLocation()));
            }
        }
    }
}
