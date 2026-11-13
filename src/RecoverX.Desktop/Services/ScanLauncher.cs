using System;
using RecoverX.Core.Models;

namespace RecoverX.Desktop.Services
{
    public sealed class ScanRequest
    {
        public DriveModel Drive { get; set; }
        public ScanMode Mode { get; set; }
    }

    /// <summary>Lets any page ask for a scan or for the drive picker without the pages knowing about each other.</summary>
    public interface IScanLauncher
    {
        event Action<ScanRequest> ScanRequested;
        event Action PickerRequested;
        void RequestScan(DriveModel drive, ScanMode mode);
        void RequestPicker();
    }

    public sealed class ScanLauncher : IScanLauncher
    {
        public event Action<ScanRequest> ScanRequested;
        public event Action PickerRequested;
        public void RequestScan(DriveModel drive, ScanMode mode) { var h = ScanRequested; if (h != null) h(new ScanRequest { Drive = drive, Mode = mode }); }
        public void RequestPicker() { var h = PickerRequested; if (h != null) h(); }
    }
}
