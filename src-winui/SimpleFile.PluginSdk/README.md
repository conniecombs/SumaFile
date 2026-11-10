# SumaFile.PluginSdk

The contracts for [SumaFile](https://github.com/conniecombs/SumaFile) plugins: the interfaces a plugin's UI assembly
implements (`IPlugin`, `ICommandHandler`, the sidebar, settings-page, panel and row-decoration providers) and the host
services it receives in `IPlugin.ActivateAsync` (`IPluginHost`: the backend, settings, progress, dialogs, the
workspace, the log, windows, panels and decorations).

The package version is the SDK contract version, `PluginSdk.Version`. A plugin's `plugin.json` names it as
`sdkVersion`, and SumaFile loads a plugin only when that major version is one it supports.

## Referencing the SDK

SumaFile ships the SDK and loads the host's copy for every plugin, so reference it compile-only. No copy goes into the
plugin folder:

```xml
<PackageReference Include="SumaFile.PluginSdk" Version="1.0.0" ExcludeAssets="runtime" PrivateAssets="all" />
```

The SDK is WinUI-free (`net10.0-windows`, no Windows App SDK reference). UI objects cross as `object`: a provider
returns a `Microsoft.UI.Xaml.FrameworkElement`, `IPluginWindows.Attach` takes a `Microsoft.UI.Xaml.Window` and
`IPluginDialogs.ShowDialogAsync` a `Microsoft.UI.Xaml.Controls.ContentDialog`. The host checks each type at run time.
A plugin that builds WinUI objects references `Microsoft.WindowsAppSDK` compile-only too, at the version SumaFile ships.

## What the XML documentation covers

Every public type and member is documented, and the documentation ships in this package. It states:

- the thread each call runs on, and which host services may be called from any thread;
- the `object` hand-off rule above;
- the window options' ranges (320-3840 by 240-2160 DIPs, titles up to 120 characters);
- the folder flag grammar `^[A-Za-z][A-Za-z0-9_-]{0,63}$`;
- the deadlines the host applies (deactivation, backend start and handshake, row decorations, progress cancel).

The author guide, `docs/PLUGINS.md` in the SumaFile repository, covers `plugin.json`, the contribution kinds, the
backend protocol, trust and signing, packaging a `.sfplugin`, and the plugin template.

## License

MIT.
