using System.Buffers.Binary;
using System.Text;

namespace SimpleFile.Ipc;

public static class BinaryFrameCodec
{
    private static readonly byte[] Magic = "SFB1"u8.ToArray();

    public static bool TryDecode(ReadOnlySpan<byte> payload, out BinaryFrameMessage? message)
    {
        message = null;
        if (payload.Length < 6 || !payload[..4].SequenceEqual(Magic))
        {
            return false;
        }

        var reader = new BinaryPayloadReader(payload[6..]);
        var version = payload[4];
        var tag = payload[5];
        if (version != Protocol.BinaryFrameVersion)
        {
            throw new InvalidOperationException($"Unsupported SumaFile binary frame version {version}.");
        }

        message = tag switch
        {
            Protocol.BinaryListDirectoryChunk => BinaryFrameMessage.Notification(
                Protocol.ListDirectoryChunkEvent,
                ReadDirectoryListingChunkNotification(ref reader)),
            Protocol.BinaryListDirectoryResult => BinaryFrameMessage.Response(
                reader.ReadInt32(),
                ReadDirectoryListing(ref reader)),
            Protocol.BinarySearchResultsBatch => BinaryFrameMessage.Notification(
                Protocol.SearchResultsBatchEvent,
                ReadSearchResults(ref reader)),
            Protocol.BinarySearchResultsResult => BinaryFrameMessage.Response(
                reader.ReadInt32(),
                ReadSearchResults(ref reader)),
            Protocol.BinaryOperationProgress => BinaryFrameMessage.Notification(
                Protocol.OperationProgressEvent,
                ReadProgressUpdate(ref reader)),
            Protocol.BinaryFileChange => BinaryFrameMessage.Notification(
                Protocol.FileChangeEvent,
                ReadFileChange(ref reader)),
            Protocol.BinaryThumbnailResult => BinaryFrameMessage.Response(
                reader.ReadInt32(),
                reader.ReadString()),
            Protocol.BinaryThumbnailsResult => BinaryFrameMessage.Response(
                reader.ReadInt32(),
                ReadThumbnailResults(ref reader)),
            _ => throw new InvalidOperationException($"Unknown SumaFile binary frame tag {tag}."),
        };

        reader.EnsureComplete();
        return true;
    }

    public static byte[] EncodeDirectoryListingChunk(DirectoryListingChunkNotification chunk)
    {
        var writer = new BinaryPayloadWriter(Protocol.BinaryListDirectoryChunk);
        writer.WriteInt32(chunk.RequestId);
        WriteDirectoryListingChunk(writer, chunk);
        return writer.ToArray();
    }

    public static byte[] EncodeDirectoryListingResult(int requestId, DirectoryListing listing)
    {
        var writer = new BinaryPayloadWriter(Protocol.BinaryListDirectoryResult);
        writer.WriteInt32(requestId);
        writer.WriteString(listing.Path);
        writer.WriteOptionalString(listing.Parent);
        WriteEntries(writer, listing.Entries);
        writer.WriteBool(listing.IsNetwork);
        return writer.ToArray();
    }

    public static byte[] EncodeSearchResultsBatch(IReadOnlyList<SearchResult> results)
    {
        var writer = new BinaryPayloadWriter(Protocol.BinarySearchResultsBatch);
        WriteSearchResults(writer, results);
        return writer.ToArray();
    }

    public static byte[] EncodeSearchResultsResult(int requestId, IReadOnlyList<SearchResult> results)
    {
        var writer = new BinaryPayloadWriter(Protocol.BinarySearchResultsResult);
        writer.WriteInt32(requestId);
        WriteSearchResults(writer, results);
        return writer.ToArray();
    }

    public static byte[] EncodeProgressUpdate(ProgressUpdate update)
    {
        var writer = new BinaryPayloadWriter(Protocol.BinaryOperationProgress);
        writer.WriteString(update.OperationId);
        writer.WriteString(update.OperationType);
        writer.WriteUInt64(update.Current);
        writer.WriteUInt64(update.Total);
        writer.WriteUInt64(update.CurrentFiles);
        writer.WriteUInt64(update.TotalFiles);
        writer.WriteString(update.CurrentItem);
        writer.WriteString(update.Status);
        writer.WriteOptionalString(update.Error);
        return writer.ToArray();
    }

    public static byte[] EncodeFileChange(FileChangeEvent change)
    {
        var writer = new BinaryPayloadWriter(Protocol.BinaryFileChange);
        writer.WriteString(change.Path);
        writer.WriteString(change.Kind);
        return writer.ToArray();
    }

    public static byte[] EncodeThumbnailResult(int requestId, string data)
    {
        var writer = new BinaryPayloadWriter(Protocol.BinaryThumbnailResult);
        writer.WriteInt32(requestId);
        writer.WriteString(data);
        return writer.ToArray();
    }

