// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Utils;

/// <summary>
/// DistributedApplication.CreateBuilder() creates a builder that includes configuration to read from appsettings.json.
/// The builder has a FileSystemWatcher, which can't be cleaned up unless a DistributedApplication is built and disposed.
/// This class wraps the builder and provides a way to automatically dispose it to prevent test failures from excessive
/// FileSystemWatcher instances from many tests.
/// </summary>
public static class TestDistributedApplicationBuilder
{
    public static IDistributedApplicationTestingBuilder Create(DistributedApplicationOperation operation)
    {
        var args = operation switch
        {
            DistributedApplicationOperation.Run => (string[])[],
            DistributedApplicationOperation.Publish => ["Publishing:Publisher=manifest"],
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        return Create(args);
    }

    /// <summary>
    /// Creates a new instance of <see cref="DistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <returns>
    /// A new instance of <see cref="DistributedApplicationTestingBuilder"/>.
    /// </returns>
    public static IDistributedApplicationTestingBuilder Create(params string[] args)
    {
        return new TestingBuilder((o, a) => o.Args = args);
    }

    /// <summary>
    /// Creates a new instance of <see cref="DistributedApplicationTestingBuilder"/>.
    /// </summary>
    /// <returns>
    /// A new instance of <see cref="DistributedApplicationTestingBuilder"/>.
    /// </returns>
    public static IDistributedApplicationTestingBuilder Create(Action<DistributedApplicationOptions> configureOptions)
    {
        return new TestingBuilder((o, a) => configureOptions(o));
    }

    private sealed class TestingBuilder : IDistributedApplicationTestingBuilder
    {
        private readonly DistributedApplicationBuilder _innerBuilder;
        private bool _didBuild;
        private bool _disposedValue;

        public TestingBuilder(Action<DistributedApplicationOptions, HostApplicationBuilderSettings>? configureOptions)
        {
            var appAssembly = typeof(TestDistributedApplicationBuilder).Assembly;
            var assemblyName = appAssembly.FullName;

            _innerBuilder = BuilderInterceptor.CreateBuilder(Configure);

            _innerBuilder.Services.Configure<DashboardOptions>(o =>
            {
                // Make sure we have a dashboard URL and OTLP endpoint URL (but don't overwrite them if they're already set)
                o.DashboardUrl ??= "http://localhost:8080";
                o.OtlpEndpointUrl ??= "http://localhost:4317";
            });

            _innerBuilder.Services.AddHttpClient();
            _innerBuilder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

            void Configure(DistributedApplicationOptions applicationOptions, HostApplicationBuilderSettings hostBuilderOptions)
            {
                hostBuilderOptions.EnvironmentName = Environments.Development;
                hostBuilderOptions.ApplicationName = appAssembly.GetName().Name;
                applicationOptions.AssemblyName = assemblyName;
                applicationOptions.DisableDashboard = true;
                var cfg = hostBuilderOptions.Configuration ??= new();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DcpPublisher:RandomizePorts"] = "true",
                    ["DcpPublisher:DeleteResourcesOnShutdown"] = "true",
                    ["DcpPublisher:ResourceNameSuffix"] = $"{Random.Shared.Next():x}",
                });

                // If there is no assembly specified, apply default values for DCP and the dashboard.
                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["DcpPublisher:DcpCliPath"] = "dcp",
                        ["DcpPublisher:aspiredashboardpath"] = "dashboard",
                    });
                }

                configureOptions?.Invoke(applicationOptions, hostBuilderOptions);
            }
        }

        public ConfigurationManager Configuration => _innerBuilder.Configuration;

        public string AppHostDirectory => _innerBuilder.AppHostDirectory;

        public IHostEnvironment Environment => _innerBuilder.Environment;

        public IServiceCollection Services => _innerBuilder.Services;

        public DistributedApplicationExecutionContext ExecutionContext => _innerBuilder.ExecutionContext;

        public IResourceCollection Resources => _innerBuilder.Resources;

        public IResourceBuilder<T> AddResource<T>(T resource) where T : IResource => _innerBuilder.AddResource(resource);

        public DistributedApplication Build()
        {
            try
            {
                return _innerBuilder.Build();
            }
            finally
            {
                _didBuild = true;
            }
        }

        public Task<DistributedApplication> BuildAsync(CancellationToken cancellationToken = default) => Task.FromResult(Build());

        public IResourceBuilder<T> CreateResourceBuilder<T>(T resource) where T : IResource
        {
            return _innerBuilder.CreateResourceBuilder(resource);
        }

        public void Dispose()
        {
            if (!_disposedValue)
            {
                _disposedValue = true;
                if (!_didBuild)
                {
                    try
                    {
                        using var app = Build();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private sealed class BuilderInterceptor : IObserver<DiagnosticListener>
        {
            private static readonly ThreadLocal<BuilderInterceptor?> s_currentListener = new();
            private readonly ApplicationBuilderDiagnosticListener _applicationBuilderListener;
            private readonly Action<DistributedApplicationOptions, HostApplicationBuilderSettings>? _onConstructing;

            private BuilderInterceptor(Action<DistributedApplicationOptions, HostApplicationBuilderSettings>? onConstructing)
            {
                _onConstructing = onConstructing;
                _applicationBuilderListener = new(this);
            }

            public static DistributedApplicationBuilder CreateBuilder(Action<DistributedApplicationOptions, HostApplicationBuilderSettings> onConstructing)
            {
                var interceptor = new BuilderInterceptor(onConstructing);
                var original = s_currentListener.Value;
                s_currentListener.Value = interceptor;
                try
                {
                    using var subscription = DiagnosticListener.AllListeners.Subscribe(interceptor);
                    return new DistributedApplicationBuilder([]);
                }
                finally
                {
                    s_currentListener.Value = original;
                }
            }

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {

            }

            public void OnNext(DiagnosticListener value)
            {
                if (s_currentListener.Value != this)
                {
                    // Ignore events that aren't for this listener
                    return;
                }

                if (value.Name == "Aspire.Hosting")
                {
                    _applicationBuilderListener.Subscribe(value);
                }
            }

            private sealed class ApplicationBuilderDiagnosticListener(BuilderInterceptor owner) : IObserver<KeyValuePair<string, object?>>
            {
                private IDisposable? _disposable;

                public void Subscribe(DiagnosticListener listener)
                {
                    _disposable = listener.Subscribe(this);
                }

                public void OnCompleted()
                {
                    _disposable?.Dispose();
                }

                public void OnError(Exception error)
                {
                }

                public void OnNext(KeyValuePair<string, object?> value)
                {
                    if (s_currentListener.Value != owner)
                    {
                        // Ignore events that aren't for this listener
                        return;
                    }

                    if (value.Key == "DistributedApplicationBuilderConstructing")
                    {
                        var args = ((DistributedApplicationOptions Options, HostApplicationBuilderSettings InnerBuilderOptions))value.Value!;
                        owner._onConstructing?.Invoke(args.Options, args.InnerBuilderOptions);
                    }
                }
            }
        }
    }

}

