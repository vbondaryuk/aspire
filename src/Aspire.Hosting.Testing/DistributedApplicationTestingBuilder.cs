// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Methods for creating distributed application instances for testing purposes.
/// </summary>
public static class DistributedApplicationTestingBuilder
{
    /// <summary>
    /// Creates a new instance of <see cref="DistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <typeparam name="TEntryPoint">
    /// A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.
    /// </typeparam>
    /// <returns>
    /// A new instance of <see cref="DistributedApplicationTestingBuilder"/>.
    /// </returns>
    public static async Task<IDistributedApplicationTestingBuilder> CreateAsync<TEntryPoint>(
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder,
        CancellationToken cancellationToken = default)
        where TEntryPoint : class
    {
        var factory = new SuspendingDistributedApplicationFactory((_, __) => { }, assembly: typeof(TEntryPoint).Assembly);
        return await factory.CreateBuilderAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new instance of <see cref="DistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <typeparam name="TEntryPoint">
    /// A type in the entry point assembly of the target Aspire AppHost. Typically, the Program class can be used.
    /// </typeparam>
    /// <returns>
    /// A new instance of <see cref="DistributedApplicationTestingBuilder"/>.
    /// </returns>
    public static async Task<IDistributedApplicationTestingBuilder> CreateAsync<TEntryPoint>(CancellationToken cancellationToken = default) where TEntryPoint : class
    {
        var factory = new SuspendingDistributedApplicationFactory((_, __) => { }, assembly: typeof(TEntryPoint).Assembly);
        return await factory.CreateBuilderAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class SuspendingDistributedApplicationFactory(Action<DistributedApplicationOptions, HostApplicationBuilderSettings> configureBuilder, Assembly? assembly)
        : DistributedApplicationFactory(assembly)
    {
        private readonly SemaphoreSlim _continueBuilding = new(0);

        public async Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(CancellationToken cancellationToken)
        {
            var innerBuilder = await ResolveBuilderAsync(cancellationToken).ConfigureAwait(false);
            return new Builder(this, innerBuilder);
        }

        protected override void OnBuilderCreating(DistributedApplicationOptions applicationOptions, HostApplicationBuilderSettings hostOptions)
        {
            base.OnBuilderCreating(applicationOptions, hostOptions);
            configureBuilder(applicationOptions, hostOptions);
        }

        protected override void OnBuilderCreated(DistributedApplicationBuilder applicationBuilder)
        {
            base.OnBuilderCreated(applicationBuilder);
        }

        protected override void OnBuilding(DistributedApplicationBuilder applicationBuilder)
        {
            base.OnBuilding(applicationBuilder);

            // Wait until the owner signals that building can continue by calling BuildAsync().
            _continueBuilding.Wait();
        }

        public async Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken)
        {
            _continueBuilding.Release();
            return await ResolveApplicationAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask DisposeAsync()
        {
            _continueBuilding.Release();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override void Dispose()
        {
            _continueBuilding.Release();
            base.Dispose();
        }

        private sealed class Builder(SuspendingDistributedApplicationFactory factory, DistributedApplicationBuilder innerBuilder) : IDistributedApplicationTestingBuilder
        {
            private bool _builtApp;

            public ConfigurationManager Configuration => innerBuilder.Configuration;

            public string AppHostDirectory => innerBuilder.AppHostDirectory;

            public IHostEnvironment Environment => innerBuilder.Environment;

            public IServiceCollection Services => innerBuilder.Services;

            public DistributedApplicationExecutionContext ExecutionContext => innerBuilder.ExecutionContext;

            public IResourceCollection Resources => innerBuilder.Resources;

            public IResourceBuilder<T> AddResource<T>(T resource) where T : IResource => innerBuilder.AddResource(resource);

            public DistributedApplication Build() => BuildAsync(CancellationToken.None).GetAwaiter().GetResult();

            public async Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken)
            {
                _builtApp = true;
                var innerApp = await factory.BuildAsync(cancellationToken).ConfigureAwait(false);
                return new DelegatedDistributedApplication(new DelegatedHost(factory, innerApp));
            }

            public IResourceBuilder<T> CreateResourceBuilder<T>(T resource) where T : IResource => innerBuilder.CreateResourceBuilder(resource);

            public void Dispose()
            {
                // When the builder is disposed we build a host and then dispose it.
                // This cleans up unmanaged resources on the inner builder.
                if (!_builtApp)
                {
                    try
                    {
                        Build().Dispose();
                    }
                    catch
                    {
                        // Ignore errors.
                    }
                }
            }
        }

        private sealed class DelegatedDistributedApplication(DelegatedHost host) : DistributedApplication(host)
        {
            private readonly DelegatedHost _host = host;

            public override async Task RunAsync(CancellationToken cancellationToken)
            {
                // Avoid calling the base here, since it will execute the pre-start hooks
                // before calling the corresponding host method, which also executes the same pre-start hooks.
                await _host.RunAsync(cancellationToken).ConfigureAwait(false);
            }

            public override async Task StartAsync(CancellationToken cancellationToken)
            {
                // Avoid calling the base here, since it will execute the pre-start hooks
                // before calling the corresponding host method, which also executes the same pre-start hooks.
                await _host.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            public override async Task StopAsync(CancellationToken cancellationToken)
            {
                await _host.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class DelegatedHost(SuspendingDistributedApplicationFactory appFactory, DistributedApplication innerApp) : IHost, IAsyncDisposable
        {
            public IServiceProvider Services => innerApp.Services;

            public void Dispose()
            {
                appFactory.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                await appFactory.DisposeAsync().ConfigureAwait(false);
            }

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                await appFactory.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            public async Task StopAsync(CancellationToken cancellationToken)
            {
                await appFactory.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// A builder for creating instances of <see cref="DistributedApplication"/> for testing purposes.
/// </summary>
public interface IDistributedApplicationTestingBuilder : IDistributedApplicationBuilder, IDisposable
{
    /// <summary>
    /// Builds and returns a new <see cref="DistributedApplication"/> instance. This can only be called once.
    /// </summary>
    /// <returns>A new <see cref="DistributedApplication"/> instance.</returns>
    Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken = default);
}