    public static byte[] EncodeThumbnailResultsResult(int requestId, IReadOnlyList<ThumbnailResult> results)
    {
        var writer = new BinaryPayloadWriter(Protocol.BinaryThumbnailsResult);
        writer.WriteInt32(requestId);
        writer.WriteCount(results.Count);
        foreach (var result in results)
        {
            writer.WriteString(result.Path);
            writer.WriteOptionalString(result.Data);
            writer.WriteOptionalString(result.Error);
        }

        return writer.ToArray();
    }

    private static DirectoryListingChunkNotification ReadDirectoryListingChunkNotification(
        ref BinaryPayloadReader reader)
    {
        var requestId = reader.ReadInt32();
        var chunk = ReadDirectoryListingChunk(ref reader);
        return new DirectoryListingChunkNotification
        {
            RequestId = requestId,
            Path = chunk.Path,
            Parent = chunk.Parent,
            Entries = chunk.Entries,
            ChunkIndex = chunk.ChunkIndex,
            Done = chunk.Done,
            IsNetwork = chunk.IsNetwork,
        };
    }

    private static DirectoryListing ReadDirectoryListing(ref BinaryPayloadReader reader)
    {
        return new DirectoryListing
        {
            Path = reader.ReadString(),
            Parent = reader.ReadOptionalString(),
            Entries = ReadEntries(ref reader),
            IsNetwork = reader.ReadBool(),
        };
    }

    private static DirectoryListingChunk ReadDirectoryListingChunk(ref BinaryPayloadReader reader)
    {
        return new DirectoryListingChunk
        {
            Path = reader.ReadString(),
            Parent = reader.ReadOptionalString(),
            Entries = ReadEntries(ref reader),
            ChunkIndex = reader.ReadUInt32(),
            Done = reader.ReadBool(),
            IsNetwork = reader.ReadBool(),
        };
    }

    private static List<FileEntry> ReadEntries(ref BinaryPayloadReader reader)
    {
        var count = reader.ReadCount();
        var entries = new List<FileEntry>(count);
        for (var i = 0; i < count; i++)
        {
            entries.Add(new FileEntry
            {
                Name = reader.ReadString(),
                Path = reader.ReadString(),
                IsDir = reader.ReadBool(),
                IsSymlink = reader.ReadBool(),
                Size = reader.ReadUInt64(),
                Modified = reader.ReadString(),
                Extension = reader.ReadString(),
                Permissions = reader.ReadOptionalString(),
                SymlinkTarget = reader.ReadOptionalString(),
                GitStatus = reader.ReadOptionalString(),
            });
        }

        return entries;
    }

    private static SearchResult[] ReadSearchResults(ref BinaryPayloadReader reader)
    {
        var count = reader.ReadCount();
        var results = new SearchResult[count];
        for (var i = 0; i < count; i++)
        {
            results[i] = new SearchResult
            {
                Name = reader.ReadString(),
                Path = reader.ReadString(),
                IsDir = reader.ReadBool(),
                Size = reader.ReadUInt64(),
                Modified = reader.ReadString(),
                Extension = reader.ReadString(),
                MatchType = reader.ReadString(),
            };
        }

        return results;
    }

    private static ProgressUpdate ReadProgressUpdate(ref BinaryPayloadReader reader)
    {
        return new ProgressUpdate
        {
            OperationId = reader.ReadString(),
            OperationType = reader.ReadString(),
            Current = reader.ReadUInt64(),
            Total = reader.ReadUInt64(),
            CurrentFiles = reader.ReadUInt64(),
            TotalFiles = reader.ReadUInt64(),
            CurrentItem = reader.ReadString(),
            Status = reader.ReadString(),
            Error = reader.ReadOptionalString(),
        };
    }

    private static FileChangeEvent ReadFileChange(ref BinaryPayloadReader reader)
    {
        return new FileChangeEvent
        {
            Path = reader.ReadString(),
            Kind = reader.ReadString(),
        };
    }

    private static ThumbnailResult[] ReadThumbnailResults(ref BinaryPayloadReader reader)
    {
        var count = reader.ReadCount();
        var results = new ThumbnailResult[count];
        for (var i = 0; i < count; i++)
        {
            results[i] = new ThumbnailResult
            {
                Path = reader.ReadString(),
                Data = reader.ReadOptionalString(),
                Error = reader.ReadOptionalString(),
            };
        }

        return results;
    }

    private static void WriteDirectoryListingChunk(
        BinaryPayloadWriter writer,
        DirectoryListingChunkNotification chunk)
    {
        writer.WriteString(chunk.Path);
        writer.WriteOptionalString(chunk.Parent);
        WriteEntries(writer, chunk.Entries);
        writer.WriteUInt32(chunk.ChunkIndex);
        writer.WriteBool(chunk.Done);
        writer.WriteBool(chunk.IsNetwork);
    }

