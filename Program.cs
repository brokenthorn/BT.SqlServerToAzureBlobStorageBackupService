using BT.SqlServerToAzureBlobStorageBackupService;

IHostBuilder builder = Host
    .CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<SqlServerBackupService>();
    });

IHost host = builder.Build();

host.Run();
