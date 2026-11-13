using System.Collections.Generic;

namespace RecoverX.Core.Models
{
    /// <summary>A user-configurable group of file extensions (Photos, Videos, ...). Id is a stable key, never translated.</summary>
    public sealed class FileCategory
    {
        public string Id { get; set; }
        public IList<string> Extensions { get; set; } = new List<string>();
        public bool Enabled { get; set; } = true;
        public int SortOrder { get; set; }
        /// <summary>The "Other Files" category matches anything the others do not; its extension list is not editable.</summary>
        public bool IsCatchAll { get { return Id == DefaultCategories.OtherId; } }
    }

    public static class DefaultCategories
    {
        public const string OtherId = "Other";

        public static IList<FileCategory> Create()
        {
            return new List<FileCategory>
            {
                New("Photos",    0, "jpg", "jpeg", "png", "bmp", "gif", "tif", "tiff", "webp", "heic", "avif", "cr3"),
                New("Videos",    1, "mp4", "avi", "mov", "mkv", "wmv", "flv", "webm", "m4v", "3gp"),
                New("Documents", 2, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp", "epub", "msg"),
                New("Audio",     3, "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma"),
                New("Archives",  4, "zip", "rar", "7z", "tar", "gz"),
                New(OtherId,     5)
            };
        }

        private static FileCategory New(string id, int order, params string[] ext)
        {
            return new FileCategory { Id = id, SortOrder = order, Enabled = true, Extensions = new List<string>(ext) };
        }
    }
}