    private static void WriteEntries(BinaryPayloadWriter writer, IReadOnlyList<FileEntry> entries)
    {
        writer.WriteCount(entries.Count);
        foreach (var entry in entries)
        {
            writer.WriteString(entry.Name);
            writer.WriteString(entry.Path);
            writer.WriteBool(entry.IsDir);
            writer.WriteBool(entry.IsSymlink);
            writer.WriteUInt64(entry.Size);
            writer.WriteString(entry.Modified);
            writer.WriteString(entry.Extension);
            writer.WriteOptionalString(entry.Permissions);
            writer.WriteOptionalString(entry.SymlinkTarget);
            writer.WriteOptionalString(entry.GitStatus);
        }
    }

    private static void WriteSearchResults(BinaryPayloadWriter writer, IReadOnlyList<SearchResult> results)
    {
        writer.WriteCount(results.Count);
        foreach (var result in results)
        {
            writer.WriteString(result.Name);
            writer.WriteString(result.Path);
            writer.WriteBool(result.IsDir);
            writer.WriteUInt64(result.Size);
            writer.WriteString(result.Modified);
            writer.WriteString(result.Extension);
            writer.WriteString(result.MatchType);
        }
    }

    private ref struct BinaryPayloadReader
    {
        private ReadOnlySpan<byte> _remaining;

        public BinaryPayloadReader(ReadOnlySpan<byte> payload)
        {
            _remaining = payload;
        }

        public bool ReadBool()
        {
            Ensure(1);
            var value = _remaining[0] != 0;
            _remaining = _remaining[1..];
            return value;
        }

        public int ReadInt32()
        {
            Ensure(sizeof(int));
            var value = BinaryPrimitives.ReadInt32LittleEndian(_remaining);
            _remaining = _remaining[sizeof(int)..];
            return value;
        }

        public uint ReadUInt32()
        {
            Ensure(sizeof(uint));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_remaining);
            _remaining = _remaining[sizeof(uint)..];
            return value;
        }

        public ulong ReadUInt64()
        {
            Ensure(sizeof(ulong));
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_remaining);
            _remaining = _remaining[sizeof(ulong)..];
            return value;
        }

        public int ReadCount()
        {
            var value = ReadUInt32();
            if (value > int.MaxValue)
            {
                throw new InvalidOperationException("Binary payload count exceeds Int32.MaxValue.");
            }

            return (int)value;
        }

        public string ReadString()
        {
            var length = ReadCount();
            Ensure(length);
            var value = Encoding.UTF8.GetString(_remaining[..length]);
            _remaining = _remaining[length..];
            return value;
        }

        public string? ReadOptionalString()
        {
            return ReadBool() ? ReadString() : null;
        }

        public void EnsureComplete()
        {
            if (!_remaining.IsEmpty)
            {
                throw new InvalidOperationException("Binary payload has trailing bytes.");
            }
        }

        private readonly void Ensure(int length)
        {
            if (_remaining.Length < length)
            {
                throw new InvalidOperationException("Binary payload is truncated.");
            }
        }
    }

    private sealed class BinaryPayloadWriter
    {
        private readonly List<byte> _bytes = new(256);

        public BinaryPayloadWriter(byte tag)
        {
            _bytes.AddRange(Magic);
            _bytes.Add(Protocol.BinaryFrameVersion);
            _bytes.Add(tag);
        }

        public byte[] ToArray() => _bytes.ToArray();

        public void WriteBool(bool value) => _bytes.Add(value ? (byte)1 : (byte)0);

        public void WriteInt32(int value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            WriteBytes(buffer);
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            WriteBytes(buffer);
        }

        public void WriteUInt64(ulong value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            WriteBytes(buffer);
        }

        public void WriteCount(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            WriteUInt32((uint)count);
        }

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteCount(bytes.Length);
            _bytes.AddRange(bytes);
        }

        public void WriteOptionalString(string? value)
        {
            WriteBool(value is not null);
            if (value is not null)
            {
                WriteString(value);
            }
        }

        private void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                _bytes.Add(bytes[i]);
            }
        }
    }
}

public sealed class BinaryFrameMessage
{
    private BinaryFrameMessage(string? eventName, int? responseId, object? payload)
    {
        EventName = eventName;
        ResponseId = responseId;
        Payload = payload;
    }

    public string? EventName { get; }

    public int? ResponseId { get; }

    public object? Payload { get; }

    public static BinaryFrameMessage Notification(string eventName, object payload)
    {
        return new BinaryFrameMessage(eventName, null, payload);
    }

    public static BinaryFrameMessage Response(int responseId, object? payload)
    {
        return new BinaryFrameMessage(null, responseId, payload);
    }
}
