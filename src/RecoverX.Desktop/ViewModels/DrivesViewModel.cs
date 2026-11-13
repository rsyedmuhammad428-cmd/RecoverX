using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Desktop.Services;

namespace RecoverX.Desktop.ViewModels
{
    public sealed class DrivesViewModel : ViewModelBase, IRefreshable
    {
        private readonly IDriveService _drives;
        private readonly IEngineClient _engine;
        private readonly ILocalizer _l;
        private readonly IDialogService _dialogs;
        private readonly IElevationService _elevation;
        private readonly IAppLogger _log;
        private readonly IScanLauncher _launcher;
        private readonly ISettingsService _settings;
        private bool _isBusy;
        private string _status = string.Empty;

        public ObservableCollection<DriveCardViewModel> Drives { get; } = new ObservableCollection<DriveCardViewModel>();
        public ICommand RefreshCommand { get; }
        public ICommand VerifyAccessCommand { get; }
        public ICommand RestartAsAdminCommand { get; }
        public ICommand ScanCommand { get; }
        public ICommand DeepScanCommand { get; }
        public ICommand SnapshotCommand { get; }

        public bool IsBusy { get { return _isBusy; } private set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(HasNoDrives)); } }
        public string Status { get { return _status; } private set { SetProperty(ref _status, value); } }
        public bool HasNoDrives { get { return Drives.Count == 0 && !IsBusy; } }
        public bool ShowAdminBanner { get { return !_elevation.IsElevated; } }

        public DrivesViewModel(IDriveService drives, IEngineClient engine, ILocalizer l, IDialogService dialogs,
                               IElevationService elevation, IScanLauncher launcher, ISettingsService settings, IAppLogger log)
        {
            _settings = settings;
            _launcher = launcher;
            _drives = drives; _engine = engine; _l = l; _dialogs = dialogs; _elevation = elevation; _log = log;
            RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
            VerifyAccessCommand = new RelayCommand(async p => await VerifyAsync(p as DriveCardViewModel), p => p is DriveCardViewModel && !IsBusy);
            RestartAsAdminCommand = new RelayCommand(_ => RestartAsAdmin());
            ScanCommand = new RelayCommand(p => _launcher.RequestScan(((DriveCardViewModel)p).Model, ScanMode.Quick), p => p is DriveCardViewModel && ((DriveCardViewModel)p).CanScan);
            DeepScanCommand = new RelayCommand(p => _launcher.RequestScan(((DriveCardViewModel)p).Model, ScanMode.Deep), p => p is DriveCardViewModel && ((DriveCardViewModel)p).CanDeepScan);
            SnapshotCommand = new RelayCommand(p => _launcher.RequestScan(((DriveCardViewModel)p).Model, ScanMode.PreviousVersions), p => p is DriveCardViewModel && ((DriveCardViewModel)p).CanSnapshot);
            var ignored = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            Status = _l.Get("Drives_Detecting");
            try
            {
                // Drive queries can block on slow removable media, so keep them off the UI thread.
                var found = await Task.Run(() => _drives.GetDrives().ToList());
                Drives.Clear();
                bool deepDefault = false;
                try { deepDefault = _settings.Load().DefaultScanType == AppSettings.DeepScan; } catch (Exception ex) { _log.Warn("Settings unavailable: " + ex.Message); }
                foreach (var d in found) Drives.Add(new DriveCardViewModel(d, _l, deepDefault));
                Status = _l.Format("Drives_Count", found.Count);
            }
            catch (Exception ex)
            {
                _log.Error("Drive refresh failed", ex);
                Status = _l.Get("Drives_Failed");
            }
            finally
            {
                IsBusy = false;
                OnPropertyChanged(nameof(HasNoDrives));
            }
        }

        private async Task VerifyAsync(DriveCardViewModel card)
        {
            if (card == null) return;
            string title = _l.Get("Verify_Result_Title");
            if (!_dialogs.Confirm(_l.Get("Verify_Confirm_Title"), _l.Format("Verify_Confirm_Body", card.Letter))) return;

            IsBusy = true;
            try
            {
                VolumeProbeResult r = await Task.Run(() => _engine.ProbeVolume(card.Letter));
                string msg;
                switch (r.Status)
                {
                    case EngineStatus.Ok:
                        msg = _l.Format("Verify_Success", card.Letter, FsName(r.FileSystem), r.SectorSize, ByteFormatter.Format((long)r.TotalBytes));
                        break;
                    case EngineStatus.AccessDenied: msg = _l.Format("Verify_Denied", card.Letter); break;
                    case EngineStatus.NotFound: msg = _l.Format("Verify_NotFound", card.Letter); break;
                    case EngineStatus.Unavailable: msg = _l.Format("Verify_EngineMissing", _engine.UnavailableReason); break;
                    default: msg = _l.Format("Verify_Io", card.Letter, r.Win32Error); break;
                }
                _log.Info("Verify read access " + card.Letter + ": " + r.Status);
                _dialogs.Info(title, msg);
            }
            catch (EngineException ex)
            {
                _dialogs.Info(title, _l.Format("Verify_EngineMissing", ex.Message));
            }
            finally { IsBusy = false; }
        }

        private void RestartAsAdmin()
        {
            if (!_dialogs.Confirm(_l.Get("Admin_Confirm_Title"), _l.Get("Admin_Confirm_Body"))) return;
            if (!_elevation.RestartElevated()) _dialogs.Info(_l.Get("Admin_Confirm_Title"), _l.Get("Admin_Failed"));
        }

        private string FsName(FileSystemId id)
        {
            switch (id)
            {
                case FileSystemId.Ntfs: return "NTFS";
                case FileSystemId.Fat32: return "FAT32";
                case FileSystemId.ExFat: return "exFAT";
                case FileSystemId.Fat16: return "FAT16";
                case FileSystemId.Fat12: return "FAT12";
                default: return _l.Get("Drive_FsUnknown");
            }
        }
    }
}
