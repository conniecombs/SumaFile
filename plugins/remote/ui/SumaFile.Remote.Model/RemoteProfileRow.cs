using System.Globalization;

namespace SumaFile.Remote.Model;

/// <summary>One row of the Connections sidebar section (ported from the built-in sidebar's row).</summary>
public sealed class RemoteProfileRow
{
    public string ProfileId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>The protocol in upper case, for the row's pill.</summary>
    public string Protocol { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>Plain FTP the user confirmed: passwords and files travel unencrypted.</summary>
    public bool IsInsecure { get; init; }

    public static RemoteProfileRow From(RemoteProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new RemoteProfileRow
        {
            ProfileId = profile.Id,
            Name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name,
            Protocol = profile.Protocol.ToUpperInvariant(),
            Description = FormatDescription(profile),
            IsInsecure = RemoteManagerViewModel.IsInsecureProfile(profile),
        };
    }

    private static string FormatDescription(RemoteProfile profile)
    {
        var protocol = string.IsNullOrWhiteSpace(profile.Protocol)
            ? "remote"
            : profile.Protocol.ToLowerInvariant();
        var userPrefix = string.IsNullOrWhiteSpace(profile.Username) ? "" : profile.Username + "@";
        var port = profile.Port > 0 ? ":" + profile.Port.ToString(CultureInfo.InvariantCulture) : "";
        var root = string.IsNullOrWhiteSpace(profile.RootPath) ? "/" : profile.RootPath;
        return $"{protocol}://{userPrefix}{profile.Host}{port}{root}";
    }
}
