using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    /// <summary>
    /// Turns raw hardware facts into resource keys for the UI. Pure logic, no Windows calls.
    /// Known limits: an external eSATA drive reports bus SATA and is shown as "Internal"; USB bridges that hide the
    /// drive's real type may report Unknown media; Storage Spaces / RAID volumes can hide the physical disks.
    /// </summary>
    public static class DriveClassifier
    {
        public static string MediaKey(MediaType m)
        {
            switch (m)
            {
                case MediaType.Ssd: return "Media_SSD";
                case MediaType.Hdd: return "Media_HDD";
                default: return "Media_Unknown";
            }
        }

        public static string BusKey(BusType b)
        {
            switch (b)
            {
                case BusType.Usb: return "Bus_USB";
                case BusType.Sata: return "Bus_SATA";
                case BusType.Ata: case BusType.Atapi: return "Bus_ATA";
                case BusType.Nvme: return "Bus_NVMe";
                case BusType.Sd: return "Bus_SD";
                case BusType.Mmc: return "Bus_MMC";
                case BusType.Scsi: return "Bus_SCSI";
                case BusType.Sas: return "Bus_SAS";
                case BusType.Raid: return "Bus_RAID";
                case BusType.Virtual: case BusType.FileBackedVirtual: return "Bus_Virtual";
                case BusType.Spaces: return "Bus_Spaces";
                case BusType.Ieee1394: return "Bus_1394";
                case BusType.Unknown: return "Bus_Unknown";
                default: return "Bus_Other";
            }
        }

        public static string LocationKey(DriveModel d)
        {
            switch (d.Bus)
            {
                case BusType.Usb: return "Loc_External";
                case BusType.Sd: case BusType.Mmc: return "Loc_Card";
                case BusType.Virtual: case BusType.FileBackedVirtual: case BusType.Spaces: return "Loc_Virtual";
                case BusType.Unknown:
                    return d.Kind == DriveKind.Removable ? "Loc_Removable" : "Loc_Unknown";
                default:
                    return d.RemovableMedia == true ? "Loc_Removable" : "Loc_Internal";
            }
        }

        /// <summary>Resource key of a recovery-chance warning, or null when there is nothing to warn about.</summary>
        public static string WarningKey(DriveModel d)
        {
            if (d.Media != MediaType.Ssd) return null;
            return d.TrimEnabled == true ? "Warn_Trim" : "Warn_SsdNoTrim";
        }

        public static string SourceKey(DetectionSource s)
        {
            switch (s)
            {
                case DetectionSource.Native: return "Source_Native";
                case DetectionSource.NativePartial: return "Source_NativePartial";
                default: return "Source_Basic";
            }
        }

        public static void Apply(DriveModel d, VolumeHardwareInfo hw)
        {
            d.Bus = hw.Bus;
            d.RemovableMedia = hw.RemovableMedia;
            d.TrimEnabled = hw.TrimEnabled;
            d.Vendor = hw.Vendor;
            d.Product = hw.Product;
            d.Media = hw.MediaQueried ? hw.Media : MediaType.Unknown;
            d.Source = hw.DeviceQueried
                ? (hw.MediaQueried ? DetectionSource.Native : DetectionSource.NativePartial)
                : DetectionSource.Basic;
        }
    }
}
