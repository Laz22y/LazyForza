namespace LazyForza.Modules.Abstractions;

public sealed class ModuleManager : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, ILazyForzaModule> modules;
    private readonly IModuleContext context;

    public ModuleManager(IEnumerable<ILazyForzaModule> modules, IModuleContext context)
    {
        this.modules = modules.ToDictionary(module => module.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
        this.context = context;
    }

    public IReadOnlyCollection<ILazyForzaModule> Modules => modules.Values.ToArray();

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var module in modules.Values)
        {
            await module.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask SetEnabledAsync(string moduleId, bool enabled, CancellationToken cancellationToken)
    {
        await SetEnabledCoreAsync(
                moduleId,
                enabled,
                persistPreference: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SetRuntimeEnabledAsync(
        string moduleId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await SetEnabledCoreAsync(
                moduleId,
                enabled,
                persistPreference: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask SetEnabledCoreAsync(
        string moduleId,
        bool enabled,
        bool persistPreference,
        CancellationToken cancellationToken)
    {
        if (!modules.TryGetValue(moduleId, out var module))
        {
            throw new KeyNotFoundException($"Unknown module '{moduleId}'.");
        }

        if (enabled)
        {
            foreach (var dependencyId in module.Descriptor.Dependencies)
            {
                if (!modules.TryGetValue(dependencyId, out var dependency))
                {
                    throw new InvalidOperationException($"Missing dependency '{dependencyId}' for '{moduleId}'.");
                }

                await dependency.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            await module.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var dependent = modules.Values.FirstOrDefault(candidate =>
                candidate.Status.IsEnabled && candidate.Descriptor.Dependencies.Contains(moduleId, StringComparer.OrdinalIgnoreCase));
            if (dependent is not null)
            {
                throw new InvalidOperationException($"Module '{moduleId}' is required by running module '{dependent.Descriptor.Id}'.");
            }

            await module.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        if (persistPreference)
        {
            await context.Settings
                .SetAsync(moduleId, "enabled", enabled.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var module in modules.Values.Reverse())
        {
            await module.DisposeAsync().ConfigureAwait(false);
        }
    }
}
