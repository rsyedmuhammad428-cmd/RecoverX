using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Core.Services;

namespace RecoverX.Desktop.ViewModels
{
    /// <summary>Localized, display-ready view of one drive. All wording comes from the resource files.</summary>
    public sealed class DriveCardViewModel
    {
        private readonly ILocalizer _l;
        public DriveModel Model { get; }

        public DriveCardViewModel(DriveModel model, ILocalizer l) { Model = model; _l = l; }

        public string Letter { get { return Model.Letter; } }
        public string RootPath { get { return Model.RootPath; } }
        public double UsedPercent { get { return Model.UsedPercent; } }
        public bool IsReady { get { return Model.IsReady; } }

        public string Title
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Model.VolumeName)) return Model.VolumeName;
                return _l.Get(Model.Kind == DriveKind.Removable ? "Drive_Removable" : "Drive_Local");
            }
        }

        public bool IsSystem { get { return Model.IsSystemDrive; } }
        public string SystemBadge { get { return _l.Get("Drive_System"); } }

        public bool HasDevice { get { return Model.DeviceName.Length > 0; } }
        public string DeviceLine { get { return _l.Format("Drive_Device", Model.DeviceName); } }

        public string TypeLine { get { return _l.Format("Drive_Type", _l.Get(DriveClassifier.MediaKey(Model.Media))); } }

        public string ConnectionLine
        {
            get
            {
                string loc = _l.Get(DriveClassifier.LocationKey(Model));
                string text = Model.Bus == BusType.Unknown ? loc : _l.Get(DriveClassifier.BusKey(Model.Bus)) + " \u00B7 " + loc;
                return _l.Format("Drive_Connection", text);
            }
        }

        public string FileSystemLine
        {
            get { return _l.Format("Drive_FileSystem", string.IsNullOrEmpty(Model.FileSystem) ? _l.Get("Drive_FsUnknown") : Model.FileSystem); }
        }

        public string StatusLine { get { return _l.Format("Drive_Status", _l.Get(Model.IsReady ? "Drive_Ready" : "Drive_NotReady")); } }
        public string UsedLine { get { return _l.Format("Drive_Used", Model.UsedDisplay, Model.TotalDisplay); } }
        public string FreeLine { get { return _l.Format("Drive_Free", Model.FreeDisplay); } }
        public string SourceLine { get { return _l.Format("Drive_Source", _l.Get(DriveClassifier.SourceKey(Model.Source))); } }

        /// <summary>Quick Scan reads the NTFS file table, so it is offered for ready NTFS drives only.</summary>
        public bool CanScan { get { return Model.IsReady && string.Equals(Model.FileSystem, "NTFS", System.StringComparison.OrdinalIgnoreCase); } }
        public string ScanTooltip { get { return _l.Get(CanScan ? "Drive_Scan_Tip" : "Drive_Scan_NotNtfs"); } }

        public bool HasWarning { get { return DriveClassifier.WarningKey(Model) != null; } }
        public string WarningText
        {
            get { string k = DriveClassifier.WarningKey(Model); return k == null ? string.Empty : _l.Get(k); }
        }
    }
}
