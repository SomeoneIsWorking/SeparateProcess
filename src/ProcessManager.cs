using System.Diagnostics;
using System.IO.Pipes;
using System.Linq.Expressions;
using MessagePack;

namespace SeparateProcess;

public class ProcessManager<THandler> where THandler : IProcessHandler
{
    private Process? process;
    private NamedPipeServerStream? commandPipe;
    private NamedPipeServerStream? responsePipe;
    private readonly string commandPipeName = $"w{Guid.NewGuid().ToString("N")[..8]}c";
    private readonly string responsePipeName = $"w{Guid.NewGuid().ToString("N")[..8]}r";
    private readonly Dictionary<int, TaskCompletionSource<object?>> pendingRequests = [];
    private readonly Dictionary<string, (Delegate handler, Type argType)> eventHandlers = [];
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
        process.StartInfo.Arguments = $"--process {typeof(THandler).FullName} --command-pipe {commandPipeName} --response-pipe {responsePipeName}";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.Start();

        // Start reading stdout/stderr early so logs are visible
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (s, e) => { Console.WriteLine($"[Process stdout] {e.Data}"); };
        process.ErrorDataReceived += (s, e) => { Console.WriteLine($"[Process stderr] {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        exitedHandler = new EventHandler((s, e) =>
        {
            try { Console.WriteLine($"[Process] Process exited with code {process.ExitCode}"); } catch { Console.WriteLine("[Process] Process exited"); }
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

    public async Task Stop()
    {
        if (writer != null)
        {
            await Call(x => x.Stop());
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

    public async Task Call(Expression<Action<THandler>> expr)
    {
        var call = (MethodCallExpression)expr.Body;
        var methodName = call.Method.Name;
        var args = call.Arguments.Select(a => Expression.Lambda(a).Compile().DynamicInvoke()).ToArray();
        await SendCall(methodName, args);
    }

    public async Task Call(Expression<Func<THandler, Task>> expr)
    {
        var call = (MethodCallExpression)expr.Body;
        var methodName = call.Method.Name;
        var args = call.Arguments.Select(a => Expression.Lambda(a).Compile().DynamicInvoke()).ToArray();
        await SendCall(methodName, args);
    }


    public async Task<T> Call<T>(Expression<Func<THandler, T>> expr)
    {
        var call = (MethodCallExpression)expr.Body;
        var methodName = call.Method.Name;
        var args = call.Arguments.Select(a => Expression.Lambda(a).Compile().DynamicInvoke()).ToArray();
        var result = await SendCall(methodName, args);
        return (T)result!;
    }

    public void On<T>(Expression<Func<THandler, Action<T>>> eventExpr, Action<T> handler)
    {
        var member = (MemberExpression)eventExpr.Body;
        var eventName = member.Member.Name;
        eventHandlers[eventName] = (handler, typeof(T));
    }

    private async Task<object?> SendCall(string method, object?[] args)
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
            catch (Exception ex)
            {
                Console.WriteLine($"[Main] Error reading message: {ex}");
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
            if (eventHandlers.TryGetValue(eventName, out var tuple))
            {
                var obj = MessagePackSerializer.Deserialize(tuple.argType, bytes);
                tuple.handler.DynamicInvoke(obj);
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
        if (pendingRequests.TryGetValue(id, out var tcs))
        {
            pendingRequests.Remove(id);
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
}