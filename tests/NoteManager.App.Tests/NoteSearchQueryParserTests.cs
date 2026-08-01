using NoteManager.App.Services;
using Xunit;

namespace NoteManager.App.Tests;

public sealed class NoteSearchQueryParserTests
{
    [Theory]
    [InlineData("alpha beta", NoteSearchMode.Strict)]
    [InlineData("all: alpha beta", NoteSearchMode.Strict)]
    [InlineData("=alpha beta", NoteSearchMode.Strict)]
    [InlineData("best: alpha beta", NoteSearchMode.BestMatch)]
    [InlineData("~alpha beta", NoteSearchMode.BestMatch)]
    public void Parse_ModeSelectors_AreRecognized(
        string text,
        NoteSearchMode expectedMode)
    {
        var result = NoteSearchQueryParser.Parse(text);

        Assert.True(result.IsValid);
        Assert.Equal(expectedMode, result.Query!.Mode);
        Assert.False(result.Query.IsEmpty);
    }

    [Fact]
    public void Parse_StrictAndBestModesUseDifferentImplicitOperators()
    {
        var strict = NoteSearchQueryParser.Parse("alpha beta");
        var best = NoteSearchQueryParser.Parse("~ alpha beta");

        Assert.IsType<NoteSearchAnd>(strict.Query!.Root);
        Assert.IsType<NoteSearchOr>(best.Query!.Root);
    }

    [Theory]
    [InlineData("all:")]
    [InlineData("=")]
    [InlineData("best:")]
    [InlineData("~")]
    public void Parse_ModeOnlyInput_IsAnEmptyValidSearch(string text)
    {
        var result = NoteSearchQueryParser.Parse(text);

        Assert.True(result.IsValid);
        Assert.True(result.Query!.IsEmpty);
    }

    [Fact]
    public void Parse_RequiredExcludedPhraseAndFields_AreRepresented()
    {
        var result = NoteSearchQueryParser.Parse(
            "~ +tag:Active \"Project Plan\" -path:Archive/");

        Assert.True(result.IsValid);
        Assert.Single(result.Query!.RequiredExpressions);
        var terms = NoteSearchQueryParser
            .EnumerateTerms(result.Query.Root)
            .ToArray();
        Assert.Contains(
            terms,
            term => term.Field == NoteSearchField.Tag
                    && term.Text == "active");
        Assert.Contains(
            terms,
            term => term.IsPhrase
                    && term.Text == "project plan");
        Assert.Contains(
            terms,
            term => term.Field == NoteSearchField.Path
                    && term.Text == "archive/");
    }

    [Theory]
    [InlineData(@"docs/search.md")]
    [InlineData(@"C:\Projects\NoteManager")]
    [InlineData("customer@example.com")]
    [InlineData("release-1.2")]
    public void Parse_PunctuationRemainsPartOfBareTerms(string text)
    {
        var result = NoteSearchQueryParser.Parse(text);

        var term = Assert.IsType<NoteSearchTerm>(result.Query!.Root);
        Assert.Equal(
            NoteSearchQueryParser.NormalizeLiteral(text),
            term.Text);
    }

    [Theory]
    [InlineData("\"unfinished", "Incomplete quoted phrase")]
    [InlineData("alpha OR", "incomplete")]
    [InlineData("()", "cannot be empty")]
    [InlineData("body:", "requires a term")]
    public void Parse_InvalidExpressions_ReturnReadableErrors(
        string text,
        string expectedError)
    {
        var result = NoteSearchQueryParser.Parse(text);

        Assert.False(result.IsValid);
        Assert.Contains(
            expectedError,
            result.Error!,
            StringComparison.OrdinalIgnoreCase);
    }
}
