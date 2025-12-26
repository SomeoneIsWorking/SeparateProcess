namespace SeparateProcess;

public interface IBackgroundService
{
    Task StartAsync();
    Task StopAsync();
}