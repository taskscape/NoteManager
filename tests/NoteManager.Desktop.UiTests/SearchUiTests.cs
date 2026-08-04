using NoteManager.Desktop.UiTests.Infrastructure;
using NUnit.Framework;

namespace NoteManager.Desktop.UiTests;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Category("EndToEnd")]
internal sealed class SearchUiTests : UiTestBase
{
    [Test]
    public void IndexingADirectory_DisablesSearchUntilTheIndexIsReady()
    {
        Vault.CreateAdditionalIndexingNotes(1_000);
        var app = Launch(expectedNoteCount: 1_007, waitForIndex: false);

        app.WaitForSearchBoxEnabled(expected: false);
        app.WaitForSearchBoxName("Indexing in progress");

        app.WaitForIndexReady();
        app.WaitForSearchBoxName("Search notes");
    }

    [Test]
    public void TypingAQueryAndPressingEnter_WithNoMatches_ShowsAnEmptyResultState()
    {
        var app = Launch();

        app.TypeSearchAndPressEnter("receipt");

        app.WaitForNoteCount(0);
        app.WaitForDisplayedNoteItems(0);
        app.WaitForNoSearchResults();
        app.WaitForStatusContaining("Strict search · 0 notes");
    }

    [Test]
    public void TypingAfterACompletedLiveSearch_DoesNotRequireRefocusingTheSearchBox()
    {
        var app = Launch();

        app.FocusSearchAndType("project");
        app.WaitForNoteCount(3);

        app.TypeWithoutRefocusing(" plan");

        app.WaitForSearchText("project plan");
        app.WaitForNoteCount(2);
    }

    [Test]
    public void StrictSearch_PhrasesFieldsAndLiteralSymbols_FilterTheVisibleList()
    {
        var app = Launch();

        app.SetSearchText("project plan");
        app.WaitForNoteCount(2);
        app.WaitForSelectedTitle("02 Project planning.md");

        app.SetSearchText("\"quarterly project plan\"");
        app.WaitForNoteCount(1);
        app.WaitForSelectedTitle("01 Project plan.md");

        app.SetSearchText("body:docs/search.md");
        app.WaitForNoteCount(1);
        app.WaitForSelectedTitle("01 Project plan.md");

        app.SetSearchText("name:invoice -name:draft");
        app.WaitForNoteCount(1);
        app.WaitForSelectedTitle("03 Invoice.md");
    }

    [Test]
    public void BestMatch_RequiredExcludedAndMatchAllOperatorsControlResults()
    {
        var app = Launch();

        app.SetSearchText("~ project beta");
        app.WaitForNoteCount(4);
        app.WaitForSelectedTitle("02 Project planning.md");

        app.SetSearchText("~ +project beta");
        app.WaitForNoteCount(3);
        app.WaitForSelectedTitle("02 Project planning.md");

        app.SetSearchText("~ project -beta");
        app.WaitForNoteCount(2);
        app.WaitForSelectedTitle("01 Project plan.md");

        app.SetSearchText("~ * project");
        app.WaitForNoteCount(7);
        app.WaitForSelectedTitle("01 Project plan.md");
    }

    [Test]
    public void SearchOwnsSorting_InvalidInputKeepsResults_AndClearRestoresBrowsing()
    {
        var app = Launch();
        app.WaitForSortButtonEnabled(expected: true);

        app.SetSearchText("invoice paid");
        app.WaitForNoteCount(1);
        app.WaitForSelectedTitle("03 Invoice.md");
        app.WaitForSortButtonEnabled(expected: false);

        app.SetSearchText("\"unfinished");
        app.WaitForStatusContaining("Incomplete quoted phrase");
        app.WaitForNoteCount(1);
        app.WaitForSelectedTitle("03 Invoice.md");
        app.WaitForSortButtonEnabled(expected: false);

        app.SetSearchText(string.Empty);
        app.WaitForNoteCount(7);
        app.WaitForSortButtonEnabled(expected: true);
    }

    [Test]
    public void SearchRemainsInsideTheSelectedTagNavigationScope()
    {
        var app = Launch();

        app.SelectNavigationItem("active");
        app.WaitForNoteCount(2);

        app.SetSearchText("invoice");
        app.WaitForNoteCount(0);

        app.SetSearchText("project");
        app.WaitForNoteCount(2);
        app.WaitForSelectedTitle("02 Project planning.md");
    }
}
