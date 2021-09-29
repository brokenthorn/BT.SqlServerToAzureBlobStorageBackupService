namespace BT.SqlServerToAzureBlobStorageBackupService
{
    internal record Credential
    {
        public string Name { get; set; } = "";
        public string Identity { get; set; } = "";
        public string Secret { get; set; } = "";
    }
}
