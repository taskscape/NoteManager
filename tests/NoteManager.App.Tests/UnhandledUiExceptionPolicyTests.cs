using NoteManager.App.Services;
using System.Runtime.InteropServices;
using Xunit;

namespace NoteManager.App.Tests;

[Trait("Category", "Unit")]
public sealed class UnhandledUiExceptionPolicyTests
{
    [Fact]
    public void AccessDeniedFromWebView2_KeepsTheApplicationRunning()
    {
        var exception = new UnauthorizedAccessException(
            "Odmowa dostępu. (0x80070005 (E_ACCESSDENIED))");

        Assert.True(UnhandledUiExceptionPolicy.ShouldKeepApplicationRunning(exception));
    }

    [Fact]
    public void ComFailureFromWebView2_KeepsTheApplicationRunning()
    {
        var exception = new COMException(
            "Access is denied.",
            unchecked((int)0x80070005));

        Assert.True(UnhandledUiExceptionPolicy.ShouldKeepApplicationRunning(exception));
    }

    [Fact]
    public void AggregatedAccessDenied_KeepsTheApplicationRunning()
    {
        var exception = new AggregateException(
            new UnauthorizedAccessException(
                "Odmowa dostępu. (0x80070005 (E_ACCESSDENIED))"));

        Assert.True(UnhandledUiExceptionPolicy.ShouldKeepApplicationRunning(exception));
    }

    [Fact]
    public void NestedAccessDenied_KeepsTheApplicationRunning()
    {
        var exception = new InvalidOperationException(
            "preview failed",
            new COMException("WebView unavailable", unchecked((int)0x80004005)));

        Assert.True(UnhandledUiExceptionPolicy.ShouldKeepApplicationRunning(exception));
    }

    [Fact]
    public void UnrelatedUiFailure_DoesNotKeepTheApplicationRunning()
    {
        Assert.False(
            UnhandledUiExceptionPolicy.ShouldKeepApplicationRunning(
                new InvalidOperationException("binding failed")));
    }

    [Fact]
    public void WebView2UserDataFolder_IsUnderLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoteManager",
            "WebView2");

        Assert.Equal(
            Path.GetFullPath(expected),
            Path.GetFullPath(ApplicationDataPaths.WebView2UserDataFolder));
    }
}
