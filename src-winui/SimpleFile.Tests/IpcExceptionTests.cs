using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class IpcExceptionTests
{
    [Fact]
    public void ConflictPrefix_IsApplicationError()
    {
        var error = new IpcException(
            Protocol.ErrApplication,
            "CONFLICT: destination already exists: C:\\dest\\copy.txt");

        Assert.Equal(IpcErrorKind.Application, error.Kind);
        Assert.True(error.IsConflict);
        Assert.False(error.IsTrashUnavailable);
        Assert.False(error.IsResultTooLarge);
        Assert.False(error.IsHostOwned);
        Assert.Equal("CONFLICT: destination already exists: C:\\dest\\copy.txt", error.Message);
    }

    [Fact]
    public void TrashUnavailablePrefix_IsPreservedExactly()
    {
        var error = new IpcException(
            Protocol.ErrApplication,
            "TRASH_UNAVAILABLE: Recycle Bin is not available");

        Assert.True(error.IsTrashUnavailable);
        Assert.False(error.IsConflict);
        Assert.Equal("TRASH_UNAVAILABLE: Recycle Bin is not available", error.Message);
    }

    [Fact]
    public void ResultTooLargePrefix_IsApplicationError()
    {
        var error = new IpcException(
            Protocol.ErrApplication,
            "RESULT_TOO_LARGE: list_directory result exceeds 80 MiB; use streamed chunks");

        Assert.True(error.IsResultTooLarge);
        Assert.Equal(IpcErrorKind.Application, error.Kind);
    }

    [Fact]
    public void HostOwned_UsesDedicatedCode()
    {
        var error = new IpcException(Protocol.ErrHostOwned, "HOST_OWNED: select_directory");

        Assert.Equal(IpcErrorKind.HostOwned, error.Kind);
        Assert.True(error.IsHostOwned);
        Assert.Equal(Protocol.ErrHostOwned, error.Code);
    }

    [Theory]
    [InlineData(Protocol.ErrParse, IpcErrorKind.Parse)]
    [InlineData(Protocol.ErrInvalidRequest, IpcErrorKind.InvalidRequest)]
    [InlineData(Protocol.ErrMethodNotFound, IpcErrorKind.MethodNotFound)]
    [InlineData(Protocol.ErrInvalidParams, IpcErrorKind.InvalidParams)]
    [InlineData(Protocol.ErrInternal, IpcErrorKind.Internal)]
    [InlineData(Protocol.ErrHandshake, IpcErrorKind.Handshake)]
    public void KindFromCode_MapsJsonRpcCodes(int code, IpcErrorKind kind)
    {
        Assert.Equal(kind, IpcException.KindFromCode(code));
        Assert.Equal(kind, new IpcException(code, "x").Kind);
    }

    [Fact]
    public void Transport_DoesNotWrapApplicationMessage()
    {
        var error = IpcException.Transport("IPC pipe closed.");
        Assert.Equal(IpcErrorKind.Transport, error.Kind);
        Assert.Equal("IPC pipe closed.", error.Message);
        Assert.Equal(0, error.Code);
    }
}
