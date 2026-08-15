using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoteManager.App.Services;

public enum NoteSearchMode
{
    Strict,
    BestMatch
}

public enum NoteSearchField
{
    Any,
    Name,
    Tag,
    Path,
    Body
}

public abstract record NoteSearchExpression;

public sealed record NoteSearchTerm(
    string Text,
    NoteSearchField Field,
    bool IsPhrase,
    bool IsMatchAll = false) : NoteSearchExpression;

public sealed record NoteSearchAnd(
    NoteSearchExpression Left,
    NoteSearchExpression Right) : NoteSearchExpression;

public sealed record NoteSearchOr(
    NoteSearchExpression Left,
    NoteSearchExpression Right) : NoteSearchExpression;

public sealed record NoteSearchNot(
    NoteSearchExpression Operand) : NoteSearchExpression;

public sealed record ParsedNoteSearchQuery(
    NoteSearchMode Mode,
    NoteSearchExpression? Root,
    IReadOnlyList<NoteSearchExpression> RequiredExpressions,
    IReadOnlyList<NoteSearchExpression> ExcludedExpressions,
    string ExpressionText)
{
    public bool IsEmpty => Root is null;
}

public sealed record NoteSearchParseResult(
    ParsedNoteSearchQuery? Query,
    string? Error,
    int ErrorPosition)
{
    public bool IsValid => Query is not null && Error is null;
}

public static partial class NoteSearchQueryParser
{
    public static NoteSearchParseResult Parse(string? searchText)
    {
        var source = searchText?.Trim() ?? string.Empty;
        var mode = NoteSearchMode.Strict;
        var expressionOffset = 0;

        if (source.StartsWith("all:", StringComparison.OrdinalIgnoreCase))
        {
            expressionOffset = 4;
        }
        else if (source.StartsWith("best:", StringComparison.OrdinalIgnoreCase))
        {
            mode = NoteSearchMode.BestMatch;
            expressionOffset = 5;
        }
        else if (source.StartsWith('='))
        {
            expressionOffset = 1;
        }
        else if (source.StartsWith('~'))
        {
            mode = NoteSearchMode.BestMatch;
            expressionOffset = 1;
        }

        var expressionText = source[expressionOffset..].Trim();
        if (expressionText.Length == 0)
        {
            return new NoteSearchParseResult(
                new ParsedNoteSearchQuery(
                    mode,
                    Root: null,
                    RequiredExpressions: [],
                    ExcludedExpressions: [],
                    expressionText),
                Error: null,
                ErrorPosition: -1);
        }

        try
        {
            var tokens = Tokenize(expressionText);
            tokens = InsertImplicitOperators(tokens, mode);
            var parser = new Parser(tokens);
            var root = parser.ParseExpression();
            parser.RequireEnd();
            return new NoteSearchParseResult(
                new ParsedNoteSearchQuery(
                    mode,
                    root,
                    parser.RequiredExpressions.ToArray(),
                    parser.ExcludedExpressions.ToArray(),
                    expressionText),
                Error: null,
                ErrorPosition: -1);
        }
        catch (SearchSyntaxException exception)
        {
            return new NoteSearchParseResult(
                Query: null,
                exception.Message,
                exception.Position);
        }
    }

