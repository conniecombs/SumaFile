namespace SimpleFile.Ipc;

public static class Protocol
{
    public const int Version = 1;
    public const string JsonRpc = "2.0";
    public const uint MaxFrameBytes = 80 * 1024 * 1024;
    public const int DomainMethodCount = 76;
    public const byte BinaryFrameVersion = 1;
    public const byte BinaryListDirectoryChunk = 1;
    public const byte BinaryListDirectoryResult = 2;
    public const byte BinarySearchResultsBatch = 3;
    public const byte BinarySearchResultsResult = 4;
    public const byte BinaryOperationProgress = 5;
    public const byte BinaryFileChange = 6;
    public const byte BinaryThumbnailResult = 7;
    public const byte BinaryThumbnailsResult = 8;

    public const string HandshakeMethod = "ipc.handshake";
    public const string HealthMethod = "ipc.health";
    public const string ShutdownMethod = "ipc.shutdown";
    public const string GetAppVersionMethod = "get_app_version";
    public const string GetHomeDirMethod = "get_home_dir";
    public const string ListDrivesMethod = "list_drives";
    public const string ListDirectoryMethod = "list_directory";
    public const string SelectDirectoryMethod = "select_directory";
    public const string ShowMainWindowMethod = "show_main_window";
    public const string GetDbSettingMethod = "get_db_setting";
    public const string SetDbSettingMethod = "set_db_setting";

    public const string CreateDirectoryMethod = "create_directory";
    public const string CreateFileMethod = "create_file";
    public const string DeleteEntryMethod = "delete_entry";
    public const string MoveToTrashMethod = "move_to_trash";
    public const string RenameEntryMethod = "rename_entry";
    public const string BatchRenameMethod = "batch_rename";
    public const string CopyEntryMethod = "copy_entry";
    public const string MoveEntryMethod = "move_entry";
    public const string CopyEntryResolvedMethod = "copy_entry_resolved";
    public const string MoveEntryResolvedMethod = "move_entry_resolved";
    public const string GetEntryInfoMethod = "get_entry_info";
    public const string OpenFileMethod = "open_file";
    public const string RevealInFolderMethod = "reveal_in_folder";
    public const string OpenExternalUrlMethod = "open_external_url";
    public const string ListArchiveMethod = "list_archive";
    public const string ExtractArchiveMethod = "extract_archive";
    public const string CreateArchiveMethod = "create_archive";
    public const string ReadFilePreviewMethod = "read_file_preview";
    public const string GenerateThumbnailMethod = "generate_thumbnail";
    public const string GenerateThumbnailsMethod = "generate_thumbnails";
    public const string OpenFileWithMethod = "open_file_with";
    public const string CompareFilesMethod = "compare_files";
    public const string ComputeChecksumMethod = "compute_checksum";
    public const string GetImageMetadataMethod = "get_image_metadata";
    public const string GetFileMetadataMethod = "get_file_metadata";
    public const string ListSubdirectoriesMethod = "list_subdirectories";
    public const string CalculateFolderSizeMethod = "calculate_folder_size";
    public const string CountFolderItemsMethod = "count_folder_items";
    public const string GetFolderMetricsMethod = "get_folder_metrics";
    public const string CopyWithProgressMethod = "copy_with_progress";
    public const string MoveWithProgressMethod = "move_with_progress";
    public const string CancelOperationMethod = "cancel_operation";
    public const string SearchFilesMethod = "search_files";
    public const string CancelSearchMethod = "cancel_search";
    public const string WatchDirectoryMethod = "watch_directory";
    public const string UnwatchDirectoryMethod = "unwatch_directory";

    public const string CancelFolderSizeMethod = "cancel_folder_size";
    public const string CancelFolderItemCountMethod = "cancel_folder_item_count";
    public const string CancelCountItemsMethod = "cancel_count_items";
    public const string CancelFolderMetricsMethod = "cancel_folder_metrics";
    public const string CheckRarInstalledMethod = "check_rar_installed";
    public const string PrepareRarInstallMethod = "prepare_rar_install";
    public const string DiscardRarInstallMethod = "discard_rar_install";
    public const string InstallRarMethod = "install_rar";
    public const string DiskCleanupMethod = "disk_cleanup";
    public const string CancelDiskCleanupMethod = "cancel_disk_cleanup";
    public const string DuplicateCheckMethod = "duplicate_check";
    public const string CancelDuplicateCheckMethod = "cancel_duplicate_check";
    public const string GetAllTagsMethod = "get_all_tags";
    public const string CreateTagMethod = "create_tag";
    public const string UpdateTagMethod = "update_tag";
    public const string DeleteTagMethod = "delete_tag";
    public const string GetTagsForPathMethod = "get_tags_for_path";
    public const string SetTagsForPathMethod = "set_tags_for_path";
    public const string GetAllFileTagsMethod = "get_all_file_tags";
    public const string GetFilesWithTagMethod = "get_files_with_tag";
    public const string LoadSmartFoldersMethod = "load_smart_folders";
    public const string SaveSmartFolderMethod = "save_smart_folder";
    public const string DeleteSmartFolderMethod = "delete_smart_folder";
    public const string GetAppAboutInfoMethod = "get_app_about_info";
    public const string CheckForUpdateMethod = "check_for_update";
    public const string InstallUpdateMethod = "install_update";
    public const string OpenTerminalMethod = "open_terminal";
    public const string OpenPowershellAdminMethod = "open_powershell_admin";
    public const string GetGitStatusMethod = "get_git_status";
    public const string GetGitFileStatusesMethod = "get_git_file_statuses";
    public const string GitPullMethod = "git_pull";
    public const string GitPushMethod = "git_push";

    public const string ListDirectoryChunkEvent = "list_directory.chunk";
    public const string OperationProgressEvent = "operation-progress";
    public const string FileChangeEvent = "file-change";
    public const string SearchResultsBatchEvent = "search-results-batch";
    public const string SearchCompleteEvent = "search-complete";
    public const string UpdateChunkEvent = "update-chunk";

    public const int ErrParse = -32700;
    public const int ErrInvalidRequest = -32600;
    public const int ErrMethodNotFound = -32601;
    public const int ErrInvalidParams = -32602;
    public const int ErrInternal = -32603;
    public const int ErrApplication = -32000;
    public const int ErrHostOwned = -32001;
    public const int ErrHandshake = -32002;

    public const string PrefixConflict = "CONFLICT:";
    public const string PrefixTrashUnavailable = "TRASH_UNAVAILABLE:";
    public const string PrefixResultTooLarge = "RESULT_TOO_LARGE:";
    public const string PrefixHostOwned = "HOST_OWNED:";

    public const string ClientName = "SumaFile.App";
    public const string Identifier = "com.simplefile.desktop";

    public static TimeSpan ConnectTimeout { get; } =
#if DEBUG
        TimeSpan.FromSeconds(5);
#else
        TimeSpan.FromSeconds(2);
#endif
}
