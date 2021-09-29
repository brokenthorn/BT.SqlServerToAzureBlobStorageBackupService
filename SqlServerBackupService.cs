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

            if (ListContainsSimilarlyNamedConfigs(_sqlServerConfigs))
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

        private static bool ListContainsSimilarlyNamedConfigs(List<SqlServerBackupConfig> configs)
        {
            if (configs.Count <= 1) return false;

            for (int i = 0; i < configs.Count - 1; i++)
            {
                for (int j = 1; j < configs.Count; j++)
                {
                    if (string.Compare(configs[i].Name, configs[j].Name, StringComparison.InvariantCultureIgnoreCase) == 0)
                        return true;
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

                logger.LogInformation("Thread {CurrentManagedThreadId} ({Name}) started.", Environment.CurrentManagedThreadId, Thread.CurrentThread.Name);

                while (!cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    logger.LogInformation("Thread {CurrentManagedThreadId} ({Name}) tick...", Environment.CurrentManagedThreadId, Thread.CurrentThread.Name);

                    var utcNow = DateTime.UtcNow;
                    var nextOccurrenceDate = cronExpression.GetNextOccurrence(utcNow);
                    var timespanTillNextOccurrence = (nextOccurrenceDate - utcNow);

                    if (timespanTillNextOccurrence != null && (nextOccurrenceDate > utcNow))
                    {
                        logger.LogInformation("Thread {CurrentManagedThreadId} ({Name}) sleeping for {TotalSeconds} seconds",
                                              Environment.CurrentManagedThreadId,
                                              Thread.CurrentThread.Name,
                                              timespanTillNextOccurrence.Value.TotalSeconds);

                        Thread.Sleep(timespanTillNextOccurrence.Value);
                    }
                    else
                    {
                        logger.LogError("Thread {CurrentManagedThreadId} ({Name}) failed to get next occurrence.",
                                        Environment.CurrentManagedThreadId,
                                        Thread.CurrentThread.Name);

                        break;
                    }

                    /* is more work pending */
                    // dequeue work item
                    // process work item                    
                }

                logger.LogInformation("Thread {CurrentManagedThreadId} ({Name}) stopped.", Environment.CurrentManagedThreadId, Thread.CurrentThread.Name);
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
            _logger.LogInformation("Stopping all worker threads.");

            _threadsCancellationSource.Cancel();

            Thread.Sleep(1000);

            _threads.ForEach(t =>
            {
                if (t.IsAlive)
                {
                    _logger.LogWarning("Thread {ManagedThreadId} ({Name}) is still alive. Waiting for it to finish.", t.ManagedThreadId, t.Name);
                    t.Join();
                }
            });

            _logger.LogInformation("All worker threads stopped.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            StartThreads();

            if (_threads.Count != 0)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (_threads.All(t => t.IsAlive))
                        await Task.Delay(2000, stoppingToken);
                    else
                        break;
                }
            }
            else
            {
                _logger.LogError("No worker threads were started. Check appsettings.json for existing configurations.");
            }

            // stops the entire application when this service finishes executing:
            _hostApplication.StopApplication();
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SQL Server Backup Service is starting.");

            return base.StartAsync(cancellationToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SQL Server Backup Service is shutting down.");

            StopThreads();

            return base.StopAsync(cancellationToken);
        }
    }
}