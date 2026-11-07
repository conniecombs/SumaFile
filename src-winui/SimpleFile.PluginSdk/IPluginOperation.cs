namespace SimpleFile.PluginSdk;

public interface IPluginOperation : IDisposable
{
    CancellationToken CancellationToken { get; }
    void Report(long current, long total, string? detail);
    void Complete();
    void Fail(string message);
}
