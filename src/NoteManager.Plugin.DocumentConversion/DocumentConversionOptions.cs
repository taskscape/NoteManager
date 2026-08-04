using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoteManager.Plugin.DocumentConversion;

public sealed class DocumentConversionOptions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 5;

    public bool Recursive { get; set; } = true;

    public int CommandTimeoutMinutes { get; set; } = 60;

    public string PdfProcessing { get; set; } = "local";

    public string OcrLanguages { get; set; } = "eng+pol";

    public static DocumentConversionOptions LoadOrCreate(
        string configurationDirectory)
    {
        Directory.CreateDirectory(configurationDirectory);
        var path = Path.Combine(configurationDirectory, "settings.json");
        if (!File.Exists(path))
        {
            var defaults = new DocumentConversionOptions();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            return defaults;
        }

        var json = File.ReadAllText(path);
        var options = JsonSerializer.Deserialize<DocumentConversionOptions>(
                          json,
                          JsonOptions)
                      ?? new DocumentConversionOptions();
        options.Validate();
        RemoveLegacyExecutableOverrides(path, json, options);
        return options;
    }

    public void Validate()
    {
        if (IntervalMinutes is < 1 or > 1440)
        {
            throw new InvalidDataException(
                "Document conversion IntervalMinutes must be between 1 and 1440.");
        }

        if (CommandTimeoutMinutes is < 1 or > 1440)
        {
            throw new InvalidDataException(
                "Document conversion CommandTimeoutMinutes must be between 1 and 1440.");
        }

        if (PdfProcessing is not ("local" or "azure" or "markitdown"))
        {
            throw new InvalidDataException(
                "Document conversion PdfProcessing must be local, azure, or markitdown.");
        }

        if (string.IsNullOrWhiteSpace(OcrLanguages)
            || OcrLanguages.Contains('\r')
            || OcrLanguages.Contains('\n'))
        {
            throw new InvalidDataException(
                "Document conversion OcrLanguages must contain one non-empty line.");
        }
    }

    private static void RemoveLegacyExecutableOverrides(
        string path,
        string json,
        DocumentConversionOptions options)
    {
        using var document = JsonDocument.Parse(json);
        var legacyNames = new[]
        {
            "CliExecutablePath",
            "MarkItDownCommandPath",
            "LibreOfficeExecutablePath",
            "TessdataPath"
        };
        if (legacyNames.Any(name => document.RootElement.TryGetProperty(name, out _)))
        {
            // DOC2MD owns its installed dependencies, so vault settings no
            // longer retain paths into a source checkout or plugin bundle.
            File.WriteAllText(path, JsonSerializer.Serialize(options, JsonOptions));
        }
    }
}
