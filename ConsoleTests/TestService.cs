using SeparateProcess;
using Microsoft.Extensions.Logging;

public class TestService(ILogger<TestService> logger) : IBackgroundService
{
    private readonly ILogger logger = logger;
    public virtual event Action<string>? OnMessage;
    public virtual Task StartAsync() => Task.CompletedTask;
    public virtual Task StopAsync() => Task.CompletedTask;
    public virtual int Add(int a, int b) => a + b;
    public virtual string Echo(string msg)
    {
        logger.LogDebug("Echo called with {Msg}, OnMessage is {OnMessage}", msg, OnMessage);
        OnMessage?.Invoke("Echoed: " + msg);
        return "Echoed: " + msg;
    }
    public virtual void ThrowException() => throw new Exception("Test exception");
    public virtual void HardExit() => Environment.Exit(1);
}
