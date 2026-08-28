namespace SimpleFile.Ipc;

public static partial class Protocol
{
    public const int Version = 1;
    public const string JsonRpc = "2.0";
    public const uint MaxFrameBytes = 80 * 1024 * 1024;
    public const byte BinaryFrameVersion = 1;

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
