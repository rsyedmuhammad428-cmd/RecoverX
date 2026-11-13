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

    /// <summary>Maps conditions to ratings and resource keys; the UI localizes them.</summary>
    public static class ConditionInfo
    {
        /// <summary>
        /// Evidence-based condition -> the three user-facing ratings. VERIFIED and LIKELY both mean "no sign of damage was
        /// found" (the detail text says whether the structure could be verified); PARTIAL = some data overwritten/broken;
        /// LOST = overwritten or unusable. Unsupported, empty and unknown files are not rated as recoverable.
        /// </summary>
        public static RecoveryRating Rating(RecoveryCondition c)
        {
            switch (c)
            {
                case RecoveryCondition.Verified:
                case RecoveryCondition.Likely: return RecoveryRating.Excellent;
                case RecoveryCondition.Partial: return RecoveryRating.Partial;
                case RecoveryCondition.Lost: return RecoveryRating.Poor;
                default: return RecoveryRating.NotRecoverable;
            }
        }

        public static string RatingKey(RecoveryRating r)
        {
            switch (r)
            {
                case RecoveryRating.Excellent: return "Rating_Excellent";
                case RecoveryRating.Partial: return "Rating_Partial";
                case RecoveryRating.Poor: return "Rating_Poor";
                default: return "Rating_NotRecoverable";
            }
        }

        public const string SnapshotTipKey = "Snap_Tip";

        /// <summary>Detail key for a Deep Scan result, describing what was actually verified.</summary>
        public static string CarvedTipKey(ScanEntry e)
        {
            if ((e.Evidence & CarveEvidence.InUse) != 0 && e.Condition == RecoveryCondition.Verified) return "CarveTip_InUseChecksum";
            if ((e.Evidence & CarveEvidence.InUse) != 0) return "CarveTip_InUse";
            switch (e.Condition)
            {
                case RecoveryCondition.Verified: return (e.Evidence & CarveEvidence.ChecksumOk) != 0 ? "CarveTip_Checksum" : "CarveTip_Verified";
                case RecoveryCondition.Likely: return "CarveTip_Likely";
                case RecoveryCondition.Partial: return (e.Evidence & CarveEvidence.Truncated) != 0 ? "CarveTip_Truncated" : "CarveTip_Partial";
                default: return "CarveTip_Lost";
            }
        }

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
