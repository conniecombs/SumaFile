namespace SimpleFile.PluginSdk;

public interface ICommandHandler
{
    Task ExecuteAsync(PluginCommandContext context, CancellationToken ct);
}
