using System;
using System.Threading.Tasks;
using System.Windows.Input;
using RecoverX.Core.Interfaces;
using RecoverX.Desktop.Services;

namespace RecoverX.Desktop.ViewModels
{
    public sealed class DashboardViewModel : ViewModelBase, IRefreshable
    {
        private readonly IRecoveredFilesRepository _recovered;
        private long _recoveredCount;

        public DrivesViewModel Drives { get; }
        public HistoryViewModel History { get; }
        public ICommand QuickScanCommand { get; }
        public ICommand DeepScanCommand { get; }
        public string[] SupportedFileSystems { get; } = { "NTFS", "FAT32", "exFAT" };
        public string[] SafetyTips { get; }
        public long RecoveredCount { get { return _recoveredCount; } private set { SetProperty(ref _recoveredCount, value); } }

        public DashboardViewModel(DrivesViewModel drives, HistoryViewModel history, IRecoveredFilesRepository recovered, IScanLauncher launcher, ILocalizer l)
        {
            Drives = drives;
            History = history;
            _recovered = recovered;
            QuickScanCommand = new RelayCommand(_ => launcher.RequestPicker());
            DeepScanCommand = new RelayCommand(_ => launcher.RequestPicker());   // both scans start from a drive card
            SafetyTips = new[] { l.Get("Tip_1"), l.Get("Tip_2"), l.Get("Tip_3"), l.Get("Tip_4"), l.Get("Tip_5") };
            var ignored = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            await History.RefreshAsync();
            try { RecoveredCount = await Task.Run(() => _recovered.Count()); } catch (Exception) { /* statistics only */ }
        }
    }
}
