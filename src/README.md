# SeparateProcess

A .NET library for inter-process communication using named pipes and MessagePack serialization.

## Features

- Asynchronous RPC calls between processes
- Event handling from child to parent process
- Logging integration
- Cross-platform support (named pipes work on Unix-like systems)

## Usage

### 1. Define a Handler Interface

```csharp
using SeparateProcess;

public interface IMyHandler : IProcessHandler
{
    Task<string> GetData();
    void SendMessage(string message);
}
```

### 2. Implement the Handler

```csharp
using Microsoft.Extensions.Logging;

public class MyHandler : IMyHandler
{
    private readonly ILogger logger;

    public MyHandler(ILogger logger)
    {
        this.logger = logger;
    }

    public Task Stop() => Task.CompletedTask;

    public Task<string> GetData() => Task.FromResult("Hello from separate process!");

    public void SendMessage(string message)
    {
        logger.LogInformation("Received message: {Message}", message);
    }
}
```

### 3. In the Main Process

```csharp
using SeparateProcess;

var manager = new ProcessManager<MyHandler>();
await manager.StartProcess();

// Call methods
var data = await manager.Call(x => x.GetData());

// Listen for events (if your handler has Action<T> fields)
manager.On(x => x.OnSomeEvent, (data) => Console.WriteLine($"Event: {data}"));

await manager.Stop();
```

### 4. In the Entry Point

```csharp
using SeparateProcess;

if (ProcessRunner.Get(args) is ProcessRunner runner)
{
    await runner.Run();
    return;
}

// Your main application logic here
```

## Dependencies

- MessagePack 3.1.4
- Microsoft.Extensions.Logging 8.0.0

## Building

```bash
dotnet build
```

## License

[Your License Here]