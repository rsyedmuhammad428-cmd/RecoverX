using System;
using RecoverX.Core.Models;

namespace RecoverX.Desktop.Services
{
    /// <summary>Lets any page ask for a scan or for the drive picker without the pages knowing about each other.</summary>
    public interface IScanLauncher
    {
        event Action<DriveModel> ScanRequested;
        event Action PickerRequested;
        void RequestScan(DriveModel drive);
        void RequestPicker();
    }

    public sealed class ScanLauncher : IScanLauncher
    {
        public event Action<DriveModel> ScanRequested;
        public event Action PickerRequested;
        public void RequestScan(DriveModel drive) { var h = ScanRequested; if (h != null) h(drive); }
        public void RequestPicker() { var h = PickerRequested; if (h != null) h(); }
    }
}
