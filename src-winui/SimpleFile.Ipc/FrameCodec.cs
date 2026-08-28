namespace SimpleFile.Ipc;

public static class FrameCodec
{
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > Protocol.MaxFrameBytes)
        {
            throw new InvalidOperationException($"IPC frame exceeds {Protocol.MaxFrameBytes} bytes.");
        }

        var frame = new byte[4 + payload.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    public static uint DecodeLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            throw new InvalidOperationException("IPC frame header is truncated.");
        }

        var length = BitConverter.ToUInt32(header);
        if (length > Protocol.MaxFrameBytes)
        {
            throw new InvalidOperationException($"IPC frame length {length} exceeds {Protocol.MaxFrameBytes}.");
        }

        return length;
    }
}
