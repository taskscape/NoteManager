using System.Reflection;
using System.Runtime.Loader;

namespace NoteManager.Plugins;

public sealed record DiscoveredPlugin(
    string AssemblyPath,
    INoteManagerPlugin? Instance,
    string? LoadError)
{
    public bool IsAvailable => Instance is not null;
}

public sealed class PluginCatalog
{
    public const string BinaryFolderName = "Plugins";

    public IReadOnlyList<DiscoveredPlugin> Discover(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        var pluginDirectory = Path.Combine(
            Path.GetFullPath(applicationDirectory),
            BinaryFolderName);
        if (!Directory.Exists(pluginDirectory))
        {
            return [];
        }

        var plugins = new List<DiscoveredPlugin>();
        foreach (var assemblyPath in Directory
                     .EnumerateFiles(
                         pluginDirectory,
                         "NoteManager.Plugin.*.dll",
                         SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            DiscoverAssembly(assemblyPath, plugins);
        }

        return plugins;
    }

    private static void DiscoverAssembly(
        string assemblyPath,
        ICollection<DiscoveredPlugin> plugins)
    {
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
                Path.GetFullPath(assemblyPath));
            var pluginTypes = assembly
                .GetTypes()
                .Where(type => !type.IsAbstract
                               && typeof(INoteManagerPlugin).IsAssignableFrom(type))
                .ToArray();

            if (pluginTypes.Length == 0)
            {
                plugins.Add(new DiscoveredPlugin(
                    assemblyPath,
                    null,
                    "The assembly does not contain an INoteManagerPlugin implementation."));
                return;
            }

            foreach (var pluginType in pluginTypes)
            {
                try
                {
                    var instance = Activator.CreateInstance(pluginType)
                                   as INoteManagerPlugin
                                   ?? throw new InvalidOperationException(
                                       $"Could not create {pluginType.FullName}.");
                    ValidateMetadata(instance.Metadata);
                    plugins.Add(new DiscoveredPlugin(assemblyPath, instance, null));
                }
                catch (Exception exception)
                {
                    plugins.Add(new DiscoveredPlugin(
                        assemblyPath,
                        null,
                        $"{pluginType.FullName}: {exception.Message}"));
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or BadImageFormatException
            or FileLoadException
            or ReflectionTypeLoadException)
        {
            plugins.Add(new DiscoveredPlugin(
                assemblyPath,
                null,
                exception.Message));
        }
    }

    private static void ValidateMetadata(PluginMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.Name);

        if (metadata.Id is "." or ".."
            || metadata.Id.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException(
                $"Plugin id '{metadata.Id}' contains unsupported characters.");
        }
    }
}
