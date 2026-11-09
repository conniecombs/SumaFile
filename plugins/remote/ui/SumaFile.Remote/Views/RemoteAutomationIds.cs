namespace SumaFile.Remote.Views;

/// <summary>
/// Automation ids of the remote plugin's UI. The UI smoke finds controls by these exact strings, so they are part of
/// the plugin's test contract: change them only together with <c>MainWindow.UiSmoke.Remote.cs</c>.
/// </summary>
public static class RemoteAutomationIds
{
    // Manager window.
    public const string ProfileList = "Remote.ProfileList";
    public const string NewProfile = "Remote.NewProfile";
    public const string EditProfile = "Remote.EditProfile";
    public const string DeleteProfile = "Remote.DeleteProfile";
    public const string Connect = "Remote.Connect";
    public const string Disconnect = "Remote.Disconnect";
    public const string Refresh = "Remote.Refresh";
    public const string LocalPath = "Remote.LocalPath";
    public const string LocalGo = "Remote.LocalGo";
    public const string LocalList = "Remote.LocalList";
    public const string RemotePath = "Remote.RemotePath";
    public const string RemoteList = "Remote.RemoteList";
    public const string Upload = "Remote.Upload";
    public const string Download = "Remote.Download";
    public const string NewFolder = "Remote.NewFolder";
    public const string Rename = "Remote.Rename";
    public const string DeleteEntries = "Remote.DeleteEntries";
    public const string TransferList = "Remote.TransferList";
    public const string CancelTransfer = "Remote.CancelTransfer";
    public const string RetryTransfer = "Remote.RetryTransfer";
    public const string TransferOptions = "Remote.TransferOptions";
    public const string Conflict = "Remote.Conflict";
    public const string Status = "Remote.Status";

    // Profile dialog.
    public const string ProfileName = "RemoteProfile.Name";
    public const string ProfileProtocol = "RemoteProfile.Protocol";
    public const string ProfileHost = "RemoteProfile.Host";
    public const string ProfilePort = "RemoteProfile.Port";
    public const string ProfileUser = "RemoteProfile.User";
    public const string ProfileRootPath = "RemoteProfile.RootPath";
    public const string ProfileAuth = "RemoteProfile.Auth";
    public const string ProfileSecret = "RemoteProfile.Secret";
    public const string ProfileSaveSecret = "RemoteProfile.SaveSecret";
    public const string ProfileKeyPath = "RemoteProfile.KeyPath";
    public const string ProfilePassiveMode = "RemoteProfile.PassiveMode";
    public const string ProfileAllowPlainFtp = "RemoteProfile.AllowPlainFtp";

    // Other dialogs.
    public const string HostKeyFingerprint = "RemoteHostKey.Fingerprint";
    public const string NamePrompt = "Remote.NamePrompt";

    // Sidebar section and settings page.
    public const string SidebarList = "Remote.SidebarList";
    public const string SidebarEmpty = "Remote.SidebarEmpty";
    public const string SettingsOpenManager = "Remote.SettingsOpenManager";
}
