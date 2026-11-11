using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

public class RemoteProfileRowTests
{
    [Theory]
    [InlineData("ftp", true, true)]
    [InlineData(" FTP ", true, true)]
    [InlineData("ftp", false, false)]
    [InlineData("ftps", true, false)]
    [InlineData("sftp", true, false)]
    [InlineData("", true, false)]
    public void IsInsecure_OnlyForPlainFtpWithConsent(string protocol, bool consent, bool expected)
    {
        var profile = new RemoteProfile { Id = "p", Name = "P", Protocol = protocol, Host = "h", InsecurePlainFtp = consent };

        Assert.Equal(expected, RemoteProfileRow.From(profile).IsInsecure);
        Assert.Equal(expected, RemoteManagerViewModel.IsInsecureProfile(profile));
    }

    [Fact]
    public void Description_MatchesTheTextTheBuiltInSidebarShowed()
    {
        // Each expected string is what ExplorerRows.RemoteProfileSidebarRow.FormatDescription (1.0.4) returned.
        var sftp = RemoteProfileRow.From(new RemoteProfile
        {
            Id = "prod",
            Name = "Production",
            Protocol = "sftp",
            Host = "example.com",
            Port = 22,
            Username = "deploy",
            RootPath = "/var/www",
        });
        Assert.Equal("sftp://deploy@example.com:22/var/www", sftp.Description);
        Assert.Equal(("prod", "Production", "SFTP", false), (sftp.ProfileId, sftp.Name, sftp.Protocol, sftp.IsInsecure));

        var ftp = RemoteProfileRow.From(new RemoteProfile
        {
            Id = "drop",
            Name = "",
            Protocol = "FTP",
            Host = "files.example.com",
            Port = 21,
            Username = "",
            RootPath = "incoming",
            InsecurePlainFtp = true,
        });
        Assert.Equal("ftp://files.example.com:21incoming", ftp.Description);
        Assert.Equal(("files.example.com", "FTP", true), (ftp.Name, ftp.Protocol, ftp.IsInsecure));

        var bare = RemoteProfileRow.From(new RemoteProfile
        {
            Id = "bare",
            Name = "Bare",
            Protocol = "",
            Host = "café.example",
            Port = 0,
            Username = "user name",
            RootPath = " ",
        });
        Assert.Equal("remote://user name@café.example/", bare.Description);

        Assert.Throws<ArgumentNullException>(() => RemoteProfileRow.From(null!));
    }
}
