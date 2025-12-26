using SeparateProcess;

public class TestService : IBackgroundService
{
    public virtual event Action<string>? OnMessage;
    public virtual Task StartAsync() => Task.CompletedTask;
    public virtual Task StopAsync() => Task.CompletedTask;
    public virtual int Add(int a, int b) => a + b;
    public virtual string Echo(string msg)
    {
        Console.WriteLine($"[Handler] Echo called with {msg}, OnMessage is {OnMessage}");
        OnMessage?.Invoke("Echoed: " + msg);
        return "Echoed: " + msg;
    }
    public virtual void ThrowException() => throw new Exception("Test exception");
    public virtual void HardExit() => Environment.Exit(1);
}
