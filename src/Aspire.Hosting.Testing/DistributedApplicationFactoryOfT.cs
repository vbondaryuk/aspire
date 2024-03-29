// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Factory for creating a distributed application for testing.
/// </summary>
/// <typeparam name="TEntryPoint">
/// A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.
/// </typeparam>
public class DistributedApplicationFactory<TEntryPoint> : DistributedApplicationFactory where TEntryPoint : class
{
    /// <summary>
    /// Initializes a new <see cref="DistributedApplicationFactory{TEntryPoint}"/> instance.
    /// </summary>
    public DistributedApplicationFactory() : base(typeof(TEntryPoint).Assembly)
    { }
}

/// <summary>
/// Factory for creating a distributed application for testing.
/// </summary>
public class DistributedApplicationFactory : IDisposable, IAsyncDisposable
{
    private readonly TaskCompletionSource _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DistributedApplicationBuilder> _builderTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DistributedApplication> _appTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lockObj = new();
    private readonly Assembly? _entryPointAssembly;
    private bool _entryPointStarted;
    private IHostApplicationLifetime? _hostApplicationLifetime;
    private IEntryPointInvoker? _entryPointInvoker;

    /// <summary>
    /// Initializes a new <see cref="DistributedApplicationFactory"/> instance.
    /// </summary>
    /// <param name="entryPointAssembly">
    /// The assembly containing the executable entry point which will be instrumented by this factory.
    /// </param>
    public DistributedApplicationFactory(Assembly? entryPointAssembly)
    {
        _entryPointAssembly = entryPointAssembly;
    }

    /// <summary>
    /// Gets the distributed application associated with this instance.
    /// </summary>
    internal async Task<DistributedApplicationBuilder> ResolveBuilderAsync(CancellationToken cancellationToken = default)
    {
        EnsureEntryPointStarted();
        return await _builderTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the distributed application associated with this instance.
    /// </summary>
    internal async Task<DistributedApplication> ResolveApplicationAsync(CancellationToken cancellationToken = default)
    {
        EnsureEntryPointStarted();
        return await _appTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the application.
    /// </summary>
    /// <param name="cancellationToken">A token used to signal cancellation.</param>
    /// <returns>A <see cref="Task"/> representing the completion of the operation.</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureEntryPointStarted();
        await _startedTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an instance of <see cref="HttpClient"/> that is configured to route requests to the specified resource and endpoint.
    /// </summary>
    /// <returns>The <see cref="HttpClient"/>.</returns>
    public HttpClient CreateHttpClient(string resourceName, string? endpointName = default)
    {
        return GetStartedApplication().CreateHttpClient(resourceName, endpointName);
    }

    /// <summary>
    /// Gets the connection string for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The connection string for the specified resource.</returns>
    /// <exception cref="ArgumentException">The resource was not found or does not expose a connection string.</exception>
    public ValueTask<string?> GetConnectionString(string resourceName)
    {
        return GetStartedApplication().GetConnectionStringAsync(resourceName);
    }

    /// <summary>
    /// Gets the endpoint for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <param name="endpointName">The optional endpoint name. If none are specified, the single defined endpoint is returned.</param>
    /// <returns>A URI representation of the endpoint.</returns>
    /// <exception cref="ArgumentException">The resource was not found, no matching endpoint was found, or multiple endpoints were found.</exception>
    /// <exception cref="InvalidOperationException">The resource has no endpoints.</exception>
    public Uri GetEndpoint(string resourceName, string? endpointName = default)
    {
        return GetStartedApplication().GetEndpoint(resourceName, endpointName);
    }

    /// <summary>
    /// Called when the application builder is being created.
    /// </summary>
    /// <param name="applicationOptions">The application options.</param>
    /// <param name="hostOptions">The host builder options.</param>
    protected virtual void OnBuilderCreating(DistributedApplicationOptions applicationOptions, HostApplicationBuilderSettings hostOptions)
    {
    }

    /// <summary>
    /// Called when the application builder is created.
    /// </summary>
    /// <param name="applicationBuilder">The application builder.</param>
    protected virtual void OnBuilderCreated(DistributedApplicationBuilder applicationBuilder)
    {
    }

    /// <summary>
    /// Called when the application is being built.
    /// </summary>
    /// <param name="applicationBuilder">The application builder.</param>
    protected virtual void OnBuilding(DistributedApplicationBuilder applicationBuilder)
    {
    }

    /// <summary>
    /// Called when the application has been built.
    /// </summary>
    /// <param name="application">The application.</param>
    protected virtual void OnBuilt(DistributedApplication application)
    {
    }

    private void OnBuiltCore(DistributedApplication application)
    {
        _appTcs.TrySetResult(application);
        OnBuilt(application);
    }

    private void OnBuilderCreatingCore(DistributedApplicationOptions applicationOptions, HostApplicationBuilderSettings hostBuilderOptions)
    {
        hostBuilderOptions.EnvironmentName = Environments.Development;
        hostBuilderOptions.ApplicationName = _entryPointAssembly?.GetName().Name ?? string.Empty;
        applicationOptions.AssemblyName = _entryPointAssembly?.GetName().Name ?? string.Empty;
        applicationOptions.DisableDashboard = true;
        var cfg = hostBuilderOptions.Configuration ??= new();
        cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DcpPublisher:RandomizePorts"] = "true",
            ["DcpPublisher:DeleteResourcesOnShutdown"] = "true",
            ["DcpPublisher:ResourceNameSuffix"] = $"{Random.Shared.Next():x}",
        });

        OnBuilderCreating(applicationOptions, hostBuilderOptions);
    }

