using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SimpleFile.App;

public sealed partial class PaneContextBar : UserControl
{
    public PaneContextBar() => InitializeComponent();

    public event EventHandler? ClearFilter;
    public event EventHandler? ReturnToFolder;
    internal Button ClearButton => FilterClearButton;
    internal Button ReturnButton => ReturnFolderButton;

    public void Update(string filter, int visibleCount, int totalCount, bool searching, string query, string root, bool running)
    {
        var filtered = !string.IsNullOrWhiteSpace(filter);
        Visibility = filtered || searching ? Visibility.Visible : Visibility.Collapsed;
        FilterRow.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        FilterText.Text = $"Filter: {filter}";
        ToolTipService.SetToolTip(FilterText, FilterText.Text);
        FilterCount.Text = searching ? "Applies when you return to the folder" : $"{visibleCount:N0} of {totalCount:N0} items";
        SearchRow.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        SearchText.Text = $"{(running ? "Searching" : "Search")}: {query}";
        ToolTipService.SetToolTip(SearchText, SearchText.Text);
        SearchScope.Text = $"{visibleCount:N0} results · {root}";
        ToolTipService.SetToolTip(SearchScope, root);
    }

    private void OnClearFilter(object sender, RoutedEventArgs e) => ClearFilter?.Invoke(this, EventArgs.Empty);
    private void OnReturnToFolder(object sender, RoutedEventArgs e) => ReturnToFolder?.Invoke(this, EventArgs.Empty);
}
