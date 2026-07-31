# Full-text search specification

## Status

This document describes the implemented NoteManager full-text search system.
Search is operated entirely through expressions entered in the existing
**Search notes** box. There is no advanced-search window or query-builder UI.

The implementation provides:

- strict and best-match modes;
- words, phrases, grouping, and Boolean operators;
- required and excluded operands;
- file-name, tag, relative-path, and body field operators;
- literal punctuation, including `/` and `\`;
- deterministic search-owned ordering;
- a Unicode word index and a punctuation-preserving literal index.

## Design principles

The query language follows these rules:

1. A query without a mode selector behaves like the previous strict search.
2. Explicit operators have the same meaning in every mode.
3. The mode changes the implicit relationship between adjacent terms and the
   result ordering.
4. Unrelated notes are not best-match results unless the user explicitly adds
   the match-all operator.
5. The search expression is the complete and only search configuration.
6. Invalid and stale queries never replace a completed result.

## Search scope

Searchable fields are:

1. note file name;
2. parsed tags;
3. path relative to the opened vault;
4. complete Markdown source.

The absolute vault path is not indexed. Search is applied inside the selected
tag, **All notes**, or **Untagged** navigation scope.

## Query modes

A mode selector is recognized only at the beginning of the trimmed search
text. Keyword selectors are case-insensitive.

| Mode | Keyword | Symbol | Adjacent terms | Ordering |
| --- | --- | --- | --- | --- |
| Strict | `all:` | `=` | Implicit `AND` | Modified descending |
| Best match | `best:` | `~` | Implicit `OR` | Relevance descending |

Strict is the default:

```text
project plan
all: project plan
= project plan
```

All three expressions require both `project` and `plan`.

Best-match examples:

```text
best: project plan
~ project plan
```

These expressions return notes matching `project` or `plan`, with notes
matching both normally ranked first.

The symbol may touch the expression, so `=project` and `~project` are valid.
A keyword selector must include its colon. `all` and `best` without a colon
are ordinary terms.

A selector without an expression is an empty search. It does not activate
search ordering or clear the normal sort selection.

## Terms

### Word and prefix terms

A bare term containing only Unicode letters, numbers, or underscores is a
word-prefix search:

```text
plan
```

It can match `plan`, `plans`, or `planning`. An exact word receives a ranking
bonus over a prefix-only match.

Matching is case-insensitive. Text and queries are normalized consistently for
diacritics and whitespace.

### Literal punctuation

A bare term containing other punctuation is a literal substring:

```text
docs/search.md
C:\Projects\NoteManager
customer@example.com
release-1.2
#planning
```

Every punctuation character must be present in the indexed field. `/` is not
a regular-expression delimiter, and `\` is not an escape character. Slash
direction is significant.

A leading `-` is the exclusion operator. To search for literal text beginning
with a dash, place it in double quotes.

### Quoted phrases

Double quotes create one contiguous phrase, including spaces:

```text
"quarterly project plan"
"C:\Shared Notes\roadmap.md"
```

The phrase must occur inside one indexed field. It cannot start in the file
name and finish in the body. Runs of whitespace are normalized to one space;
other punctuation remains significant.

Backslash is always literal. A double quote inside a phrase is written as two
double quotes:

```text
"the ""approved"" plan"
```

An unmatched or empty quoted phrase is a syntax error.

## Operators

### Boolean and modifier operators

| Operator | Meaning |
| --- | --- |
| `AND` | Both operands must match |
| `OR` | At least one operand must match |
| `NOT` | Exclude the following operand |
| `+operand` | Require the following term, phrase, or group |
| `-operand` | Short form of `NOT operand` |
| `( ... )` | Group an expression |
| `*` | Match the complete current navigation scope |

`AND`, `OR`, and `NOT` are case-insensitive only when they are complete,
unquoted words. `"AND"` searches for the word as a phrase, and `not-ready`
remains a literal term.

Operator precedence is:

1. `NOT`, `+`, and `-`;
2. `AND`;
3. `OR`.

Parentheses override precedence:

```text
(invoice OR receipt) NOT draft
all: "project plan" AND approved
```

Required and excluded operands apply to the complete result:

```text
~ +invoice paid -archived
```

This requires `invoice`, uses `paid` as an additional ranking signal, and
excludes every note matching `archived`.

Best-match adjacency is an implicit `OR`, but an explicit `AND` still requires
both sides:

```text
~ invoice paid
~ invoice AND paid
```

The first expression accepts either term. The second accepts only notes
matching both.

### Match-all operator

Best match normally returns notes matching at least one positive term. The
`*` operator explicitly includes every note in the current navigation scope:

```text
~ * project plan
```

Notes matching `project` or `plan` are ranked first. Notes matching neither
remain at the end with zero relevance. This is the only implicit way to retain
unrelated notes in a best-match result.

`* NOT archived` returns the complete current scope except notes matching
`archived`.

## Field operators

| Operator | Indexed field |
| --- | --- |
| `name:` | Note file name |
| `title:` | Alias for `name:` |
| `tag:` | Parsed tags |
| `path:` | Vault-relative path |
| `body:` | Markdown source |

A field operator accepts one word, literal term, or quoted phrase:

```text
name:roadmap
name:"project plan"
tag:active
path:Clients/Acme
path:"Clients\Acme Notes"
body:"approved budget"
```

Field operands combine with every other operator:

```text
~ +tag:active body:roadmap -path:archive/
(name:invoice OR tag:receipt) NOT body:draft
```

Only the known field names above are parsed as field operators. Other
colon-containing values, such as `https://example.com`, are literal terms.
A field operator without an operand is a syntax error.

