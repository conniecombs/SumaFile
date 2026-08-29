using System.Text.Json;
using SimpleFile.Ipc;
using Xunit;
using DriveInfo = SimpleFile.Ipc.DriveInfo;

namespace SimpleFile.Tests;

public class ModelsTests
{
    [Fact]
    public void FileEntry_DeserializesSnakeCaseGolden()
    {
        const string json = """
            {
              "name": "notes.txt",
              "path": "C:\\Users\\Public\\notes.txt",
              "is_dir": false,
              "is_symlink": false,
              "size": 12,
              "modified": "2026-01-01T00:00:00.000Z",
              "extension": "txt"
            }
            """;

        var entry = JsonSerializer.Deserialize<FileEntry>(json, IpcJson.Options);
        Assert.NotNull(entry);
        Assert.Equal("notes.txt", entry.Name);
        Assert.Equal(@"C:\Users\Public\notes.txt", entry.Path);
        Assert.False(entry.IsDir);
        Assert.False(entry.IsSymlink);
        Assert.Equal(12ul, entry.Size);
        Assert.Equal("txt", entry.Extension);
        Assert.Null(entry.Permissions);
        Assert.Null(entry.GitStatus);
        Assert.Null(entry.ItemCount);
        Assert.False(entry.IsHidden);
        Assert.False(entry.IsSystem);
    }

    [Fact]
    public void FileEntry_DeserializesHiddenAndSystemFlags()
    {
        const string json = """
            {
              "name": "desktop.ini",
              "path": "C:\\desktop.ini",
              "is_dir": false,
              "is_symlink": false,
              "is_hidden": true,
              "is_system": true,
              "size": 42,
              "modified": "2026-01-01T00:00:00.000Z",
              "extension": "ini"
            }
            """;

        var entry = JsonSerializer.Deserialize<FileEntry>(json, IpcJson.Options);
        Assert.NotNull(entry);
        Assert.True(entry.IsHidden);
        Assert.True(entry.IsSystem);
    }

    [Fact]
    public void DirectoryListingChunkNotification_DeserializesRequestIdAndSnakeCase()
    {
        const string json = """
            {
              "requestId": 7,
              "path": "C:\\Users\\Public",
              "parent": "C:\\Users",
              "entries": [],
              "chunk_index": 0,
              "done": true,
              "is_network": false
            }
            """;

        var notification = JsonSerializer.Deserialize<DirectoryListingChunkNotification>(json, IpcJson.Options);
        Assert.NotNull(notification);
        Assert.Equal(7, notification.RequestId);
        Assert.Equal(0u, notification.ChunkIndex);
        Assert.True(notification.Done);
        Assert.False(notification.IsNetwork);
        Assert.Equal(@"C:\Users", notification.Parent);

        var chunk = notification.ToChunk();
        Assert.Equal(notification.Path, chunk.Path);
        Assert.True(chunk.Done);
    }

    [Fact]
    public void HandshakeParams_SerializeCamelCase()
    {
        var request = new JsonRpcRequest
        {
            Id = 1,
            Method = Protocol.HandshakeMethod,
            Params = new HandshakeParams
            {
                AuthToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            },
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, IpcJson.Options));
        var root = document.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.Equal("ipc.handshake", root.GetProperty("method").GetString());
        var parameters = root.GetProperty("params");
        Assert.Equal(1, parameters.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(Protocol.ClientName, parameters.GetProperty("clientName").GetString());
        Assert.False(parameters.TryGetProperty("protocol_version", out _));
    }

    [Fact]
    public void DriveInfo_DeserializesSnakeCase()
    {
        const string json = """
            {
              "name": "Windows",
              "path": "C:\\",
              "drive_type": "fixed",
              "file_system": "NTFS",
              "total_space": 100,
              "free_space": 40,
              "remote_path": null,
              "drive_status": "ready"
            }
            """;

        var drive = JsonSerializer.Deserialize<DriveInfo>(json, IpcJson.Options);
        Assert.NotNull(drive);
        Assert.Equal("Windows", drive.Name);
        Assert.Equal("fixed", drive.DriveType);
        Assert.Equal("NTFS", drive.FileSystem);
        Assert.Equal(100ul, drive.TotalSpace);
        Assert.Equal("ready", drive.DriveStatus);
        Assert.Null(drive.RemotePath);
    }

