using System.IO.Pipes;
using MessagePack;
using Microsoft.Extensions.Logging;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SeparateProcess;

public class ProcessRunner
{
    private readonly string commandPipeName;
    private readonly string responsePipeName;
    private readonly ServiceProvider provider;
    private readonly ILogger logger;
    private readonly string handlerTypeName;
    private IBackgroundService? handler;
    private NamedPipeClientStream? commandPipe;
    private NamedPipeClientStream? responsePipe;
    private readonly object pipeLock = new();
    private BinaryWriter? writer;

    public ProcessRunner(string[] args)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new ProcessLoggerProvider(SendLog)));
        provider = services.BuildServiceProvider();
        logger = provider.GetRequiredService<ILogger<ProcessRunner>>();

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
        commandPipe = new NamedPipeClientStream(".", commandPipeName, PipeDirection.In);
        commandPipe.Connect();

        responsePipe = new NamedPipeClientStream(".", responsePipeName, PipeDirection.Out);
        responsePipe.Connect();

        writer = new BinaryWriter(responsePipe, System.Text.Encoding.UTF8, leaveOpen: true);

        InitializeHandler();

        // Start listening for messages
        var listenTask = Task.Run(() => ListenForMessages(commandPipe));

        logger.LogInformation("Process initialized");

        // Wait for stop
        await listenTask;

        logger.LogInformation("Process stopped");
        return 0;
    }

    private async Task ListenForMessages(NamedPipeClientStream pipe)
    {
        using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        while (true)
        {
            await ProcessMessage(reader);
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
        logger.LogDebug($"Received call {method} with id {id}");
        logger.LogDebug($"Handling {method} with id {id}");
        var length = reader.ReadInt32();
        var bytes = reader.ReadBytes(length);
        var args = MessagePackSerializer.Deserialize<object[]>(bytes);
        try
        {
            var methodInfo = handler!.GetType().GetMethod(method)
                ?? throw new Exception($"Method {method} not found");
            var invokeResult = methodInfo.Invoke(handler, args);
            object? result = null;
            if (invokeResult is Task task)
            {
                await task;
                if (methodInfo.ReturnType.IsGenericType && methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var resultProperty = methodInfo.ReturnType.GetProperty(nameof(Task<object>.Result));
                    result = resultProperty?.GetValue(task);
                }
            }
            else
            {
                result = invokeResult;
            }
            logger.LogDebug($"{method} with id {id} completed: {result}");
            SendResponse(id, "success", result);
            logger.LogDebug($"Sent response for {method} with id {id}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error handling message {method}");
            var errorMessage = ex is TargetInvocationException tie ? tie.InnerException?.Message ?? ex.Message : ex.Message;
            SendResponse(id, "error", errorMessage);
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
        logger.LogDebug($"Sending event {eventName}: {data}");
    }

    private Action<T> CreateAction<T>(string eventName)
    {
        return data =>
        {
            logger.LogDebug($"Action {eventName} called with {data}");
            SendEvent(eventName, data);
        };
    }

    private void InitializeHandler()
    {
        // Create handler using reflection
        Type? handlerType = Assembly.GetEntryAssembly()!.GetType(handlerTypeName);
        logger.LogDebug($"Handler type name: {handlerTypeName}, type: {handlerType}");
        if (handlerType == null)
        {
            throw new Exception($"Handler type {handlerTypeName} not found");
        }
        handler = (IBackgroundService)ActivatorUtilities.CreateInstance(provider, handlerType);
        logger.LogDebug("Handler created");
        // Set up events
        var eventInfos = handlerType.GetEvents(BindingFlags.Public | BindingFlags.Instance);
        foreach (var eventInfo in eventInfos)
        {
            var eventName = eventInfo.Name;
            var eventType = eventInfo.EventHandlerType;
            if (eventType is { IsGenericType: true } && eventType.GetGenericTypeDefinition() == typeof(Action<>))
            {
                var argType = eventType.GetGenericArguments()[0];
                var method = typeof(ProcessRunner).GetMethod(nameof(CreateAction), BindingFlags.NonPublic | BindingFlags.Instance);
                var genericMethod = method!.MakeGenericMethod(argType);
                var action = genericMethod.Invoke(this, [eventName]);
                var addMethod = eventInfo.GetAddMethod();
                var addMethodGeneric = addMethod!.IsGenericMethod ? addMethod.MakeGenericMethod(argType) : addMethod;
                addMethodGeneric.Invoke(handler, [action]);
            }
        }
        // Start the handler
        _ = Task.Run(async () => await handler.StartAsync());
    }

    public void SendLog(LogLevel level, string message)
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
}