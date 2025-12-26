using System.IO.Pipes;
using MessagePack;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace SeparateProcess;

public class ProcessRunner
{
    public static ProcessRunner? Get(string[] args)
    {
        if (args.Length > 1 && args[0] == "--process")
        {
            return new ProcessRunner(args);
        }
        return null;
    }

    private readonly string commandPipeName;
    private readonly string responsePipeName;
    private readonly string handlerTypeName;
    private IProcessHandler? handler;
    private NamedPipeClientStream? commandPipe;
    private NamedPipeClientStream? responsePipe;
    private readonly object pipeLock = new();
    private BinaryWriter? writer;

    private ProcessRunner(string[] args)
    {
        // Parse args: --process <type> --command-pipe <name> --response-pipe <name>
        handlerTypeName = "";
        commandPipeName = "";
        responsePipeName = "";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--process" && i + 1 < args.Length)
            {
                handlerTypeName = args[i + 1];
                i++;
            }
            else if (args[i] == "--command-pipe" && i + 1 < args.Length)
            {
                commandPipeName = args[i + 1];
                i++;
            }
            else if (args[i] == "--response-pipe" && i + 1 < args.Length)
            {
                responsePipeName = args[i + 1];
                i++;
            }
        }
    }

    public async Task<int> Run()
    {
        try
        {
            Log("Starting process");
            commandPipe = new NamedPipeClientStream(".", commandPipeName, PipeDirection.In);
            Log("Connecting to command pipe");
            commandPipe.Connect();

            responsePipe = new NamedPipeClientStream(".", responsePipeName, PipeDirection.Out);
            Log("Connecting to response pipe");
            responsePipe.Connect();

            writer = new BinaryWriter(responsePipe, System.Text.Encoding.UTF8, leaveOpen: true);

            InitializeHandler();

            // Start listening for messages
            var listenTask = Task.Run(() => ListenForMessages(commandPipe));

            Log("Process initialized");

            // Wait for stop
            await listenTask;

            Log("Process stopped");
            return 0;
        }
        catch (Exception ex)
        {
            LogError("Process error", ex);
            return 1;
        }
    }

    private async Task ListenForMessages(NamedPipeClientStream pipe)
    {
        using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        while (true)
        {
            try
            {
                await ProcessMessage(reader);
            }
            catch (Exception ex)
            {
                LogError("Error reading message", ex);
                break;
            }
        }
    }

    private async Task ProcessMessage(BinaryReader reader)
    {
        var type = (MessageType)reader.ReadByte();
        if (type == MessageType.Call)
        {
            await HandleCall(reader);
        }
    }

    private async Task HandleCall(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var method = reader.ReadString();
        Console.WriteLine($"[Process] Received call {method} with id {id}");
        Log($"Handling {method} with id {id}");
        var length = reader.ReadInt32();
        var bytes = reader.ReadBytes(length);
        var args = MessagePackSerializer.Deserialize<object[]>(bytes);
        try
        {
            var methodInfo = handler!.GetType().GetMethod(method);
            if (methodInfo == null)
            {
                throw new Exception($"Method {method} not found");
            }
            var invokeResult = methodInfo.Invoke(handler, args);
            object? result = null;
            if (invokeResult is Task task)
            {
                await task;
                if (methodInfo.ReturnType.IsGenericType && methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var resultProperty = methodInfo.ReturnType.GetProperty("Result");
                    result = resultProperty?.GetValue(task);
                }
            }
            else
            {
                result = invokeResult;
            }
            Console.WriteLine($"[Process] {method} with id {id} completed: {result}");
            SendResponse(id, "success", result);
            Console.WriteLine($"[Process] Sent response for {method} with id {id}");
        }
        catch (Exception ex)
        {
            LogError($"Error handling message {method}", ex);
        }
    }

    private void SendResponse(int id, string status, object? result)
    {
        var resultBytes = result != null ? MessagePackSerializer.Serialize(result) : Array.Empty<byte>();
        lock (pipeLock)
        {
            writer!.Write((byte)MessageType.Response);
            writer.Write(id);
            writer.Write(status);
            writer.Write(resultBytes.Length);
            if (resultBytes.Length > 0)
            {
                writer.Write(resultBytes);
            }
            writer.Flush();
            responsePipe!.Flush();
        }
    }

    private void SendEvent(string eventName, object? data)
    {
        var dataBytes = MessagePackSerializer.Serialize(data);
        lock (pipeLock)
        {
            writer!.Write((byte)MessageType.Event);
            writer.Write(eventName);
            writer.Write(dataBytes.Length);
            writer.Write(dataBytes);
            writer.Flush();
            responsePipe!.Flush();
        }
    }

    private void SendLog(LogLevel level, string message)
    {
        lock (pipeLock)
        {
            writer!.Write((byte)MessageType.Log);
            writer.Write(level.ToString());
            writer.Write(message);
            writer.Flush();
            responsePipe!.Flush();
        }
    }

    private void Log(string message)
    {
        Console.WriteLine($"[Process] {message}");
    }

    private void LogError(string message, Exception ex)
    {
        Console.Error.WriteLine($"[Process Error] {message}: {ex}");
    }

    private Action<T> CreateAction<T>(string eventName)
    {
        return data => SendEvent(eventName, data);
    }

    private void InitializeHandler()
    {
        // Create handler using reflection
        Type? handlerType = Assembly.GetExecutingAssembly().GetType(handlerTypeName);
        Console.WriteLine($"Handler type name: {handlerTypeName}, type: {handlerType}");
        if (handlerType != null)
        {
            var processLogger = new ProcessLogger((level, msg) => SendLog(level, msg));
            handler = (IProcessHandler)Activator.CreateInstance(handlerType, processLogger)!;
            Console.WriteLine("Handler created");
            if (handler != null)
            {
                // Set up action fields
                var actionFields = handlerType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(Action<>));
                foreach (var field in actionFields)
                {
                    var actionType = field.FieldType;
                    var argType = actionType.GetGenericArguments()[0];
                    var method = typeof(ProcessRunner).GetMethod(nameof(CreateAction), BindingFlags.NonPublic | BindingFlags.Instance);
                    var genericMethod = method!.MakeGenericMethod(argType);
                    var action = genericMethod.Invoke(this, [field.Name]);
                    field.SetValue(handler, action);
                }
            }
        }
        else
        {
            Console.WriteLine("Handler type not found");
        }
    }
}