using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDExport.Services;

/// <summary>
/// Hands a file being opened to an MDExport window that is already running. Every instance
/// listens on a per-session named pipe, so a launch with a file connects, sends the path and
/// exits. Reaching no listener simply means nothing is running.
/// </summary>
internal static class SingleInstance
{
    // Long enough to reach a listener between two connections; paid in full on a cold start.
    private const int ConnectTimeoutMs = 250;
    private const int StartupGateTimeoutMs = 5000;

    private static readonly int SessionId = Process.GetCurrentProcess().SessionId;
    private static readonly string PipeName = $"MDExport.OpenFile.{SessionId}";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

    /// <summary>
    /// Runs <paramref name="startup"/> exclusively of other starting instances. Without this,
    /// opening several files at once starts processes that all find nothing running yet.
    /// </summary>
    public static void RunExclusive(Action startup)
    {
        using var gate = new Mutex(false, @"Local\MDExport.Startup");
        bool held;
        try
        {
            held = gate.WaitOne(StartupGateTimeoutMs);
        }
        catch (AbandonedMutexException)
        {
            held = true; // The previous holder crashed; ownership passed to us.
        }

        try
        {
            startup();
        }
        finally
        {
            if (held) gate.ReleaseMutex();
        }
    }

    /// <summary>
    /// Sends <paramref name="path"/> to a running instance. Returns false when none took it,
    /// in which case the caller must open the file itself.
    /// </summary>
    public static bool TryForward(string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMs);

            // Only the process the user launched may take the foreground, so pass that on.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid))
                AllowSetForegroundWindow(serverPid);

            var bytes = Encoding.UTF8.GetBytes(path);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts accepting paths; <paramref name="onFile"/> runs on a background thread. The pipe
    /// exists by the time this returns. A failure only costs this instance its forwarded files.
    /// </summary>
    public static void Listen(Action<string> onFile)
    {
        try
        {
            _ = ListenLoopAsync(CreateServer(), onFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Debug.WriteLine("Single-instance listener unavailable: " + ex.Message);
        }
    }

    private static NamedPipeServerStream CreateServer() => new(
        PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task ListenLoopAsync(NamedPipeServerStream server, Action<string> onFile)
    {
        while (true)
        {
            try
            {
                await using (server)
                {
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var path = await reader.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(path)) onFile(path);
                }
            }
            catch (IOException)
            {
                // The sender vanished mid-message; keep listening.
            }

            server = CreateServer();
        }
    }
}
