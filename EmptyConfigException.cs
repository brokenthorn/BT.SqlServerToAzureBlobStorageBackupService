namespace BT.SqlServerToAzureBlobStorageBackupService
{
    [Serializable]
    internal class EmptyConfigException : Exception
    {
        public EmptyConfigException(string message) : base(message)
        {
        }
    }
}