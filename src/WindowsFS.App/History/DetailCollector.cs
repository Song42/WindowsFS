using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32.SafeHandles;

namespace WindowsFS.App.History;

// The elevated process only observes ETW. The unelevated UI owns the database and the local-only pipe.
internal sealed class DetailClient
{
    private readonly CancellationTokenSource stop = new();
    private Task? running;
    internal string EndStatus { get; private set; } = "프로그램 추적 종료";
    internal Task RunAsync(string[] roots, Action<Observation> receive) => running = Task.Run(() => ConnectAsync(roots, receive));
    internal async Task StopAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        if (running != null) await running.ConfigureAwait(false);
    }
    private async Task ConnectAsync(string[] roots, Action<Observation> receive)
    {
        Guid nonce = Guid.NewGuid();
        using var pipe = new NamedPipeServerStream(DetailCollector.PipeName(nonce), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add("--history-collector"); info.ArgumentList.Add(nonce.ToString("N"));
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            using var child = Process.Start(info);
            if (child == null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!DetailCollector.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != child.Id) return;
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(roots));
            while (!stop.IsCancellationRequested && await reader.ReadLineAsync(stop.Token) is { } line)
            {
                var observation = JsonSerializer.Deserialize<Observation>(line);
                if (observation != null) receive(observation);
            }
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { EndStatus = "프로그램 추적 권한 승인 취소"; }
        catch (Exception e) when (e is Win32Exception or IOException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        { EndStatus = "프로그램 추적 연결 종료"; }
        finally { pipe.Close(); }
    }
}
internal static class DetailCollector
{
    internal static string PipeName(Guid id) => "WindowsFS.History." + id.ToString("N");
    [DllImport("kernel32.dll")] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);

    internal static async Task RunAsync(Guid nonce, int parent)
    {
        // Anonymous SQOS prevents the server from impersonating the elevated client.
        using var pipe = new NamedPipeClientStream(".", PipeName(nonce), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(10000);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != parent) return;
        using var host = Process.GetProcessById(parent);
        if (!string.Equals(host.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = false };
        var roots = JsonSerializer.Deserialize<string[]>(await reader.ReadLineAsync() ?? "[]") ?? [];
        if (roots.Length == 0 || roots.Any(p => !Path.IsPathFullyQualified(p) || p.StartsWith(@"\\", StringComparison.Ordinal))) return;
        await CollectAsync(nonce, roots, reader.ReadLineAsync(), observation => writer.WriteLineAsync(JsonSerializer.Serialize(observation)), writer.FlushAsync);
    }

    internal static async Task CollectAsync(Guid nonce, string[] roots, Task disconnected, Func<Observation, Task> emit, Func<Task> flush)
    {
        using var session = new TraceEventSession("WindowsFS-" + nonce.ToString("N"), null, TraceEventSessionOptions.NoRestartOnCreate);
        session.BufferSizeMB = 16;
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread |
            KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.DiskFileIO);
        var source = session.Source;
        var pending = new Dictionary<(string Path, string Op, int Pid, string? Name), (long Start, long End)>();
        var processNames = new Dictionary<int, (DateTime Observed, string Name)>();
        bool overflow = false;
        void Observe(string path, string op, int processId, DateTime time)
        {
            if (processId <= 0 || !HistoryRecorder.Includes(path, roots)) return;
            long t = new DateTimeOffset(time).ToUnixTimeMilliseconds();
            lock (pending)
            {
                var name = processNames.TryGetValue(processId, out var process) && process.Observed <= time ? process.Name : null;
                var key = (path, op, processId, name);
                if (pending.TryGetValue(key, out var old)) pending[key] = (old.Start, t);
                else if (pending.Count < 4096) pending[key] = (t, t);
                else overflow = true;
            }
        }
        var kernel = source.Kernel;
        void ProcessStart(ProcessTraceData e)
        {
            var name = Path.GetFileName(e.ImageFileName);
            if (name.Length > 0 && processNames.Count < 65536)
                processNames[e.ProcessID] = (e.TimeStamp, name + " (" + e.ProcessID + ")");
        }
        kernel.ProcessStart += ProcessStart;
        kernel.ProcessDCStart += ProcessStart;
        kernel.ProcessStop += e => processNames.Remove(e.ProcessID);
        kernel.FileIOWrite += e => Observe(e.FileName, "쓰기 요청", e.ProcessID, e.TimeStamp);
        kernel.FileIODelete += e => Observe(e.FileName, "삭제 요청", e.ProcessID, e.TimeStamp);
        kernel.FileIOSetInfo += e =>
        {
            if (e.InfoClass is 19 or 20) Observe(e.FileName, "쓰기 요청", e.ProcessID, e.TimeStamp);
            if (e.InfoClass is 13 or 64) Observe(e.FileName, "삭제 요청", e.ProcessID, e.TimeStamp);
        };
        var processing = Task.Run(source.Process);
        int lastLost = 0;
        try
        {
            await emit(new Observation(HistoryStore.Now, "", "상태", "프로그램 추적 중"));
            await flush();
            while (!disconnected.IsCompleted && !processing.IsCompleted)
            {
                await Task.WhenAny(disconnected, processing, Task.Delay(500));
                KeyValuePair<(string Path, string Op, int Pid, string? Name), (long Start, long End)>[] batch;
                bool dropped;
                lock (pending) { batch = pending.ToArray(); pending.Clear(); dropped = overflow; overflow = false; }
                Dictionary<int, (string Name, long Born)> names = [];
                foreach (var item in batch)
                {
                    string? name = item.Key.Name;
                    if (name == null)
                    {
                        if (!names.TryGetValue(item.Key.Pid, out var actor))
                            names[item.Key.Pid] = actor = Name(item.Key.Pid);
                        name = actor.Born <= item.Value.Start ? actor.Name : "PID " + item.Key.Pid;
                    }
                    await emit(new Observation(item.Value.End, item.Key.Path, item.Key.Op, name));
                }
                var lost = session.EventsLost;
                if (dropped || lost != lastLost)
                    await emit(new Observation(HistoryStore.Now, "", "상태", "프로그램 추적 중 · 이벤트 일부 누락"));
                lastLost = lost;
                await flush();
            }
        }
        finally { session.Stop(); await processing; }
    }
    private static (string, long) Name(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return (process.ProcessName + " (" + pid + ")", new DateTimeOffset(process.StartTime).ToUnixTimeMilliseconds());
        }
        catch (Exception e) when (e is ArgumentException or Win32Exception or InvalidOperationException) { return ("PID " + pid, 0); }
    }
}
