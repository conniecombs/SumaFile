using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace SimpleFile.Ipc;

/// <summary>
/// Reads data from a named Windows shared memory section created by the SumaFile backend.
/// Enables zero-copy memory transfer for large payloads and when disk caching is disabled.
/// </summary>
public static class SharedMemoryReader
{
    /// <summary>
    /// Reads all bytes from the specified named memory mapped file.
    /// </summary>
    public static byte[] Read(string name, long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        using var stream = mmf.CreateViewStream(0, length, MemoryMappedFileAccess.Read);
        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = stream.Read(buffer, totalRead, (int)(length - totalRead));
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

        return buffer;
    }

    /// <summary>
    /// Attempts to read all bytes from the specified named memory mapped file.
    /// </summary>
    public static bool TryRead(string name, long length, out byte[]? data)
    {
        try
        {
            data = Read(name, length);
            return true;
        }
        catch
        {
            data = null;
            return false;
        }
    }
}
