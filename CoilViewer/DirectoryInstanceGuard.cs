using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace CoilViewer;

internal sealed class DirectoryInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private RegisteredWaitHandle? _registeredWaitHandle;
    private Window? _window;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _listenerCts = new();
    private Task? _listenerTask;
    private Action<string?>? _requestHandler;

    private DirectoryInstanceGuard(string directory, Mutex mutex, EventWaitHandle activationEvent)
    {
        Directory = directory;
        _mutex = mutex;
        _activationEvent = activationEvent;
        _pipeName = BuildPipeName(directory);
        _listenerTask = Task.Run(() => ListenForRequestsAsync(_listenerCts.Token));
    }

    public string Directory { get; }

    public static bool TryAcquire(string directory, out DirectoryInstanceGuard? guard)
    {
        guard = null;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var mutex = new Mutex(initiallyOwned: true, name: BuildMutexName(directory), out var createdNew);
            var mutexTime = sw.ElapsedMilliseconds;
            
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }

            sw.Restart();
            var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, BuildEventName(directory));
            var eventTime = sw.ElapsedMilliseconds;
            
            sw.Restart();
            guard = new DirectoryInstanceGuard(directory, mutex, activationEvent);
            var guardTime = sw.ElapsedMilliseconds;
            
            Logger.Log($"Acquired directory guard for '{directory}' (Mutex: {mutexTime}ms, Event: {eventTime}ms, Guard: {guardTime}ms)");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool SignalExisting(string directory, string? requestedPath)
    {
        var requestSent = SendOpenRequest(directory, requestedPath);
        var signalled = false;

        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(BuildEventName(directory));
            activationEvent.Set();
            Logger.Log($"Signalled existing Coil Viewer instance for '{directory}'.");
            signalled = true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // instance may have exited between checks
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to signal existing instance for '{directory}'.", ex);
        }

        return requestSent || signalled;
    }

    private static bool SendOpenRequest(string directory, string? requestedPath)
    {
        var pipeName = BuildPipeName(directory);

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(2000);

            using (var writer = new StreamWriter(client, Encoding.UTF8, bufferSize: 1024, leaveOpen: true))
            {
                writer.AutoFlush = true;
                writer.WriteLine(requestedPath ?? string.Empty);
            }

            client.WaitForPipeDrain();
            return true;
        }
        catch (TimeoutException)
        {
            Logger.Log($"Timed out sending open request for '{directory}' with target '{requestedPath}'.");
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to send open request for '{directory}' with target '{requestedPath}'.", ex);
            return false;
        }
    }

    public static string? ResolveDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (System.IO.Directory.Exists(fullPath))
            {
                return Path.TrimEndingDirectorySeparator(fullPath);
            }

            if (System.IO.File.Exists(fullPath))
            {
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    var parentFull = Path.GetFullPath(parent);
                    return Path.TrimEndingDirectorySeparator(parentFull);
                }
            }
        }
        catch
        {
            // ignore resolution errors
        }

        return null;
    }

    public void AttachWindow(Window window)
    {
        _window = window;
        _registeredWaitHandle?.Unregister(null);
        _registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (timedOut || _window == null)
                {
                    return;
                }

                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ActivateWindow(_window);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to activate window for '{Directory}'.", ex);
                    }
                }));
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void SetRequestHandler(Action<string?>? handler)
    {
        _requestHandler = handler;
    }

    private async Task ListenForRequestsAsync(CancellationToken token)
    {
        var buffer = new char[512];

        while (!token.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var builder = new StringBuilder();

#if NET8_0_OR_GREATER
                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    builder.Append(buffer, 0, read);

                    if (pipe.IsMessageComplete)
                    {
                        break;
                    }
                }
#else
                while (true)
                {
                    var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    builder.Append(buffer, 0, read);

                    if (pipe.IsMessageComplete)
                    {
                        break;
                    }
                }
#endif

                var message = builder.ToString().TrimEnd('\0', '\r', '\n');
                Logger.Log($"Received open request for '{Directory}' with target '{message}'.");

                if (_window != null)
                {
                    _ = _window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(message))
                            {
                                _requestHandler?.Invoke(message);
                            }
                            else
                            {
                                _requestHandler?.Invoke(null);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"Failed to process open request for '{Directory}'.", ex);
                        }
                        finally
                        {
                            ActivateWindow(_window);
                        }
                    }));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Named pipe listener error for '{Directory}'.", ex);
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        _registeredWaitHandle?.Unregister(null);
        _registeredWaitHandle = null;
        _window = null;
        _requestHandler = null;

        _listenerCts.Cancel();
        if (_listenerTask != null)
        {
            try
            {
                _listenerTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                // expected during shutdown
            }
            catch (OperationCanceledException)
            {
                // expected during shutdown
            }
            catch (Exception ex)
            {
                Logger.LogError($"Directory guard listener shutdown failed for '{Directory}'.", ex);
            }
        }

        _listenerTask = null;
        _listenerCts.Dispose();

        _activationEvent.Dispose();

        try
        {
            _mutex.ReleaseMutex();
            Logger.Log($"Released directory guard for '{Directory}'.");
        }
        catch (ApplicationException)
        {
            // already released
        }
        finally
        {
            _mutex.Dispose();
        }
    }

    private static void ActivateWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();

        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle;
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, SW_SHOW);
            SetForegroundWindow(handle);
        }

        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private static string BuildMutexName(string directory)
    {
        return $@"Global\CoilViewer.Directory.{ComputeHash(directory)}";
    }

    private static string BuildEventName(string directory)
    {
        return $@"Global\CoilViewer.Activate.{ComputeHash(directory)}";
    }

    private static string BuildPipeName(string directory)
    {
        return $"CoilViewer.Pipe.{ComputeHash(directory)}";
    }

    private static string ComputeHash(string value)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes);
    }

    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}