    private void OnBuilderCreatedCore(DistributedApplicationBuilder applicationBuilder)
    {
        OnBuilderCreated(applicationBuilder);
    }

    private void OnBuildingCore(DistributedApplicationBuilder applicationBuilder)
    {
        // Patch DcpOptions configuration
        var services = applicationBuilder.Services;

        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

        InterceptHostCreation(applicationBuilder);

        _builderTcs.TrySetResult(applicationBuilder);
        OnBuilding(applicationBuilder);
    }

    private void EnsureEntryPointStarted()
    {
        if (!_entryPointStarted)
        {
            lock (_lockObj)
            {
                if (!_entryPointStarted)
                {
                    EnsureDepsFile();

                    // This helper launches the target assembly's entry point and hooks into the lifecycle
                    // so we can intercept execution at key stages.
                    _entryPointInvoker = DistributedApplicationEntryPointInvoker.ResolveEntryPoint(
                        _entryPointAssembly,
                        onConstructing: OnBuilderCreatingCore,
                        onConstructed: OnBuilderCreatedCore,
                        onBuilding: OnBuildingCore,
                        entryPointCompleted: OnEntryPointExit);

                    if (_entryPointInvoker is null)
                    {
                        throw new InvalidOperationException(
                            $"Could not intercept application builder instance. Ensure that {_entryPointAssembly} is an executable assembly, that the entry point creates an {typeof(DistributedApplicationBuilder)}, and that the resulting {typeof(DistributedApplication)} is being started.");
                    }

                    _ = InvokeEntryPoint(_entryPointInvoker);
                    _entryPointStarted = true;
                }
            }
        }
    }

