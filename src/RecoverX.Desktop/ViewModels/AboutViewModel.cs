using System.Collections.Generic;
using System.Reflection;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Services;
using RecoverX.Desktop.Services;

namespace RecoverX.Desktop.ViewModels
{
    public sealed class AboutViewModel : ViewModelBase
    {
        public string VersionLine { get; }
        public string Status { get; }
        public string Limitations { get; }
        public string CompatNote { get; }
        public IList<string> EnvironmentLines { get; }

        public AboutViewModel(ILocalizer l, IEngineClient engine, IElevationService elevation)
        {
            VersionLine = l.Format("About_Version", Assembly.GetExecutingAssembly().GetName().Version);
            Status = l.Get("About_Status");
            Limitations = l.Get("About_Limits");
            CompatNote = l.Get("About_Compat");

            string engineText = engine.IsAvailable ? engine.Version : l.Format("Env_EngineUnavailable", engine.UnavailableReason);
            var lines = new List<string>
            {
                l.Format("Env_OS", SystemInfo.OsDescription()),
                l.Format("Env_Dotnet", SystemInfo.DotNetFramework()),
                l.Format("Env_Process", l.Get(SystemInfo.Is64BitProcess ? "Env_Bits64" : "Env_Bits32")),
                l.Format("Env_Admin", l.Get(elevation.IsElevated ? "Common_Yes" : "Common_No")),
                l.Format("Env_Engine", engineText)
            };
            if (engine.IsAvailable) lines.Add(l.Format("Env_EngineFile", engine.LoadedPath));
            EnvironmentLines = lines;
        }
    }
}
