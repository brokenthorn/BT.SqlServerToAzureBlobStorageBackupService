namespace BT.SqlServerToAzureBlobStorageBackupService
{
    [Serializable]
    internal class FailedToLoadConfigException : Exception
    {
        public FailedToLoadConfigException(string message) : base(message)
        {
        }
    }
}