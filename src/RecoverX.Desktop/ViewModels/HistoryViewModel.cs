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
    public interface IRefreshable { Task RefreshAsync(); }

    /// <summary>One localized row of scan history.</summary>
    public sealed class SessionItem
    {
        public string Summary { get; set; }
        public string StartedDisplay { get; set; }
        public string ResumableText { get; set; }
    }

    public sealed class HistoryViewModel : ViewModelBase, IRefreshable
    {
        private readonly IScanHistoryRepository _repo;
        private readonly ILocalizer _l;
        private readonly IDialogService _dialogs;
        private readonly IAppLogger _log;
        private ScanStats _stats = new ScanStats();
        private string _status = string.Empty;

        public ObservableCollection<SessionItem> Sessions { get; } = new ObservableCollection<SessionItem>();
        public ICommand RefreshCommand { get; }
        public ICommand ClearCommand { get; }
        public ScanStats Stats { get { return _stats; } private set { SetProperty(ref _stats, value); } }
        public string Status { get { return _status; } private set { SetProperty(ref _status, value); } }
        public bool HasNoSessions { get { return Sessions.Count == 0; } }

        public HistoryViewModel(IScanHistoryRepository repo, ILocalizer l, IDialogService dialogs, IAppLogger log)
        {
            _repo = repo; _l = l; _dialogs = dialogs; _log = log;
            RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
            ClearCommand = new RelayCommand(async _ => await ClearAsync(), _ => Sessions.Count > 0);
            var ignored = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            try
            {
                var sessions = await Task.Run(() => _repo.GetRecent(100).ToList());
                var stats = await Task.Run(() => _repo.GetStats());
                Sessions.Clear();
                foreach (var s in sessions) Sessions.Add(ToItem(s));
                Stats = stats;
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                _log.Error("History load failed", ex);
                Status = _l.Get("Hist_LoadFailed");
            }
            OnPropertyChanged(nameof(HasNoSessions));
        }

        private async Task ClearAsync()
        {
            long total = Stats.TotalScans;
            if (!_dialogs.Confirm(_l.Get("Hist_Clear_Title"), _l.Format("Hist_Clear_Confirm", total))) return;
            try { await Task.Run(() => _repo.ClearAll()); }
            catch (Exception ex) { _log.Error("Clearing history failed", ex); Status = _l.Get("Hist_LoadFailed"); return; }
            await RefreshAsync();
        }

        private SessionItem ToItem(ScanSession s)
        {
            return new SessionItem
            {
                Summary = _l.Format("Hist_Summary", Localize("ScanType_", s.ScanType), s.DriveLetter, Localize("Status_", s.Status), s.FilesFound),
                StartedDisplay = s.StartedUtc.ToLocalTime().ToString("g", _l.Culture),
                ResumableText = _l.Get(s.Resumable ? "Hist_Resumable" : "Hist_NotResumable")
            };
        }

        // Stored values are invariant English identifiers ("Quick", "Completed"); unknown values are shown as stored.
        private string Localize(string prefix, string value)
        {
            string key = prefix + value;
            string text = _l.Get(key);
            return text.StartsWith("[", StringComparison.Ordinal) ? value : text;
        }
    }
}
