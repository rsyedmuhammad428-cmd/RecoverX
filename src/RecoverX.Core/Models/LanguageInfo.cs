using System;
using System.Collections.Generic;
using System.Linq;

namespace RecoverX.Core.Models
{
    public sealed class LanguageInfo
    {
        public string Code { get; set; }
        public string NativeName { get; set; }
        public bool IsRightToLeft { get; set; }
        /// <summary>False when the architecture supports the language but no translation file ships yet.</summary>
        public bool IsAvailable { get; set; }
        public override string ToString() { return NativeName; }
    }

    /// <summary>
    /// The set of languages RecoverX knows about. Adding Urdu means: translate Resources/Strings.resx into
    /// Resources/Strings.ur.resx, then set IsAvailable = true for "ur" below. Nothing else changes: the UI already
    /// flips to right-to-left layout from IsRightToLeft.
    /// </summary>
    public static class LanguageCatalog
    {
        public static readonly IList<LanguageInfo> All = new List<LanguageInfo>
        {
            new LanguageInfo { Code = "en", NativeName = "English", IsRightToLeft = false, IsAvailable = true },
            new LanguageInfo { Code = "ur", NativeName = "\u0627\u0631\u062F\u0648", IsRightToLeft = true, IsAvailable = false }
        };

        public static IList<LanguageInfo> Available { get { return All.Where(l => l.IsAvailable).ToList(); } }

        /// <summary>Returns the requested language if it is available, otherwise English.</summary>
        public static LanguageInfo Resolve(string code)
        {
            var hit = All.FirstOrDefault(l => l.IsAvailable && string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            return hit ?? All.First(l => l.Code == "en");
        }
    }
}
