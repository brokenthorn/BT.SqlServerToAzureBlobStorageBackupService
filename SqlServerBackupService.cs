using Cronos;

namespace BT.SqlServerToAzureBlobStorageBackupService
{
    public class SqlServerBackupService : BackgroundService
    {
        // Information log events:
        private const string NewWorkerCreatedMessage = "New worker created.";
        private readonly EventId NewWorkerCreatedEventId = new(1000, NewWorkerCreatedMessage);

        // Error log events:
        private const string FailedToLoadConfigErrorMessage = $"Failed to load {nameof(SqlServerBackupConfig)} from appsettings";
        private readonly EventId FailedToLoadConfigEventId = new(2000, FailedToLoadConfigErrorMessage);

        private const string EmptyConfigErrorMessage = $"{nameof(SqlServerBackupConfig)} was empty";
        private readonly EventId EmptyConfigEventId = new(2001, EmptyConfigErrorMessage);

        private const string DuplicateSqlServerConfigEntriesFoundMessage = $"Duplicate {nameof(SqlServerBackupConfig)} entries found";
        private readonly EventId DuplicateSqlServerConfigEntriesFoundEventId = new(2002, DuplicateSqlServerConfigEntriesFoundMessage);


        private readonly ILogger<SqlServerBackupService> _logger;
        private readonly IConfiguration _appConfiguration;
        private readonly IHostApplicationLifetime _hostApplication;
        private readonly List<SqlServerBackupConfig> _sqlServerConfigs;
        private readonly List<Thread> _threads = new();

        private readonly CancellationTokenSource _threadsCancellationSource = new();

        public SqlServerBackupService(ILogger<SqlServerBackupService> logger, IConfiguration configuration, IHostApplicationLifetime hostApplicationLifetime)
        {
            _logger = logger;
            _appConfiguration = configuration;
            _hostApplication = hostApplicationLifetime;
            _sqlServerConfigs = new();

            var sqlServersSection = _appConfiguration.GetSection("SqlServers");

            if (sqlServersSection == null)
            {
                _logger.LogError(FailedToLoadConfigEventId, FailedToLoadConfigErrorMessage);
                throw new FailedToLoadConfigException(FailedToLoadConfigErrorMessage);
            }

            sqlServersSection.Bind(_sqlServerConfigs);

            if (_sqlServerConfigs.Count == 0)
            {
                _logger.LogError(EmptyConfigEventId, EmptyConfigErrorMessage);
                throw new EmptyConfigException(EmptyConfigErrorMessage);
            }

            if (ListContainsDuplicateSqlServerConfigs(_sqlServerConfigs))
            {
                _logger.LogError(DuplicateSqlServerConfigEntriesFoundEventId, DuplicateSqlServerConfigEntriesFoundMessage);
                throw new DuplicateSqlServerConfigException(DuplicateSqlServerConfigEntriesFoundMessage);
            }

            var names = string.Join(", ", _sqlServerConfigs.Select(c => $"\"{c.Name}\"").ToList());

            _logger.LogInformation(NewWorkerCreatedEventId,
                                   "{NewWorkerCreatedMessage} {Count} SQL Server backup configuration(s) loaded from appsettings called: {names}.",
                                   NewWorkerCreatedMessage,
                                   _sqlServerConfigs.Count,
                                   names);
        }

