using BT.SqlServerToAzureBlobStorageBackupService;

IHostBuilder builder = Host
    .CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<SqlServerBackupService>();
    });

IHost host = builder.Build();

try
{
    host.Run();
}
catch (Exception e)
{
    Console.WriteLine("Fatal error: {0}", e.Message);
}
