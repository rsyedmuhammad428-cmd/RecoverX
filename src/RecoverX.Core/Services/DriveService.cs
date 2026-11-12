using System;
using System.Collections.Generic;
using System.IO;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    /// <summary>
    /// Phase 1 drive enumeration using System.IO.DriveInfo (read-only, no raw disk handles).
    /// Limitation: DriveInfo cannot tell HDD from SSD or USB from internal; Phase 2's native engine will refine this.
    /// </summary>
    public sealed class DriveService : IDriveService
    {
        private readonly IAppLogger _log;

        public DriveService(IAppLogger log) { _log = log; }

        public IReadOnlyList<DriveModel> GetDrives()
        {
            var result = new List<DriveModel>();
            string systemRoot = SafeSystemRoot();

            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                DriveKind kind;
                switch (d.DriveType)
                {
                    case DriveType.Fixed: kind = DriveKind.Fixed; break;
                    case DriveType.Removable: kind = DriveKind.Removable; break;
                    default: continue;   // skip optical, network, RAM disks
                }
                result.Add(Describe(d, kind, systemRoot));
            }
            return result;
        }

        private DriveModel Describe(DriveInfo d, DriveKind kind, string systemRoot)
        {
            var m = new DriveModel
            {
                RootPath = d.Name,
                Letter = d.Name.TrimEnd('\\'),
                Kind = kind,
                IsSystemDrive = string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase)
            };
            try
            {
                if (d.IsReady)
                {
                    m.IsReady = true;
                    m.VolumeName = d.VolumeLabel;
                    m.FileSystem = d.DriveFormat;
                    m.TotalBytes = d.TotalSize;
                    m.FreeBytes = d.AvailableFreeSpace;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                m.IsReady = false;
                _log.Warn("Drive " + d.Name + " could not be read: " + ex.GetType().Name);
            }
            return m;
        }

        private static string SafeSystemRoot()
        {
            try { return Path.GetPathRoot(Environment.SystemDirectory); }
            catch (Exception) { return string.Empty; }
        }
    }
}
