using System.Text;
using System.Text.RegularExpressions;

namespace NoteManager.App.Services;

public sealed record TagInputResult(
    string[] Tags,
    string[] InvalidTags);

public static partial class MarkdownTagEditorService
{
    public static bool IsValidTag(string tag)
        => !string.IsNullOrWhiteSpace(tag)
           && ValidTagRegex().IsMatch(tag);

    public static TagInputResult ParseTagInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new TagInputResult([], []);
        }

        var tags = new List<string>();
        var invalidTags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in TagInputSeparatorRegex()
                     .Split(input)
                     .Where(token => token.Length > 0))
        {
            var normalized = token.Trim().ToLowerInvariant();
            if (!seen.Add(normalized))
            {
                continue;
            }

            if (IsValidTag(normalized))
            {
                tags.Add(normalized);
            }
            else
            {
                invalidTags.Add(token.Trim());
            }
        }

        return new TagInputResult(tags.ToArray(), invalidTags.ToArray());
    }

    public static string[] NormalizeTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        return tags
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Where(IsValidTag)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static string RewriteTagBlocks(
        string markdown,
        IEnumerable<string> selectedTags)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var tags = NormalizeTags(selectedTags);
        var blocks = FindTagBlocks(markdown);
        var newLine = DetectNewLine(markdown);

        if (blocks.Count == 0)
        {
            if (tags.Length == 0)
            {
                return markdown;
            }

            var formattedBlock = FormatTagBlock(tags, newLine);
            var content = markdown.TrimEnd('\r', '\n');
            return content.Length == 0
                ? formattedBlock
                : $"{content}{newLine}{newLine}{formattedBlock}";
        }

        var builder = new StringBuilder(markdown);
        for (var blockIndex = blocks.Count - 1; blockIndex >= 0; blockIndex--)
        {
            var block = blocks[blockIndex];
            var replacement = string.Empty;
            if (blockIndex == 0 && tags.Length > 0)
            {
                replacement = FormatTagBlock(tags, newLine);
                if (block.EndsWithLineBreak)
                {
                    replacement += newLine;
                }
            }

            builder.Remove(block.Start, block.Length);
            builder.Insert(block.Start, replacement);
        }

        return builder.ToString();
    }

    private static List<TagBlockSpan> FindTagBlocks(string markdown)
    {
        var lines = ReadLines(markdown);
        var blocks = new List<TagBlockSpan>();

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var header = lines[lineIndex];
            if (!TagHeaderRegex().IsMatch(header.Text))
            {
                continue;
            }

            var blockEnd = header.End;
            var foundTag = false;
            var scanIndex = lineIndex + 1;

            for (; scanIndex < lines.Count; scanIndex++)
            {
                var line = lines[scanIndex];
                if (string.IsNullOrWhiteSpace(line.Text))
                {
                    if (foundTag)
                    {
                        break;
                    }

                    continue;
                }

                if (!TagItemRegex().IsMatch(line.Text))
                {
                    break;
                }

                foundTag = true;
                blockEnd = line.End;
            }

            blocks.Add(new TagBlockSpan(
                header.Start,
                blockEnd - header.Start,
                blockEnd > header.Start
                && EndsWithLineBreak(markdown, blockEnd)));
            lineIndex = Math.Max(lineIndex, scanIndex - 1);
        }

        return blocks;
    }

    private static List<MarkdownLine> ReadLines(string markdown)
    {
        var lines = new List<MarkdownLine>();
        var index = 0;

        while (index < markdown.Length)
        {
            var start = index;
            while (index < markdown.Length
                   && markdown[index] is not '\r' and not '\n')
            {
                index++;
            }

            var text = markdown[start..index];
            if (index < markdown.Length && markdown[index] == '\r')
            {
                index++;
                if (index < markdown.Length && markdown[index] == '\n')
                {
                    index++;
                }
            }
            else if (index < markdown.Length && markdown[index] == '\n')
            {
                index++;
            }

            lines.Add(new MarkdownLine(start, index, text));
        }

        return lines;
    }

    private static string FormatTagBlock(
        IEnumerable<string> tags,
        string newLine)
        => $"tags:{newLine}"
           + string.Join(
               newLine,
               tags.Select(tag => $"  - {tag}"));

    private static string DetectNewLine(string markdown)
        => markdown.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : markdown.Contains('\n')
                ? "\n"
                : Environment.NewLine;

    private static bool EndsWithLineBreak(string markdown, int end)
        => end > 0 && markdown[end - 1] is '\r' or '\n';

    [GeneratedRegex(@"^[\t ]*tags[\t ]*:[\t ]*(?:#.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TagHeaderRegex();

    [GeneratedRegex(@"^[\t ]*-[\t ]+.+?[\t ]*$")]
    private static partial Regex TagItemRegex();

    [GeneratedRegex(@"^[\p{L}\p{N}.-]+$")]
    private static partial Regex ValidTagRegex();

    [GeneratedRegex(@"[\s,;]+")]
    private static partial Regex TagInputSeparatorRegex();

    private sealed record MarkdownLine(
        int Start,
        int End,
        string Text);

    private sealed record TagBlockSpan(
        int Start,
        int Length,
        bool EndsWithLineBreak);
}
