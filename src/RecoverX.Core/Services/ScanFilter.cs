using System.Collections.Generic;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    public sealed class FilterResult
    {
        public List<ScanEntry> Shown { get; } = new List<ScanEntry>();
        public int HiddenBySettings { get; set; }
    }

    public static class ScanFilter
    {
        /// <summary>Applies the user's Settings (enabled file categories, minimum file size) to scan results.</summary>
        public static FilterResult Apply(IEnumerable<ScanEntry> all, IList<FileCategory> categories, AppSettings settings)
        {
            var result = new FilterResult();
            long minBytes = (long)settings.MinFileSizeKb * 1024;
            foreach (var e in all)
            {
                bool tooSmall = minBytes > 0 && e.Size < minBytes;
                bool wanted = FileCategorizer.IsWanted(e.Name, categories);
                if (tooSmall || !wanted) result.HiddenBySettings++;
                else result.Shown.Add(e);
            }
            return result;
        }
    }

    /// <summary>Maps conditions to resource keys; the UI localizes them.</summary>
    public static class ConditionInfo
    {
        public static string Key(RecoveryCondition c)
        {
            switch (c)
            {
                case RecoveryCondition.Verified: return "Cond_Verified";
                case RecoveryCondition.Likely: return "Cond_Likely";
                case RecoveryCondition.Partial: return "Cond_Partial";
                case RecoveryCondition.Lost: return "Cond_Lost";
                case RecoveryCondition.Unsupported: return "Cond_Unsupported";
                case RecoveryCondition.Empty: return "Cond_Empty";
                default: return "Cond_Unknown";
            }
        }

        public static string TipKey(RecoveryCondition c)
        {
            switch (c)
            {
                case RecoveryCondition.Verified: return "CondTip_Verified";
                case RecoveryCondition.Likely: return "CondTip_Likely";
                case RecoveryCondition.Partial: return "CondTip_Partial";
                case RecoveryCondition.Lost: return "CondTip_Lost";
                case RecoveryCondition.Unsupported: return "CondTip_Unsupported";
                case RecoveryCondition.Empty: return "CondTip_Empty";
                default: return "CondTip_Unknown";
            }
        }
    }
}
