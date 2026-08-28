namespace SimpleFile.Ipc;

public enum IpcErrorKind
{
    Unknown = 0,
    Transport,
    Parse,
    InvalidRequest,
    MethodNotFound,
    InvalidParams,
    Internal,
    Application,
    HostOwned,
    Handshake,
}

public sealed class IpcException : Exception
{
    public int Code { get; }

    public IpcErrorKind Kind { get; }

    public IpcException(int code, string message)
        : this(code, message, inner: null, KindFromCode(code))
    {
    }

    public IpcException(int code, string message, Exception? inner)
        : this(code, message, inner, KindFromCode(code))
    {
    }

    private IpcException(int code, string message, Exception? inner, IpcErrorKind kind)
        : base(message, inner)
    {
        Code = code;
        Kind = kind;
    }

    public bool IsConflict => HasPrefix(Protocol.PrefixConflict);

    public bool IsTrashUnavailable => HasPrefix(Protocol.PrefixTrashUnavailable);

    public bool IsResultTooLarge => HasPrefix(Protocol.PrefixResultTooLarge);

    public bool IsHostOwned =>
        Code == Protocol.ErrHostOwned || HasPrefix(Protocol.PrefixHostOwned);

    public static IpcException Transport(string message, Exception? inner = null)
    {
        return new IpcException(0, message, inner, IpcErrorKind.Transport);
    }

    public static IpcErrorKind KindFromCode(int code)
    {
        return code switch
        {
            Protocol.ErrParse => IpcErrorKind.Parse,
            Protocol.ErrInvalidRequest => IpcErrorKind.InvalidRequest,
            Protocol.ErrMethodNotFound => IpcErrorKind.MethodNotFound,
            Protocol.ErrInvalidParams => IpcErrorKind.InvalidParams,
            Protocol.ErrInternal => IpcErrorKind.Internal,
            Protocol.ErrApplication => IpcErrorKind.Application,
            Protocol.ErrHostOwned => IpcErrorKind.HostOwned,
            Protocol.ErrHandshake => IpcErrorKind.Handshake,
            _ => IpcErrorKind.Unknown,
        };
    }

    private bool HasPrefix(string prefix)
    {
        return Message.StartsWith(prefix, StringComparison.Ordinal);
    }
}
