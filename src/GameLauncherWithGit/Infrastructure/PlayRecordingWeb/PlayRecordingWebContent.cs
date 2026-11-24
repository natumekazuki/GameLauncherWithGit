using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

/// <summary>Pairs a non-routable panel with the operation service owned by the desktop app.</summary>
public sealed class PlayRecordingWebContent
{
    private readonly ServiceDescriptor _borrowedService;

    private PlayRecordingWebContent(Type panelType, ServiceDescriptor borrowedService)
    {
        if (!typeof(IComponent).IsAssignableFrom(panelType) || panelType.IsAbstract
            || panelType.IsDefined(typeof(RouteAttribute), inherit: false))
            throw new ArgumentException("A concrete non-routable Razor component is required.", nameof(panelType));

        PanelType = panelType;
        _borrowedService = borrowedService;
    }

    internal Type PanelType { get; }

    /// <summary>
    /// Bridges the given instance without transferring disposal ownership to the Web host.
    /// </summary>
    public static PlayRecordingWebContent WithService<TService>(Type panelType, TService service) where TService : class
    {
        ArgumentNullException.ThrowIfNull(panelType);
        ArgumentNullException.ThrowIfNull(service);
        return new(panelType, ServiceDescriptor.Singleton(service));
    }

    internal void Register(IServiceCollection services) => services.Add(_borrowedService);
}
