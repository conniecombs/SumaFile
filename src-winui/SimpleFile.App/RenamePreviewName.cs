using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using SimpleFile.Core;

namespace SimpleFile.App;

public sealed class RenamePreviewName : UserControl
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(
        nameof(Before), typeof(string), typeof(RenamePreviewName), new PropertyMetadata("", OnNameChanged));
    public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(
        nameof(After), typeof(string), typeof(RenamePreviewName), new PropertyMetadata("", OnNameChanged));
    public static readonly DependencyProperty ShowAfterProperty = DependencyProperty.Register(
        nameof(ShowAfter), typeof(bool), typeof(RenamePreviewName), new PropertyMetadata(false, OnNameChanged));
    public string Before { get => (string)GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public string After { get => (string)GetValue(AfterProperty); set => SetValue(AfterProperty, value); }
    public bool ShowAfter { get => (bool)GetValue(ShowAfterProperty); set => SetValue(ShowAfterProperty, value); }

    public RenamePreviewName()
    {
        Content = _text;
        Loaded += (_, _) => RenderName();
        ActualThemeChanged += (_, _) => RenderName();
    }

    private static void OnNameChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e) => ((RenamePreviewName)owner).RenderName();
    private void RenderName()
    {
        var before = Before ?? "";
        var after = After ?? "";
        var changes = RenameNameChange.Between(before, after);
        _text.Text = ShowAfter ? after : before;
        ToolTipService.SetToolTip(this, _text.Text);
        _text.TextHighlighters.Clear();
        var highlight = new TextHighlighter
        {
            Background = ThemeResourceLookup.Brush(this, "SfAccentSoftBrush"),
            Foreground = ThemeResourceLookup.Brush(this, "SfTextPrimaryBrush"),
        };
        foreach (var change in changes)
        {
            var length = ShowAfter ? change.NewLength : change.OldLength;
            if (length > 0) highlight.Ranges.Add(new TextRange { StartIndex = ShowAfter ? change.NewStart : change.OldStart, Length = length });
        }
        if (highlight.Ranges.Count == 0) return;
        _text.TextHighlighters.Add(highlight);
    }
}
