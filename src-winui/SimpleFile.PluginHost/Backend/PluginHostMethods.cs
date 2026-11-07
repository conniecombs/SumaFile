using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using SimpleFile.Core;
using SimpleFile.Ipc;

namespace SimpleFile.PluginHost.Backend;

/// <summary>The <c>host.*</c> JSON-RPC methods a plugin backend can call.</summary>
public static partial class PluginHostMethods
{
    public const string GetSettings = "host.get_settings";
    public const string SetSetting = "host.set_setting";
    public const string ProgressBegin = "host.progress_begin";
    public const string ProgressReport = "host.progress_report";
    public const string ProgressComplete = "host.progress_complete";
    public const string ProgressFail = "host.progress_fail";
    public const string Log = "host.log";
    public const string Notify = "host.notify";

    [GeneratedRegex("^[a-z0-9][a-zA-Z0-9_-]{0,63}\\z")]
    private static partial Regex KeyPattern();

    public static string SettingsKey(string pluginId, string key) => $"plugin.{pluginId}.{key}";

    public static bool IsValidSettingKey(string key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>Registers handlers only; performs no awaits, so it is safe to call from the pre-handshake configure callback.</summary>
    public static void Register(
        PluginBackendConnection connection,
        string pluginId,
        ISettingsBackend settings,
        IPluginProgressSink progress,
        IPluginNotifier notifier,
        Action<string, string> log)
    {
        // Operations this backend started; ids from elsewhere are rejected.
        var operations = new ConcurrentDictionary<string, byte>();

        // A dead backend can never finish its operations; fail them so the UI does not show them forever.
        connection.Lost += (_, _) =>
        {
            foreach (var openId in operations.Keys.ToList())
            {
                if (operations.TryRemove(openId, out var removed))
                {
                    try { progress.Fail(openId, "Plugin backend stopped."); } catch { }
                }
            }
        };

        connection.OnHostRequest(GetSettings, async (p, ct) =>
        {
            if (p.ValueKind != JsonValueKind.Object
                || !p.TryGetProperty("keys", out var keysEl)
                || keysEl.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("'keys' must be an array.");
            }

            var keys = new List<string>();
            foreach (var item in keysEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !IsValidSettingKey(item.GetString()!))
                {
                    throw Invalid("Invalid settings key.");
                }

                keys.Add(item.GetString()!);
            }

            var prefixed = keys.Select(k => SettingsKey(pluginId, k)).ToList();
            var raw = await settings.GetSettingsAsync(prefixed, ct).ConfigureAwait(false);
            var values = new Dictionary<string, string?>();
            for (var i = 0; i < keys.Count; i++)
            {
                values[keys[i]] = raw.TryGetValue(prefixed[i], out var v) ? v : null;
            }

            return (object?)new { values };
        });

        connection.OnHostRequest(SetSetting, async (p, ct) =>
        {
            var key = RequireString(p, "key");
            var value = RequireString(p, "value");
            if (!IsValidSettingKey(key))
            {
                throw Invalid("Invalid settings key.");
            }

            await settings.SetSettingAsync(SettingsKey(pluginId, key), value, ct).ConfigureAwait(false);
            return (object?)null;
        });

        connection.OnHostRequest(ProgressBegin, (p, _) =>
        {
            var title = RequireString(p, "title");
            string? id = null;
            id = progress.Begin(pluginId, title, () => SendCancel(connection, id!));
            operations[id] = 0;
            return Task.FromResult<object?>(new { operationId = id });
        });

        connection.OnHostRequest(ProgressReport, (p, _) =>
        {
            var id = RequireOperation(p, operations);
            var current = RequireLong(p, "current");
            var total = RequireLong(p, "total");
            string? detail = null;
            if (p.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
            {
                detail = d.GetString();
            }

            progress.Report(id, current, total, detail);
            return Task.FromResult<object?>(null);
        });

        connection.OnHostRequest(ProgressComplete, (p, _) =>
        {
            var id = RequireOperation(p, operations);
            operations.TryRemove(id, out var removed);
            progress.Complete(id);
            return Task.FromResult<object?>(null);
        });

        connection.OnHostRequest(ProgressFail, (p, _) =>
        {
            var id = RequireOperation(p, operations);
            var message = RequireString(p, "message");
            operations.TryRemove(id, out var removed);
            progress.Fail(id, message);
            return Task.FromResult<object?>(null);
        });

        connection.OnHostRequest(Log, (p, _) =>
        {
            var level = RequireString(p, "level");
            if (level is not ("info" or "warn" or "error"))
            {
                throw Invalid("'level' must be info, warn or error.");
            }

            log(pluginId, $"[{level}] {RequireString(p, "message")}");
            return Task.FromResult<object?>(null);
        });

        connection.OnHostRequest(Notify, (p, _) =>
        {
            var message = RequireString(p, "message");
            var isError = p.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
            notifier.Notify(pluginId, message, isError);
            return Task.FromResult<object?>(null);
        });
    }

    private static void SendCancel(PluginBackendConnection connection, string operationId)
    {
        // Fire and forget; a dead pipe just means there is nothing left to cancel.
        _ = Task.Run(async () =>
        {
            try { await connection.InvokeAsync<object?>("cancel", new { operationId }, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        });
    }

    private static IpcException Invalid(string message) => new(Protocol.ErrInvalidParams, message);

    private static string RequireString(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty(name, out var el)
            || el.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"'{name}' must be a string.");
        }

        return el.GetString()!;
    }

    private static long RequireLong(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty(name, out var el)
            || el.ValueKind != JsonValueKind.Number
            || !el.TryGetInt64(out var value))
        {
            throw Invalid($"'{name}' must be an integer.");
        }

        return value;
    }

    private static string RequireOperation(JsonElement p, ConcurrentDictionary<string, byte> operations)
    {
        var id = RequireString(p, "operationId");
        if (!operations.ContainsKey(id))
        {
            throw Invalid("Unknown operationId.");
        }

        return id;
    }
}
