using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using UsageTrackerNative.Shell;

namespace UsageTrackerNative;

internal sealed class NativeControlPipeServer : IDisposable
{
    public const string PipeName = "Shiji.NativeControl";
    private readonly Dispatcher _dispatcher;
    private readonly ITrayHost _host;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _serverTask;

    public NativeControlPipeServer(Dispatcher dispatcher, ITrayHost host)
    {
        _dispatcher = dispatcher;
        _host = host;
        _serverTask = RunAsync();
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_shutdown.Token);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                var command = await reader.ReadLineAsync(_shutdown.Token);
                var response = await DispatchAsync(command);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                App.LogStartupException("NativeControlPipe", ex);
                await Task.Delay(250, _shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<ControlResponse> DispatchAsync(string? command)
    {
        var normalized = command?.Trim().ToLowerInvariant();
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                switch (normalized)
                {
                    case "show":
                        _host.RestoreFromTray();
                        break;
                    case "hide":
                        if (_host is ShellWindow shell)
                        {
                            shell.HideFromLauncher();
                        }
                        break;
                    case "compact":
                        _host.ShowCompactSessionWindow();
                        break;
                    case "exit":
                        _host.ExitFromTray();
                        break;
                }
            });

            return normalized is "show" or "hide" or "compact" or "exit" or "status"
                ? new ControlResponse(true, normalized, "")
                : new ControlResponse(false, "unknown", "未知命令");
        }
        catch (Exception ex)
        {
            App.LogStartupException("NativeControlPipe.Dispatch", ex);
            return new ControlResponse(false, normalized ?? "", ex.Message);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _serverTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _shutdown.Dispose();
    }

    private sealed record ControlResponse(bool Ok, string State, string Error);
}
