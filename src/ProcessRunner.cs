using System.IO.Pipes;
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
    private ISeparateProcess? handler;
    private NamedPipeClientStream? commandPipe;
    private NamedPipeClientStream? responsePipe;
    private readonly object pipeLock = new();
    private BinaryWriter? writer;
    private volatile bool isStopped = false;
    private int _stopId;

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

        await Task.Run(() => ListenForMessages(commandPipe));

        logger.LogDebug("Process initialized");
        SendResponse(_stopId, "success", null);
        logger.LogDebug("Process stopped");
        return 0;
    }

    private async Task ListenForMessages(NamedPipeClientStream pipe)
    {
        using var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
        while (!isStopped)
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
        var (id, method, returnType, args) = MessageProtocol.ReadCall(reader);
        logger.LogDebug($"Received call {method} with id {id}");
        logger.LogDebug($"Handling {method} with id {id}");
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

            // Do not send response for StopAsync here
            // The main process will do it after event loop ends
            if (method == nameof(ISeparateProcess.StopAsync))
            {
                isStopped = true;
                _stopId = id;
            }
            else
            {
                SendResponse(id, "success", result);
            }

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
        lock (pipeLock)
        {
            MessageProtocol.WriteResponse(writer!, id, status, result);
            writer!.Flush();
            responsePipe!.Flush();
        }
    }

    private void SendEvent(string eventName, object? data)
    {
        lock (pipeLock)
        {
            MessageProtocol.WriteEvent(writer!, eventName, data);
            writer!.Flush();
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
        handler = (ISeparateProcess)ActivatorUtilities.CreateInstance(provider, handlerType);
        logger.LogDebug("Handler created");
        // Set up events
        var eventInfos = handlerType.GetEvents(BindingFlags.Public | BindingFlags.Instance);
        foreach (var eventInfo in eventInfos)
        {
            var eventName = eventInfo.Name;
            var eventType = eventInfo.EventHandlerType;
            var argType = eventType!.GetGenericArguments()[0];
            var method = typeof(ProcessRunner).GetMethod(nameof(CreateAction), BindingFlags.NonPublic | BindingFlags.Instance);
            var genericMethod = method!.MakeGenericMethod(argType);
            var action = genericMethod.Invoke(this, [eventName]);
            eventInfo.AddMethod?.Invoke(handler, [action]);
        }
    }

    public void SendLog(LogLevel level, string message)
    {
        lock (pipeLock)
        {
            MessageProtocol.WriteLog(writer!, level, message);
            writer!.Flush();
            responsePipe!.Flush();
        }
    }
}