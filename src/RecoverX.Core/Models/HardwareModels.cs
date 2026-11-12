namespace RecoverX.Core.Models
{
    /// <summary>Values mirror the Windows STORAGE_BUS_TYPE enumeration (and RX_BUS_* in rx_engine.h).</summary>
    public enum BusType
    {
        Unknown = 0, Scsi = 1, Atapi = 2, Ata = 3, Ieee1394 = 4, Ssa = 5, Fibre = 6, Usb = 7, Raid = 8, Iscsi = 9,
        Sas = 10, Sata = 11, Sd = 12, Mmc = 13, Virtual = 14, FileBackedVirtual = 15, Spaces = 16, Nvme = 17
    }

    public enum MediaType { Unknown = 0, Hdd = 1, Ssd = 2 }

    public enum DetectionSource { Basic, NativePartial, Native }

    public enum EngineStatus
    {
        Unavailable = -1, Ok = 0, InvalidArgument = 1, BufferTooSmall = 2, AccessDenied = 3, NotFound = 4,
        IoError = 5, Unsupported = 6, Cancelled = 7, NotNtfs = 8, Corrupt = 9, Internal = 99
    }

    public enum FileSystemId { Unknown = 0, Ntfs = 1, Fat32 = 2, ExFat = 3, Fat16 = 4, Fat12 = 5 }

    public enum DestinationVerdict { Safe = 0, SameVolume = 1, NotFound = 2, Unverifiable = 3 }

    /// <summary>Hardware facts for one lettered volume, as returned by the native engine.</summary>
    public sealed class VolumeHardwareInfo
    {
        public string Letter { get; set; }            // "C:"
        public BusType Bus { get; set; }
        public MediaType Media { get; set; }          // meaningful only when MediaQueried
        public bool? RemovableMedia { get; set; }
        public bool? TrimEnabled { get; set; }
        public string Vendor { get; set; }
        public string Product { get; set; }
        public bool DeviceQueried { get; set; }
        public bool MediaQueried { get; set; }
        public uint Win32Error { get; set; }
    }

    public sealed class VolumeProbeResult
    {
        public EngineStatus Status { get; set; }
        public FileSystemId FileSystem { get; set; }
        public uint SectorSize { get; set; }
        public ulong TotalBytes { get; set; }
        public uint Win32Error { get; set; }
    }
}
