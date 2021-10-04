using Cronos;

using System.Diagnostics;

namespace BT.SqlServerToAzureBlobStorageBackupService
{
    public class SqlServerBackupService : BackgroundService
    {
        // Error log events:
        private const string FailedToLoadConfigErrorMessage = $"Failed to load {nameof(SqlServerBackupConfig)} from appsettings";
        private readonly EventId FailedToLoadConfigEventId = new(2000, FailedToLoadConfigErrorMessage);

        private const string EmptyConfigErrorMessage = $"{nameof(SqlServerBackupConfig)} was empty";
        private readonly EventId EmptyConfigEventId = new(2001, EmptyConfigErrorMessage);

        private const string DuplicateSqlServerConfigEntriesFoundMessage = $"Duplicate {nameof(SqlServerBackupConfig)} entries found";
        private readonly EventId DuplicateSqlServerConfigEntriesFoundEventId = new(2002, DuplicateSqlServerConfigEntriesFoundMessage);

        private readonly ILogger<SqlServerBackupService> _logger;
        private readonly IConfiguration _appConfiguration;

        private readonly List<SqlServerBackupConfig> _sqlServerConfigs = new();
        private readonly List<Thread> _threads = new();

        public SqlServerBackupService(ILogger<SqlServerBackupService> logger,
                                      IConfiguration configuration)
        {
            _logger = logger;
            _appConfiguration = configuration;

            var sqlServersSection = _appConfiguration.GetSection("SqlServerConfigs");

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

            _logger.LogInformation("{Count} SQL Server backup configuration(s) loaded from appsettings called: {names}.", _sqlServerConfigs.Count, names);
        }

        private static bool ListContainsSimilarlyNamedConfigs(List<SqlServerBackupConfig> configs)
        {
            if (configs.Count <= 1)
                return false;

            for (int i = 0; i < configs.Count - 1; i++)
            {
                for (int j = 1; j < configs.Count; j++)
                {
                    if (string.Compare(configs[i].Name,
                                       configs[j].Name,
                                       StringComparison.InvariantCultureIgnoreCase) == 0)
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
                    cronExpression = CronExpression.Parse(sqlServerConfig.CronScheduleExpression, CronFormat.IncludeSeconds);
                }
                catch (Exception e)
                {
                    logger.LogError("Failed to parse Cron expression for config with Name \"{Name}\": {Message}",
                                    sqlServerConfig.Name,
                                    e.Message);

                    return;
                }

                // TODO: for each database in sqlServerConfig, create script
                var sqlScripts = new List<string>() {
                    $"BACKUP DATABASE {1} TO URL = N'{sqlServerConfig.Url}' WITH FORMAT, {(sqlServerConfig.UseCompression ? "COMPRESSION, " : null)}{(sqlServerConfig.CopyOnly ? "COPY_ONLY, " : null)}BLOCKSIZE={sqlServerConfig.BlockSize}, MAXTRANSFERSIZE={sqlServerConfig.MaxTransferSize};"
                };

                logger.LogInformation("Thread {CurrentManagedThreadId} ({Name}) started.",
                                      Environment.CurrentManagedThreadId,
                                      Thread.CurrentThread.Name);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var utcNow = DateTime.UtcNow;
                    var nextOccurrenceDate = cronExpression.GetNextOccurrence(utcNow);
                    var timespanTillNextOccurrence = nextOccurrenceDate - utcNow;

                    if (timespanTillNextOccurrence is not null && (nextOccurrenceDate > utcNow))
                    {
                        var secondsToNextOccurrence = timespanTillNextOccurrence.Value.TotalSeconds;
                        var sleepInterval = TimeSpan.FromSeconds(1);
                        var timeSpanCounter = new TimeSpan(0);
                        var shouldStop = false;

                        // sleep till next occurrence, but wake up at predefined intervals in order to check for cancellation:
                        while (timeSpanCounter.TotalSeconds < secondsToNextOccurrence)
                        {
                            logger.LogInformation("Thread {CurrentManagedThreadId} ({Name}) sleeping for {TotalSeconds} second(s)",
                                              Environment.CurrentManagedThreadId,
                                              Thread.CurrentThread.Name,
                                              sleepInterval.TotalSeconds);

                            Thread.Sleep(sleepInterval);

                            timeSpanCounter += TimeSpan.FromSeconds(1);

                            if (cancellationToken.IsCancellationRequested)
                            {
                                shouldStop = true;
                                break;
                            }
                                
                        }

                        if (shouldStop)
                            break;

                        // TODO: now start doing the actual work...
                        foreach (var script in sqlScripts)
                        {
                            // TODO: research if you can send more than one command to execute in parallel through one SqlConnection
                            logger.LogInformation(script);
                        }
                    }
                    else
                    {
                        logger.LogError("Thread {CurrentManagedThreadId} ({Name}) failed to get its next occurrence.",
                                        Environment.CurrentManagedThreadId,
                                        Thread.CurrentThread.Name);

                        break; // we could sleep 1s here instead of breaking, and let the code try getting next occurrence again...
                    }
                }

                logger.LogInformation("Thread {CurrentManagedThreadId} ({Name}) stopped.",
                                      Environment.CurrentManagedThreadId,
                                      Thread.CurrentThread.Name);
            });
        }

        private void CreateThreads(CancellationToken cancellationToken, bool start = false)
        {
            _logger.LogInformation("Creating {AndStarting}all worker threads.", start ? "and starting " : null);

            foreach (var sqlServerConfig in _sqlServerConfigs)
            {
                var threadStartDelegate = CreateBackupJobThreadStartDelegate(sqlServerConfig,
                                                                             _logger,
                                                                             cancellationToken);

                var thread = new Thread(threadStartDelegate) { Name = sqlServerConfig.Name };

                _threads.Add(thread);

                if (start) thread.Start();
            }

            _logger.LogInformation("{Count} worker threads created{AndStarted}.", _threads.Count, start ? " and started" : null);
        }

        /// <summary>
        /// Waits for all worker threads to stop up to the specified number of seconds.
        /// If not all threads stopped by that time, logs a warning and returns.
        /// </summary>
        /// <param name="seconds"></param>
        /// <returns></returns>
        private async Task WaitForThreadsToStop(double seconds = 60)
        {
            _logger.LogInformation("Waiting up to {seconds} seconds for all threads to stop.", seconds);

            var sw = new Stopwatch();
            sw.Start();

            while (_threads.Any(t => t.IsAlive))
            {
                await Task.Delay(1000);

                if (sw.Elapsed.TotalSeconds >= seconds)
                {
                    _logger.LogWarning("More than {TotalSeconds} have passed and not all threads stopped running. Not waiting any more.", seconds);

                    sw.Stop();

                    break;
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            CreateThreads(stoppingToken, true);

            if (_threads.Count != 0)
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // keep the process running while all threads are alive:
                    if (_threads.All(t => t.IsAlive))
                        await Task.Delay(2000, stoppingToken);
                    else
                        break;
                }

                await WaitForThreadsToStop();
            }
            else
            {
                _logger.LogError("No worker threads were started. Check appsettings.json for existing configurations.");
            }
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SQL Server Backup Service is starting up.");

            return base.StartAsync(cancellationToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("SQL Server Backup Service is shutting down.");

            return base.StopAsync(cancellationToken);
        }
    }
}