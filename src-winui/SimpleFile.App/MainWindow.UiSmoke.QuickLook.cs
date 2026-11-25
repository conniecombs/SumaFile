#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;

namespace SimpleFile.App;

// Quick Look as a lightbox: the counter beside the name, stepping with Right, zooming an image from the keyboard,
// and the Source | Rendered switch of a Markdown file. Excluded from release builds.
public sealed partial class MainWindow
{
    private const int SmokeVkSpace = 0x20;
    private const int SmokeVkZero = 0x30;
    private const int SmokeVkAdd = 0x6B;
    private const int SmokeVkSubtract = 0x6D;

    // The smallest PNG there is: one pixel.
    private const string SmokeOnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private async Task SmokeQuickLookAsync(List<object> results, string root)
    {
        var workspace = _workspace!;
        var folder = Path.Combine(root, "quick-look");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a-notes.md"), "# Heading\n\nSome *text*.\n");
        File.WriteAllBytes(Path.Combine(folder, "b-picture.png"), Convert.FromBase64String(SmokeOnePixelPng));
        File.WriteAllText(Path.Combine(folder, "c-plain.txt"), "plain");
        Task? showing = null;
        try
        {
            workspace.ActivatePane(PaneId.Primary);
            workspace.SetFileListView(PaneId.Primary, "details");
            await workspace.NavigatePaneAsync(PaneId.Primary, folder);
            SyncFromWorkspace();
            await SmokeRemoteWaitAsync(
                () => PathRules.PathsEqual(workspace.Primary.Path, folder) && !workspace.Primary.ListingInProgress && PrimaryFiles.Count == 3,
                () => $"the quick-look folder with 3 rows; it shows {PrimaryFiles.Count} of {workspace.Primary.Path}");
            SelectRow(PaneId.Primary, Path.Combine(folder, "a-notes.md"));
            await SmokeRemoteWaitAsync(() => ActiveSelectedRow?.Name == "a-notes.md", () => $"a-notes.md to be selected, selected: {ActiveSelectedRow?.Name}");

            showing = ShowQuickLookAsync();
            ContentDialog? dialog = null;
            await SmokeRemoteWaitAsync(() => (dialog = SmokeOpenDialog(Content.XamlRoot!)) is { IsLoaded: true }, () => "the Quick Look dialog");
            string Position() => SmokeDescendants<TextBlock>(dialog!).FirstOrDefault(text => AutomationProperties.GetAutomationId(text) == "QuickLook.Position")?.Text ?? "<none>";
            var close = SmokeDialogButton(dialog!, "CloseButton");

            // 1. The Markdown file: its place beside the name, and the pane's Source | Rendered choice.
            await SmokeRemoteWaitAsync(() => Position() == "1 of 3", () => $"the counter to read '1 of 3', it reads '{Position()}'");
            var selector = SmokeFindById<SelectorBar>(dialog!, "QuickLook.RenderMode");
            var rendered = SmokeFindById<Grid>(dialog!, "QuickLook.Rendered");
            var source = SmokeDescendants<TextBox>(dialog!).FirstOrDefault(box => box.IsReadOnly);
            SmokeAssert(selector is not null && rendered is not null && source is not null, "Quick Look of a Markdown file has no Source | Rendered switch");
            SmokeAssert(selector!.Items.Select(item => item.Text).SequenceEqual(["Source", "Rendered"]), "The switch does not offer Source and Rendered");
            var opensRendered = workspace.Settings.PreviewRenderHtml;
            SmokeAssert((rendered!.Visibility == Visibility.Visible) == opensRendered && (source!.Visibility == Visibility.Visible) != opensRendered,
                $"Quick Look opened {(rendered.Visibility == Visibility.Visible ? "rendered" : "as source")}, the pane's mode is {(opensRendered ? "rendered" : "source")}");
            selector.SelectedItem = selector.Items[1];
            await SmokeRemoteWaitAsync(() => rendered.Visibility == Visibility.Visible && source!.Visibility == Visibility.Collapsed && rendered.Children.Count == 1,
                () => "Rendered to show the rendered view in place of the source");
            selector.SelectedItem = selector.Items[0];
            await SmokeRemoteWaitAsync(() => rendered.Visibility == Visibility.Collapsed && source!.Visibility == Visibility.Visible,
                () => "Source to show the source again");
            SmokeAssert(source!.Text.Contains("# Heading", StringComparison.Ordinal), $"The source view reads '{source.Text}'");

            // 2. Right steps to the picture, which zooms with + and -, and 0 fits it again.
            await SmokePressKeyAsync(close, SmokeVkRight, "Quick Look's Close button");
            ScrollViewer? image = null;
            await SmokeRemoteWaitAsync(() => Position() == "2 of 3" && (image = SmokeFindById<ScrollViewer>(dialog!, "QuickLook.Image")) is { IsLoaded: true },
                () => $"Right to show the picture as '2 of 3', the counter reads '{Position()}'");
            SmokeAssert(SmokeFindById<SelectorBar>(dialog!, "QuickLook.RenderMode") is null, "The Source | Rendered switch stayed for a picture");
            SmokeAssert(Math.Abs(image!.ZoomFactor - 1) < 0.01, $"The picture opens at zoom {image.ZoomFactor}, not fitted");
            await SmokePressKeyAsync(close, SmokeVkAdd, "Quick Look's Close button");
            await SmokeRemoteWaitAsync(() => Math.Abs(image.ZoomFactor - 1.25) < 0.01, () => $"+ to zoom to 1.25, the zoom is {image.ZoomFactor}");
            await SmokePressKeyAsync(close, SmokeVkAdd, "Quick Look's Close button");
            await SmokeRemoteWaitAsync(() => Math.Abs(image.ZoomFactor - 1.5) < 0.01, () => $"+ to zoom to 1.5, the zoom is {image.ZoomFactor}");
            await SmokePressKeyAsync(close, SmokeVkSubtract, "Quick Look's Close button");
            await SmokeRemoteWaitAsync(() => Math.Abs(image.ZoomFactor - 1.25) < 0.01, () => $"- to zoom back to 1.25, the zoom is {image.ZoomFactor}");
            await SmokePressKeyAsync(close, SmokeVkAdd, "Quick Look's Close button");
            await SmokePressKeyAsync(close, SmokeVkZero, "Quick Look's Close button");
            await SmokeRemoteWaitAsync(() => Math.Abs(image.ZoomFactor - 1) < 0.01, () => $"0 to fit the picture again, the zoom is {image.ZoomFactor}");

            // 3. Right again: the plain text file, which has nothing to render and so no switch.
            await SmokePressKeyAsync(close, SmokeVkRight, "Quick Look's Close button");
            await SmokeRemoteWaitAsync(
                () => Position() == "3 of 3" && SmokeDescendants<TextBox>(dialog!).Any(box => box.IsReadOnly && box.Text == "plain"),
                () => $"Right to show the text file as '3 of 3', the counter reads '{Position()}'");
            SmokeAssert(SmokeFindById<SelectorBar>(dialog!, "QuickLook.RenderMode") is null, "A plain text file has a Source | Rendered switch");
            SmokeAssert(ActiveSelectedRow?.Name == "c-plain.txt", $"Stepping left the selection on {ActiveSelectedRow?.Name}");

            // 4. Space closes it.
            await SmokePressKeyAsync(close, SmokeVkSpace, "Quick Look's Close button");
            await SmokeRemoteWaitAsync(() => SmokeOpenDialog(Content.XamlRoot!) is null, () => "Space to close Quick Look");
            await showing.WaitAsync(TimeSpan.FromSeconds(5));
            showing = null;
            results.Add(new { scenario = "quick-look", passed = true });
        }
        finally
        {
            if (showing is not null && SmokeOpenDialog(Content.XamlRoot!) is { } open)
            {
                open.Hide();
            }

            workspace.SetFileListView(PaneId.Primary, "list");
            await workspace.NavigatePaneAsync(PaneId.Primary, Path.Combine(root, "empty"));
            SyncFromWorkspace();
        }
    }
}
#endif
