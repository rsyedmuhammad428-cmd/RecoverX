using System;
using System.IO;
using RecoverX.Core.Interfaces;

namespace RecoverX.Core.Services
{
    /// <summary>Minimal daily-file logger. Logs never contain file contents or recovered filenames.</summary>
    public sealed class FileLogger : IAppLogger
    {
        private readonly string _dir;
        private readonly object _gate = new object();

        public FileLogger(string directory)
        {
            _dir = directory;
        }

        public void Info(string message) { Write("INFO ", message); }
        public void Warn(string message) { Write("WARN ", message); }
        public void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + " | " + ex);
        }

        private void Write(string level, string message)
        {
            try
            {
                lock (_gate)
                {
                    Directory.CreateDirectory(_dir);
                    string file = Path.Combine(_dir, "recoverx-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + message + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Logging must never crash the app.
            }
        }
    }
}
