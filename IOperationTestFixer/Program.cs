// Copyright (c) Fredric Silberberg.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace IOperationTestFixer
{
    class Program
    {
        // Method Name -> New test contents
        private static Dictionary<string, string> replaceDetails = new Dictionary<string, string>();
        private static ImmutableArray<String> s_validIdentifierNames = ImmutableArray.Create("expectedOperationTree", "expectedFlowGraph", "expectedGraph");

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: IOperationTestFixer <Path to failure file> <paths to recursively search for cs or vb files to fix>");
                Console.ReadKey();
                return 1;
            }

            ParseFailures(args[0]);
            DoReplace(args.Skip(1));
            Console.WriteLine("Completed Test Fixes");
            return 0;
        }

        private enum ParseState
        {
            NotParsing,
            FindingActual,
            ParsingActual,
            SkippingException
        }

        static void ParseFailures(string failureFilePath)
        {
            Console.WriteLine("Parsing test results");
            string MethodName = string.Empty;

            var currentState = ParseState.NotParsing;
            string lastLine = null;
            StringBuilder sb = null;
            var lines = File.ReadLines(failureFilePath);

            foreach (string line in lines)
            {
                switch (currentState)
                {
                    case ParseState.NotParsing:
                        if (line.EndsWith("FAILED:"))
                        {
                            MethodName = line.Replace("'", "").Replace(" FAILED:", "").Trim();
                            currentState = ParseState.FindingActual;
                        }
                        break;

                    case ParseState.FindingActual:
                        if (line.StartsWith("Actual:") && !line.StartsWith("Actual:   True"))
                        {
                            Debug.Assert(sb == null);
                            sb = new StringBuilder();
                            currentState = ParseState.ParsingActual;
                        }
                        else if (CheckForStacktrace())
                        {
                            Console.WriteLine($"Could not parse test result for {MethodName}");
                            ResetParsing();
                        }
                        break;

                    case ParseState.ParsingActual:
                        if (CheckForStacktrace())
                        {
                            var replaceText = sb.ToString().Replace("Actual:", "").Trim();
                            replaceDetails[MethodName] = replaceText;
                            ResetParsing();
                        }
                        else
                        {
                            // Lag a line behind because we don't want to append the empty line before
                            // the exception stacktrace to the output
                            if (lastLine != null)
                            {
                                sb.AppendLine(lastLine);
                            }
                            lastLine = line;
                        }
                        break;

                }
                bool CheckForStacktrace() => line.Trim().StartsWith("Exception stacktrace");
                void ResetParsing()
                {
                    currentState = ParseState.NotParsing;
                    sb = null;
                    lastLine = null;
                }
            }
        }

        static void DoReplace(IEnumerable<string> folders)
        {
            Console.WriteLine("Searching for files to fix");
            var fileBuilder = ImmutableArray.CreateBuilder<string>();
            foreach (var folder in folders)
            {
                fileBuilder.AddRange(Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories));
                fileBuilder.AddRange(Directory.EnumerateFiles(folder, "*.vb", SearchOption.AllDirectories));
            }

            var filePaths = fileBuilder.ToImmutable();
            Console.WriteLine($"Found {filePaths.Length} files. Parsing into syntax trees.");

            var syntaxTreesBuilder = ImmutableArray.CreateBuilder<(SyntaxTree tree, string path)>();
            foreach (var filePath in filePaths)
            {
                if (filePath.EndsWith("cs"))
                {
                    syntaxTreesBuilder.Add((tree: CSharpSyntaxTree.ParseText(File.ReadAllText(filePath)), path: filePath));
                }
                else
                {
                    syntaxTreesBuilder.Add((tree: VisualBasicSyntaxTree.ParseText(File.ReadAllText(filePath)), path: filePath));
                }
            }

            Console.WriteLine("Parsed syntax trees. Fixing tests");

            foreach (var (tree, path) in syntaxTreesBuilder.ToImmutable())
            {
                if (replaceDetails.Count == 0) break;
                SyntaxNode root = tree.GetRoot();
                SyntaxNode fixedRoot;
                if (root.Language == LanguageNames.CSharp)
                {
                    var fixer = new CSharpFixer();
                    fixedRoot = fixer.Visit(root);
                }
                else
                {
                    var fixer = new VBFixer();
                    fixedRoot = fixer.Visit(root);
                }

                if (fixedRoot != root)
                {
                    Console.WriteLine($"Committing changes to {path}");
                    Encoding encoding;
                    using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
                    {
                        reader.Peek();
                        encoding = reader.CurrentEncoding;
                    }
                    File.WriteAllText(path, fixedRoot.ToFullString(), encoding);
                }
            }
        }

        private static string GetMethodName(IMethodSymbol symbol) =>
                $"{symbol.ContainingType.ToDisplayString()}.{symbol.Name.ToString().Replace("(", "").Replace(")", "")}".Trim();

        private class CSharpFixer : CSharpSyntaxRewriter
        {
            private string _methodName = null;
            private string _namespace = null;
            private string _class = null;

            public CSharpFixer()
            {
            }

            public override SyntaxNode VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
            {
                _namespace = node.Name.ToFullString().Trim();
                return base.VisitNamespaceDeclaration(node);
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                _class = node.Identifier.Text;
                return base.VisitClassDeclaration(node);
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                if (replaceDetails.Count == 0) return node;
                var name = $"{_namespace}.{_class}.{node.Identifier.GetIdentifierText()}";
                if (!replaceDetails.ContainsKey(name))
                {
                    return node;
                }

                _methodName = name;

                return base.VisitMethodDeclaration(node);
            }

            public override SyntaxNode VisitLocalDeclarationStatement(Microsoft.CodeAnalysis.CSharp.Syntax.LocalDeclarationStatementSyntax node)
            {
                if (_methodName == null) return node;
                if (node.Declaration.Variables.Count > 1)
                {
                    return node;
                }
                var identifier = node.Declaration.Variables.Single().Identifier.ValueText;
                if (!s_validIdentifierNames.Contains(identifier))
                {
                    return node;
                }

                Console.WriteLine($"Fixing {_methodName}");
                var oldInitializer = node.Declaration.Variables.Single().Initializer.Value;
                var newInitializer = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.LiteralExpression(
                                                Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression,
                                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(
                                                    $@"@""
{replaceDetails[_methodName].Replace("\"", "\"\"")}
""",
                                                    Environment.NewLine));

                replaceDetails.Remove(_methodName);
                _methodName = null;
                return node.ReplaceNode(oldInitializer, newInitializer);
            }
        }

        private class VBFixer : VisualBasicSyntaxRewriter
        {
            private string _methodName;
            private string _namespace;
            private string _class;

            public VBFixer()
            {
            }

            public override SyntaxNode VisitNamespaceBlock(NamespaceBlockSyntax node)
            {
                _namespace = node.NamespaceStatement.Name.ToFullString().Trim();
                return base.VisitNamespaceBlock(node);
            }

            public override SyntaxNode VisitClassBlock(ClassBlockSyntax node)
            {
                _class = node.ClassStatement.Identifier.Text;
                return base.VisitClassBlock(node);
            }

            public override SyntaxNode VisitMethodBlock(MethodBlockSyntax node)
            {
                if (replaceDetails.Count == 0) return node;
                var name = $"{_namespace}.{_class}.{node.SubOrFunctionStatement.Identifier.GetIdentifierText()}";
                if (!replaceDetails.ContainsKey(name))
                {
                    return node;
                }

                _methodName = name;
                return base.VisitMethodBlock(node);
            }

            public override SyntaxNode VisitLocalDeclarationStatement(Microsoft.CodeAnalysis.VisualBasic.Syntax.LocalDeclarationStatementSyntax node)
            {
                if (_methodName == null) return node;

                if (node.Declarators.Count > 1 ||
                    node.Declarators.First().Names.Count > 1)
                {
                    return node;
                }
                var identifier = node.Declarators.First().Names.Single().Identifier.ValueText;
                if (s_validIdentifierNames.Contains(identifier))
                {
                    return node;
                }

                Console.WriteLine($"Fixing {_methodName}");
                var oldInitializer = (Microsoft.CodeAnalysis.VisualBasic.Syntax.XmlCDataSectionSyntax)
                    ((Microsoft.CodeAnalysis.VisualBasic.Syntax.MemberAccessExpressionSyntax)node.Declarators.Single().Initializer.Value).Expression;
                var cdataOpen = oldInitializer.BeginCDataToken;
                var cdataClose = oldInitializer.EndCDataToken;
                var xmlTokens = new List<SyntaxToken>
                {
                    Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory.XmlTextLiteralToken(Environment.NewLine, "")
                };
                foreach (var line in replaceDetails[_methodName].Split('\n'))
                {
                    var trimmedLine = line.Replace("\r", "") + Environment.NewLine;
                    xmlTokens.Add(Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory.XmlTextLiteralToken(trimmedLine, Environment.NewLine));
                }
                var newInitializer = Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory.XmlCDataSection(
                    cdataOpen,
                    Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory.TokenList(xmlTokens),
                    cdataClose);

                replaceDetails.Remove(_methodName);
                _methodName = null;
                return node.ReplaceNode(oldInitializer, newInitializer);
            }
        }
    }
}
