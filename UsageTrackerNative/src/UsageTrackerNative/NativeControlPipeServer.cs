using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace UsageTrackerNative;

internal sealed class NativeControlPipeServer : IDisposable
{
    public const string PipeName = "Shiji.NativeControl";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly ITrayHost _host;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _serverTask;

    public NativeControlPipeServer(ITrayHost host)
    {
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
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                NativeLogger.LogStartupException("NativeControlPipe", ex);
                await Task.Delay(250, _shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<ControlResponse> DispatchAsync(string? command)
    {
        var rawCommand = command?.Trim() ?? string.Empty;
        var normalized = rawCommand.ToLowerInvariant();
        try
        {
            AgentStatusSnapshot? status = null;
            object? data = null;
            var ok = true;
            string error = string.Empty;
            await _commandGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                if (rawCommand.StartsWith("web:", StringComparison.OrdinalIgnoreCase))
                {
                    var result = _host.ExecuteWebCommand(rawCommand[4..]);
                    ok = result.Ok;
                    error = result.Error;
                    data = result.Data;
                }
                else
                {
                    switch (normalized)
                    {
                        case "show":
                        case "compact":
                            ok = false;
                            error = "Web-only 模式不提供 WPF 窗口";
                            break;
                        case "hide": _host.HideCompactSessionWindow(); break;
                        case "exit": _host.ExitFromTray(); break;
                        case "status": status = _host.GetAgentStatus(); break;
                        case "idle": ok = _host.EnterManualIdleFromWeb(); break;
                        default: ok = false; break;
                    }
                }
            }
            finally
            {
                _commandGate.Release();
            }

            return new ControlResponse(
                ok,
                rawCommand,
                ok ? string.Empty : (string.IsNullOrWhiteSpace(error) ? "未知命令" : error),
                status,
                data);
        }
        catch (Exception ex)
        {
            NativeLogger.LogStartupException("NativeControlPipe.Dispatch", ex);
            return new ControlResponse(false, rawCommand, ex.Message, null, null);
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
        _commandGate.Dispose();
    }

    private sealed record ControlResponse(bool Ok, string State, string Error, AgentStatusSnapshot? Status, object? Data);
}
