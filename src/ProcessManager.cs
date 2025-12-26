using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using MessagePack;

namespace SeparateProcess;

public class ProcessManager(Type serviceType)
{
    private Process? process;
    private NamedPipeServerStream? commandPipe;
    private NamedPipeServerStream? responsePipe;
    private readonly string commandPipeName = $"w{Guid.NewGuid().ToString("N")[..8]}c";
    private readonly string responsePipeName = $"w{Guid.NewGuid().ToString("N")[..8]}r";
    private readonly ConcurrentDictionary<int, TaskCompletionSource<object?>> pendingRequests = [];
    private readonly Dictionary<string, Delegate> eventHandlers = [];
    private int nextId = 0;

    private readonly Lock writePipeLock = new();

    private BinaryWriter? writer;
    private DataReceivedEventHandler? outputHandler;
    private DataReceivedEventHandler? errorHandler;
    private EventHandler? exitedHandler;


    public async Task StartProcess()
    {
        Console.WriteLine("[Main] Starting process");
        // Start process
        process = new Process();
        process.StartInfo.FileName = Environment.ProcessPath!;
        process.StartInfo.Arguments = $"--process {serviceType.FullName} --command-pipe {commandPipeName} --response-pipe {responsePipeName}";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.Start();

        // Start reading stdout/stderr early so logs are visible
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        exitedHandler = new EventHandler((s, e) =>
        {
            try { Console.WriteLine($"[Process] Process exited with code {process.ExitCode}"); } catch { Console.WriteLine("[Process] Process exited"); }
            // Fail all pending requests
            var items = pendingRequests.ToArray();
            foreach (var kvp in items)
            {
                kvp.Value.SetException(new Exception("Process exited unexpectedly"));
            }
            pendingRequests.Clear();
        });
        process.Exited += exitedHandler;

        // Start command pipe server (main writes, process reads)
        commandPipe = new NamedPipeServerStream(commandPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4 * 1024 * 1024, 4 * 1024 * 1024);
        Console.WriteLine("[Main] Waiting for command pipe connection");
        await commandPipe.WaitForConnectionAsync();

        // Start response pipe server (main reads, process writes)
        responsePipe = new NamedPipeServerStream(responsePipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 4 * 1024 * 1024, 4 * 1024 * 1024);
        Console.WriteLine("[Main] Waiting for response pipe connection");
        await responsePipe.WaitForConnectionAsync();

        // Check if process exited
        if (process.HasExited)
        {
            throw new Exception($"Process exited early with code {process.ExitCode}");
        }

        // Create proxy
        writer = new BinaryWriter(commandPipe, System.Text.Encoding.UTF8, leaveOpen: true);
        Console.WriteLine("[Main] Process started successfully");

        // Start listening for messages
        _ = Task.Run(ListenForMessages);
    }

    public async Task GracefulShutdownAsync()
    {
        Console.WriteLine("[Main] Shutting down process");
        if (writer != null && process != null && !process.HasExited)
        {
            await CallMethodAsync(nameof(IBackgroundService.StopAsync), []);
            writer = null;
        }
        if (commandPipe != null)
        {
            commandPipe.Close();
            commandPipe = null;
        }
        if (responsePipe != null)
        {
            responsePipe.Close();
            responsePipe = null;
        }
        if (process != null)
        {
            if (outputHandler != null) { process.OutputDataReceived -= outputHandler; outputHandler = null; }
            if (errorHandler != null) { process.ErrorDataReceived -= errorHandler; errorHandler = null; }
            if (exitedHandler != null) { process.Exited -= exitedHandler; exitedHandler = null; }
            if (!process.HasExited)
            {
                process.Kill();
            }
            await process.WaitForExitAsync();
            process = null;
        }
    }

    public void RegisterEventHandler(string eventName, Delegate handler)
    {
        eventHandlers[eventName] = (Delegate)Delegate.Combine(eventHandlers.GetValueOrDefault(eventName), handler);
        Console.WriteLine($"[Main] Registered event handler for {eventName}");
    }

    public void RemoveEventHandler(string eventName, Delegate handler)
    {
        if (eventHandlers.TryGetValue(eventName, out var existing))
        {
            Delegate? removed = Delegate.Remove(existing, handler);
            if (removed == null)
                eventHandlers.Remove(eventName);
            else
                eventHandlers[eventName] = removed;
        }
    }

    internal async Task<object?> SendCall(string method, object?[] args)
    {
        int id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<object?>();
        pendingRequests[id] = tcs;
        var argsBytes = MessagePackSerializer.Serialize(args);
        lock (writePipeLock)
        {
            writer!.Write((byte)MessageType.Call);
            writer.Write(id);
            writer.Write(method);
            writer.Write(argsBytes.Length);
            writer.Write(argsBytes);
            writer.Flush();
            commandPipe!.Flush();
        }
        Console.WriteLine($"[Main] Sending {method} with id {id}");
        var result = await tcs.Task;
        Console.WriteLine($"[Main] {method} completed");
        return result;
    }

    public async Task CallMethodAsync(string method, object?[] args)
    {
        await SendCall(method, args);
    }

    public async Task<T> CallMethodGenericAsync<T>(string method, object?[] args)
    {
        var result = await SendCall(method, args);
        return (T)result!;
    }

    public T CallMethodGeneric<T>(string method, object?[] args)
    {
        var result = SendCall(method, args).Result;
        return (T)result!;
    }

    public void CallMethod(string method, object?[] args)
    {
        SendCall(method, args).Wait();
    }

    public void ListenForMessages()
    {
        if (responsePipe == null) return;
        using var reader = new BinaryReader(responsePipe, System.Text.Encoding.UTF8, leaveOpen: true);
        while (true)
        {
            try
            {
                HandleMessage(reader);
            }
            catch
            {
                break;
            }
        }
    }

    private void HandleMessage(BinaryReader reader)
    {
        var type = (MessageType)reader.ReadByte();
        if (type == MessageType.Event)
        {
            var eventName = reader.ReadString();
            var length = reader.ReadInt32();
            var bytes = reader.ReadBytes(length);
            var obj = MessagePackSerializer.Deserialize<object>(bytes);
            Console.WriteLine($"[Main] Received event {eventName}: {obj}");
            if (eventHandlers.TryGetValue(eventName, out var del))
            {
                Console.WriteLine($"[Main] Invoking event {eventName} with {obj}");
                try
                {
                    del?.DynamicInvoke(obj);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Main] Error invoking event: {ex}");
                }
            }
        }
        else if (type == MessageType.Log)
        {
            var level = reader.ReadString();
            var msg = reader.ReadString();
            Console.WriteLine($"[{level}] {msg}");
        }
        else if (type == MessageType.Response)
        {
            var id = reader.ReadInt32();
            var status = reader.ReadString();
            var length = reader.ReadInt32();
            var bytes = reader.ReadBytes(length);
            var result = length > 0 ? MessagePackSerializer.Deserialize<object>(bytes) : null;
            HandleResponse(id, status, result);
        }
    }

    private void HandleResponse(int id, string status, object? result)
    {
        if (pendingRequests.TryRemove(id, out var tcs))
        {
            if (status == "success")
            {
                tcs.SetResult(result);
            }
            else if (status == "error")
            {
                tcs.SetException(new Exception(result?.ToString() ?? "Unknown error"));
            }
        }
    }

    private void OnOutputDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            Console.WriteLine($"[Process stdout] {e.Data}");
        }
    }
    private void OnErrorDataReceived(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            Console.WriteLine($"[Process stderr] {e.Data}");
        }
    }
}
