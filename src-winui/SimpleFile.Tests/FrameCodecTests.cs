using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class FrameCodecTests
{
    [Fact]
    public void EncodeThenDecodeLength_RoundTripsPayloadSize()
    {
        var payload = """{"jsonrpc":"2.0","id":1}"""u8.ToArray();
        var frame = FrameCodec.Encode(payload);
        Assert.Equal(payload.Length + 4, frame.Length);
        Assert.Equal((uint)payload.Length, FrameCodec.DecodeLength(frame));
        Assert.Equal(payload, frame[4..]);
    }

    [Fact]
    public void DecodeLength_RejectsOversizePrefix()
    {
        var header = BitConverter.GetBytes(Protocol.MaxFrameBytes + 1);
        var error = Assert.Throws<InvalidOperationException>(() => FrameCodec.DecodeLength(header));
        Assert.Contains("exceeds", error.Message);
    }
}
