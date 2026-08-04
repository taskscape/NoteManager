using NoteManager.Plugins;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Unit")]
public sealed class PluginActivationStoreTests
{
    [Fact]
    public void SaveAndLoad_UsesTheVaultLocalNotePluginFolder()
    {
        var vault = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            $"NoteManager.PluginActivation.{Guid.NewGuid():N}"));

        try
        {
            var store = new PluginActivationStore();
            store.Save(vault.FullName, ["git-integration", "git-integration"]);

            Assert.Equal(
                Path.Combine(vault.FullName, ".note", "plugins", "activated.json"),
                store.GetActivationFilePath(vault.FullName));
            Assert.Contains("git-integration", store.Load(vault.FullName));
            Assert.Single(store.Load(vault.FullName));
        }
        finally
        {
            Directory.Delete(vault.FullName, recursive: true);
        }
    }

    [Fact]
    public void PluginConfigurationDirectory_IsContainedByThePluginRoot()
    {
        var store = new PluginActivationStore();
        var vault = Path.Combine(Path.GetTempPath(), "NoteManager.PluginVault");

        var path = store.GetPluginConfigurationDirectory(vault, "git-integration");

        Assert.Equal(
            Path.Combine(vault, ".note", "plugins", "git-integration"),
            path);
        Assert.Throws<ArgumentException>(() =>
            store.GetPluginConfigurationDirectory(vault, "..\\outside"));
        Assert.Throws<ArgumentException>(() =>
            store.GetPluginConfigurationDirectory(vault, ".."));
    }
}
