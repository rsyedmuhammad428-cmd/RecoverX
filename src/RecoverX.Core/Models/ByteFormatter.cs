using System.Globalization;

namespace RecoverX.Core.Models
{
    public static class ByteFormatter
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        public static string Format(long bytes)
        {
            if (bytes < 0) bytes = 0;
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
            return i == 0
                ? bytes.ToString(CultureInfo.CurrentCulture) + " B"
                : v.ToString("0.0", CultureInfo.CurrentCulture) + " " + Units[i];
        }
    }
}
