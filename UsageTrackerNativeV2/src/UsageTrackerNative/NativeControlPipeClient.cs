using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace UsageTrackerNative;

internal static class NativeControlPipeClient
{
    private const string PipeName = "Shiji.NativeControl";

    public static async Task<bool> TrySendAsync(string command)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(700);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            await writer.WriteLineAsync(command);
            var response = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(response)) return false;
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                || document.RootElement.TryGetProperty("Ok", out ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
