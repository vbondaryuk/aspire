using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Testing;

internal interface IEntryPointInvoker
{
    Task<DistributedApplication> StartAsync(string[] args, CancellationToken cancellationToken);
}

internal sealed class DistributedApplicationEntryPointInvoker
{
    // This helpers encapsulates all of the complex logic required to:
    // 1. Execute the entry point of the specified assembly in a different thread.
    // 2. Wait for the diagnostic source events to fire
    // 3. Give the caller a chance to execute logic to mutate the IDistributedApplicationBuilder
    // 4. Resolve the instance of the DistributedApplication
    // 5. Allow the caller to determine if the entry point has completed
    public static IEntryPointInvoker? ResolveEntryPoint(
        Assembly? assembly,
        Action<DistributedApplicationOptions, HostApplicationBuilderSettings>? onConstructing = null,
        Action<DistributedApplicationBuilder>? onConstructed = null,
        Action<DistributedApplicationBuilder>? onBuilding = null,
        Action<Exception?>? entryPointCompleted = null)
    {
        if (assembly?.EntryPoint is null)
        {
            return null;
        }

        return new EntryPointInvoker(
                assembly.EntryPoint,
                onConstructing,
                onConstructed,
                onBuilding,
                entryPointCompleted);
    }

    private sealed class EntryPointInvoker : IEntryPointInvoker, IObserver<DiagnosticListener>
    {
        private static readonly AsyncLocal<EntryPointInvoker> s_currentListener = new();
        private readonly Func<string[], object?> _entryPoint;
        private readonly TaskCompletionSource<DistributedApplication> _appTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entryPointTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ApplicationBuilderDiagnosticListener _applicationBuilderListener;
        private readonly Action<DistributedApplicationOptions, HostApplicationBuilderSettings>? _onConstructing;
        private readonly Action<DistributedApplicationBuilder>? _onConstructed;
        private readonly Action<DistributedApplicationBuilder>? _onBuilding;
        private readonly Action<Exception?>? _entryPointCompleted;
        private readonly string _invokerThreadName = "DistributedApplicationFactory.EntryPoint";

        public EntryPointInvoker(
            MethodInfo entryPoint,
            Action<DistributedApplicationOptions, HostApplicationBuilderSettings>? onConstructing,
            Action<DistributedApplicationBuilder>? onConstructed,
            Action<DistributedApplicationBuilder>? onBuilding,
            Action<Exception?>? entryPointCompleted) : this(
                GetEntryPointInvoker(entryPoint),
                onConstructing,
                onConstructed,
                onBuilding,
                entryPointCompleted,
                $"{entryPoint.DeclaringType?.Assembly.GetName().Name ?? "Unknown"}.EntryPoint")
        {
        }

        public EntryPointInvoker(
            Func<string[], object?> entryPoint,
            Action<DistributedApplicationOptions, HostApplicationBuilderSettings>? onConstructing,
            Action<DistributedApplicationBuilder>? onConstructed,
            Action<DistributedApplicationBuilder>? onBuilding,
            Action<Exception?>? entryPointCompleted,
            string invokerThreadName)
        {
            _entryPoint = entryPoint;
            _onConstructing = onConstructing;
            _onConstructed = onConstructed;
            _onBuilding = onBuilding;
            _entryPointCompleted = entryPointCompleted;
            _invokerThreadName = invokerThreadName;
            _applicationBuilderListener = new(this);
        }

        private static Func<string[], object?> GetEntryPointInvoker(MethodInfo entryPoint)
        {
            var parameters = entryPoint.GetParameters();
            if (parameters.Length == 0)
            {
                return args => entryPoint.Invoke(null, []);
            }
            else
            {
                return args => entryPoint.Invoke(null, [args]);
            }
        }

        public async Task<DistributedApplication> StartAsync(string[] args, CancellationToken cancellationToken)
        {
            using var subscription = DiagnosticListener.AllListeners.Subscribe(this);

            // Kick off the entry point on a new thread so we don't block the current one
            // in case we need to timeout the execution
            var thread = new Thread(() =>
            {
                Exception? exception = null;

                try
                {
                    // Set the async local to the instance of the HostingListener so we can filter events that
                    // aren't scoped to this execution of the entry point.
                    s_currentListener.Value = this;

                    // Invoke the entry point.
                    _ = _entryPoint(args);

                    // Try to set an exception if the entry point returns gracefully, this will force
                    // build to throw
                    _appTcs.TrySetException(new InvalidOperationException($"The entry point exited without building a {nameof(DistributedApplication)}."));
                }
                catch (TargetInvocationException tie)
                {
                    exception = tie.InnerException ?? tie;

                    // Another exception happened, propagate that to the caller
                    _appTcs.TrySetException(exception);
                }
                catch (Exception ex)
                {
                    exception = ex;

                    // Another exception happened, propagate that to the caller
                    _appTcs.TrySetException(exception);
                }
                finally
                {
                    if (exception is AggregateException ae)
                    {
                        if (ae.InnerExceptions.Count == 1)
                        {
                            exception = ae.InnerExceptions[0];
                        }
                    }

                    // Signal that the entry point is completed
                    if (exception is not null)
                    {
                        _entryPointTcs.SetException(exception);
                    }
                    else
                    {
                        _entryPointTcs.SetResult();
                    }

                    _entryPointCompleted?.Invoke(exception);
                }
            })
            {
                // Make sure this doesn't hang the process
                IsBackground = true,
                Name = _invokerThreadName
            };

            // Start the thread
            thread.Start();

            return await _appTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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

        private sealed class ApplicationBuilderDiagnosticListener(EntryPointInvoker owner) : IObserver<KeyValuePair<string, object?>>
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

                if (value.Key == "DistributedApplicationBuilderConstructed")
                {
                    owner._onConstructed?.Invoke((DistributedApplicationBuilder)value.Value!);
                }

                if (value.Key == "DistributedApplicationBuilding")
                {
                    owner._onBuilding?.Invoke((DistributedApplicationBuilder)value.Value!);
                }

                if (value.Key == "DistributedApplicationBuilt")
                {
                    owner._appTcs.TrySetResult((DistributedApplication)value.Value!);
                }
            }
        }
    }
}
