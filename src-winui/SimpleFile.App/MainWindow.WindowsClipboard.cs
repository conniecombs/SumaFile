using SimpleFile.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace SimpleFile.App;

public sealed partial class MainWindow
{
    private sealed record ClipboardTransferPayload(
        ClipboardOperation Operation,
        string[] SourcePaths,
        bool IsInternal);

    /// <summary>
    /// Custom clipboard format carrying the <see cref="ClipboardState.Stamp"/> of the export, so the
    /// app can tell whether the system clipboard still holds what SumaFile last copied.
    /// </summary>
    private const string ClipboardStampFormat = "SumaFile.ClipboardStamp";

    private void AttachWindowsClipboard()
    {
        Clipboard.ContentChanged += OnWindowsClipboardContentChanged;
    }

    private void DetachWindowsClipboard()
    {
        Clipboard.ContentChanged -= OnWindowsClipboardContentChanged;
    }

    private void OnWindowsClipboardContentChanged(object? sender, object e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var workspace = _workspace;
            if (workspace is null || !workspace.Clipboard.HasItems)
            {
                return;
            }

            var stamp = await TryReadWindowsClipboardStampAsync();
            workspace.Clipboard.ReconcileWith(stamp);
            if (!workspace.Clipboard.HasItems)
            {
                SyncFromWorkspace();
            }
        });
    }

    private static async Task<string?> TryReadWindowsClipboardStampAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(ClipboardStampFormat))
            {
                return null;
            }

            return await content.GetDataAsync(ClipboardStampFormat) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasWindowsFileClipboardContent()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.StorageItems);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TrySetWindowsFileClipboardAsync(
        string[] paths,
        ClipboardOperation operation,
        string stamp)
    {
        try
        {
            var package = new DataPackage
            {
                RequestedOperation = operation == ClipboardOperation.Cut
                    ? DataPackageOperation.Move
                    : DataPackageOperation.Copy,
            };

            package.SetText(string.Join(Environment.NewLine, paths));
            package.SetData(ClipboardStampFormat, stamp);

            var items = new List<IStorageItem>();
            foreach (var path in paths)
            {
                var item = await TryResolveStorageItemAsync(path);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            if (items.Count > 0)
            {
                package.SetStorageItems(items);
            }

            Clipboard.SetContent(package);
            Clipboard.Flush();
            return items.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ClipboardTransferPayload?> TryReadWindowsFileClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            var operation = content.RequestedOperation == DataPackageOperation.Move
                ? ClipboardOperation.Cut
                : ClipboardOperation.Copy;

            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                var paths = items
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (paths.Length > 0)
                {
                    return new ClipboardTransferPayload(operation, paths, IsInternal: false);
                }
            }

            if (content.Contains(StandardDataFormats.Text))
            {
                var paths = ParseClipboardPathText(await content.GetTextAsync());
                if (paths.Length > 0)
                {
                    return new ClipboardTransferPayload(ClipboardOperation.Copy, paths, IsInternal: false);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static async Task<IStorageItem?> TryResolveStorageItemAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return await StorageFolder.GetFolderFromPathAsync(path);
            }

            if (File.Exists(path))
            {
                return await StorageFile.GetFileFromPathAsync(path);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string[] ParseClipboardPathText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Trim('"'))
            .Where(path => path.Length > 0 && (File.Exists(path) || Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
