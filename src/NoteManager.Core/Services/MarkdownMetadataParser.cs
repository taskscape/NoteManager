using System.Text.RegularExpressions;

namespace NoteManager.App.Services;

public static partial class MarkdownMetadataParser
{
    public static string[] ParseTags(string markdown)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match header in TagsHeaderRegex().Matches(markdown))
        {
            using var reader = new StringReader(markdown[(header.Index + header.Length)..]);
            var foundTag = false;

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (foundTag)
                    {
                        break;
                    }

                    continue;
                }

                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    break;
                }

                var item = TagItemRegex().Match(line);
                if (!item.Success)
                {
                    break;
                }

                var tag = NormalizeTag(item.Groups["tag"].Value);
                if (tag.Length > 0 && seen.Add(tag))
                {
                    tags.Add(tag);
                }

                foundTag = true;
            }
        }

        return tags.ToArray();
    }

    public static string[] ParseInlineEmbeddedMediaEmbeds(string markdown)
    {
        var embeds = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in InlineEmbeddedMediaEmbedRegex().Matches(markdown))
        {
            var target = match.Groups["target"].Value.Trim();
            if (target.Length > 0 && seen.Add(target))
            {
                embeds.Add(target);
            }
        }

        return embeds.ToArray();
    }

    private static string NormalizeTag(string value)
    {
        var tag = value.Trim();
        var commentIndex = tag.IndexOf(" #", StringComparison.Ordinal);
        if (commentIndex >= 0)
        {
            tag = tag[..commentIndex].TrimEnd();
        }

        if (tag.Length >= 2
            && ((tag[0] == '"' && tag[^1] == '"')
                || (tag[0] == '\'' && tag[^1] == '\'')
                || (tag[0] == '`' && tag[^1] == '`')))
        {
            tag = tag[1..^1].Trim();
        }

        return tag.ToLowerInvariant();
    }

    [GeneratedRegex(@"^[\t ]*tags[\t ]*:[\t ]*(?:#.*)?\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TagsHeaderRegex();

    [GeneratedRegex(@"^[\t ]*-[\t ]+(?<tag>.+?)[\t ]*\r?$")]
    private static partial Regex TagItemRegex();

    [GeneratedRegex(@"!\[\[\s*(?<target>[^\]\r\n|#]+?\.(?:pdf|png|jpe?g|bmp))(?:[|#][^\]\r\n]*)?\s*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex InlineEmbeddedMediaEmbedRegex();
}
