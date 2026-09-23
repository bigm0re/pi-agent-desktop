using System.IO.Pipes;

namespace PiAgentDesktop;

/// <summary>
/// Guarantees a single running shell. A second launch hands a "show" command to
/// the running instance over a named pipe and exits immediately.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PiAgentDesktop.SingleInstance.v1";
    private const string PipeName = "PiAgentDesktop.Activate.v1";
    private const string ShowCommand = "show";

    private Mutex? _mutex;
    private CancellationTokenSource? _cancellation;
    private Task? _listener;

    /// <summary>Returns false when another instance already owns the mutex.</summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return createdNew;
    }

    public void StartListening(Action onActivate)
    {
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        _listener = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var command = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (string.Equals(command, ShowCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        onActivate();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // A failed connection must never kill the listener loop.
                    await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, token);
    }

    /// <summary>Asks the running instance to reveal its window.</summary>
    public static bool SignalRunningInstance(int attempts = 6)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(400);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ShowCommand);
                return true;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            /* ignore */
        }

        _cancellation?.Dispose();
        _mutex?.Dispose();
        _ = _listener;
    }
}
