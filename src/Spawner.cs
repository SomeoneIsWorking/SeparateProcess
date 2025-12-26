using Microsoft.Extensions.Logging;

namespace SeparateProcess;

public static class Spawner
{
    public static ProcessRunner? GetRunner(string[] args)
    {
        if (args.Length > 1 && args[0] == "--process")
        {
            return new ProcessRunner(args);
        }
        return null;
    }

    public static async Task<TService> Spawn<TService>(ILogger logger) where TService : class, IBackgroundService
    {
        var manager = new ProcessManager(typeof(TService), logger);
        var service = ProxyGenerator<TService>.CreateProxy(manager);
        await manager.StartProcess();
        return service;
    }
}