using System.IO.Pipes;
using System.Text;

namespace BootVideoManager.App.Services;

/// <summary>
/// Keeps one running copy per user session. A second launch asks the first one, through a named pipe, to bring its
/// window to the front, then exits. The mutex also lets the Windows installer detect a running copy.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Also referenced by <c>packaging/windows/BootVideoManager.iss</c>: keep both in sync.</summary>
    public const string MutexName = "BootVideoManager.Running";

    private const string ActivateMessage = "activate";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned, string pipeName)
    {
        _mutex = mutex;
        _owned = owned;
        _pipeName = pipeName;
    }

    /// <summary>Raised on a background thread when another launch asks this instance to show itself.</summary>
    public static event EventHandler? ActivationRequested;

    /// <summary>True for the first running copy.</summary>
    public bool IsPrimary => _owned;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return new SingleInstance(mutex, createdNew, $"BootVideoManager.Activate.{Environment.UserName}");
    }

    /// <summary>Primary instance: waits for activation requests until disposed.</summary>
    public void StartListening() => _ = Task.Run(ListenAsync);

    /// <summary>Secondary instance: asks the primary one to show its window.</summary>
    /// <returns><c>false</c> if the primary instance did not answer.</returns>
    public bool SignalPrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(ConnectTimeout);
            client.Write(Encoding.UTF8.GetBytes(ActivateMessage));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Releases the mutex (before a restart, or at exit).</summary>
    public void Dispose()
    {
        _stop.Cancel();
        if (_owned)
        {
            _mutex.ReleaseMutex();
            _owned = false;
        }

        _mutex.Dispose();
        _stop.Dispose();
    }

    private async Task ListenAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                var buffer = new byte[32];
                var read = await server.ReadAsync(buffer, token).ConfigureAwait(false);
                if (Encoding.UTF8.GetString(buffer, 0, read) == ActivateMessage)
                {
                    ActivationRequested?.Invoke(null, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("Single-instance pipe failed; retrying.", ex);
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
