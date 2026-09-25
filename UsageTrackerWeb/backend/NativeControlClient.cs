using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace UsageTrackerWeb;

internal static class NativeControlClient
{
    private const string PipeName = "Shiji.NativeControl";

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
            using var process = Process.Start(new ProcessStartInfo(executable, "--startup-compact")
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

    private static async Task<bool> SendAsync(string command, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            await writer.WriteLineAsync(command).ConfigureAwait(false);
            var response = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response)) return false;
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("ok", out var ok) || document.RootElement.TryGetProperty("Ok", out ok))
            {
                return ok.GetBoolean();
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindNativeExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "native", "时迹.exe"),
            Path.Combine(baseDirectory, "时迹.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "UsageTrackerNativeV2_publish", "时迹.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "UsageTrackerNativeV2_publish", "时迹.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "UsageTrackerNativeV2", "src", "UsageTrackerNative", "bin", "Release", "net8.0-windows", "win-x64", "时迹.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "UsageTrackerNativeV2", "src", "UsageTrackerNative", "bin", "Release", "net8.0-windows", "win-x64", "时迹.exe")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageTrackerNative", "时迹.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "时迹", "时迹.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