        private static bool ListContainsDuplicateSqlServerConfigs(List<SqlServerBackupConfig> configs)
        {
            if (configs.Count > 1)
            {
                foreach (var config in configs)
                {
                    if (configs
                        .Count(c => string.Compare(c.Name, config.Name, StringComparison.InvariantCultureIgnoreCase) == 0)
                        > 1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static ThreadStart CreateBackupJobThreadStartDelegate(SqlServerBackupConfig sqlServerConfig,
                                                                      ILogger logger,
                                                                      CancellationToken cancellationToken)
        {
            return new ThreadStart(() =>
            {
                CronExpression? cronExpression = null;

                try
                {
                    cronExpression = CronExpression.Parse(sqlServerConfig.CronScheduleExpression, Cronos.CronFormat.IncludeSeconds);
                }
                catch (Exception e)
                {
                    logger.LogError("Failed to parse Cron expression for config with Name \"{Name}\": {Message}", sqlServerConfig.Name, e.Message);

                    return;
                }

                Console.WriteLine("Thread {0} ({1}) started.", Environment.CurrentManagedThreadId, Thread.CurrentThread.Name);

                while (!cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Console.WriteLine("Thread {0} ({1}) tick...", Environment.CurrentManagedThreadId, Thread.CurrentThread.Name);

                    var utcNow = DateTime.UtcNow;
                    var nextOccurrenceDate = cronExpression.GetNextOccurrence(utcNow);
                    var timespanTillNextOccurrence = (nextOccurrenceDate - utcNow);

                    if (timespanTillNextOccurrence != null && (nextOccurrenceDate > utcNow))
                    {
                        Console.WriteLine("Thread {0} ({1}) sleeping for {2} seconds",
                                          Environment.CurrentManagedThreadId,
                                          Thread.CurrentThread.Name,
                                          timespanTillNextOccurrence.Value.TotalSeconds);
                        Thread.Sleep(timespanTillNextOccurrence.Value);
                    }
                    else
                    {
                        Console.WriteLine("Error with next occurrence...");
                        break;
                    }

                    /* is more work pending */
                    // dequeue work item
                    // process work item                    
                }

                Console.WriteLine("Thread {0} ({1}) stopped.", Environment.CurrentManagedThreadId, Thread.CurrentThread.Name);
            });
        }

        // Algo:
        // 1. create a thread for each server to be backed up that is defined
        // 2. make the threads aware of their particular schedule
        // 3. when each thread is created, make them check if they should wait for the next run or run now
        // 4. after each run, each thread must wait again until its next run (use the Cronos lib to determine when that is)
        // this way the OS manages the thread resources
        // store the threads in a dictionary, with the server names as keys
        // when the stopping token is triggered, manually stop each thread.
        // in the main thread, sleep for 2 seconds then check the token and repeat


        private void StartThreads()
        {
            foreach (var config in _sqlServerConfigs)
            {
                var threadStartDelegate = CreateBackupJobThreadStartDelegate(config, _logger, _threadsCancellationSource.Token);

                var thread = new Thread(threadStartDelegate)
                {
                    Name = config.Name
                };

                if (!thread.IsAlive) thread.Start();

                _threads.Add(thread);
            }
        }

        private void StopThreads()
        {
            // try and let the threads exist by themselves:
            _threadsCancellationSource.Cancel();
            Thread.Sleep(1000);

            // if some threads are still running, wait for them to finish:
            foreach (var thread in _threads)
            {
                if (thread.IsAlive)
                {
                    _logger.LogWarning("Thread {0} ({1}) is still alive. Waiting for it to finish.", thread.ManagedThreadId, thread.Name);
                    thread.Join();
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            StartThreads();

            // make sure all threads get time to be scheduled before below condition gets checked:
            await Task.Delay(2000);

            if (_threads.Count != 0 &&
                _threads.All(t => t.ThreadState == ThreadState.Running))
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(3000, stoppingToken);
                }

                // stops the entire application when this service is stopped via the cancellation token:
                _hostApplication.StopApplication();
            }
            else
            {
                _logger.LogError("Not all worker threads were started. Stopping application.");
            }

            // stops the entire application when this service finished executing because no worker threads were started:
            _hostApplication.StopApplication();
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SQL Server Backup Service is starting.");

            return base.StartAsync(cancellationToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SQL Server Backup Service is performing a graceful shutdown.");

            StopThreads();

            return base.StopAsync(cancellationToken);
        }
    }
}