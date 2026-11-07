using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleFile.PluginHost.Manifest;

public static class PluginManifestReader
{
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9.-]{1,63}\z",RegexOptions.CultureInvariant);
    private static readonly Regex SemVerPattern = new(@"^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$", RegexOptions.CultureInvariant);

    private static readonly string[] ActivateOnValues = ["first-call", "startup"];
    private static readonly string[] PlacementValues = ["primary", "overflow"];
    private static readonly string[] SettingTypes = ["boolean", "string", "integer", "enum", "path"];

    public static bool IsSdkCompatible(string sdkVersion)
    {
        var m = SemVerPattern.Match(sdkVersion ?? string.Empty);
        return m.Success
            && int.TryParse(m.Groups[1].Value, out var major)
            && major == SimpleFile.PluginSdk.PluginSdk.Major;
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
                if (!IdPattern.IsMatch(id))
                    errors.Add($"The id '{id}' is invalid: use 2 to 64 lowercase letters, digits, dots or hyphens, starting with a letter or digit.");
                else if (!string.Equals(id, folderName, StringComparison.Ordinal))
                    errors.Add($"The id '{id}' does not match the plugin folder name '{folderName}'; the folder must be named exactly after the id.");
            }

            var name = RequiredString(root, "name", "name", errors);
            var version = RequiredString(root, "version", "version", errors);
            var sdkVersion = RequiredString(root, "sdkVersion", "sdkVersion", errors);
            if (sdkVersion is not null)
            {
                if (!SemVerPattern.IsMatch(sdkVersion))
                    errors.Add($"The sdkVersion '{sdkVersion}' is not a valid semantic version (major.minor.patch).");
                else if (!IsSdkCompatible(sdkVersion))
                    errors.Add($"The sdkVersion '{sdkVersion}' is not compatible with this host (supports major version {SimpleFile.PluginSdk.PluginSdk.Major}).");
            }

            var publisher = ReadPublisher(root, errors);
            var ui = ReadUi(root, errors);
            var backend = ReadBackend(root, errors);
            var contributions = ReadContributions(root, errors);
            var settings = ReadSettings(root, errors);
            var permissions = OptionalStringList(root, "permissions", "permissions", errors) ?? [];

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
        var name = RequiredString(el, "name", "publisher.name", errors);
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

    private static PluginContributions ReadContributions(JsonElement root, List<string> errors)
    {
        var commands = new List<PluginCommandSpec>();
        var menus = new List<PluginContextMenuSpec>();
        var toolbar = new List<PluginToolbarSpec>();
        var formats = new List<PluginArchiveFormatSpec>();

        var el = OptionalObject(root, "contributions", "contributions", errors);
        if (el is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (item, path) in Items(el.Value, "commands", "contributions.commands", errors))
            {
                var id = RequiredString(item, "id", path + ".id", errors);
                var label = RequiredString(item, "label", path + ".label", errors);
                var group = OptionalString(item, "group", path + ".group", errors) ?? "";
                var shortcut = OptionalString(item, "shortcut", path + ".shortcut", errors);
                if (id is null) continue;
                if (!seen.Add(id))
                    errors.Add($"The command id '{id}' is declared more than once in contributions.commands.");
                if (label is not null)
                    commands.Add(new PluginCommandSpec(id, label, group, shortcut));
            }

            foreach (var (item, path) in Items(el.Value, "contextMenu", "contributions.contextMenu", errors))
            {
                var group = OptionalString(item, "group", path + ".group", errors) ?? "";
                var items = new List<PluginContextMenuItemSpec>();
                foreach (var (sub, subPath) in Items(item, "items", path + ".items", errors))
                {
                    var cmd = RequiredString(sub, "command", subPath + ".command", errors);
                    var label = RequiredString(sub, "label", subPath + ".label", errors);
                    var when = OptionalString(sub, "when", subPath + ".when", errors) ?? "";
                    if (cmd is not null && label is not null)
                        items.Add(new PluginContextMenuItemSpec(cmd, label, when));
                }
                menus.Add(new PluginContextMenuSpec(group, items));
            }

            foreach (var (item, path) in Items(el.Value, "toolbar", "contributions.toolbar", errors))
            {
                var cmd = RequiredString(item, "command", path + ".command", errors);
                var label = RequiredString(item, "label", path + ".label", errors);
                var glyph = OptionalString(item, "glyph", path + ".glyph", errors) ?? "";
                var hasPlacement = item.TryGetProperty("placement", out var pEl) && pEl.ValueKind != JsonValueKind.Null;
                var placement = OptionalString(item, "placement", path + ".placement", errors);
                if (!hasPlacement)
                    placement = "overflow";
                else if (placement is not null && !PlacementValues.Contains(placement, StringComparer.Ordinal))
                    errors.Add($"The {path}.placement value '{placement}' is not recognised; use 'primary' or 'overflow'.");
                if (cmd is not null && label is not null && placement is not null && PlacementValues.Contains(placement, StringComparer.Ordinal))
                    toolbar.Add(new PluginToolbarSpec(cmd, label, glyph, placement));
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
        }

        return new PluginContributions(commands, menus, toolbar, formats);
    }

    private static List<PluginSettingSpec> ReadSettings(JsonElement root, List<string> errors)
    {
        var result = new List<PluginSettingSpec>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, path) in Items(root, "settings", "settings", errors))
        {
            var key = RequiredString(item, "key", path + ".key", errors);
            var type = RequiredString(item, "type", path + ".type", errors);
            var label = RequiredString(item, "label", path + ".label", errors);
            var description = OptionalString(item, "description", path + ".description", errors);
            var group = OptionalString(item, "group", path + ".group", errors);
            var options = OptionalStringList(item, "options", path + ".options", errors);
            JsonElement? def = item.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Null
                ? d.Clone()
                : null;
            if (type is not null && !SettingTypes.Contains(type, StringComparer.Ordinal))
                errors.Add($"The {path}.type value '{type}' is not recognised; use boolean, string, integer, enum or path.");
            if (key is not null && !seenKeys.Add(key))
                errors.Add($"The setting key '{key}' is declared more than once in settings.");
            if (type == "enum" && (options is null || options.Count == 0))
                errors.Add($"The {path}.options field must be a non-empty array for an enum setting.");
            else if (type is not null && type != "enum" && options is not null)
                errors.Add($"The {path}.options field is only allowed on enum settings.");
            if (key is not null && type is not null && label is not null)
                result.Add(new PluginSettingSpec(key, type, label, description, def, group, options));
        }
        return result;
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