    private async Task InvokeEntryPoint(IEntryPointInvoker entryPointInvoker)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            using var cts = new CancellationTokenSource(GetConfiguredTimeout());
            var app = await entryPointInvoker.StartAsync([], cts.Token).ConfigureAwait(false);
            _hostApplicationLifetime = app.Services.GetService<IHostApplicationLifetime>()
                ?? throw new InvalidOperationException($"Application did not register an implementation of {typeof(IHostApplicationLifetime)}.");
            OnBuiltCore(app);
        }
        catch (Exception exception)
        {
            _appTcs.TrySetException(exception);
        }

        static TimeSpan GetConfiguredTimeout()
        {
            const string TimeoutEnvironmentKey = "DOTNET_HOST_FACTORY_RESOLVER_DEFAULT_TIMEOUT_IN_SECONDS";
            if (Debugger.IsAttached)
            {
                return Timeout.InfiniteTimeSpan;
            }

            if (uint.TryParse(Environment.GetEnvironmentVariable(TimeoutEnvironmentKey), out var timeoutInSeconds))
            {
                return TimeSpan.FromSeconds((int)timeoutInSeconds);
            }

            return TimeSpan.FromMinutes(5);
        }
    }

    private void OnEntryPointExit(Exception? exception)
    {
        if (exception is not null)
        {
            _exitTcs.TrySetException(exception);
            _appTcs.TrySetException(exception);
            _startedTcs.TrySetException(exception);
            _builderTcs.TrySetException(exception);
        }
        else
        {
            _exitTcs.TrySetResult();
            _appTcs.TrySetCanceled();
            _startedTcs.TrySetCanceled();
            _builderTcs.TrySetCanceled();
        }
    }

    private void ThrowIfNotInitialized()
    {
        if (!_startedTcs.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("The application has not been initialized.");
        }
    }

    private void EnsureDepsFile()
    {
        if (_entryPointAssembly is null)
        {
            return;
        }

        if (_entryPointAssembly.EntryPoint == null)
        {
            throw new InvalidOperationException($"Assembly {_entryPointAssembly} does not have an entry point.");
        }

        var depsFileName = $"{_entryPointAssembly.GetName().Name}.deps.json";
        var depsFile = new FileInfo(Path.Combine(AppContext.BaseDirectory, depsFileName));
        if (!depsFile.Exists)
        {
            throw new InvalidOperationException($"Missing deps file '{Path.GetFileName(depsFile.FullName)}'. Make sure the project has been built.");
        }
    }

    private DistributedApplication GetStartedApplication()
    {
        ThrowIfNotInitialized();
        return _appTcs.Task.GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public virtual void Dispose()
    {
        _builderTcs.TrySetCanceled();
        _startedTcs.TrySetCanceled();
        if (_hostApplicationLifetime is null || _appTcs.Task is not { IsCompletedSuccessfully: true } appTask)
        {
            return;
        }

        _hostApplicationLifetime?.StopApplication();
        appTask.GetAwaiter().GetResult()?.Dispose();
    }

    /// <inheritdoc/>
    public virtual async ValueTask DisposeAsync()
    {
        _builderTcs.TrySetCanceled();
        _startedTcs.TrySetCanceled();
        if (_appTcs.Task is not { IsCompletedSuccessfully: true } appTask)
        {
            return;
        }

        _hostApplicationLifetime?.StopApplication();
        await _exitTcs.Task.ConfigureAwait(false);

        if (appTask.GetAwaiter().GetResult() is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Replaces the IHost registration with an InterceptedHost registration which delegates to the original registration.
    private void InterceptHostCreation(DistributedApplicationBuilder applicationBuilder)
    {
        // Find the original IHost registration and remove it.
        var hostDescriptor = applicationBuilder.Services.Single(s => s.ServiceType == typeof(IHost) && s.ServiceKey is null);
        applicationBuilder.Services.Remove(hostDescriptor);

        Func<IServiceProvider, IHost> innerHostFactory = hostDescriptor switch
        {
            { ImplementationFactory: { } factory } => sp => (IHost)factory(sp),
            { ImplementationInstance: { } instance } => _ => (IHost)instance,
            { ImplementationType: { } type } => sp => (IHost)ActivatorUtilities.CreateInstance(sp, type),
            _ => throw new InvalidOperationException($"Registered service descriptor for {typeof(IHost)} does not conform to any known pattern.")
        };

        // Register the replacement, which will construct the original host during resolution, enabling interception.
        applicationBuilder.Services.AddSingleton<IHost>(sp => new ObservedHost(innerHostFactory(sp), this));
    }

    private sealed class ObservedHost(IHost innerHost, DistributedApplicationFactory appFactory) : IHost, IAsyncDisposable
    {
        private bool _disposing;

        public IServiceProvider Services => innerHost.Services;

        public void Dispose()
        {
            if (_disposing)
            {
                return;
            }

            _disposing = true;
            innerHost.Dispose();
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposing)
            {
                return;
            }

            _disposing = true;
            if (innerHost is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                innerHost.Dispose();
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await innerHost.StartAsync(cancellationToken).ConfigureAwait(false);
                appFactory._startedTcs.TrySetResult();
            }
            catch (Exception exception)
            {
                appFactory._startedTcs.TrySetException(exception);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await innerHost.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