    public static string NormalizeLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        var previousWasWhitespace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character)
                == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace && normalized.Length > 0)
                {
                    normalized.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            normalized.Append(char.ToLowerInvariant(character));
            previousWasWhitespace = false;
        }

        return normalized.ToString().Trim();
    }

    public static bool IsWordTerm(string value)
        => WordTermRegex().IsMatch(value);

    public static IEnumerable<NoteSearchTerm> EnumerateTerms(
        NoteSearchExpression? expression,
        bool includeNegated = true)
    {
        if (expression is null)
        {
            yield break;
        }

        foreach (var term in EnumerateTermsCore(
                     expression,
                     isNegated: false,
                     includeNegated))
        {
            yield return term;
        }
    }

    private static IEnumerable<NoteSearchTerm> EnumerateTermsCore(
        NoteSearchExpression expression,
        bool isNegated,
        bool includeNegated)
    {
        switch (expression)
        {
            case NoteSearchTerm term when includeNegated || !isNegated:
                yield return term;
                break;

            case NoteSearchNot not:
                foreach (var term in EnumerateTermsCore(
                             not.Operand,
                             !isNegated,
                             includeNegated))
                {
                    yield return term;
                }

                break;

            case NoteSearchAnd and:
                foreach (var term in EnumerateTermsCore(
                             and.Left,
                             isNegated,
                             includeNegated))
                {
                    yield return term;
                }

                foreach (var term in EnumerateTermsCore(
                             and.Right,
                             isNegated,
                             includeNegated))
                {
                    yield return term;
                }

                break;

            case NoteSearchOr or:
                foreach (var term in EnumerateTermsCore(
                             or.Left,
                             isNegated,
                             includeNegated))
                {
                    yield return term;
                }

                foreach (var term in EnumerateTermsCore(
                             or.Right,
                             isNegated,
                             includeNegated))
                {
                    yield return term;
                }

                break;
        }
    }

    private static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < expression.Length)
        {
            if (char.IsWhiteSpace(expression[index]))
            {
                index++;
                continue;
            }

            var position = index;
            switch (expression[index])
            {
                case '(':
                    tokens.Add(new Token(TokenType.LeftParenthesis, "(", position));
                    index++;
                    continue;

                case ')':
                    tokens.Add(new Token(TokenType.RightParenthesis, ")", position));
                    index++;
                    continue;

                case '+':
                    tokens.Add(new Token(TokenType.Required, "+", position));
                    index++;
                    continue;

                case '-':
                    tokens.Add(new Token(TokenType.Not, "-", position));
                    index++;
                    continue;

                case '"':
                    tokens.Add(ReadPhrase(expression, ref index));
                    continue;
            }

            var start = index;
            while (index < expression.Length
                   && !char.IsWhiteSpace(expression[index])
                   && expression[index] is not '(' and not ')' and not '"')
            {
                index++;
            }

            var text = expression[start..index];
            AddBareToken(tokens, text, start, recognizeOperator: true);
        }

        tokens.Add(new Token(TokenType.End, string.Empty, expression.Length));
        return tokens;
    }

    private static Token ReadPhrase(string expression, ref int index)
    {
        var position = index++;
        var phrase = new StringBuilder();
        while (index < expression.Length)
        {
            if (expression[index] != '"')
            {
                phrase.Append(expression[index++]);
                continue;
            }

            if (index + 1 < expression.Length && expression[index + 1] == '"')
            {
                phrase.Append('"');
                index += 2;
                continue;
            }

            index++;
            if (string.IsNullOrWhiteSpace(phrase.ToString()))
            {
                throw new SearchSyntaxException(
                    "A quoted phrase cannot be empty",
                    position);
            }

            return new Token(TokenType.Phrase, phrase.ToString(), position);
        }

        throw new SearchSyntaxException("Incomplete quoted phrase", position);
    }

    private static void AddBareToken(
        ICollection<Token> tokens,
        string text,
        int position,
        bool recognizeOperator)
    {
        if (recognizeOperator)
        {
            if (text.Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(new Token(TokenType.And, text, position));
                return;
            }

            if (text.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(new Token(TokenType.Or, text, position));
                return;
            }

            if (text.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(new Token(TokenType.Not, text, position));
                return;
            }
        }

        var separator = text.IndexOf(':');
        if (separator > 0
            && TryParseField(text[..separator], out var field))
        {
            tokens.Add(new Token(TokenType.Field, text[..(separator + 1)], position, field));
            var remainder = text[(separator + 1)..];
            if (remainder.Length > 0)
            {
                AddBareToken(
                    tokens,
                    remainder,
                    position + separator + 1,
                    recognizeOperator: false);
            }

            return;
        }

        tokens.Add(
            text == "*"
                ? new Token(TokenType.MatchAll, text, position)
                : new Token(TokenType.Term, text, position));
    }

    private static bool TryParseField(string value, out NoteSearchField field)
    {
        if (value.Equals("name", StringComparison.OrdinalIgnoreCase)
            || value.Equals("title", StringComparison.OrdinalIgnoreCase))
        {
            field = NoteSearchField.Name;
            return true;
        }

        if (value.Equals("tag", StringComparison.OrdinalIgnoreCase))
        {
            field = NoteSearchField.Tag;
            return true;
        }

        if (value.Equals("path", StringComparison.OrdinalIgnoreCase))
        {
            field = NoteSearchField.Path;
            return true;
        }

        if (value.Equals("body", StringComparison.OrdinalIgnoreCase))
        {
            field = NoteSearchField.Body;
            return true;
        }

        field = NoteSearchField.Any;
        return false;
    }

    private static List<Token> InsertImplicitOperators(
        IReadOnlyList<Token> tokens,
        NoteSearchMode mode)
    {
        var result = new List<Token>(tokens.Count * 2);
        for (var index = 0; index < tokens.Count; index++)
        {
            var current = tokens[index];
            if (index > 0
                && EndsOperand(tokens[index - 1].Type)
                && StartsOperand(current.Type))
            {
                var operatorType = current.Type == TokenType.Not
                    ? TokenType.And
                    : mode == NoteSearchMode.Strict
                        ? TokenType.And
                        : TokenType.Or;
                result.Add(new Token(operatorType, string.Empty, current.Position));
            }

            result.Add(current);
        }

        return result;
    }

    private static bool EndsOperand(TokenType type)
        => type is TokenType.Term
            or TokenType.Phrase
            or TokenType.MatchAll
            or TokenType.RightParenthesis;

    private static bool StartsOperand(TokenType type)
        => type is TokenType.Term
            or TokenType.Phrase
            or TokenType.MatchAll
            or TokenType.Field
            or TokenType.LeftParenthesis
            or TokenType.Required
            or TokenType.Not;

    [GeneratedRegex(@"^[\p{L}\p{N}_]+$")]
    private static partial Regex WordTermRegex();

    private enum TokenType
    {
        Term,
        Phrase,
        MatchAll,
        Field,
        And,
        Or,
        Not,
        Required,
        LeftParenthesis,
        RightParenthesis,
        End
    }

    private sealed record Token(
        TokenType Type,
        string Text,
        int Position,
        NoteSearchField Field = NoteSearchField.Any);

    private sealed class Parser(IReadOnlyList<Token> tokens)
    {
        private int _index;
        private readonly List<NoteSearchExpression> _requiredExpressions = [];
        private readonly List<NoteSearchExpression> _excludedExpressions = [];

        public IReadOnlyList<NoteSearchExpression> RequiredExpressions
            => _requiredExpressions;

        public IReadOnlyList<NoteSearchExpression> ExcludedExpressions
            => _excludedExpressions;

        public NoteSearchExpression ParseExpression() => ParseOr();

        public void RequireEnd()
        {
            if (Current.Type == TokenType.End)
            {
                return;
            }

            throw new SearchSyntaxException(
                Current.Type == TokenType.RightParenthesis
                    ? "Unexpected closing parenthesis"
                    : $"Unexpected search term '{Current.Text}'",
                Current.Position);
        }

        private NoteSearchExpression ParseOr()
        {
            var expression = ParseAnd();
            while (Match(TokenType.Or))
            {
                expression = new NoteSearchOr(expression, ParseAnd());
            }

            return expression;
        }

        private NoteSearchExpression ParseAnd()
        {
            var expression = ParseUnary();
            while (Match(TokenType.And))
            {
                expression = new NoteSearchAnd(expression, ParseUnary());
            }

            return expression;
        }

        private NoteSearchExpression ParseUnary()
        {
            if (Match(TokenType.Not))
            {
                var excluded = ParseUnary();
                _excludedExpressions.Add(excluded);
                return new NoteSearchNot(excluded);
            }

            if (Match(TokenType.Required))
            {
                var required = ParseUnary();
                _requiredExpressions.Add(required);
                return required;
            }

            return ParsePrimary();
        }

        private NoteSearchExpression ParsePrimary()
        {
            if (Match(TokenType.LeftParenthesis))
            {
                if (Current.Type == TokenType.RightParenthesis)
                {
                    throw new SearchSyntaxException(
                        "A search group cannot be empty",
                        Current.Position);
                }

                var expression = ParseOr();
                if (!Match(TokenType.RightParenthesis))
                {
                    throw new SearchSyntaxException(
                        "Missing closing parenthesis",
                        Current.Position);
                }

                return expression;
            }

            var field = NoteSearchField.Any;
            if (Current.Type == TokenType.Field)
            {
                field = Current.Field;
                _index++;
            }

            if (Current.Type is TokenType.Term or TokenType.Phrase)
            {
                var token = Current;
                _index++;
                return new NoteSearchTerm(
                    NormalizeLiteral(token.Text),
                    field,
                    token.Type == TokenType.Phrase);
            }

            if (Current.Type == TokenType.MatchAll && field == NoteSearchField.Any)
            {
                _index++;
                return new NoteSearchTerm(
                    "*",
                    NoteSearchField.Any,
                    IsPhrase: false,
                    IsMatchAll: true);
            }

            if (field != NoteSearchField.Any)
            {
                throw new SearchSyntaxException(
                    "A field operator requires a term or quoted phrase",
                    Current.Position);
            }

            throw new SearchSyntaxException(
                Current.Type switch
                {
                    TokenType.End => "The search expression is incomplete",
                    TokenType.RightParenthesis => "A search operand is missing",
                    TokenType.And or TokenType.Or => "A search operand is missing",
                    _ => $"Unexpected search term '{Current.Text}'"
                },
                Current.Position);
        }

        private bool Match(TokenType type)
        {
            if (Current.Type != type)
            {
                return false;
            }

            _index++;
            return true;
        }

        private Token Current => tokens[_index];
    }

    private sealed class SearchSyntaxException(string message, int position)
        : Exception(message)
    {
        public int Position { get; } = position;
    }
}
