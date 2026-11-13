using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using RecoverX.Core.Services;

namespace RecoverX.Desktop.Services
{
    public interface IDialogService
    {
        bool Confirm(string title, string message);
        void Info(string title, string message);
        /// <summary>Folder picker. Returns null if cancelled.</summary>
        string PickFolder(string description, string initialPath);
    }

    public sealed class WpfDialogService : IDialogService
    {
        public bool Confirm(string title, string message)
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        public void Info(string title, string message)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public string PickFolder(string description, string initialPath)
        {
            // WinForms FolderBrowserDialog: part of .NET Framework 4.8 and available on Windows 7; no extra dependency.
            using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = description, ShowNewFolderButton = true })
            {
                try { if (!string.IsNullOrWhiteSpace(initialPath) && System.IO.Directory.Exists(initialPath)) d.SelectedPath = initialPath; }
                catch (Exception) { /* invalid path text: start without a selection */ }
                return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
            }
        }
    }

    public interface IElevationService
    {
        bool IsElevated { get; }
        /// <summary>Starts a new elevated instance (UAC prompt) and shuts this one down. False if cancelled or failed.</summary>
        bool RestartElevated();
    }

    public sealed class ElevationService : IElevationService
    {
        public bool IsElevated { get { return SystemInfo.IsAdministrator(); } }

        public bool RestartElevated()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch (Win32Exception) { return false; }     // user declined the UAC prompt (error 1223) or launch failed
            catch (InvalidOperationException) { return false; }
            Application.Current.Shutdown();
            return true;
        }
    }
}
