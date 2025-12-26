namespace SeparateProcess;

public static class Spawner
{
    public static async Task<TService> Spawn<TService>() where TService : class, IBackgroundService
    {
        var manager = new ProcessManager(typeof(TService));
        var service = ProxyGenerator.CreateProxy<TService>(manager);
        await manager.StartProcess();
        return service;
    }
}