    [Fact]
    public void InspectionModels_DeserializeSnakeCaseTuples()
    {
        const string json = """
            {
              "file_type": "text",
              "mime_type": "text/plain",
              "size": 5,
              "content": "hello",
              "encoding": "utf-8"
            }
            """;

        var preview = JsonSerializer.Deserialize<FilePreview>(json, IpcJson.Options);
        Assert.NotNull(preview);
        Assert.Equal("text", preview.FileType);
        Assert.Equal("hello", preview.Content);

        const string metadataJson = """
            {
              "kind": "image",
              "summary": "10 x 20",
              "fields": [["Width", "10"], ["Height", "20"]]
            }
            """;

        var metadata = JsonSerializer.Deserialize<FileMetadata>(metadataJson, IpcJson.Options);
        Assert.NotNull(metadata);
        Assert.Equal("image", metadata.Kind);
        Assert.Equal(["Width", "10"], metadata.Fields[0]);

        const string comparisonJson = """
            {
              "left_path": "C:\\left.txt",
              "right_path": "C:\\right.txt",
              "left_name": "left.txt",
              "right_name": "right.txt",
              "left_size": 5,
              "right_size": 6,
              "identical": false,
              "added": 1,
              "removed": 0,
              "changed": 0,
              "rows": [{"kind": "added", "left_line": null, "right_line": 1, "left_text": null, "right_text": "hello"}]
            }
            """;

        var comparison = JsonSerializer.Deserialize<FileComparison>(comparisonJson, IpcJson.Options);
        Assert.NotNull(comparison);
        Assert.False(comparison.Identical);
        Assert.Equal("added", comparison.Rows[0].Kind);
        Assert.Equal(1, comparison.Rows[0].RightLine);

        const string binaryComparisonJson = """
            {
              "left_path": "C:\\left.exe",
              "right_path": "C:\\right.exe",
              "left_name": "left.exe",
              "right_name": "right.exe",
              "left_size": 16,
              "right_size": 18,
              "identical": false,
              "added": 2,
              "removed": 0,
              "changed": 1,
              "comparison_type": "binary",
              "compared_bytes": 18,
              "different_bytes": 3,
              "first_difference": 5,
              "binary_rows_truncated": false,
              "rows": [],
              "binary_rows": [
                {
                  "offset": 0,
                  "left_hex": "00 01",
                  "right_hex": "00 FF",
                  "left_ascii": "..",
                  "right_ascii": "..",
                  "different": true
                }
              ]
            }
            """;

        var binaryComparison = JsonSerializer.Deserialize<FileComparison>(binaryComparisonJson, IpcJson.Options);
        Assert.NotNull(binaryComparison);
        Assert.Equal("binary", binaryComparison.ComparisonType);
        Assert.Equal(18ul, binaryComparison.ComparedBytes);
        Assert.Equal(3ul, binaryComparison.DifferentBytes);
        Assert.Equal(5ul, binaryComparison.FirstDifference);
        Assert.Single(binaryComparison.BinaryRows);
        Assert.True(binaryComparison.BinaryRows[0].Different);
    }

    [Fact]
    public void ArchiveModels_DeserializeSnakeCase()
    {
        const string json = """
            {
              "path": "C:\\pack.zip",
              "format": "zip",
              "entries": [
                {
                  "name": "notes.txt",
                  "path": "folder/notes.txt",
                  "is_dir": false,
                  "size": 12,
                  "compressed_size": 8
                }
              ],
              "unsafe_entries": ["../escape.txt"],
              "total_size": 12,
              "compressed_size": 8
            }
            """;

        var archive = JsonSerializer.Deserialize<ArchiveInfo>(json, IpcJson.Options);
        Assert.NotNull(archive);
        Assert.Equal("zip", archive.Format);
        Assert.Equal(12ul, archive.TotalSize);
        Assert.Equal("../escape.txt", archive.UnsafeEntries[0]);
        Assert.Equal("notes.txt", archive.Entries[0].Name);
        Assert.Equal(8ul, archive.Entries[0].CompressedSize);
    }
}
