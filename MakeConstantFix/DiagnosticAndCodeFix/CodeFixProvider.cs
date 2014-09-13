﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Formatting;

namespace DiagnosticAndCodeFix
{
    [ExportCodeFixProvider(DiagnosticAnalyzer.DiagnosticId, LanguageNames.CSharp)]
    public class CodeFixProvider : ICodeFixProvider
    {
        public IEnumerable<string> GetFixableDiagnosticIds()
        {
            return new[] { DiagnosticAnalyzer.DiagnosticId };
        }

        public async Task<IEnumerable<CodeAction>> GetFixesAsync(Document document, TextSpan span, IEnumerable<Diagnostic> diagnostics, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var diagnosticSpan = diagnostics.First().Location.SourceSpan;

            var declaration = root.FindToken(diagnosticSpan.Start).Parent.AncestorsAndSelf()
                .OfType<LocalDeclarationStatementSyntax>().First();

            // 定数化する、というFixをActionを作成
            return new[] { CodeAction.Create("Make constant", c => MakeConstAsync(document, declaration, c)) };
        }

        private async Task<Document> MakeConstAsync(Document document,
            LocalDeclarationStatementSyntax localDeclaration,
            CancellationToken cancellationToken)
        {
            // 宣言文から前方のTrivia（空白など）を削除する
            var firstToken = localDeclaration.GetFirstToken();
            var leadingTrivia = firstToken.LeadingTrivia;
            var trimmedLocal = localDeclaration.ReplaceToken(
                firstToken, firstToken.WithLeadingTrivia(SyntaxTriviaList.Empty));

            // 前方のTriviaがあるconstトークンを作成する
            var constToken = SyntaxFactory.Token(leadingTrivia, SyntaxKind.ConstKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.ElasticMarker));

            // 修飾子リストにconstトークンを挿入し、変更後の修飾子リストを作成する
            var newModifiers = trimmedLocal.Modifiers.Insert(0, constToken);

            // 宣言の型がvarの場合、型名を推測して変更する
            var variableDeclaration = localDeclaration.Declaration;
            var variableTypeName = variableDeclaration.Type;
            if (variableTypeName.IsVar)
            {
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

                // 特殊ケース：varが何かのエイリアスである場合
                // (例: using var = System.String とされている場合など)
                var aliasInfo = semanticModel.GetAliasInfo(variableTypeName);
                if (aliasInfo == null)
                {
                    // varが型推論としてのキーワードである時
                    // varに対応する型を取得する
                    var type = semanticModel.GetTypeInfo(variableTypeName).ConvertedType;

                    // 特殊ケース：varがvarという名前の型であるか確認する
                    if (type.Name != "var")
                    {
                        // 推測された型に対するTypeSyntaxを作成する
                        // （varの前後のTriviaが保持されるよう、それぞれ取得して渡す）
                        var typeName = SyntaxFactory.ParseTypeName(type.ToDisplayString())
                            .WithLeadingTrivia(variableTypeName.GetLeadingTrivia())
                            .WithTrailingTrivia(variableTypeName.GetTrailingTrivia());

                        // 型名を単純化するようにアノテーションを付与する
                        var simplifiedTypeName = typeName.WithAdditionalAnnotations(Simplifier.Annotation);

                        // 宣言時の型を置き換える
                        variableDeclaration = variableDeclaration.WithType(simplifiedTypeName);
                    }
                }
            }

            // ローカル変数の宣言を作成する
            var newLocal = trimmedLocal.WithModifiers(newModifiers)
                                        .WithDeclaration(variableDeclaration);

            // 整形用のアノテーションを付与する
            var formattedLocal = 
                newLocal.WithAdditionalAnnotations(Formatter.Annotation);

            // 古い宣言文を新しいものに置き換える
            var oldRoot = await document.GetSyntaxRootAsync(cancellationToken);
            var newRoot = oldRoot.ReplaceNode(localDeclaration, formattedLocal);

            // 変更後の木を返す
            return document.WithSyntaxRoot(newRoot);
        }


    }
}