using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Desktop.Services;

namespace RecoverX.Desktop.ViewModels
{
    public sealed class RecoveredRow
    {
        public string Name { get; set; }
        public string Line { get; set; }
        public string SavedPath { get; set; }
        public string OriginalPath { get; set; }
    }

    public sealed class RecoveredFilesViewModel : ViewModelBase, IRefreshable
    {
        private readonly IRecoveredFilesRepository _repo;
        private readonly ILocalizer _l;
        private readonly IDialogService _dialogs;
        private readonly IAppLogger _log;
        private string _status = string.Empty;
        private long _count;

        public ObservableCollection<RecoveredRow> Rows { get; } = new ObservableCollection<RecoveredRow>();
        public ICommand RefreshCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand ShowInFolderCommand { get; }
        public bool HasNone { get { return Rows.Count == 0; } }
        public string Status { get { return _status; } private set { SetProperty(ref _status, value); } }

        public RecoveredFilesViewModel(IRecoveredFilesRepository repo, ILocalizer l, IDialogService dialogs, IAppLogger log)
        {
            _repo = repo; _l = l; _dialogs = dialogs; _log = log;
            RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
            ClearCommand = new RelayCommand(async _ => await ClearAsync(), _ => Rows.Count > 0);
            ShowInFolderCommand = new RelayCommand(p => Show(p as RecoveredRow), p => p is RecoveredRow);
            var ignored = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            try
            {
                var rows = await Task.Run(() => _repo.GetRecent(500).ToList());
                _count = await Task.Run(() => _repo.Count());
                Rows.Clear();
                foreach (var r in rows)
                {
                    string status = _l.Get("Status_" + r.Status);
                    if (status.StartsWith("[", StringComparison.Ordinal)) status = r.Status;
                    Rows.Add(new RecoveredRow
                    {
                        Name = Path.GetFileName(r.SavedPath),
                        Line = _l.Format("Rec_Line", status, ByteFormatter.Format(r.Size), r.SourceDrive),
                        SavedPath = r.SavedPath, OriginalPath = r.OriginalPath
                    });
                }
                Status = string.Empty;
            }
            catch (Exception ex) { _log.Error("Loading recovered files failed", ex); Status = _l.Get("Rec_Load_Failed"); }
            OnPropertyChanged(nameof(HasNone));
        }

        private async Task ClearAsync()
        {
            if (!_dialogs.Confirm(_l.Get("Rec_Clear_Title"), _l.Format("Rec_Clear_Confirm", _count))) return;
            try { await Task.Run(() => _repo.ClearAll()); }
            catch (Exception ex) { _log.Error("Clearing recovered list failed", ex); Status = _l.Get("Rec_Load_Failed"); return; }
            await RefreshAsync();
        }

        private void Show(RecoveredRow row)
        {
            if (row == null) return;
            try
            {
                if (File.Exists(row.SavedPath)) Process.Start("explorer.exe", "/select,\"" + row.SavedPath + "\"");
                else { string dir = Path.GetDirectoryName(row.SavedPath); if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\""); }
            }
            catch (Exception ex) { _log.Warn("Show in folder failed: " + ex.Message); }
        }
    }
}