## Strict mode

Strict mode returns only notes for which the complete expression is true.
Adjacent terms are implicitly joined with `AND`.

Examples:

| Query | Required result |
| --- | --- |
| `alpha beta` | Both terms |
| `"alpha beta"` | The contiguous phrase |
| `alpha OR beta` | Either term |
| `alpha NOT beta` | `alpha` without `beta` |
| `path:docs\search.md` | Literal relative path |

Results are ordered by:

1. file modification time descending;
2. file name ascending, case-insensitive;
3. relative path ascending, case-insensitive.

Relevance does not change strict ordering.

## Best-match mode

Best match returns notes satisfying the positive expression and all required
and excluded operands. Adjacent positive terms are implicitly joined with
`OR`.

Ranking considers:

- number of distinct positive terms matched;
- weighted field matches;
- FTS5 BM25 relevance;
- exact-word matches over prefix-only matches;
- quoted phrases and punctuation-preserving literal matches;
- repeated occurrences with bounded frequency contribution;
- coverage of explicit `AND` groups.

Initial field weights are:

| Field | Weight |
| --- | ---: |
| File name | 6 |
| Tags | 4 |
| Relative path | 2 |
| Markdown content | 1 |

Each distinct positive term supplies a coverage bonus before field-level
signals are compared. This makes a note matching more of the requested terms
normally outrank a note with one strong field match.

Results are ordered by:

1. relevance score descending;
2. distinct positive term count descending;
3. file modification time descending;
4. file name ascending, case-insensitive;
5. relative path ascending, case-insensitive.

Modification time is a tie breaker, not part of relevance.

## Search and normal sorting

The persisted normal sort and active search order are separate states:

- `SelectedSortType` remains the preferred Title, Created, Updated, or Size
  sort for the vault;
- an accepted non-empty result activates strict or best-match search order.

When the latest valid search result is accepted:

1. all normal sort-menu checkmarks are cleared;
2. the sort button is disabled;
3. its tooltip explains that search controls note order;
4. strict or best-match ordering is applied;
5. the persisted normal sort preference remains unchanged.

Clearing the search box cancels pending work, deactivates search ordering,
restores the preferred sort, and restores its checkmark.

An incomplete or invalid expression does not clear the normal sort and does
not replace the last accepted search result.

## Search-box interaction

Typing starts a 250 millisecond debounce so search updates without requiring a
button. Pressing `Enter` cancels that wait and submits the current text
immediately. Enter is a submission action only and is not part of the search
grammar.

