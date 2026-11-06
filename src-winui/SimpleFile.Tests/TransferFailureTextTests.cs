using SimpleFile.Core;
using Xunit;

namespace SimpleFile.Tests;

public class TransferFailureTextTests
{
    [Theory]
    [InlineData("access_denied", null, "Can't write to report.txt. Access was denied.")]
    [InlineData(null, "Access to the path is DENIED", "Can't write to report.txt. Access was denied.")]
    [InlineData(null, "Permission error", "Can't write to report.txt. Access was denied.")]
    [InlineData("disk_full", null, "The destination drive is full.")]
    [InlineData(null, "There is not enough space on the disk. No space left", "The destination drive is full.")]
    [InlineData("not_found", null, "report.txt no longer exists.")]
    [InlineData(null, "Could not find file: Not Found", "report.txt no longer exists.")]
    [InlineData(null, "Connection reset by peer", "Lost the connection to the server.")]
    [InlineData(null, "operation timed out", "Lost the connection to the server.")]
    [InlineData(null, "No such host is known", "Lost the connection to the server.")]
    [InlineData(null, "getaddrinfo: host not found", "Lost the connection to the server.")]
    [InlineData(null, "The ghost file vanished", "Couldn't finish report.txt.")]
    [InlineData("cancelled", null, "The transfer was cancelled.")]
    [InlineData(null, "Operation was canceled", "The transfer was cancelled.")]
    [InlineData("weird", "boom", "Couldn't finish report.txt.")]
    public void Describe_MapsKnownFailures(string? kind, string? raw, string expected)
    {
        var (summary, _) = TransferFailureText.Describe(kind, raw, @"C:\Docs\report.txt");

        Assert.Equal(expected, summary);
    }

    [Fact]
    public void Describe_UsesThisFileWithoutAPathAndKeepsRawDetails()
    {
        var (summary, details) = TransferFailureText.Describe(null, "boom", null);
        Assert.Equal("Couldn't finish this file.", summary);
        Assert.Equal("boom", details);
        Assert.Equal("", TransferFailureText.Describe(null, null, null).Details);
    }
}
