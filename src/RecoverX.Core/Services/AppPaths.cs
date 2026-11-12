using System;
using System.IO;

namespace RecoverX.Core.Services
{
    public static class AppPaths
    {
        public static string DataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoverX"); }
        }
        public static string LogsDirectory { get { return Path.Combine(DataDirectory, "logs"); } }
        public static string DatabasePath { get { return Path.Combine(DataDirectory, "recoverx.db"); } }
    }
}
