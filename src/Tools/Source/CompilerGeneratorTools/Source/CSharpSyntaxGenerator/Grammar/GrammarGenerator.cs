﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

// We only support grammar generation in the command line version for now which is the netcoreapp target
#if NETCOREAPP

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpSyntaxGenerator.Grammar
{
    internal static class GrammarGenerator
    {
        public static string Run(List<TreeType> types)
        {
            var rules = types.ToDictionary(n => n.Name, _ => new List<Production>());
            foreach (var type in types)
            {
                if (type.Base != null && rules.TryGetValue(type.Base, out var productions))
                    productions.Add(RuleReference(type.Name));

                if (type is Node && type.Children.Count > 0)
                {
                    // Convert rules like `a: (x | y) ...` into:
                    //
                    // a: x ...
                    //  | y ...;
                    //
                    // Note: if we have `a: (a1 | b1) ... (ax | bx) presume that that's a paired construct and generate:
                    //
                    // a: a1 ... ax
                    //  | b1 ... bx;

                    if (type.Children.First() is Field firstField && firstField.Kinds.Count > 0)
                    {
                        var originalFirstFieldKinds = firstField.Kinds.ToList();
                        if (type.Children.Count >= 2 && type.Children.Last() is Field lastField && lastField.Kinds.Count == firstField.Kinds.Count)
                        {
                            var originalLastFieldKinds = lastField.Kinds.ToList();
                            for (int i = 0; i < originalFirstFieldKinds.Count; i++)
                            {
                                firstField.Kinds = [originalFirstFieldKinds[i]];
                                lastField.Kinds = [originalLastFieldKinds[i]];
                                rules[type.Name].Add(Sequence(type.Children.Select(ToProduction)));
                            }
                        }
                        else
                        {
                            for (int i = 0; i < originalFirstFieldKinds.Count; i++)
                            {
                                firstField.Kinds = [originalFirstFieldKinds[i]];
                                rules[type.Name].Add(Sequence(type.Children.Select(ToProduction)));
                            }
                        }
                    }
                    else
                    {
                        rules[type.Name].Add(Sequence(type.Children.Select(ToProduction)));
                    }
                }
            }

            // Add some rules not present in Syntax.xml.
            AddLexicalRules(rules);

            // The grammar will bottom out with certain lexical productions. Create rules for these.
            var lexicalRules = rules.Values.SelectMany(ps => ps).SelectMany(p => p.ReferencedRules)
                .Where(r => !rules.TryGetValue(r, out var productions) || productions.Count == 0).ToArray();
            foreach (var name in lexicalRules)
                rules[name] = [new("/* see lexical specification */")];

            var seen = new HashSet<string>();

            // Define a few major sections to help keep the grammar file naturally grouped.
            List<string> majorRules = [
                "CompilationUnitSyntax",
                "MemberDeclarationSyntax",
                "TypeSyntax",
                "StatementSyntax",
                "ExpressionSyntax",
                "XmlNodeSyntax",
                "StructuredTriviaSyntax",
                // Place all syntax tokens at the end to keep them out of the way.
                "SyntaxToken",
                .. rules["SyntaxToken"].SelectMany(r => r.ReferencedRules)];

            var result = "// <auto-generated />" + Environment.NewLine + "grammar csharp;" + Environment.NewLine;

            // Handle each major section first and then walk any rules not hit transitively from them.
            foreach (var rule in majorRules.Concat(rules.Keys.OrderBy(a => a)))
                processRule(rule, ref result);

            return result;

            void processRule(string name, ref string result)
            {
                if (name != "CSharpSyntaxNode" && seen.Add(name))
                {
                    // Order the productions to keep us independent from whatever changes happen in Syntax.xml.
                    var sorted = rules[name].OrderBy(v => v);
                    result += Environment.NewLine + RuleReference(name).Text + Environment.NewLine + "  : " +
                        string.Join(Environment.NewLine + "  | ", sorted) + Environment.NewLine + "  ;" + Environment.NewLine;

                    // Now proceed in depth-first fashion through the referenced rules to keep related rules
                    // close by. Don't recurse into major-sections to help keep them separated in grammar file.
                    foreach (var production in sorted)
                        foreach (var referencedRule in production.ReferencedRules)
                            if (!majorRules.Concat(lexicalRules).Contains(referencedRule))
                                processRule(referencedRule, ref result);
                }
            }
        }

        private static void AddLexicalRules(Dictionary<string, List<Production>> rules)
        {
            addUtf8Rules();
            addTokenRules();
            addIdentifierRules();
            addRealLiteralRules();
            addNumericLiteralRules();
            addIntegerLiteralRules();
            addEscapeSequenceRules();
            addStringLiteralRules();
            addCharacterLiteralRules();

            void addUtf8Rules()
            {
                var utf8Suffix = Choice(anyCasing("U8"));
                rules.Add("Utf8StringLiteralToken", [Sequence([RuleReference("StringLiteralToken"), utf8Suffix])]);
                rules.Add("Utf8MultiLineRawStringLiteralToken", [Sequence([RuleReference("MultiLineRawStringLiteralToken"), utf8Suffix])]);
                rules.Add("Utf8SingleLineRawStringLiteralToken", [Sequence([RuleReference("SingleLineRawStringLiteralToken"), utf8Suffix])]);
            }

            void addTokenRules()
            {
                rules["SyntaxToken"].AddRange([RuleReference("IdentifierToken"), RuleReference("Keyword"), RuleReference("NumericLiteralToken"), RuleReference("CharacterLiteralToken"), RuleReference("StringLiteralToken"), RuleReference("OperatorToken"), RuleReference("PunctuationToken")]);

                var modifierWords = GetMembers<DeclarationModifiers>()
                    .Where(n => GetSyntaxKind(n + "Keyword") != SyntaxKind.None)
                    .Select(n => n.ToString().ToLower());
                rules.Add("Modifier", JoinWords(modifierWords.ToArray()));

                var keywords = JoinWords(GetMembers<SyntaxKind>().Where(k => SyntaxFacts.IsReservedKeyword(k)).Select(SyntaxFacts.GetText).Where(t => !modifierWords.Contains(t)).ToArray());
                keywords.Add(RuleReference("Modifier"));
                rules.Add("Keyword", keywords);

                var operatorTokens = GetMembers<SyntaxKind>().Where(m => SyntaxFacts.IsBinaryExpressionOperatorToken(m) || SyntaxFacts.IsPostfixUnaryExpression(m) || SyntaxFacts.IsPrefixUnaryExpression(m) || SyntaxFacts.IsAssignmentExpressionOperatorToken(m));
                rules.Add("OperatorToken", JoinWords(operatorTokens.Select(SyntaxFacts.GetText).ToArray()));

                rules.Add("PunctuationToken", JoinWords(GetMembers<SyntaxKind>()
                    .Where(m => SyntaxFacts.IsLanguagePunctuation(m) && !operatorTokens.Contains(m) && !m.ToString().StartsWith("Xml"))
                    .Select(SyntaxFacts.GetText).ToArray()));
            }

            void addIdentifierRules()
            {
                rules.Add("IdentifierToken", [Sequence([Text("@").Optional, RuleReference("IdentifierStartCharacter"), RuleReference("IdentifierPartCharacter")])]);
                rules.Add("IdentifierStartCharacter", [RuleReference("LetterCharacter"), RuleReference("UnderscoreCharacter")]);
                rules.Add("IdentifierPartCharacter", [RuleReference("LetterCharacter"), RuleReference("DecimalDigitCharacter"), RuleReference("ConnectingCharacter"), RuleReference("CombiningCharacter"), RuleReference("FormattingCharacter")]);
                rules.Add("UnderscoreCharacter", [Text("_"), new("""'\\u005' /* unicode_escape_sequence for underscore */""")]);
                rules.Add("LetterCharacter", [
                    new("""/* [\p{L}\p{Nl}] category letter, all subcategories; category number, subcategory letter */"""),
                    new("unicode_escape_sequence /* only escapes for categories L & Nl allowed */")]);

                rules.Add("CombiningCharacter", [
                    new("""/* [\p{Mn}\p{Mc}] category Mark, subcategories non-spacing and spacing combining */"""),
                    new("unicode_escape_sequence /* only escapes for categories Mn & Mc allowed */")]);

                rules.Add("DecimalDigitCharacter", [
                    new("""/* [\p{Nd}] category number, subcategory decimal digit */"""),
                    new("unicode_escape_sequence /* only escapes for category Nd allowed */")]);

                rules.Add("ConnectingCharacter", [
                    new("""/* [\p{Pc}] category Punctuation, subcategory connector */"""),
                    new("unicode_escape_sequence /* only escapes for category Pc allowed */")]);

                rules.Add("FormattingCharacter", [
                    new("""/* [\p{Cf}] category Other, subcategory format. */"""),
                    new("unicode_escape_sequence /* only escapes for category Cf allowed */")]);
            }

            void addRealLiteralRules()
            {
                var decimalDigitPlus = RuleReference("DecimalDigit").OneOrMany;
                var exponentPart = RuleReference("ExponentPart");
                var exponentPartOpt = exponentPart.Optional;
                var realTypeSuffix = RuleReference("RealTypeSuffix");
                var realTypeSuffixOpt = realTypeSuffix.Optional;

                rules.Add("RealLiteralToken", [
                    Sequence([decimalDigitPlus, Text("."), decimalDigitPlus, exponentPartOpt, realTypeSuffixOpt]),
                    Sequence([Text("."), decimalDigitPlus, exponentPartOpt, realTypeSuffixOpt]),
                    Sequence([decimalDigitPlus, exponentPart, realTypeSuffixOpt]),
                    Sequence([decimalDigitPlus, realTypeSuffix]),
                ]);

                rules.Add("ExponentPart", [Sequence([Choice(anyCasing("E")), Choice([Text("+"), Text("-")]).Optional, decimalDigitPlus])]);
                rules.Add("RealTypeSuffix", [.. anyCasing("F"), .. anyCasing("D"), .. anyCasing("M")]);
            }

            void addNumericLiteralRules()
            {
                rules.Add("NumericLiteralToken", [RuleReference("IntegerLiteralToken"), RuleReference("RealLiteralToken")]);
            }

            void addIntegerLiteralRules()
            {
                var decimalDigit = RuleReference("DecimalDigit");
                var decimalDigitPlus = decimalDigit.OneOrMany;
                var integerTypeSuffixOpt = RuleReference("IntegerTypeSuffix").Optional;

                rules.Add("IntegerLiteralToken", [RuleReference("DecimalIntegerLiteralToken"), RuleReference("HexadecimalIntegerLiteralToken")]);
                rules.Add("DecimalIntegerLiteralToken", [Sequence([decimalDigitPlus, integerTypeSuffixOpt])]);
                rules.Add("IntegerTypeSuffix", [.. anyCasing("U"), .. anyCasing("L"), .. anyCasing("UL"), .. anyCasing("LU")]);
                rules.Add("DecimalDigit", [.. productionRange('0', '9')]);
                rules.Add("HexadecimalDigit", [decimalDigit, .. productionRange('A', 'F'), .. productionRange('a', 'f')]);
                rules.Add("HexadecimalIntegerLiteralToken", [Sequence([Choice([Text("0x"), Text("0X")]), RuleReference("HexadecimalDigit").OneOrMany, integerTypeSuffixOpt])]);
            }

            void addEscapeSequenceRules()
            {
                var hexDigit = RuleReference("HexadecimalDigit");
                var hexDigitOpt = hexDigit.Optional;

                rules.Add("SimpleEscapeSequence", [Text(@"\'"), Text(@"\"""), Text(@"\\"), Text(@"\0"), Text(@"\a"), Text(@"\b"), Text(@"\f"), Text(@"\n"), Text(@"\r"), Text(@"\t"), Text(@"\v")]);
                rules.Add("HexadecimalEscapeSequence", [Sequence([Text(@"\x"), hexDigit, .. repeat(hexDigitOpt, 3)])]);
                rules.Add("UnicodeEscapeSequence", [Sequence([Text(@"\u"), .. repeat(hexDigit, 4)]), Sequence([Text(@"\U"), .. repeat(hexDigit, 8)])]);
            }

            void addStringLiteralRules()
            {
                rules.Add("StringLiteralToken", [RuleReference("RegularStringLiteralToken"), RuleReference("VerbatimStringLiteralToken")]);

                rules.Add("RegularStringLiteralToken", [Sequence([Text("\""), RuleReference("RegularStringLiteralCharacter").ZeroOrMany, Text("\"")])]);
                rules.Add("RegularStringLiteralCharacter", [RuleReference("SingleRegularStringLiteralCharacter"), RuleReference("SimpleEscapeSequence"), RuleReference("HexadecimalEscapeSequence"), RuleReference("UnicodeEscapeSequence")]);
                rules.Add("SingleRegularStringLiteralCharacter", [new("""/* ~["\\\u000D\u000A\u0085\u2028\u2029] anything but ", \, and new_line_character */""")]);

                rules.Add("VerbatimStringLiteralToken", [Sequence([Text("@\""), RuleReference("VerbatimStringLiteralCharacter").ZeroOrMany, Text("\"")])]);
                rules.Add("VerbatimStringLiteralCharacter", [RuleReference("SingleVerbatimStringLiteralCharacter"), RuleReference("QuoteEscapeSequence")]);
                rules.Add("SingleVerbatimStringLiteralCharacter", [new("/* anything but quotation mark (U+0022) */")]);

                rules.Add("QuoteEscapeSequence", [Text("\"\"")]);

                rules.Add("InterpolatedMultiLineRawStringStartToken", [new(""""'$'+ '"""' '"'*"""")]);
                rules.Add("InterpolatedRawStringEndToken", [new(""""'"""' '"'* /* must match number of quotes in raw_string_start_token */"""")]);
                rules.Add("InterpolatedSingleLineRawStringStartToken", [new(""""'$'+ '"""' '"'*"""")]);
            }

            void addCharacterLiteralRules()
            {
                rules.Add("CharacterLiteralToken", [Sequence([Text("'"), RuleReference("Character"), Text("'")])]);
                rules.Add("Character", [RuleReference("SingleCharacter"), RuleReference("SimpleEscapeSequence"), RuleReference("HexadecimalEscapeSequence"), RuleReference("UnicodeEscapeSequence")]);
                rules.Add("SingleCharacter", [new("""/* ~['\\\u000D\u000A\u0085\u2028\u2029] anything but ', \\, and new_line_character */""")]);
            }

            IEnumerable<Production> productionRange(char start, char end)
            {
                for (char c = start; c <= end; c++)
                    yield return Text($"{c}");
            }

            IEnumerable<Production> repeat(Production production, int count)
                => Enumerable.Repeat(production, count);

            IEnumerable<Production> anyCasing(string value)
            {
                var array = value.Select(c => char.IsLetter(c) ? [char.ToUpperInvariant(c), char.ToLowerInvariant(c)] : new[] { c }).ToArray();

                var indices = new int[array.Length];
                var builder = new StringBuilder();
                do
                {
                    for (var i = 0; i < value.Length; i++)
                        builder.Append(array[i][indices[i]]);

                    yield return Text($"{builder}");
                    builder.Clear();

                    for (var i = 0; i < indices.Length; i++)
                    {
                        if (++indices[i] < array[i].Length)
                            break;

                        indices[i] = 0;
                    }
                }
                while (!indices.All(i => i == 0));
            }
        }

        private static List<Production> JoinWords(params string[] strings)
            => strings.Select(s => new Production($"""'{Escape(s)}'""")).ToList();

        private static string Escape(string s)
            => s.Replace(@"\", @"\\").Replace("'", @"\'");

        private static Production Text(string value)
            => new($"'{Escape(value)}'");

        private static Production Join(IEnumerable<Production> productions, string delim)
            => new(string.Join(delim, productions.Where(p => p.Text.Length > 0)), productions.SelectMany(p => p.ReferencedRules));

        private static Production ToProduction(TreeTypeChild child)
            => child switch
            {
                Choice c => Choice(c.Children.Select(ToProduction)).Suffix("?", when: c.Optional),
                Sequence s => Sequence(s.Children.Select(ToProduction)).Parenthesize(),
                Field f => HandleField(f).Suffix("?", when: f.IsOptional),
                _ => throw new InvalidOperationException(),
            };

        private static Production Choice(IEnumerable<Production> productions, bool parenthesize = true)
            => Join(productions, " | ").Parenthesize(parenthesize);

        private static Production Sequence(IEnumerable<Production> productions)
            => Join(productions, " ");

        private static Production HandleField(Field field)
            // 'bool' fields are for a few properties we generate on DirectiveTrivia. They're not
            // relevant to the grammar, so we just return an empty production to ignore them.
            => field.Type == "bool" ? new Production("") :
               field.Type == "CSharpSyntaxNode" ? RuleReference(field.Kinds.Single().Name + "Syntax") :
               field.Type.StartsWith("SeparatedSyntaxList") ? HandleSeparatedList(field, field.Type[("SeparatedSyntaxList".Length + 1)..^1]) :
               field.Type.StartsWith("SyntaxList") ? HandleList(field, field.Type[("SyntaxList".Length + 1)..^1]) :
               field.IsToken ? HandleTokenField(field) : RuleReference(field.Type);

        private static Production HandleSeparatedList(Field field, string elementType)
            => RuleReference(elementType).Suffix(" (',' " + RuleReference(elementType) + ")")
                .Suffix("*", when: field.MinCount < 2).Suffix("+", when: field.MinCount >= 2)
                .Suffix(" ','?", when: field.AllowTrailingSeparator)
                .Parenthesize(when: field.MinCount == 0).Suffix("?", when: field.MinCount == 0);

        private static Production HandleList(Field field, string elementType)
            => (elementType != "SyntaxToken" ? RuleReference(elementType) :
                field.Name == "Commas" ? Text(",") :
                field.Name == "Modifiers" ? RuleReference("Modifier") :
                field.Name == "TextTokens" ? RuleReference(nameof(SyntaxKind.XmlTextLiteralToken)) : RuleReference(elementType))
                    .Suffix(field.MinCount == 0 ? "*" : "+");

        private static Production HandleTokenField(Field field)
            => field.Kinds.Count == 0
                ? HandleTokenName(field.Name)
                : Choice(field.Kinds.Select(k => HandleTokenName(k.Name)), parenthesize: field.Kinds.Count >= 2);

        private static Production HandleTokenName(string tokenName)
            => GetSyntaxKind(tokenName) is var kind && kind == SyntaxKind.None ? RuleReference("SyntaxToken") :
               SyntaxFacts.GetText(kind) is var text && text != "" ? Text(text) :
               tokenName.StartsWith("EndOf") ? new Production("") :
               tokenName.StartsWith("Omitted") ? new Production("/* epsilon */") : RuleReference(tokenName);

        private static SyntaxKind GetSyntaxKind(string name)
            => GetMembers<SyntaxKind>().Where(k => k.ToString() == name).SingleOrDefault();

        private static IEnumerable<TEnum> GetMembers<TEnum>() where TEnum : struct, Enum
            => (IEnumerable<TEnum>)Enum.GetValues(typeof(TEnum));

        private static Production RuleReference(string name)
            => new(
                s_normalizationRegex.Replace(name.EndsWith("Syntax") ? name[..^"Syntax".Length] : name, "_").ToLower(),
                ImmutableArray.Create(name));

        // Converts a PascalCased name into snake_cased name.
        private static readonly Regex s_normalizationRegex = new(
            "(?<=[A-Z])(?=[A-Z][a-z0-9]) | (?<=[^A-Z])(?=[A-Z]) | (?<=[A-Za-z0-9])(?=[^A-Za-z0-9])",
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);
    }

    internal readonly struct Production(
        string text, IEnumerable<string> referencedRules = null) : IComparable<Production>
    {
        public readonly string Text = text;
        public readonly ImmutableArray<string> ReferencedRules = referencedRules?.ToImmutableArray() ?? ImmutableArray<string>.Empty;

        public override string ToString() => Text;
        public int CompareTo(Production other) => StringComparer.OrdinalIgnoreCase.Compare(this.Text, other.Text);
        public Production Prefix(string prefix) => new(prefix + this, ReferencedRules);
        public Production Suffix(string suffix, bool when = true) => when ? new(this + suffix, ReferencedRules) : this;
        public Production Parenthesize(bool when = true) => when ? Prefix("(").Suffix(")") : this;
        public Production Optional => Suffix("?");
        public Production ZeroOrMany => Suffix("*");
        public Production OneOrMany => Suffix("+");
    }
}

namespace Microsoft.CodeAnalysis
{
    internal static class GreenNode
    {
        internal const int ListKind = 1; // See SyntaxKind.
    }
}

#endif
