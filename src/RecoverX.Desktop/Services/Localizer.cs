using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Threading;
using System.Windows.Markup;
using RecoverX.Core.Interfaces;

namespace RecoverX.Desktop.Services
{
    /// <summary>
    /// Looks strings up in Resources/Strings.resx (and Strings.&lt;code&gt;.resx satellites such as Strings.ur.resx).
    /// A missing key shows as "[Key]" and is logged once, so gaps are visible instead of silently blank.
    /// </summary>
    public sealed class ResourceLocalizer : ILocalizer
    {
        private readonly ResourceManager _rm;
        private readonly IAppLogger _log;
        private readonly HashSet<string> _missingLogged = new HashSet<string>();

        public CultureInfo Culture { get; private set; }

        /// <summary>English only. The resource-file structure is kept so UI text stays out of the code.</summary>
        public ResourceLocalizer(IAppLogger log)
        {
            _log = log;
            _rm = new ResourceManager("RecoverX.Desktop.Resources.Strings", Assembly.GetExecutingAssembly());
            Culture = new CultureInfo("en");
            Thread.CurrentThread.CurrentUICulture = Culture;
            CultureInfo.DefaultThreadCurrentUICulture = Culture;
        }

        public string Get(string key)
        {
            string s = null;
            try { s = _rm.GetString(key, Culture); } catch (MissingManifestResourceException) { }
            if (s == null)
            {
                if (_missingLogged.Add(key)) _log.Warn("Missing localization key: " + key);
                return "[" + key + "]";
            }
            return s;
        }

        public string Format(string key, params object[] args)
        {
            try { return string.Format(Culture, Get(key), args); }
            catch (FormatException) { _log.Warn("Bad format string for key: " + key); return Get(key); }
        }
    }

    /// <summary>XAML access: Text="{loc:Loc Nav_Dashboard}". Resolved once when the view is created.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public static ILocalizer Current { get; set; }

        public string Key { get; set; }
        public LocExtension() { }
        public LocExtension(string key) { Key = key; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return Current == null ? "[" + Key + "]" : Current.Get(Key);
        }
    }
}
