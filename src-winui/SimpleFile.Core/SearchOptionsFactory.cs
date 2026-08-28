using SimpleFile.Ipc;

namespace SimpleFile.Core;

public static class SearchOptionsFactory
{
    public static SearchOptions ForRun(SearchOptions? template, string searchId, string fallbackSearchPath)
    {
        template ??= new SearchOptions();
        return new SearchOptions
        {
            Query = template.Query,
            SearchPath = string.IsNullOrWhiteSpace(template.SearchPath) ? fallbackSearchPath : template.SearchPath,
            CaseSensitive = template.CaseSensitive,
            IncludeHidden = template.IncludeHidden,
            FileTypes = template.FileTypes?.ToArray(),
            MaxResults = template.MaxResults,
            MaxDepth = template.MaxDepth,
            SearchId = searchId,
            ContentSearch = template.ContentSearch,
            MinSize = template.MinSize,
            MaxSize = template.MaxSize,
            DateAfter = template.DateAfter,
            DateBefore = template.DateBefore,
        };
    }
}
