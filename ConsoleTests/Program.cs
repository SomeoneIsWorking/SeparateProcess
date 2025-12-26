using SeparateProcess;

if (args.Length > 0 && args[0] == "--process")
{
    // Runner mode
    var runner = ProcessRunner.Get(args);
    if (runner != null)
    {
        var exitCode = await runner.Run();
        Environment.Exit(exitCode);
    }
    else
    {
        Console.WriteLine("Invalid runner args");
        Environment.Exit(1);
    }
}
else
{
    // Manager mode
    await RunTests();
}


async Task RunTests()
{
    Console.WriteLine("Starting tests...");

    var service = await Spawner.Spawn<TestService>();

    // Test simple call
    var result = service.Add(5, 3);
    Console.WriteLine($"Add result: {result}");
    if (result != 8)
    {
        Console.WriteLine("Test failed: Add");
        return;
    }

    // Test async call
    var echoResult = service.Echo("Hello");
    Console.WriteLine($"Echo result: {echoResult}");
    if (echoResult != "Echoed: Hello")
    {
        Console.WriteLine("Test failed: Echo");
        return;
    }

    // Test event
    string? receivedMessage = null;
    service.OnMessage += (msg) =>
    {
        Console.WriteLine($"[Test] Event received: {msg}");
        receivedMessage = msg;
    };

    var echoResult2 = service.Echo("World");
    await Task.Delay(100); // Wait for event
    Console.WriteLine($"Received message: {receivedMessage}");
    if (receivedMessage != "Echoed: World")
    {
        Console.WriteLine("Test failed: Event");
        return;
    }

    // Test exception
    try
    {
        service.ThrowException();
        Console.WriteLine("Test failed: Exception not thrown");
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Exception caught: {ex.Message}");
        if (!ex.Message.Contains("Test exception"))
        {
            Console.WriteLine("Test failed: Wrong exception");
            return;
        }
    }

    // Test hard exit
    try
    {
        service.HardExit();
        Console.WriteLine("Test failed: HardExit should have thrown");
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"HardExit exception caught: {ex.Message}");
        if (!ex.Message.Contains("Process exited unexpectedly"))
        {
            Console.WriteLine("Test failed: Wrong hard exit exception");
            return;
        }
    }

    service = await Spawner.Spawn<TestService>();
    await service.StopAsync();

    Console.WriteLine("All tests passed!");
}
