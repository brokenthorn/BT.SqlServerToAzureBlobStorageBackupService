namespace BT.SqlServerToAzureBlobStorageBackupService
{
    internal record NotificationSettings
    {
        public string? Email { get; set; }
        public bool OnStart { get; set; } = false;
        public bool OnComplete { get; set; } = false;
        public bool OnError { get; set; } = true;
    }
}
