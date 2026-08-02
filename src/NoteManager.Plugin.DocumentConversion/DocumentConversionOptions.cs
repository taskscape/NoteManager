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

    public string? TessdataPath { get; set; }

    public string? CliExecutablePath { get; set; }

    public string? MarkItDownCommandPath { get; set; } =
        @"C:\Projects\DOC2MD\.markitdown-venv\Scripts\markitdown.exe";

    public string? LibreOfficeExecutablePath { get; set; }

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

        var options = JsonSerializer.Deserialize<DocumentConversionOptions>(
                          File.ReadAllText(path),
                          JsonOptions)
                      ?? new DocumentConversionOptions();
        options.Validate();
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
}
