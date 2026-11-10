using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleFile.PluginHost.Manifest;

public static partial class PluginManifestReader
{
    // Source-generated: no pattern parsing or Regex construction on the startup path (spec §5.2 budget).
    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]{1,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerPattern();

    // A SumaFile settings key a declarative setting may migrate from (settings[].migrateFrom).
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LegacySettingsKeyPattern();

    private static readonly string[] ActivateOnValues = ["first-call", "startup"];
    private const int MaxWhenLength = 512;

    // Parked #73: every free-text string a manifest hands the host's UI has an upper bound. The id is bounded by its
    // grammar; a when expression by MaxWhenLength.
    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 2000;
    private const int MaxLabelLength = 200;
    private const int MaxSettingDescriptionLength = 1000;
    private const int MaxGlyphLength = 8;

    /// <summary>
    /// The largest <c>plugin.json</c> the host reads, 1 MiB, for discovery and the installer alike (parked #28). A
    /// larger one is invalid with <see cref="ManifestTooLargeMessage"/>, and is never read whole.
    /// </summary>
    public const int MaxManifestBytes = 1_048_576;

    /// <summary>The error a <c>plugin.json</c> larger than <see cref="MaxManifestBytes"/> gets.</summary>
    public const string ManifestTooLargeMessage = "plugin.json is larger than 1 MiB.";
    private static readonly string[] PlacementValues = ["primary", "overflow"];
    private static readonly string[] PanelPlacementValues = ["bottom", "right"];
    private static readonly string[] SettingTypes = ["boolean", "string", "integer", "enum", "path"];

    // Every contribution kind this host reads. Any other key is rejected, so a typo (or a kind a newer host added)
    // never loads as a silently missing slot.
    private static readonly string[] ContributionKinds =
        ["commands", "contextMenu", "toolbar", "archiveFormats", "sidebar", "settingsPage", "panels", "rowDecorations"];

    /// <summary>The plugin id grammar <c>^[a-z0-9][a-z0-9.-]{1,63}$</c>.</summary>
    internal static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public static bool IsSdkCompatible(string sdkVersion)
    {
        var m = SemVerPattern().Match(sdkVersion ?? string.Empty);
        return m.Success
            && int.TryParse(m.Groups[1].Value, out var major)
            && major == SimpleFile.PluginSdk.PluginSdk.Major;
    }

    /// <summary>
    /// Reads a <c>plugin.json</c> from <paramref name="stream"/>, taking at most <see cref="MaxManifestBytes"/> + 1 bytes.
    /// False, with no bytes, when the manifest is larger than the cap: a stream that reports a length over the cap is not
    /// read at all, and any other is read only until the cap is passed, so a long or growing file is never held whole.
    /// </summary>
    public static bool TryReadManifestBytes(Stream stream, out byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        bytes = [];
        long known = -1;
        if (stream.CanSeek)
        {
            known = Math.Max(0, stream.Length - stream.Position);
            if (known > MaxManifestBytes) return false;
        }

        // One byte past the expected end, so a file that grew since its length was read is still caught; a stream of
        // unknown length starts small and doubles up to the cap + 1.
        var buffer = new byte[known >= 0 ? known + 1 : Math.Min(16 * 1024, MaxManifestBytes + 1)];
        var total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                if (buffer.Length > MaxManifestBytes) break;
                Array.Resize(ref buffer, (int)Math.Min(buffer.Length * 2L, MaxManifestBytes + 1L));
            }

            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }

        if (total > MaxManifestBytes) return false;
        bytes = total == buffer.Length ? buffer : buffer.AsSpan(0, total).ToArray();
        return true;
    }

    public static ManifestReadResult Read(string json, string folderName)
    {
        var errors = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ManifestReadResult(null, [$"The manifest is not valid JSON: {ex.Message}"]);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ManifestReadResult(null, ["The manifest root must be a JSON object."]);

            var id = RequiredString(root, "id", "id", errors);
            if (id is not null)
            {
                if (!IdPattern().IsMatch(id))
                    errors.Add($"The id '{id}' is invalid: use 2 to 64 lowercase letters, digits, dots or hyphens, starting with a letter or digit.");
                else if (!string.Equals(id, folderName, StringComparison.Ordinal))
                    errors.Add($"The id '{id}' does not match the plugin folder name '{folderName}'; the folder must be named exactly after the id.");
            }

            var name = Bounded(RequiredString(root, "name", "name", errors), "name", MaxNameLength, errors);
            // Not kept: the host shows no description yet, but a third-party manifest still cannot carry an unbounded one.
            Bounded(OptionalString(root, "description", "description", errors), "description", MaxDescriptionLength, errors);
            var version = RequiredString(root, "version", "version", errors);
            var sdkVersion = RequiredString(root, "sdkVersion", "sdkVersion", errors);
            if (sdkVersion is not null)
            {
                if (!SemVerPattern().IsMatch(sdkVersion))
                    errors.Add($"The sdkVersion '{sdkVersion}' is not a valid semantic version (major.minor.patch).");
                else if (!IsSdkCompatible(sdkVersion))
                    errors.Add($"The sdkVersion '{sdkVersion}' is not compatible with this host (supports major version {SimpleFile.PluginSdk.PluginSdk.Major}).");
            }

            var publisher = ReadPublisher(root, errors);
            var ui = ReadUi(root, errors);
            var backend = ReadBackend(root, errors);
            var declaresUi = root.TryGetProperty("ui", out var uiElement) && uiElement.ValueKind != JsonValueKind.Null;
            var contributions = ReadContributions(root, declaresUi, errors);
            var settings = ReadSettings(root, errors);
            var permissions = OptionalStringList(root, "permissions", "permissions", errors) ?? [];

            // The service connects to an archive engine's pipe alongside the host (spec section 6.4), so the
            // backend must accept a second client. Such plugins are always started at startup; see
            // PluginManager.StartsAtStartup.
            if (contributions.ArchiveFormats.Count > 0 && backend is not { AcceptsMultipleClients: true })
                errors.Add("A plugin that declares contributions.archiveFormats needs a backend with backend.acceptsMultipleClients set to true.");

            if (errors.Count > 0)
                return new ManifestReadResult(null, errors);

            return new ManifestReadResult(
                new PluginManifest(id!, name!, version!, publisher!, sdkVersion!, ui, backend, contributions, settings, permissions),
                errors);
        }
    }

    private static PluginPublisher? ReadPublisher(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("publisher", out var el) || el.ValueKind == JsonValueKind.Null)
        {
            errors.Add("The publisher section is required (publisher.name must be set).");
            return null;
        }
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add("The publisher field must be an object.");
            return null;
        }
        var name = Bounded(RequiredString(el, "name", "publisher.name", errors), "publisher.name", MaxNameLength, errors);
        var url = OptionalString(el, "url", "publisher.url", errors);
        var key = OptionalString(el, "publicKey", "publisher.publicKey", errors);
        return name is null ? null : new PluginPublisher(name, url, key);
    }

    private static PluginUiSpec? ReadUi(JsonElement root, List<string> errors)
    {
        var el = OptionalObject(root, "ui", "ui", errors);
        if (el is null) return null;
        var assembly = RequiredString(el.Value, "assembly", "ui.assembly", errors);
        RequireBareFileName(assembly, "ui.assembly", errors);
        var entry = RequiredString(el.Value, "entryType", "ui.entryType", errors);
        return assembly is null || entry is null ? null : new PluginUiSpec(assembly, entry);
    }

    private static PluginBackendSpec? ReadBackend(JsonElement root, List<string> errors)
    {
        var el = OptionalObject(root, "backend", "backend", errors);
        if (el is null) return null;
        var exe = RequiredString(el.Value, "executable", "backend.executable", errors);
        RequireBareFileName(exe, "backend.executable", errors);
        var args = OptionalStringList(el.Value, "args", "backend.args", errors) ?? [];
        var activateOn = OptionalString(el.Value, "activateOn", "backend.activateOn", errors) ?? "first-call";
        if (!ActivateOnValues.Contains(activateOn, StringComparer.Ordinal))
            errors.Add($"The backend.activateOn value '{activateOn}' is not recognised; use 'first-call' or 'startup'.");
        var multi = OptionalBool(el.Value, "acceptsMultipleClients", "backend.acceptsMultipleClients", errors);
        return exe is null ? null : new PluginBackendSpec(exe, args, activateOn, multi);
    }

    private static PluginContributions ReadContributions(JsonElement root, bool declaresUi, List<string> errors)
    {
        var commands = new List<PluginCommandSpec>();
        var menus = new List<PluginContextMenuSpec>();
        var toolbar = new List<PluginToolbarSpec>();
        var formats = new List<PluginArchiveFormatSpec>();
        var sidebar = new List<PluginSidebarSpec>();
        var panels = new List<PluginPanelSpec>();
        PluginSettingsPageSpec? settingsPage = null;
        PluginRowDecorationSpec? rowDecorations = null;

        var el = OptionalObject(root, "contributions", "contributions", errors);
        if (el is not null)
        {
            foreach (var kind in el.Value.EnumerateObject())
            {
                if (!ContributionKinds.Contains(kind.Name, StringComparer.Ordinal))
                    errors.Add($"The contributions.{kind.Name} kind is not supported by this host.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (item, path) in Items(el.Value, "commands", "contributions.commands", errors))
            {
                var id = RequiredCommand(item, "id", path + ".id", errors);
                var label = Label(RequiredString(item, "label", path + ".label", errors), path + ".label", errors);
                var group = Label(OptionalString(item, "group", path + ".group", errors), path + ".group", errors) ?? "";
                var shortcut = OptionalString(item, "shortcut", path + ".shortcut", errors);
                if (id is null) continue;
                if (!seen.Add(id))
                    errors.Add($"The command id '{id}' is declared more than once in contributions.commands.");
                if (label is not null)
                    commands.Add(new PluginCommandSpec(id, label, group, shortcut));
            }

            foreach (var (item, path) in Items(el.Value, "contextMenu", "contributions.contextMenu", errors))
            {
                var group = Label(OptionalString(item, "group", path + ".group", errors), path + ".group", errors) ?? "";
                var items = new List<PluginContextMenuItemSpec>();
                foreach (var (sub, subPath) in Items(item, "items", path + ".items", errors))
                {
                    var cmd = RequiredCommand(sub, "command", subPath + ".command", errors);
                    var label = Label(RequiredString(sub, "label", subPath + ".label", errors), subPath + ".label", errors);
                    var when = ReadWhen(sub, subPath, errors);
                    if (cmd is not null && label is not null)
                        items.Add(new PluginContextMenuItemSpec(cmd, label, when));
                }
                menus.Add(new PluginContextMenuSpec(group, items));
            }

            // The catalog keys toolbar actions by command, so a repeated one would be dropped without a word.
            var toolbarCommands = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (item, path) in Items(el.Value, "toolbar", "contributions.toolbar", errors))
            {
                var cmd = RequiredCommand(item, "command", path + ".command", errors);
                if (cmd is not null && !toolbarCommands.Add(cmd))
                {
                    errors.Add($"The toolbar command '{cmd}' is declared more than once in contributions.toolbar.");
                    cmd = null;
                }

                var label = Label(RequiredString(item, "label", path + ".label", errors), path + ".label", errors);
                var glyph = Bounded(OptionalString(item, "glyph", path + ".glyph", errors), path + ".glyph", MaxGlyphLength, errors) ?? "";
                var hasPlacement = item.TryGetProperty("placement", out var pEl) && pEl.ValueKind != JsonValueKind.Null;
                var placement = OptionalString(item, "placement", path + ".placement", errors);
                if (!hasPlacement)
                    placement = "overflow";
                else if (placement is not null && !PlacementValues.Contains(placement, StringComparer.Ordinal))
                    errors.Add($"The {path}.placement value '{placement}' is not recognised; use 'primary' or 'overflow'.");
                var when = ReadWhen(item, path, errors);
                if (cmd is not null && label is not null && placement is not null && PlacementValues.Contains(placement, StringComparer.Ordinal))
                    toolbar.Add(new PluginToolbarSpec(cmd, label, glyph, placement) { When = when });
            }

            foreach (var (item, path) in Items(el.Value, "archiveFormats", "contributions.archiveFormats", errors))
            {
                var ext = RequiredString(item, "extension", path + ".extension", errors);
                var engine = RequiredString(item, "engineName", path + ".engineName", errors);
                var canList = OptionalBool(item, "canList", path + ".canList", errors);
                var canExtract = OptionalBool(item, "canExtract", path + ".canExtract", errors);
                var canCreate = OptionalBool(item, "canCreate", path + ".canCreate", errors);
                var canModify = OptionalBool(item, "canModify", path + ".canModify", errors);
                if (ext is not null && engine is not null)
                    formats.Add(new PluginArchiveFormatSpec(ext, canList, canExtract, canCreate, canModify, engine));
            }

            ReadSidebar(el.Value, seen, sidebar, errors, out var declaresSidebar);
            if (declaresSidebar && !declaresUi)
                errors.Add("contributions.sidebar needs a ui assembly.");

            var page = OptionalObject(el.Value, "settingsPage", "contributions.settingsPage", errors);
            if (page is not null)
            {
                var provider = RequiredString(page.Value, "provider", "contributions.settingsPage.provider", errors);
                if (!declaresUi)
                    errors.Add("contributions.settingsPage needs a ui assembly.");
                if (provider is not null)
                    settingsPage = new PluginSettingsPageSpec(provider);
            }

            ReadPanels(el.Value, seen, panels, errors, out var declaresPanels);
            if (declaresPanels && !declaresUi)
                errors.Add("contributions.panels needs a ui assembly.");

            var decorations = OptionalObject(el.Value, "rowDecorations", "contributions.rowDecorations", errors);
            if (decorations is not null)
            {
                var provider = RequiredString(decorations.Value, "provider", "contributions.rowDecorations.provider", errors);
                if (!declaresUi)
                    errors.Add("contributions.rowDecorations needs a ui assembly.");
                if (provider is not null)
                    rowDecorations = new PluginRowDecorationSpec(provider);
            }
        }

        return new PluginContributions(commands, menus, toolbar, formats)
        {
            Sidebar = sidebar,
            SettingsPage = settingsPage,
            Panels = panels,
            RowDecorations = rowDecorations,
        };
    }

    /// <summary>
    /// A required command id, declared or referenced: it must follow the plugin command grammar
    /// (<see cref="SimpleFile.Core.CommandSurfaceLayout.IsPluginCommand"/>), so every plugin command makes a toolbar item
    /// id the command surface keeps. An id outside it is reported and read as missing.
    /// </summary>
    private static string? RequiredCommand(JsonElement item, string name, string path, List<string> errors)
    {
        var command = RequiredString(item, name, path, errors);
        if (command is null || SimpleFile.Core.CommandSurfaceLayout.IsPluginCommand(command)) return command;
        errors.Add($"The {path} value '{command}' is invalid: use 1 to {SimpleFile.Core.CommandSurfaceLayout.MaxPluginCommandLength} characters with no whitespace.");
        return null;
    }

    /// <summary>
    /// A contribution's optional <c>when</c> expression: at most 512 characters, in <see cref="SimpleFile.Core.PluginMenuWhen"/>'s
    /// grammar. An invalid one is reported and read as empty.
    /// </summary>
    private static string ReadWhen(JsonElement item, string path, List<string> errors)
    {
        var when = OptionalString(item, "when", path + ".when", errors) ?? "";
        if (when.Length > MaxWhenLength)
        {
            errors.Add($"The {path}.when expression is longer than {MaxWhenLength} characters.");
            return "";
        }

        if (!SimpleFile.Core.PluginMenuWhen.IsValid(when, out var whenError))
        {
            errors.Add($"The {path}.when expression is invalid: {whenError}");
            return "";
        }

        return when;
    }

    /// <summary>
    /// <c>contributions.panels</c>: each id follows the settings key grammar (the host keys panel state by it) and is
    /// unique; title and provider are required; placement is <c>bottom</c> (the default) or <c>right</c>; detachable
    /// defaults to false; an optional command must name a declared command and toggles at most one panel.
    /// </summary>
    private static void ReadPanels(
        JsonElement contributions, HashSet<string> commandIds, List<PluginPanelSpec> panels, List<string> errors, out bool declared)
    {
        declared = false;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var toggles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, path) in Items(contributions, "panels", "contributions.panels", errors))
        {
            declared = true;
            var id = RequiredString(item, "id", path + ".id", errors);
            var title = Label(RequiredString(item, "title", path + ".title", errors), path + ".title", errors);
            var provider = RequiredString(item, "provider", path + ".provider", errors);
            var placement = OptionalString(item, "placement", path + ".placement", errors) ?? "bottom";
            var detachable = OptionalBool(item, "detachable", path + ".detachable", errors);
            var command = OptionalString(item, "command", path + ".command", errors);
            if (id is not null && !Backend.PluginHostMethods.IsValidSettingKey(id))
            {
                errors.Add($"The {path}.id value '{id}' is invalid: use 1 to 64 letters, digits, '_' or '-', starting with a lowercase letter or digit.");
                id = null;
            }
            if (id is not null && !seenIds.Add(id))
                errors.Add($"The panel id '{id}' is declared more than once in contributions.panels.");
            var placementKnown = PanelPlacementValues.Contains(placement, StringComparer.Ordinal);
            if (!placementKnown)
                errors.Add($"The {path}.placement value '{placement}' is not recognised; use 'right' or 'bottom'.");
            if (command is not null && !commandIds.Contains(command))
            {
                errors.Add($"The {path}.command value '{command}' does not name a command declared in contributions.commands.");
                command = null;
            }
            else if (command is not null && !toggles.Add(command))
            {
                errors.Add($"The {path}.command value '{command}' already toggles another panel.");
                command = null;
            }
            if (id is not null && title is not null && provider is not null && placementKnown)
                panels.Add(new PluginPanelSpec(id, title, provider, placement, detachable, command));
        }
    }

    /// <summary>
    /// <c>contributions.sidebar</c>: each id follows the settings key grammar (the host keys collapse state by it) and is
    /// unique; title and provider are required; an optional command must name a declared command.
    /// </summary>
    private static void ReadSidebar(
        JsonElement contributions, HashSet<string> commandIds, List<PluginSidebarSpec> sidebar, List<string> errors, out bool declared)
    {
        declared = false;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, path) in Items(contributions, "sidebar", "contributions.sidebar", errors))
        {
            declared = true;
            var id = RequiredString(item, "id", path + ".id", errors);
            var title = Label(RequiredString(item, "title", path + ".title", errors), path + ".title", errors);
            var provider = RequiredString(item, "provider", path + ".provider", errors);
            var command = OptionalString(item, "command", path + ".command", errors);
            if (id is not null && !Backend.PluginHostMethods.IsValidSettingKey(id))
            {
                errors.Add($"The {path}.id value '{id}' is invalid: use 1 to 64 letters, digits, '_' or '-', starting with a lowercase letter or digit.");
                id = null;
            }
            if (id is not null && !seenIds.Add(id))
                errors.Add($"The sidebar id '{id}' is declared more than once in contributions.sidebar.");
            if (command is not null && !commandIds.Contains(command))
            {
                errors.Add($"The {path}.command value '{command}' does not name a command declared in contributions.commands.");
                command = null;
            }
            if (id is not null && title is not null && provider is not null)
                sidebar.Add(new PluginSidebarSpec(id, title, provider, command));
        }
    }

    private static List<PluginSettingSpec> ReadSettings(JsonElement root, List<string> errors)
    {
        var result = new List<PluginSettingSpec>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, path) in Items(root, "settings", "settings", errors))
        {
            var key = RequiredString(item, "key", path + ".key", errors);
            var type = RequiredString(item, "type", path + ".type", errors);
            var label = Label(RequiredString(item, "label", path + ".label", errors), path + ".label", errors);
            var description = Bounded(
                OptionalString(item, "description", path + ".description", errors), path + ".description", MaxSettingDescriptionLength, errors);
            var group = Label(OptionalString(item, "group", path + ".group", errors), path + ".group", errors);
            var options = OptionalStringList(item, "options", path + ".options", errors);
            var migrateFrom = OptionalString(item, "migrateFrom", path + ".migrateFrom", errors);
            if (migrateFrom is not null)
            {
                if (!LegacySettingsKeyPattern().IsMatch(migrateFrom))
                {
                    errors.Add($"The {path}.migrateFrom value '{migrateFrom}' is not a valid settings key.");
                    migrateFrom = null;
                }
                // plugin.<id>.<key> rows belong to plugins: a migration must never read another plugin's settings.
                else if (migrateFrom.StartsWith("plugin.", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"The {path}.migrateFrom value '{migrateFrom}' must not name a plugin setting.");
                    migrateFrom = null;
                }
            }
            JsonElement? def = item.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Null
                ? d.Clone()
                : null;
            if (type is not null && !SettingTypes.Contains(type, StringComparer.Ordinal))
                errors.Add($"The {path}.type value '{type}' is not recognised; use boolean, string, integer, enum or path.");
            // Same grammar host.get_settings, host.set_setting and IPluginSettings enforce, so a declared key can
            // never reach into another plugin's plugin.<id>.<key> namespace.
            if (key is not null && !Backend.PluginHostMethods.IsValidSettingKey(key))
            {
                errors.Add($"The {path}.key value '{key}' is invalid: use 1 to 64 letters, digits, '_' or '-', starting with a lowercase letter or digit.");
                key = null;
            }
            if (key is not null && !seenKeys.Add(key))
                errors.Add($"The setting key '{key}' is declared more than once in settings.");
            if (type == "enum" && (options is null || options.Count == 0))
                errors.Add($"The {path}.options field must be a non-empty array for an enum setting.");
            else if (type is not null && type != "enum" && options is not null)
                errors.Add($"The {path}.options field is only allowed on enum settings.");
            if (key is not null && type is not null && label is not null)
                result.Add(new PluginSettingSpec(key, type, label, description, def, group, options) { MigrateFrom = migrateFrom });
        }
        return result;
    }

    /// <summary>A label, title or group: at most <see cref="MaxLabelLength"/> characters (<see cref="Bounded"/>).</summary>
    private static string? Label(string? value, string path, List<string> errors) =>
        Bounded(value, path, MaxLabelLength, errors);

    /// <summary>
    /// <paramref name="value"/> when it is at most <paramref name="max"/> UTF-16 characters, else null with the error
    /// <c>The &lt;path&gt; field is longer than &lt;max&gt; characters.</c>, which makes the manifest invalid. A length
    /// check only, so it adds nothing measurable to a startup scan.
    /// </summary>
    private static string? Bounded(string? value, string path, int max, List<string> errors)
    {
        if (value is null || value.Length <= max) return value;
        errors.Add($"The {path} field is longer than {max} characters.");
        return null;
    }

    private static void RequireBareFileName(string? value, string path, List<string> errors)
    {
        if (value is null) return;
        if (value.IndexOfAny(['/', '\\', ':']) >= 0 || value is "." or "..")
            errors.Add($"The {path} value '{value}' must be a bare file name without directories, drive letters or '..' segments.");
    }

    private static IEnumerable<(JsonElement Item, string Path)> Items(JsonElement parent, string property, string path, List<string> errors)
    {
        if (!parent.TryGetProperty(property, out var arr) || arr.ValueKind == JsonValueKind.Null)
            yield break;
        if (arr.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"The {path} field must be an array.");
            yield break;
        }
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            var itemPath = $"{path}[{i++}]";
            if (el.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"The {itemPath} entry in {path} must be an object.");
                continue;
            }
            yield return (el, itemPath);
        }
    }

    private static JsonElement? OptionalObject(JsonElement parent, string property, string path, List<string> errors)
    {
        if (!parent.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"The {path} field must be an object.");
            return null;
        }
        return el;
    }

    private static string? RequiredString(JsonElement parent, string property, string path, List<string> errors)
    {
        if (!parent.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            errors.Add($"The {path} field is required.");
            return null;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            errors.Add($"The {path} field must be a string.");
            return null;
        }
        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
        {
            errors.Add($"The {path} field must not be empty.");
            return null;
        }
        return s;
    }

    private static string? OptionalString(JsonElement parent, string property, string path, List<string> errors)
    {
        if (!parent.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.String)
        {
            errors.Add($"The {path} field must be a string.");
            return null;
        }
        return el.GetString();
    }

    private static bool OptionalBool(JsonElement parent, string property, string path, List<string> errors)
    {
        if (!parent.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null)
            return false;
        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean();
        errors.Add($"The {path} field must be true or false.");
        return false;
    }

    private static List<string>? OptionalStringList(JsonElement parent, string property, string path, List<string> errors)
    {
        if (!parent.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"The {path} field must be an array of strings.");
            return null;
        }
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add($"The {path} field must contain only strings.");
                return null;
            }
            list.Add(item.GetString()!);
        }
        return list;
    }
}
