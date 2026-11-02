using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Service;

if (args.Contains("--unregister", StringComparer.OrdinalIgnoreCase))
{
    var syncRoot = Environment.GetEnvironmentVariable("OPENCFDRIVE_ROOT") ?? SyncStatePaths.DefaultSyncRoot;

    var registrar = new SyncRootRegistrar();
    var result = registrar.Unregister(syncRoot);
    Console.WriteLine(result.Message);
    Environment.Exit(result.Success ? 0 : 1);
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<SyncWorker>();

var host = builder.Build();
host.Run();
