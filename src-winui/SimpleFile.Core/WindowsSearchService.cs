using System.Data;
using System.Data.OleDb;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SimpleFile.Ipc;

namespace SimpleFile.Core;

/// <summary>
/// Queries the Windows Search Indexer via OLE DB for near-instant search
/// results on indexed locations. Falls through gracefully when the indexer
/// is unavailable or the target path is not in the crawl scope.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSearchService
{
    // ────────────────────────────────────────────────────────────────────
    //  COM interop declarations for Windows Search Manager
    // ────────────────────────────────────────────────────────────────────

    [ComImport, Guid("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39")]
    private class CSearchManager { }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF69"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchManager
    {
        void GetIndexerVersionStr([MarshalAs(UnmanagedType.LPWStr)] out string ppszVersionString);
        void GetIndexerVersion(out uint pdwMajor, out uint pdwMinor);
        void GetParameter([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr ppValue);
        void SetParameter([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pValue);
        void GetProxyName([MarshalAs(UnmanagedType.LPWStr)] out string ppszProxyName);
        void GetBypassList([MarshalAs(UnmanagedType.LPWStr)] out string ppszBypassList);
        void SetProxy(int sUseProxy, bool fLocalByPassProxy, uint dwPortNumber,
            [MarshalAs(UnmanagedType.LPWStr)] string pszProxyName,
            [MarshalAs(UnmanagedType.LPWStr)] string pszByPassList);
        [return: MarshalAs(UnmanagedType.Interface)]
        ISearchCatalogManager GetCatalog([MarshalAs(UnmanagedType.LPWStr)] string pszCatalog);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF50"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchCatalogManager
    {
        void GetName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void GetParameter([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr ppValue);
        void SetParameter([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pValue);
        void GetCatalogStatus(out int pStatus, out int pPausedReason);
        void Reset();
        void Reindex();
        void ReindexMatchingURLs([MarshalAs(UnmanagedType.LPWStr)] string pszPattern);
        void ReindexSearchRoot([MarshalAs(UnmanagedType.LPWStr)] string pszRootURL);
        void SetConnectTimeout(uint dwConnectTimeout);
        void GetConnectTimeout(out uint pdwConnectTimeout);
        void SetDataTimeout(uint dwDataTimeout);
        void GetDataTimeout(out uint pdwDataTimeout);
        void NumberOfItems(out int plCount);
        void NumberOfItemsToIndex(out int plIncrementalCount, out int plNotificationQueue, out int plHighPriorityQueue);
        void URLBeingIndexed([MarshalAs(UnmanagedType.LPWStr)] out string pszUrl);
        void GetURLIndexingState([MarshalAs(UnmanagedType.LPWStr)] string pszURL, out uint pdwState);
        void GetPersistentItemsChangedSink(IntPtr ppISearchPersistentItemsChangedSink);
        void RegisterViewForNotification([MarshalAs(UnmanagedType.LPWStr)] string pszView, IntPtr pViewChangedSink, out uint pdwCookie);
        void GetItemsChangedSink(IntPtr pISearchNotifyInlineSite, ref Guid riid, out IntPtr ppv, out Guid pGUIDCatalogResetSignature, out Guid pGUIDCheckPointSignature, out uint pdwLastCheckPointNumber);
        void UnregisterViewForNotification(uint dwCookie);
        void SetExtensionClusion([MarshalAs(UnmanagedType.LPWStr)] string pszExtension, bool fExclude);
        void EnumerateExcludedExtensions(out IntPtr ppExtensions);
        [return: MarshalAs(UnmanagedType.Interface)]
        ISearchQueryHelper GetQueryHelper();
        void SetDiacriticSensitivity(bool fDiacriticSensitive);
        void GetDiacriticSensitivity(out bool pfDiacriticSensitive);
        [return: MarshalAs(UnmanagedType.Interface)]
        ISearchCrawlScopeManager GetCrawlScopeManager();
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF63"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchQueryHelper
    {
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string get_ConnectionString();
        void put_QueryContentLocale(uint lcid);
        void get_QueryContentLocale(out uint plcid);
        void put_QueryKeywordLocale(uint lcid);
        void get_QueryKeywordLocale(out uint plcid);
        void put_QueryTermExpansion(int expandTerms);
        void get_QueryTermExpansion(out int pExpandTerms);
        void put_QuerySyntax(int querySyntax);
        void get_QuerySyntax(out int pQuerySyntax);
        void put_QueryContentProperties([MarshalAs(UnmanagedType.LPWStr)] string pszContentProperties);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string get_QueryContentProperties();
        void put_QuerySelectColumns([MarshalAs(UnmanagedType.LPWStr)] string pszSelectColumns);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string get_QuerySelectColumns();
        void put_QueryWhereRestrictions([MarshalAs(UnmanagedType.LPWStr)] string pszRestrictions);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string get_QueryWhereRestrictions();
        void put_QuerySorting([MarshalAs(UnmanagedType.LPWStr)] string pszSorting);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string get_QuerySorting();
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GenerateSQLFromUserQuery([MarshalAs(UnmanagedType.LPWStr)] string pszQuery);
        void WriteProperties(int itemID, uint dwNumberOfColumns, IntPtr pColumns, IntPtr pValues, IntPtr pftGatherModifiedTime);
        void put_QueryMaxResults(int cMaxResults);
        void get_QueryMaxResults(out int pcMaxResults);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF55"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchCrawlScopeManager
    {
        void AddDefaultScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszURL, bool fInclude, uint fFollowFlags);
        void AddRoot(IntPtr pSearchRoot);
        void RemoveRoot([MarshalAs(UnmanagedType.LPWStr)] string pszURL);
        void EnumerateRoots(out IntPtr ppSearchRoots);
        void AddHierarchicalScope([MarshalAs(UnmanagedType.LPWStr)] string pszURL, bool fInclude, bool fDefault, bool fOverrideChildren);
        void AddUserScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszURL, bool fInclude, bool fOverrideChildren, uint fFollowFlags);
        void RemoveScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszRule);
        void EnumerateScopeRules(out IntPtr ppSearchScopeRules);
        [return: MarshalAs(UnmanagedType.Bool)]
        bool HasParentScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszURL);
        [return: MarshalAs(UnmanagedType.Bool)]
        bool HasChildScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszURL);
        [PreserveSig]
        int IncludedInCrawlScope([MarshalAs(UnmanagedType.LPWStr)] string pszURL, out int pfIsIncluded);
        [PreserveSig]
        int IncludedInCrawlScopeEx([MarshalAs(UnmanagedType.LPWStr)] string pszURL, out int pfIsIncluded, out int pReason);
        void RevertToDefaultScopes();
        void SaveAll();
        int GetParentScopeVersionId([MarshalAs(UnmanagedType.LPWStr)] string pszURL);
        void RemoveDefaultScopeRule([MarshalAs(UnmanagedType.LPWStr)] string pszURL);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Constants
    // ────────────────────────────────────────────────────────────────────

    private const string CatalogName = "SystemIndex";

    private const string SelectColumns =
        "System.ItemName, System.ItemPathDisplay, System.Size, System.DateModified, " +
        "System.FileExtension, System.ItemTypeText, System.Kind";

    private const int DefaultMaxResults = 1000;
    private const int BatchSize = 32;

    // ────────────────────────────────────────────────────────────────────
    //  Public API
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the Windows Search Indexer includes the given folder path
    /// in its crawl scope. Returns false if the path is not indexed, the indexer
    /// service is stopped, or COM initialization fails.
    /// </summary>
    public static bool IsPathIndexed(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        try
        {
            var manager = (ISearchManager)new CSearchManager();
            var catalog = manager.GetCatalog(CatalogName);
            var scopeManager = catalog.GetCrawlScopeManager();

            var url = ToFileUrl(folderPath);
            int hr = scopeManager.IncludedInCrawlScope(url, out int isIncluded);
            return hr == 0 && isIncluded != 0;
        }
        catch
        {
            // Windows Search service stopped, COM unavailable, etc.
            return false;
        }
    }

    /// <summary>
    /// Queries the Windows Search Indexer for files matching the given criteria.
    /// Returns results as <see cref="SearchResult"/> items compatible with the existing
    /// search result pipeline.
    /// </summary>
    /// <param name="query">User search text (filename substring, glob, or AQS).</param>
    /// <param name="folderScope">Folder path to restrict the search scope.</param>
    /// <param name="options">Search options matching the IPC contract.</param>
    /// <param name="onBatch">Optional callback for streaming batch results to the UI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of all matching search results.</returns>
    public static async Task<SearchResult[]> SearchIndexAsync(
        string query,
        string folderScope,
        SearchOptions options,
        Action<SearchResult[]>? onBatch = null,
        CancellationToken ct = default)
    {
        var results = new List<SearchResult>();

        var (connectionString, sql) = BuildQuery(query, folderScope, options);
        if (connectionString is null || sql is null)
        {
            return [];
        }

        var maxResults = options.MaxResults ?? DefaultMaxResults;
        var batch = new List<SearchResult>(BatchSize);

        using var conn = new OleDbConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = new OleDbCommand(sql, conn);
        using var reader = (OleDbDataReader)await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();

            var result = ReadRow(reader, options);
            if (result is null)
            {
                continue;
            }

            if (!PassesClientFilters(result, options))
            {
                continue;
            }

            results.Add(result);
            batch.Add(result);

            if (batch.Count >= BatchSize)
            {
                onBatch?.Invoke(batch.ToArray());
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            onBatch?.Invoke(batch.ToArray());
        }

        // Sort directories first, then by name, matching the Rust backend convention.
        results.Sort((a, b) =>
        {
            int dirCompare = b.IsDir.CompareTo(a.IsDir);
            return dirCompare != 0
                ? dirCompare
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return results.ToArray();
    }

    // ────────────────────────────────────────────────────────────────────
    //  Query building
    // ────────────────────────────────────────────────────────────────────

    private static (string? connectionString, string? sql) BuildQuery(
        string query,
        string folderScope,
        SearchOptions options)
    {
        try
        {
            var manager = (ISearchManager)new CSearchManager();
            var catalog = manager.GetCatalog(CatalogName);
            var helper = catalog.GetQueryHelper();

            helper.put_QuerySelectColumns(SelectColumns);
            helper.put_QueryMaxResults(options.MaxResults ?? DefaultMaxResults);

            // Build WHERE restrictions for folder scope and filters.
            var restrictions = BuildWhereRestrictions(folderScope, options);
            if (!string.IsNullOrEmpty(restrictions))
            {
                helper.put_QueryWhereRestrictions(restrictions);
            }

            // Sort directories first, then by name.
            helper.put_QuerySorting("System.ItemType DESC, System.ItemName ASC");

            // Content search: enable full-text matching when requested.
            if (options.ContentSearch)
            {
                helper.put_QueryContentProperties("System.Search.Contents");
            }

            var sql = helper.GenerateSQLFromUserQuery(query);
            var connectionString = helper.get_ConnectionString();

            return (connectionString, sql);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string BuildWhereRestrictions(string folderScope, SearchOptions options)
    {
        var parts = new List<string>();

        // Scope restriction: SCOPE includes subfolders recursively.
        if (!string.IsNullOrWhiteSpace(folderScope))
        {
            var escapedScope = folderScope.Replace("'", "''");
            parts.Add($"AND SCOPE='file:{escapedScope}'");
        }

        // File type filter.
        if (options.FileTypes is { Length: > 0 })
        {
            var extClauses = options.FileTypes
                .Select(ext => $"System.FileExtension = '.{ext.TrimStart('.')}'")
                .ToArray();
            parts.Add($"AND ({string.Join(" OR ", extClauses)})");
        }

        // Size filters.
        if (options.MinSize is > 0)
        {
            parts.Add($"AND System.Size >= {options.MinSize.Value}");
        }

        if (options.MaxSize is > 0)
        {
            parts.Add($"AND System.Size <= {options.MaxSize.Value}");
        }

        // Date filters.
        if (!string.IsNullOrEmpty(options.DateAfter))
        {
            parts.Add($"AND System.DateModified >= '{options.DateAfter}'");
        }

        if (!string.IsNullOrEmpty(options.DateBefore))
        {
            parts.Add($"AND System.DateModified <= '{options.DateBefore}'");
        }

        return string.Join(' ', parts);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Row reading & filtering
    // ────────────────────────────────────────────────────────────────────

    private static SearchResult? ReadRow(OleDbDataReader reader, SearchOptions options)
    {
        try
        {
            var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var path = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var size = reader.IsDBNull(2) ? 0UL : (ulong)Convert.ToInt64(reader.GetValue(2));
            var modified = reader.IsDBNull(3)
                ? "-"
                : reader.GetDateTime(3).ToString("yyyy-MM-dd HH:mm");
            var extension = reader.IsDBNull(4) ? "" : reader.GetString(4).TrimStart('.');
            var typeText = reader.IsDBNull(5) ? "" : reader.GetString(5);

            // Detect directories from System.Kind or System.ItemTypeText.
            // System.Kind is null/empty for folders, and "folder" appears in typeText.
            var kind = reader.IsDBNull(6) ? "" : reader.GetValue(6)?.ToString() ?? "";
            var isDir = string.IsNullOrEmpty(kind) &&
                        (typeText.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
                         string.IsNullOrEmpty(extension));

            // Filter hidden files on the client side when not included.
            if (!options.IncludeHidden && (name.StartsWith('.') || IsHiddenPath(path)))
            {
                return null;
            }

            return new SearchResult
            {
                Name = name,
                Path = path,
                IsDir = isDir,
                Size = size,
                Modified = modified,
                Extension = extension.ToLowerInvariant(),
                MatchType = "index",
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Applies client-side filters that could not be pushed into the SQL query,
    /// such as case-sensitive name matching.
    /// </summary>
    private static bool PassesClientFilters(SearchResult result, SearchOptions options)
    {
        // The indexer handles most filters via SQL, but case-sensitive matching
        // must be validated client-side since Windows Search is always case-insensitive.
        if (options.CaseSensitive && !string.IsNullOrEmpty(options.Query))
        {
            var nameMatch = result.Name.Contains(options.Query, StringComparison.Ordinal);
            // For content search, we can't validate content matches client-side,
            // so only filter name-match requests case-sensitively.
            if (!nameMatch && !options.ContentSearch)
            {
                return false;
            }
        }

        return true;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private static string ToFileUrl(string path)
    {
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        // Normalize trailing separator.
        var normalized = path.TrimEnd('\\', '/');
        return $"file:{normalized}";
    }

    private static bool IsHiddenPath(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.Hidden) != 0 || (attrs & FileAttributes.System) != 0;
        }
        catch
        {
            return false;
        }
    }
}
