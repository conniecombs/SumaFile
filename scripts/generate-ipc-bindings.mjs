import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..');
const schemaDir = path.join(repoRoot, 'ipc', 'schema', 'v1');
const checkOnly = process.argv.includes('--check');

function readJson(relativePath) {
  return JSON.parse(fs.readFileSync(path.join(schemaDir, relativePath), 'utf8'));
}

function fail(message) {
  console.error(`IPC binding generation failed: ${message}`);
  process.exit(1);
}

function quote(value) {
  return JSON.stringify(value);
}

function pascalCase(name) {
  return name
    .split(/[._-]/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join('');
}

function screamingSnake(name) {
  return name
    .replace(/^ipc\./, '')
    .replace(/[.-]/g, '_')
    .toUpperCase();
}

function methodConstName(method) {
  if (method === 'ipc.handshake') return 'HandshakeMethod';
  if (method === 'ipc.health') return 'HealthMethod';
  if (method === 'ipc.shutdown') return 'ShutdownMethod';
  return `${pascalCase(method)}Method`;
}

function eventConstName(eventName) {
  return `${pascalCase(eventName)}Event`;
}

function rustMethodConstName(method) {
  if (method === 'ipc.handshake') return 'HANDSHAKE_METHOD';
  if (method === 'ipc.health') return 'HEALTH_METHOD';
  if (method === 'ipc.shutdown') return 'SHUTDOWN_METHOD';
  return `METHOD_${screamingSnake(method)}`;
}

function rustEventConstName(eventName) {
  return screamingSnake(eventName);
}

function writeOrCheck(relativePath, content) {
  const filePath = path.join(repoRoot, relativePath);
  const normalized = `${content.replace(/\r\n/g, '\n').replace(/\s+$/u, '')}\n`;
  if (checkOnly) {
    const existing = fs.existsSync(filePath)
      ? fs.readFileSync(filePath, 'utf8').replace(/\r\n/g, '\n')
      : '';
    if (existing !== normalized) {
      console.error(`${relativePath} is stale. Run npm run generate:ipc-bindings.`);
      process.exitCode = 1;
    }
    return;
  }

  fs.writeFileSync(filePath, normalized);
  console.log(`Generated ${relativePath}`);
}

const commands = readJson('commands.json');
const events = readJson('events.json');
const protocol = readJson('protocol.json');
const methods = commands.methods ?? {};
const methodNames = Object.keys(methods);
const controlMethods = methodNames.filter((name) => name.startsWith('ipc.'));
const domainMethods = methodNames.filter((name) => !name.startsWith('ipc.'));
const emittedEvents = Object.keys(events.emitted ?? {});

if (commands.domainMethodCount !== domainMethods.length) {
  fail(`commands.json domainMethodCount=${commands.domainMethodCount}, but found ${domainMethods.length} domain methods.`);
}
if (commands.protocolVersion !== protocol.protocolVersion || events.protocolVersion !== protocol.protocolVersion) {
  fail('schema protocol versions do not agree.');
}
for (const method of ['ipc.handshake', 'ipc.health', 'ipc.shutdown']) {
  if (!methods[method]) {
    fail(`commands.json is missing ${method}.`);
  }
}

const binaryTags = [
  {
    cs: 'BinaryListDirectoryChunk',
    rust: 'BINARY_LIST_DIRECTORY_CHUNK',
    tag: methods.list_directory?.binaryFrameTags?.chunk,
    name: 'list_directory.chunk',
  },
  {
    cs: 'BinaryListDirectoryResult',
    rust: 'BINARY_LIST_DIRECTORY_RESULT',
    tag: methods.list_directory?.binaryFrameTags?.result,
    name: 'list_directory.result',
  },
  {
    cs: 'BinarySearchResultsBatch',
    rust: 'BINARY_SEARCH_RESULTS_BATCH',
    tag: methods.search_files?.binaryFrameTags?.batch,
    name: 'search_files.batch',
  },
  {
    cs: 'BinarySearchResultsResult',
    rust: 'BINARY_SEARCH_RESULTS_RESULT',
    tag: methods.search_files?.binaryFrameTags?.result,
    name: 'search_files.result',
  },
  {
    cs: 'BinaryOperationProgress',
    rust: 'BINARY_OPERATION_PROGRESS',
    tag: events.emitted?.['operation-progress']?.binaryFrameTag,
    name: 'operation-progress',
  },
  {
    cs: 'BinaryFileChange',
    rust: 'BINARY_FILE_CHANGE',
    tag: events.emitted?.['file-change']?.binaryFrameTag,
    name: 'file-change',
  },
  {
    cs: 'BinaryThumbnailResult',
    rust: 'BINARY_THUMBNAIL_RESULT',
    tag: methods.generate_thumbnail?.binaryFrameTag,
    name: 'generate_thumbnail.result',
  },
  {
    cs: 'BinaryThumbnailsResult',
    rust: 'BINARY_THUMBNAILS_RESULT',
    tag: methods.generate_thumbnails?.binaryFrameTag,
    name: 'generate_thumbnails.result',
  },
];
for (const tag of binaryTags) {
  if (!Number.isInteger(tag.tag)) {
    fail(`missing binary frame tag for ${tag.name}.`);
  }
}

const manualClientMethods = new Set(['ipc.handshake', 'list_directory', 'search_files']);
const intentionallyUnexposedClientMethods = new Set(['get_folder_metrics', 'cancel_folder_metrics']);
const wrappers = [
  { method: 'ipc.health', signature: 'public Task<HealthResult> HealthAsync(CancellationToken cancellationToken = default)', body: 'InvokeAsync<HealthResult>(Protocol.HealthMethod, new { }, cancellationToken)' },
  { method: 'ipc.shutdown', signature: 'public Task ShutdownAsync(CancellationToken cancellationToken = default)', body: 'InvokeAsync(Protocol.ShutdownMethod, new { }, cancellationToken)' },
  { method: 'get_app_version', signature: 'public Task<string> GetAppVersionAsync(CancellationToken cancellationToken = default)', body: 'InvokeAsync<string>(Protocol.GetAppVersionMethod, new { }, cancellationToken)' },
  { method: 'get_home_dir', signature: 'public Task<string> GetHomeDirAsync(CancellationToken cancellationToken = default)', body: 'InvokeAsync<string>(Protocol.GetHomeDirMethod, new { }, cancellationToken)' },
  {
    method: 'list_drives',
    block: [
      'public async Task<IReadOnlyList<DriveInfo>> ListDrivesAsync(CancellationToken cancellationToken = default)',
      '{',
      '    var drives = await InvokeAsync<DriveInfo[]>(Protocol.ListDrivesMethod, new { }, cancellationToken)',
      '        .ConfigureAwait(false);',
      '    return drives;',
      '}',
    ],
  },
  { method: 'select_directory', signature: 'public Task SelectDirectoryAsync(string? defaultPath = null, CancellationToken cancellationToken = default)', body: 'InvokeAsync(Protocol.SelectDirectoryMethod, new SelectDirectoryParams { DefaultPath = defaultPath }, cancellationToken)' },
  { method: 'show_main_window', signature: 'public Task ShowMainWindowAsync(CancellationToken cancellationToken = default)', body: 'InvokeAsync(Protocol.ShowMainWindowMethod, new { }, cancellationToken)' },
  { method: 'get_db_setting', signature: 'public Task<string?> GetDbSettingAsync(string key, CancellationToken ct = default)', body: 'InvokeAsync<string?>(Protocol.GetDbSettingMethod, new { key }, ct)' },
  { method: 'set_db_setting', signature: 'public Task SetDbSettingAsync(string key, string value, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.SetDbSettingMethod, new { key, value }, ct)' },
  { method: 'create_directory', signature: 'public Task<string> CreateDirectoryAsync(string path, string name, CancellationToken ct = default)', body: 'InvokeAsync<string>(Protocol.CreateDirectoryMethod, new { path, name }, ct)' },
  { method: 'create_file', signature: 'public Task<string> CreateFileAsync(string path, string name, CancellationToken ct = default)', body: 'InvokeAsync<string>(Protocol.CreateFileMethod, new { path, name }, ct)' },
  { method: 'delete_entry', signature: 'public Task DeleteEntryAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.DeleteEntryMethod, new { path }, ct)' },
  { method: 'move_to_trash', signature: 'public Task<string[]> MoveToTrashAsync(string[] paths, CancellationToken ct = default)', body: 'InvokeAsync<string[]>(Protocol.MoveToTrashMethod, new { paths }, ct)' },
  { method: 'restore_recycle_bin', signature: 'public Task<string[]> RestoreRecycleBinAsync(string[] paths, CancellationToken ct = default)', body: 'InvokeAsync<string[]>(Protocol.RestoreRecycleBinMethod, new { paths }, ct)' },
  { method: 'empty_recycle_bin', signature: 'public Task EmptyRecycleBinAsync(CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.EmptyRecycleBinMethod, new { }, ct)' },
  { method: 'rename_entry', signature: 'public Task<string> RenameEntryAsync(string path, string newName, CancellationToken ct = default)', body: 'InvokeAsync<string>(Protocol.RenameEntryMethod, new { path, newName }, ct)' },
  { method: 'batch_rename', signature: 'public Task<string[]> BatchRenameAsync(RenameRequest[] entries, CancellationToken ct = default)', body: 'InvokeAsync<string[]>(Protocol.BatchRenameMethod, new { entries }, ct)' },
  { method: 'copy_entry', signature: 'public Task<string> CopyEntryAsync(string source, string destination, CancellationToken ct = default)', body: 'InvokeAsync<string>(Protocol.CopyEntryMethod, new { source, destination }, ct)' },
  { method: 'move_entry', signature: 'public Task<string> MoveEntryAsync(string source, string destination, CancellationToken ct = default)', body: 'InvokeAsync<string>(Protocol.MoveEntryMethod, new { source, destination }, ct)' },
  { method: 'copy_entry_resolved', signature: 'public Task<string> CopyEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default)', body: 'InvokeAsync<string>(Protocol.CopyEntryResolvedMethod, new { source, destination, conflictAction }, ct)' },
  { method: 'move_entry_resolved', signature: 'public Task<string> MoveEntryResolvedAsync(string source, string destination, string conflictAction, CancellationToken ct = default)', body: 'InvokeAsync<string>(Protocol.MoveEntryResolvedMethod, new { source, destination, conflictAction }, ct)' },
  { method: 'get_entry_info', signature: 'public Task<FileEntry> GetEntryInfoAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<FileEntry>(Protocol.GetEntryInfoMethod, new { path }, ct)' },
  { method: 'open_file', signature: 'public Task OpenFileAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.OpenFileMethod, new { path }, ct)' },
  { method: 'reveal_in_folder', signature: 'public Task RevealInFolderAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.RevealInFolderMethod, new { path }, ct)' },
  { method: 'open_external_url', signature: 'public Task OpenExternalUrlAsync(string url, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.OpenExternalUrlMethod, new { url }, ct)' },
  { method: 'list_archive', signature: 'public Task<ArchiveInfo> ListArchiveAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<ArchiveInfo>(Protocol.ListArchiveMethod, new { path }, ct)' },
  { method: 'extract_archive', signature: 'public Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.ExtractArchiveMethod, new { archivePath, destination }, ct)' },
  { method: 'create_archive', signature: 'public Task CreateArchiveAsync(string[] paths, string archivePath, string format, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.CreateArchiveMethod, new { paths, archivePath, format }, ct)' },
  { method: 'read_file_preview', signature: 'public Task<FilePreview> ReadFilePreviewAsync(string path, ulong? maxSize = null, CancellationToken ct = default)', body: 'InvokeAsync<FilePreview>(Protocol.ReadFilePreviewMethod, new { path, maxSize }, ct)' },
  { method: 'generate_thumbnail', signature: 'public Task<string> GenerateThumbnailAsync(string path, uint size, CancellationToken ct = default)', body: 'InvokeAsync<string>(Protocol.GenerateThumbnailMethod, new { path, size }, ct)' },
  { method: 'generate_thumbnails', signature: 'public Task<ThumbnailResult[]> GenerateThumbnailsAsync(string[] paths, uint size, CancellationToken ct = default)', body: 'InvokeAsync<ThumbnailResult[]>(Protocol.GenerateThumbnailsMethod, new { paths, size }, ct)' },
  { method: 'open_file_with', signature: 'public Task OpenFileWithAsync(string path, string application, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.OpenFileWithMethod, new { path, application }, ct)' },
  { method: 'compare_files', signature: 'public Task<FileComparison> CompareFilesAsync(string pathA, string pathB, CancellationToken ct = default)', body: 'InvokeAsync<FileComparison>(Protocol.CompareFilesMethod, new { pathA, pathB }, ct)' },
  { method: 'compute_checksum', signature: 'public Task<Checksums> ComputeChecksumAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<Checksums>(Protocol.ComputeChecksumMethod, new { path }, ct)' },
  { method: 'get_image_metadata', signature: 'public Task<ImageMetadata> GetImageMetadataAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<ImageMetadata>(Protocol.GetImageMetadataMethod, new { path }, ct)' },
  { method: 'get_file_metadata', signature: 'public Task<FileMetadata> GetFileMetadataAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<FileMetadata>(Protocol.GetFileMetadataMethod, new { path }, ct)' },
  { method: 'list_subdirectories', signature: 'public Task<TreeNode[]> ListSubdirectoriesAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<TreeNode[]>(Protocol.ListSubdirectoriesMethod, new { path }, ct)' },
  { method: 'calculate_folder_size', signature: 'public Task<ulong> CalculateFolderSizeAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<ulong>(Protocol.CalculateFolderSizeMethod, new { path }, ct)' },
  { method: 'count_folder_items', signature: 'public Task<ulong> CountFolderItemsAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<ulong>(Protocol.CountFolderItemsMethod, new { path }, ct)' },
  { method: 'copy_with_progress', signature: 'public Task<TransferResult[]> CopyWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default)', body: 'InvokeAsync<TransferResult[]>(Protocol.CopyWithProgressMethod, new { sources, destination, operationId, conflictAction }, ct)' },
  { method: 'move_with_progress', signature: 'public Task<TransferResult[]> MoveWithProgressAsync(string[] sources, string destination, string? operationId, string conflictAction, CancellationToken ct = default)', body: 'InvokeAsync<TransferResult[]>(Protocol.MoveWithProgressMethod, new { sources, destination, operationId, conflictAction }, ct)' },
  { method: 'cancel_operation', signature: 'public Task CancelOperationAsync(string operationId, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.CancelOperationMethod, new { operationId }, ct)' },
  { method: 'cancel_search', signature: 'public Task CancelSearchAsync(string searchId, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.CancelSearchMethod, new { searchId }, ct)' },
  { method: 'watch_directory', signature: 'public Task WatchDirectoryAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.WatchDirectoryMethod, new { path }, ct)' },
  { method: 'unwatch_directory', signature: 'public Task UnwatchDirectoryAsync(CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.UnwatchDirectoryMethod, new { }, ct)' },
  { method: 'cancel_folder_size', signature: 'public Task CancelFolderSizeAsync(CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.CancelFolderSizeMethod, new { }, ct)' },
  { method: 'cancel_folder_item_count', signature: 'public Task CancelFolderItemCountAsync(CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.CancelFolderItemCountMethod, new { }, ct)' },
  { method: 'cancel_count_items', signature: 'public Task CancelCountItemsAsync(CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.CancelCountItemsMethod, new { }, ct)' },
  { method: 'check_rar_installed', signature: 'public Task<bool> CheckRarInstalledAsync(CancellationToken ct = default)', body: 'InvokeAsync<bool>(Protocol.CheckRarInstalledMethod, new { }, ct)' },
  { method: 'prepare_rar_install', signature: 'public Task<RarInstallPlan> PrepareRarInstallAsync(CancellationToken ct = default)', body: 'InvokeAsync<RarInstallPlan>(Protocol.PrepareRarInstallMethod, new { }, ct)' },
  { method: 'discard_rar_install', signature: 'public Task DiscardRarInstallAsync(string confirmationToken, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.DiscardRarInstallMethod, new { confirmationToken }, ct)' },
  { method: 'install_rar', signature: 'public Task<string> InstallRarAsync(string confirmationToken, CancellationToken ct = default)', body: 'InvokeAsync<string>(Protocol.InstallRarMethod, new { confirmationToken }, ct)' },
  { method: 'disk_cleanup', signature: 'public Task<CleanupResult> DiskCleanupAsync(string directory, ulong? sizeThreshold, string? operationId, CancellationToken ct = default)', body: 'InvokeAsync<CleanupResult>(Protocol.DiskCleanupMethod, new { directory, sizeThreshold, operationId }, ct)' },
  { method: 'cancel_disk_cleanup', signature: 'public Task CancelDiskCleanupAsync(CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.CancelDiskCleanupMethod, new { }, ct)' },
  { method: 'duplicate_check', signature: 'public Task<DuplicateCheckResult> DuplicateCheckAsync(string directory, ulong? minSize, ulong? partialHashBytes, string? operationId, CancellationToken ct = default)', body: 'InvokeAsync<DuplicateCheckResult>(Protocol.DuplicateCheckMethod, new { directory, minSize, partialHashBytes, operationId }, ct)' },
  { method: 'cancel_duplicate_check', signature: 'public Task CancelDuplicateCheckAsync(CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.CancelDuplicateCheckMethod, new { }, ct)' },
  { method: 'get_all_tags', signature: 'public Task<Tag[]> GetAllTagsAsync(CancellationToken ct = default)', body: 'InvokeAsync<Tag[]>(Protocol.GetAllTagsMethod, new { }, ct)' },
  { method: 'create_tag', signature: 'public Task<Tag> CreateTagAsync(string name, string color, CancellationToken ct = default)', body: 'InvokeAsync<Tag>(Protocol.CreateTagMethod, new { name, color }, ct)' },
  { method: 'update_tag', signature: 'public Task<Tag> UpdateTagAsync(long id, string name, string color, CancellationToken ct = default)', body: 'InvokeAsync<Tag>(Protocol.UpdateTagMethod, new { id, name, color }, ct)' },
  { method: 'delete_tag', signature: 'public Task DeleteTagAsync(long id, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.DeleteTagMethod, new { id }, ct)' },
  { method: 'get_tags_for_path', signature: 'public Task<Tag[]> GetTagsForPathAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<Tag[]>(Protocol.GetTagsForPathMethod, new { path }, ct)' },
  { method: 'set_tags_for_path', signature: 'public Task SetTagsForPathAsync(string path, long[] tagIds, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.SetTagsForPathMethod, new { path, tagIds }, ct)' },
  { method: 'get_all_file_tags', signature: 'public Task<Dictionary<string, Tag>> GetAllFileTagsAsync(CancellationToken ct = default)', body: 'InvokeAsync<Dictionary<string, Tag>>(Protocol.GetAllFileTagsMethod, new { }, ct)' },
  { method: 'get_files_with_tag', signature: 'public Task<string[]> GetFilesWithTagAsync(long tagId, CancellationToken ct = default)', body: 'InvokeAsync<string[]>(Protocol.GetFilesWithTagMethod, new { tagId }, ct)' },
  { method: 'load_smart_folders', signature: 'public Task<SmartFolder[]> LoadSmartFoldersAsync(CancellationToken ct = default)', body: 'InvokeAsync<SmartFolder[]>(Protocol.LoadSmartFoldersMethod, new { }, ct)' },
  { method: 'save_smart_folder', signature: 'public Task<SmartFolder[]> SaveSmartFolderAsync(SmartFolder folder, CancellationToken ct = default)', body: 'InvokeAsync<SmartFolder[]>(Protocol.SaveSmartFolderMethod, new { folder }, ct)' },
  { method: 'delete_smart_folder', signature: 'public Task<SmartFolder[]> DeleteSmartFolderAsync(string id, CancellationToken ct = default)', body: 'InvokeAsync<SmartFolder[]>(Protocol.DeleteSmartFolderMethod, new { id }, ct)' },
  { method: 'get_app_about_info', signature: 'public Task<AppAboutInfo> GetAppAboutInfoAsync(CancellationToken ct = default)', body: 'InvokeAsync<AppAboutInfo>(Protocol.GetAppAboutInfoMethod, new { }, ct)' },
  { method: 'check_for_update', signature: 'public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)', body: 'InvokeAsync<UpdateInfo?>(Protocol.CheckForUpdateMethod, new { }, ct)' },
  { method: 'install_update', signature: 'public Task InstallUpdateAsync(CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.InstallUpdateMethod, new { }, ct)' },
  { method: 'open_terminal', signature: 'public Task OpenTerminalAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.OpenTerminalMethod, new { path }, ct)' },
  { method: 'open_powershell_admin', signature: 'public Task OpenPowershellAdminAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.OpenPowershellAdminMethod, new { path }, ct)' },
  { method: 'get_git_status', signature: 'public Task<GitStatus> GetGitStatusAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<GitStatus>(Protocol.GetGitStatusMethod, new { path }, ct)' },
  { method: 'get_git_file_statuses', signature: 'public Task<FileEntry[]> GetGitFileStatusesAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<FileEntry[]>(Protocol.GetGitFileStatusesMethod, new { path }, ct)' },
  { method: 'git_pull', signature: 'public Task GitPullAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.GitPullMethod, new { path }, ct)' },
  { method: 'git_push', signature: 'public Task GitPushAsync(string path, CancellationToken ct = default)', body: 'InvokeAsync<object?>(Protocol.GitPushMethod, new { path }, ct)' },
];

const generatedWrapperMethods = new Set(wrappers.map((wrapper) => wrapper.method));
for (const wrapper of wrappers) {
  if (!methods[wrapper.method]) {
    fail(`wrapper references unknown schema method ${wrapper.method}.`);
  }
}
for (const method of methodNames) {
  if (
    !manualClientMethods.has(method)
    && !intentionallyUnexposedClientMethods.has(method)
    && !generatedWrapperMethods.has(method)
  ) {
    fail(`schema method ${method} is not covered by a generated, manual, or intentionally unexposed client method.`);
  }
}

function renderCsharpProtocol() {
  const lines = [
    '// <auto-generated />',
    '// Generated by scripts/generate-ipc-bindings.mjs from ipc/schema/v1.',
    '#nullable enable',
    'using System.Collections.Generic;',
    '',
    'namespace SimpleFile.Ipc;',
    '',
    'public static partial class Protocol',
    '{',
    `    public const int DomainMethodCount = ${domainMethods.length};`,
  ];

  for (const tag of binaryTags) {
    lines.push(`    public const byte ${tag.cs} = ${tag.tag};`);
  }

  lines.push('');
  for (const method of controlMethods) {
    lines.push(`    public const string ${methodConstName(method)} = ${quote(method)};`);
  }
  for (const method of domainMethods) {
    lines.push(`    public const string ${methodConstName(method)} = ${quote(method)};`);
  }

  lines.push('');
  for (const eventName of emittedEvents) {
    lines.push(`    public const string ${eventConstName(eventName)} = ${quote(eventName)};`);
  }

  lines.push(
    '',
    '    public static IReadOnlyList<string> DomainMethods { get; } =',
    '    [',
  );
  for (const method of domainMethods) {
    lines.push(`        ${quote(method)},`);
  }
  lines.push(
    '    ];',
    '',
    '    public static IReadOnlyList<string> HostOwnedMethods { get; } =',
    '    [',
  );
  for (const method of domainMethods.filter((name) => methods[name]?.hostOwned)) {
    lines.push(`        ${quote(method)},`);
  }
  lines.push(
    '    ];',
    '',
    '    public static IReadOnlyList<string> CancellationMethods { get; } =',
    '    [',
  );
  for (const method of domainMethods.filter((name) => methods[name]?.cancellation)) {
    lines.push(`        ${quote(method)},`);
  }
  lines.push(
    '    ];',
    '',
    '    public static IReadOnlyList<(string Name, byte Tag)> BinaryFrameMethodTags { get; } =',
    '    [',
  );
  for (const tag of binaryTags.filter((entry) => entry.name.includes('_'))) {
    lines.push(`        (${quote(tag.name)}, ${tag.cs}),`);
  }
  lines.push(
    '    ];',
    '',
    '    public static IReadOnlyList<(string Name, byte Tag)> BinaryFrameEventTags { get; } =',
    '    [',
  );
  for (const eventName of emittedEvents) {
    const tag = events.emitted?.[eventName]?.binaryFrameTag;
    if (Number.isInteger(tag)) {
      const constant = binaryTags.find((entry) => entry.name === eventName)?.cs
        ?? binaryTags.find((entry) => entry.tag === tag)?.cs;
      lines.push(`        (${quote(eventName)}, ${constant}),`);
    }
  }
  lines.push('    ];', '}');
  return lines.join('\n');
}

function renderWrapper(wrapper) {
  if (wrapper.block) {
    return wrapper.block.map((line) => `    ${line}`).join('\n');
  }

  return `    ${wrapper.signature}\n        => ${wrapper.body};`;
}

function renderCsharpClient() {
  return [
    '// <auto-generated />',
    '// Generated by scripts/generate-ipc-bindings.mjs from ipc/schema/v1.',
    '#nullable enable',
    'using System.Collections.Generic;',
    'using System.Threading;',
    'using System.Threading.Tasks;',
    '',
    'namespace SimpleFile.Ipc;',
    '',
    'public sealed partial class NamedPipeJsonClient',
    '{',
    wrappers.map(renderWrapper).join('\n\n'),
    '}',
  ].join('\n');
}

function renderRustProtocol() {
  const lines = [
    '//! Generated IPC protocol metadata.',
    '//!',
    '//! Generated by scripts/generate-ipc-bindings.mjs from ipc/schema/v1.',
    '',
    `pub const DOMAIN_METHOD_COUNT: usize = ${domainMethods.length};`,
    '',
  ];

  for (const tag of binaryTags) {
    lines.push(`pub const ${tag.rust}: u8 = ${tag.tag};`);
  }

  lines.push('');
  for (const method of controlMethods) {
    lines.push(`pub const ${rustMethodConstName(method)}: &str = ${quote(method)};`);
  }
  for (const eventName of emittedEvents) {
    lines.push(`pub const ${rustEventConstName(eventName)}: &str = ${quote(eventName)};`);
  }
  for (const method of domainMethods) {
    lines.push(`pub const ${rustMethodConstName(method)}: &str = ${quote(method)};`);
  }

  lines.push(
    '',
    'pub const DOMAIN_METHODS: &[&str] = &[',
  );
  for (const method of domainMethods) {
    lines.push(`    ${rustMethodConstName(method)},`);
  }
  const hostOwnedMethods = domainMethods.filter((name) => methods[name]?.hostOwned);
  lines.push(
    '];',
    '',
    `pub const HOST_OWNED_METHODS: &[&str] = &[${hostOwnedMethods.map(rustMethodConstName).join(', ')}];`,
    '',
    'pub const CANCELLATION_METHODS: &[&str] = &[',
  );
  for (const method of domainMethods.filter((name) => methods[name]?.cancellation)) {
    lines.push(`    ${rustMethodConstName(method)},`);
  }
  lines.push(
    '];',
    '',
    'pub const BINARY_FRAME_METHOD_TAGS: &[(&str, u8)] = &[',
  );
  for (const tag of binaryTags.filter((entry) => entry.name.includes('_'))) {
    lines.push(`    (${quote(tag.name)}, ${tag.rust}),`);
  }
  lines.push(
    '];',
    '',
    'pub const BINARY_FRAME_EVENT_TAGS: &[(&str, u8)] = &[',
  );
  for (const eventName of emittedEvents) {
    const tag = events.emitted?.[eventName]?.binaryFrameTag;
    if (Number.isInteger(tag)) {
      const constant = binaryTags.find((entry) => entry.name === eventName)?.rust
        ?? binaryTags.find((entry) => entry.tag === tag)?.rust;
      lines.push(`    (${rustEventConstName(eventName)}, ${constant}),`);
    }
  }
  lines.push(
    '];',
    '',
    'pub fn is_control_method(method: &str) -> bool {',
    '    method == HANDSHAKE_METHOD || method == HEALTH_METHOD || method == SHUTDOWN_METHOD',
    '}',
    '',
    'pub fn is_domain_method(method: &str) -> bool {',
    '    DOMAIN_METHODS.contains(&method)',
    '}',
  );
  return lines.join('\n');
}

writeOrCheck('src-winui/SimpleFile.Ipc/Protocol.Generated.cs', renderCsharpProtocol());
writeOrCheck('src-winui/SimpleFile.Ipc/NamedPipeJsonClient.Generated.cs', renderCsharpClient());
writeOrCheck('crates/simplefile-ipc/src/protocol_generated.rs', renderRustProtocol());

if (!process.exitCode && checkOnly) {
  console.log(`IPC bindings are current (${domainMethods.length} domain methods, ${emittedEvents.length} emitted events).`);
}
