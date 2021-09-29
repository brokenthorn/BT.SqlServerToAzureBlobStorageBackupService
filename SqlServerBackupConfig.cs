using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BT.SqlServerToAzureBlobStorageBackupService
{
    internal record SqlServerBackupConfig
    {
        public string Name { get; set; } = "";
        public int RetentionDays { get; set; } = 30;
        public string CronScheduleExpression { get; set; } = "";
        public string Url { get; set; } = "";
        public bool UseCompression { get; set; } = false;
        public bool CopyOnly { get; set; } = true;
        public int BlockSize { get; set; } = 65536;
        public int MaxTransferSize { get; set; } = 4194304;
        public NotificationSettings? NotificationSettings { get; set; }
        public Credential? Credential { get; set; }
    }
}