Search is unavailable until the current folder's index is complete. During an
index build, the search box is disabled and its placeholder reads **Indexing in
progress**. The box is enabled with its normal **Search notes** placeholder only
after the status becomes **Full-text ready**. If indexing fails, it remains
disabled and reads **Search unavailable**.

An accepted search with zero matches displays an empty note list and a visible
**No notes found** message. Its status remains a normal successful search
status, such as `Strict search · 0 notes`; it is not reported as a parser or
index error.

## Parser and errors

`NoteSearchQueryParser` produces a typed expression tree. Raw user input is
never passed directly to FTS5 syntax.

The parser:

- removes only an initial mode selector;
- retains literal punctuation;
- distinguishes terms, phrases, fields, groups, and operators;
- records required and excluded expressions separately;
- inserts the mode-specific implicit operator;
- normalizes literal values;
- returns a readable error and character position.

Errors include:

- unmatched or empty quotes;
- empty groups;
- missing closing parentheses;
- leading or trailing binary operators;
- missing field operands.

The application retains the last completed result while showing the error in
the status area.

## Index design

The per-vault database remains:

```text
<selected folder>\.notes\search.db
```

Schema version 2 contains:

- `indexed_notes`, holding identity and file metadata;
- `note_search`, using `unicode61 remove_diacritics 2` for word-prefix and
  BM25 searches;
- `note_literal_search`, using the FTS5 trigram tokenizer over normalized
  fields for phrases and punctuation-preserving substring searches.

Literal values containing `%` or `_` use exact `instr` verification so those
characters never become SQL wildcard operators.

When an older disposable index is opened, the search tables and index metadata
are rebuilt. Source Markdown notes are never modified or deleted by migration.

## Ordered result contract

The service returns ordered `NoteSearchHit` values containing:

```text
Path
Name
RelativePath
RelevanceScore
MatchedPositiveTermCount
ModifiedUtcTicks
```

The view model retains hit scores and counts rather than converting results to
an unordered set. It intersects hit membership with the active navigation
scope and reapplies the documented deterministic ordering.

## Responsiveness and stale-result protection

Search retains the 250-millisecond debounce. Each operation is guarded by:

- opened-folder generation;
- exact search-box text;
- monotonically increasing search generation;
- cancellation token.

Only the latest matching generation may update note membership, selection,
status, or sort state.

Index updates commit batches of 200 notes through SQLite WAL. The search box
remains disabled for the complete update so the interface never accepts a
query against a partial index. A previously entered expression is rerun after
indexing completes.

## Tests and acceptance criteria

Automated coverage verifies:

- all mode selector forms and mode-only input;
- strict implicit `AND` and best-match implicit `OR`;
- `AND`, `OR`, `NOT`, `+`, `-`, `*`, and grouping;
- global required and excluded operands;
- quoted phrases and syntax errors;
- literal slash and backslash matching;
- `name:`, `title:`, `tag:`, `path:`, and `body:`;
- case and diacritic normalization;
- strict modification-time ordering;
- best-match coverage and field ranking;
- explicit match-all zero-score ordering;
- tag navigation combined with search;
- physical typing and Enter submission;
- disabled search and indexing placeholder until the index is ready;
- the visible zero-result empty state;
- clearing and restoring normal sort state;
- cancellation and partial-index behavior.

The implementation is accepted when:

1. unqualified, `all:`, and `=` strict searches require every adjacent term;
2. `best:` and `~` use any adjacent positive term and preserve relevance
   order;
3. `+` requires and `NOT` / `-` excludes across the complete result;
4. `*` explicitly includes the complete current navigation scope;
5. phrases and literal punctuation differ from separated word queries;
6. field operators restrict both membership and scoring;
7. strict results are newest-modified first;
8. best-match results are strongest first with deterministic ties;
9. accepted searches clear and disable normal sorting;
10. clearing search restores the persisted sort;
11. invalid and stale queries cannot replace a valid result;
12. Enter submits the current search immediately;
13. a successful zero-match search shows an empty list and **No notes found**;
14. indexing disables search and displays **Indexing in progress** until ready;
15. the portable and executable UI test suites and solution build pass without
    warnings.
