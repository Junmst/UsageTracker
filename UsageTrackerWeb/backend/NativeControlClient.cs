using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace UsageTrackerWeb;

internal sealed record NativeControlResponse(
    bool Ok,
    string State,
    string Error,
    AgentStatusDto? Status,
    JsonElement? Data);

internal sealed record AgentStatusDto(
    bool Running,
    bool Tracking,
    bool IsIdle,
    bool IsManualIdle,
    bool IsVideoPlayback,
    string? ActiveProcessName,
    string? ActiveWindowTitle,
    DateTime? ActiveStartTime,
    DateTime? LastCapturedAt);

internal static class NativeControlClient
{
    private static readonly SemaphoreSlim LifecycleGate = new(1, 1);
    private static DateTime _suppressAutoStartUntilUtc = DateTime.MinValue;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private const string PipeName = "Shiji.NativeControl";

    public static Task<NativeControlResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
        => SendDetailedAsync("status", 1200, cancellationToken);

    public static async Task<NativeControlResponse?> EnsureStartedAndGetStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (response?.Ok == true) return response;
        if (DateTime.UtcNow < _suppressAutoStartUntilUtc) return response;
        return await StartAndWaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<NativeControlResponse?> StartAsync(CancellationToken cancellationToken = default)
    {
        _suppressAutoStartUntilUtc = DateTime.MinValue;
        return await StartAndWaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        _suppressAutoStartUntilUtc = DateTime.UtcNow.AddMinutes(10);
        var response = await SendDetailedAsync("exit", 2500, cancellationToken).ConfigureAwait(false);
        return response?.Ok == true;
    }

    private static async Task<NativeControlResponse?> StartAndWaitAsync(CancellationToken cancellationToken)
    {
        await LifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await SendDetailedAsync("status", 700, cancellationToken).ConfigureAwait(false);
            if (response?.Ok == true) return response;
            var executable = FindNativeExecutable();
            if (executable is null) return response;
            try
            {
                Process.Start(new ProcessStartInfo(executable, "--headless")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executable)!
                });
            }
            catch
            {
                return response;
            }

            for (var attempt = 0; attempt < 16; attempt++)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                response = await SendDetailedAsync("status", 700, cancellationToken).ConfigureAwait(false);
                if (response?.Ok == true) return response;
            }
            return response;
        }
        finally
        {
            LifecycleGate.Release();
        }
    }

    public static async Task<bool> EnsureStartedAndSendAsync(string command, Action<int>? onStarted = null, CancellationToken cancellationToken = default)
    {
        if (await SendAsync(command, 700, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var executable = FindNativeExecutable();
        if (executable is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "--headless")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            });
            if (process is not null) onStarted?.Invoke(process.Id);
        }
        catch
        {
            return false;
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (await SendAsync(command, 700, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    public static Task<bool> SendAsync(string command, CancellationToken cancellationToken = default)
        => SendAsync(command, 700, cancellationToken);

    public static async Task<NativeControlResponse?> SendWebCommandAsync(string command, object? args = null, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new { command, args }, JsonOptions);
        return await SendDetailedCoreAsync($"web:{payload}", 2500, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<NativeControlResponse?> SendDetailedAsync(string command, int timeoutMs = 700, CancellationToken cancellationToken = default)
        => await SendDetailedCoreAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);

    private static async Task<bool> SendAsync(string command, int timeoutMs, CancellationToken cancellationToken)
    {
        var response = await SendDetailedCoreAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
        return response?.Ok == true;
    }

    private static async Task<NativeControlResponse?> SendDetailedCoreAsync(string command, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            await writer.WriteLineAsync(command).ConfigureAwait(false);
            var response = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response)) return null;
            return JsonSerializer.Deserialize<NativeControlResponse>(response, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindNativeExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "native", "时迹.exe"),
            Path.Combine(baseDirectory, "时迹.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "UsageTrackerNative_publish", "时迹.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "UsageTrackerNative_publish", "时迹.exe")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageTrackerNative", "时迹.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "时迹", "时迹.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
