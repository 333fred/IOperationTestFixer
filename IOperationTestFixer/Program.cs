using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IOperationTestFixer
{
    class Program
    {
        // Method Name -> New test contents
        private static Dictionary<string, string> replaceDetails = new Dictionary<string, string>();

        static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Usage: IOperationTestFixer <Path to failure file> <path to compilers.sln>");
                Console.ReadKey();
                return 1;
            }

            ParseFailures(args[0]);
            DoReplace(args[1]).GetAwaiter().GetResult();
            Console.WriteLine("Completed Test Fixes");
            return 0;
        }

        static void ParseFailures(string failureFilePath)
        {
            Console.WriteLine("Parsing test results");
            string MethodName = string.Empty;

            int flag = 0;
            StringBuilder sb = new StringBuilder();
            var lines = System.IO.File.ReadLines(failureFilePath);

            foreach (string line in lines)
            {
                if (line.EndsWith("FAILED:"))
                {
                    MethodName = line.Replace("'", "").Replace(" FAILED:", "").Trim();
                }
                if (line.StartsWith("Actual:") && !line.StartsWith("Actual:   True"))
                {
                    flag = 1;
                    sb = new StringBuilder();

                }
                if (flag > 0)
                {
                    sb.AppendLine(line);
                }
                if (line == @"" && flag == 1)
                {
                    var replaceText = sb.ToString();
                    replaceText = replaceText.Replace("Actual:", "");
                    replaceText = replaceText.Trim();
                    replaceDetails[MethodName] = replaceText;
                    flag = 0;
                    sb = new StringBuilder();
                }
            }
        }

        static async Task DoReplace(string solutionFile)
        {
            Console.WriteLine("Creating workspace");
            var workspace = MSBuildWorkspace.Create();
            Console.WriteLine($"Opening {solutionFile}");
            var solution = await workspace.OpenSolutionAsync(solutionFile);

            Console.WriteLine("Scanning for test methods to fix");
            var projects = solution.Projects.Where(p => p.HasDocuments).SelectMany(p => p.Documents);
            var docs = solution.Projects.Where(p => p.HasDocuments)
                                             .Where(p => p.Name.EndsWith("Test"))
                                             .SelectMany(p => p.Documents)
                                             .Where(d => d.Name.EndsWith(".cs") || d.Name.EndsWith(".vb"))
                                             .Where(d => !d.Name.EndsWith("Designer.cs") || !d.Name.EndsWith("Designer.vb"))
                                             .Where(d => d.SupportsSemanticModel);

            Console.WriteLine("Test files retrieved. Starting fix pass.");
            foreach (var doc in docs)
            {
                if (replaceDetails.Count == 0) break;
                var semanticModel = await doc.GetSemanticModelAsync();
                SyntaxNode root = await doc.GetSyntaxRootAsync();
                SyntaxNode fixedRoot;
                if (root.Language == LanguageNames.CSharp)
                {
                    var fixer = new CSharpFixer(semanticModel);
                    fixedRoot = fixer.Visit(root);
                }
                else
                {
                    var fixer = new VBFixer(semanticModel);
                    fixedRoot = fixer.Visit(root);
                }

                if (fixedRoot != root)
                {
                    Console.WriteLine($"Committing changes to {doc.FilePath}");
                    File.WriteAllText(doc.FilePath, fixedRoot.ToFullString());
                }
            }
        }

        private static string GetMethodName(IMethodSymbol symbol) =>
                $"{symbol.ContainingType.ToDisplayString()}.{symbol.Name.ToString().Replace("(", "").Replace(")", "")}".Trim();

        private class CSharpFixer : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
        {
            private SemanticModel _semanticModel;

            public CSharpFixer(SemanticModel semanticModel)
            {
                _semanticModel = semanticModel;
            }

            public override SyntaxNode VisitLocalDeclarationStatement(Microsoft.CodeAnalysis.CSharp.Syntax.LocalDeclarationStatementSyntax node)
            {
                if (replaceDetails.Count == 0) return node;
                var enclosingSymbol = _semanticModel.GetEnclosingSymbol(node.SpanStart);
                if (!(enclosingSymbol is IMethodSymbol methodSymbol))
                {
                    return node;
                }
                var methodName = GetMethodName(methodSymbol);
                if (!replaceDetails.ContainsKey(methodName))
                {
                    return node;
                }

                if (node.Declaration.Variables.Count > 1 ||
                    node.Declaration.Variables.Single().Identifier.ValueText != "expectedOperationTree")
                {
                    return node;
                }

                Console.WriteLine($"Fixing {methodSymbol.Name}");
                var oldInitializer = node.Declaration.Variables.Single().Initializer.Value;
                var newInitializer = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.LiteralExpression(
                                                Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression,
                                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(
                                                    $@"@""
{replaceDetails[methodName].Replace("\"", "\"\"")}
""",
                                                    Environment.NewLine));

                replaceDetails.Remove(methodName);
                return node.ReplaceNode(oldInitializer, newInitializer);
            }
        }

        private class VBFixer : Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxRewriter
        {
            private SemanticModel _semanticModel;
            public VBFixer(SemanticModel semanticModel)
            {
                _semanticModel = semanticModel;
            }

            public override SyntaxNode VisitLocalDeclarationStatement(Microsoft.CodeAnalysis.VisualBasic.Syntax.LocalDeclarationStatementSyntax node)
            {
                if (replaceDetails.Count == 0) return node;
                var enclosingSymbol = _semanticModel.GetEnclosingSymbol(node.SpanStart);
                if (!(enclosingSymbol is IMethodSymbol methodSymbol))
                {
                    return node;
                }
                var methodName = GetMethodName(methodSymbol);
                if (!replaceDetails.ContainsKey(methodName))
                {
                    return node;
                }

                if (node.Declarators.Count > 1 ||
                    node.Declarators.First().Names.Count > 1 ||
                    node.Declarators.First().Names.Single().Identifier.ValueText != "expectedOperationTree")
                {
                    return node;
                }

                Console.WriteLine($"Fixing {methodSymbol.Name}");
                var oldInitializer = (Microsoft.CodeAnalysis.VisualBasic.Syntax.XmlCDataSectionSyntax)
                    ((Microsoft.CodeAnalysis.VisualBasic.Syntax.MemberAccessExpressionSyntax)node.Declarators.Single().Initializer.Value).Expression;
                var cdataOpen = oldInitializer.BeginCDataToken;
                var cdataClose = oldInitializer.EndCDataToken;
                var xmlTokens = new List<SyntaxToken>
                {
                    Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory.XmlTextLiteralToken(Environment.NewLine, "")
                };
                foreach (var line in replaceDetails[methodName].Split('\n'))
                {
                    var trimmedLine = line.Replace("\r", "") + Environment.NewLine;
                    xmlTokens.Add(Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory.XmlTextLiteralToken(trimmedLine, Environment.NewLine));
                }
                var newInitializer = Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory.XmlCDataSection(
                    cdataOpen,
                    Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory.TokenList(xmlTokens),
                    cdataClose);

                replaceDetails.Remove(methodName);
                return node.ReplaceNode(oldInitializer, newInitializer);
            }
        }
    }
}
