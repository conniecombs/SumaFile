# Writing SumaFile plugins

## Host-shared assemblies

A plugin's UI assembly loads in its own assembly load context. An assembly whose name starts with one of these
prefixes always comes from SumaFile, never from the plugin folder, so the types a plugin uses from it are the host's
types. Every other managed assembly the plugin's `deps.json` lists loads from the plugin folder.
`PluginLoadContext_SharedPrefixes_MatchTheTableInPluginsDoc` checks this table against the host, so it is the
complete list.

| Prefix | What it covers |
| --- | --- |
| `SimpleFile.PluginSdk` | The SDK, the `SumaFile.PluginSdk` package. |
| `Microsoft.WindowsAppSDK` | The Windows App SDK's own assemblies. |
| `Microsoft.WindowsAppRuntime` | The Windows App SDK runtime's assemblies. |
| `Microsoft.UI` | WinUI 3 (`Microsoft.UI.Xaml` and the rest of `Microsoft.UI`). |
| `Microsoft.WinUI` | The WinUI 3 projection. |
| `Microsoft.InteractiveExperiences` | The Windows App SDK's input and windowing projection. |
| `Microsoft.Graphics.Display` | The Windows App SDK's display information. |
| `Microsoft.Graphics.Imaging` | The Windows App SDK's imaging projection. Win2D (`Microsoft.Graphics.Canvas`) is not shared: a plugin that uses it ships its own copy. |
| `Microsoft.Web.WebView2` | WebView2. |
| `Microsoft.Security.Authentication.OAuth` | The Windows App SDK's OAuth projection. |
| `Microsoft.Windows` | The Windows App SDK's `Microsoft.Windows.*` projections and the Windows SDK projection (`Microsoft.Windows.SDK.NET`). |
| `WinRT` | The C#/WinRT runtime. |
| `CommunityToolkit.Mvvm` | The MVVM Toolkit. |
| `System.` | The .NET runtime libraries. |
| `Microsoft.Extensions.` | The `Microsoft.Extensions.*` libraries the host ships. |

So a plugin is bound to the versions SumaFile ships of the packages behind these assemblies. Reference them
compile-only, at the versions below or lower, so no copy goes into the plugin folder:

```xml
<PackageReference Include="Microsoft.WindowsAppSDK" Version="2.4.0" ExcludeAssets="runtime" PrivateAssets="all" />
```

| Package | Version SumaFile ships |
| --- | --- |
| `Microsoft.WindowsAppSDK` | `2.4.0` |
| `Microsoft.Windows.SDK.BuildTools` | `10.0.28000.2526` |
| `CommunityToolkit.Mvvm` | `8.4.0` |

A plugin built against a newer version than SumaFile ships can fail to load, or fail when it first calls a member
the older version lacks. The same test checks these versions against the ones SumaFile builds with.

Native DLLs follow one rule. A native DLL the plugin's `deps.json` lists loads from the plugin folder, unless
SumaFile's own folder ships a DLL of that name. Then SumaFile's copy wins, because SumaFile's code already uses it.
