using Microsoft.Extensions.DependencyInjection;

namespace SimpleFile.Core;

/// <summary>
/// Application-level dependency injection container. Replaces the static
/// ServiceLocator pattern with a proper DI container.
/// </summary>
public static class AppServices
{
    private static IServiceProvider? _provider;

    /// <summary>
    /// The global service provider. Must be configured before first use.
    /// </summary>
    public static IServiceProvider Provider =>
        _provider ?? throw new InvalidOperationException(
            "AppServices.Configure() must be called before accessing the service provider.");

    /// <summary>
    /// Configures the DI container with all ViewModels and services.
    /// Called once during app startup.
    /// </summary>
    public static void Configure(ExplorerWorkspace workspace)
    {
        var services = new ServiceCollection();

        // Register the workspace as a singleton (it's already created by app startup).
        services.AddSingleton(workspace);

        // Register ViewModels — transient so each resolution gets fresh state,
        // but in practice these are resolved once and held by the MainWindow.
        services.AddTransient<SearchViewModel>();
        services.AddTransient<TransferViewModel>();
        services.AddSingleton<TransferManagerViewModel>();
        services.AddTransient<ToolbarViewModel>();

        _provider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Resolves a service from the DI container.
    /// </summary>
    public static T GetRequired<T>() where T : notnull =>
        Provider.GetRequiredService<T>();
}
