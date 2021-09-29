namespace BT.SqlServerToAzureBlobStorageBackupService
{
    [Serializable]
    internal class DuplicateSqlServerConfigException : Exception
    {
        public DuplicateSqlServerConfigException(string message) : base(message)
        {
        }
    }
}