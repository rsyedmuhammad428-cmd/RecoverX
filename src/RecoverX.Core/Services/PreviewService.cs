using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    /// <summary>
    /// Image/Text/Pdf/Hex are decided from the file's CONTENT (not its name). Erased = the data read back is all zeros, which is
    /// what SSD TRIM or wiping leaves behind: there is nothing left to show.
    /// </summary>
    public enum PreviewKind { None = 0, Image, Text, Pdf, NotSupportedType, TooLarge, NoData, ReadFailed, Disabled, Hex, Erased }

    public sealed class PdfSummary
    {
        public string Version { get; set; }
        public int Pages { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string Producer { get; set; }
        /// <summary>Plain text found in the page content streams (layout not preserved); empty if none could be read.</summary>
        public string TextSample { get; set; }
        public bool EncryptedOrEncoded { get; set; }
    }

    /// <summary>What to show for one file. Image bytes are decoded by the UI (WPF); Core never renders.</summary>
    public sealed class PreviewContent
    {
        public PreviewKind Kind { get; set; }
        public byte[] Data { get; set; }
        public string Text { get; set; }
        public bool Truncated { get; set; }
        public PdfSummary Pdf { get; set; }
        public string Detail { get; set; }
        /// <summary>The name's extension promises a type the content does not have (e.g. a ".jpeg" that holds no image).</summary>
        public bool TypeMismatch { get; set; }
    }

    public interface IPreviewService
    {
        bool CanPreview(ScanEntry e);
        Task<PreviewContent> LoadAsync(IScanSession session, ScanEntry entry, AppSettings settings, CancellationToken cancel);
    }

    /// <summary>
    /// Reads a found file's data (read-only, from the scan session) and decides what to show from its CONTENT, so files with
    /// unusual or missing extensions (.py, .json, "37") still preview: JPEG/PNG/GIF/BMP as image bytes, PDF as a summary (version,
    /// pages, title/author, text sample), readable text (first 64 KB), otherwise a hex view of the first 4 KB. All-zero data is
    /// reported as erased. Never writes anything.
    /// </summary>
    public sealed class PreviewService : IPreviewService
    {
        public const int TextBytes = 64 * 1024;
        public const int HexBytes = 4096;
        private static readonly HashSet<string> ImageExt = new HashSet<string> { "jpg", "jpeg", "jfif", "png", "bmp", "gif" };

        /// <summary>Any file with data can be previewed: the content decides how.</summary>
        public bool CanPreview(ScanEntry e) { return e != null && e.Size > 0 && e.CanAttemptRecovery; }

        public Task<PreviewContent> LoadAsync(IScanSession session, ScanEntry entry, AppSettings settings, CancellationToken cancel)
        {
            return Task.Run(() => Load(session, entry, settings, cancel), cancel);
        }

        private enum Sniff { Image, Pdf, Text, Binary }

        private PreviewContent Load(IScanSession session, ScanEntry e, AppSettings settings, CancellationToken cancel)
        {
            if (settings != null && !settings.PreviewEnabled) return new PreviewContent { Kind = PreviewKind.Disabled };
            if (e == null || e.Size <= 0 || !e.CanAttemptRecovery) return new PreviewContent { Kind = PreviewKind.NoData };
            long max = (long)(settings == null ? 25 : settings.PreviewMaxSizeMb) * 1024 * 1024;

            byte[] head = Read(session, e, Math.Min(e.Size, TextBytes), cancel, out string error);
            if (head == null) return new PreviewContent { Kind = PreviewKind.ReadFailed, Detail = error };
            bool mismatch = ImageExt.Contains(e.Extension) || e.Extension == "pdf";
            Sniff kind = Detect(head);

            if (kind == Sniff.Image || kind == Sniff.Pdf)
            {
                if (e.Size > max) return new PreviewContent { Kind = PreviewKind.TooLarge, Detail = ByteFormatter.Format(e.Size) };
                byte[] data = e.Size <= head.Length ? head : Read(session, e, e.Size, cancel, out error);
                if (data == null) return new PreviewContent { Kind = PreviewKind.ReadFailed, Detail = error };
                if (kind == Sniff.Pdf) return new PreviewContent { Kind = PreviewKind.Pdf, Pdf = PdfInspector.Summarize(data), TypeMismatch = e.Extension != "pdf" && mismatch };
                return new PreviewContent { Kind = PreviewKind.Image, Data = data };
            }
            if (AllZero(head) && (e.Size <= head.Length || AllZero(Read(session, e, e.Size <= max ? e.Size : head.Length, cancel, out error))))
                return new PreviewContent { Kind = PreviewKind.Erased, Detail = ByteFormatter.Format(e.Size) };
            if (kind == Sniff.Text)
                return new PreviewContent { Kind = PreviewKind.Text, Text = DecodeText(head), Truncated = e.Size > head.Length, TypeMismatch = mismatch };
            int n = Math.Min(head.Length, HexBytes);
            return new PreviewContent { Kind = PreviewKind.Hex, Text = HexDump(head, n), Truncated = e.Size > n, TypeMismatch = mismatch };
        }

        private static Sniff Detect(byte[] d)
        {
            int n = d.Length;
            if (n >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return Sniff.Image;
            if (n >= 8 && d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G') return Sniff.Image;
            if (n >= 6 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'8') return Sniff.Image;
            if (n >= 26 && d[0] == (byte)'B' && d[1] == (byte)'M' && d[6] == 0 && d[7] == 0 && d[8] == 0 && d[9] == 0) return Sniff.Image;
            if (n >= 5 && d[0] == (byte)'%' && d[1] == (byte)'P' && d[2] == (byte)'D' && d[3] == (byte)'F' && d[4] == (byte)'-') return Sniff.Pdf;
            return LooksLikeText(d) ? Sniff.Text : Sniff.Binary;
        }

        /// <summary>BOM, or no NUL bytes and at most 5% control characters (tab/CR/LF/FF/ESC excluded).</summary>
        public static bool LooksLikeText(byte[] d)
        {
            if (d.Length == 0) return false;
            if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) return true;
            if (d.Length >= 2 && ((d[0] == 0xFF && d[1] == 0xFE) || (d[0] == 0xFE && d[1] == 0xFF))) return true;
            int bad = 0;
            foreach (byte c in d)
            {
                if (c == 0) return false;
                if (c < 32 && c != 9 && c != 10 && c != 13 && c != 12 && c != 27) bad++;
            }
            return bad * 20 <= d.Length;
        }

        private static bool AllZero(byte[] d)
        {
            if (d == null) return false;
            foreach (byte b in d) if (b != 0) return false;
            return true;
        }

        /// <summary>Classic hex view: offset, 16 bytes, printable characters.</summary>
        public static string HexDump(byte[] d, int count)
        {
            var sb = new StringBuilder();
            for (int row = 0; row < count; row += 16)
            {
                sb.Append(row.ToString("X8")).Append("  ");
                for (int i = 0; i < 16; i++) sb.Append(row + i < count ? d[row + i].ToString("X2") + " " : "   ");
                sb.Append(' ');
                for (int i = 0; i < 16 && row + i < count; i++) { byte b = d[row + i]; sb.Append(b >= 32 && b < 127 ? (char)b : '.'); }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static byte[] Read(IScanSession s, ScanEntry e, long count, CancellationToken cancel, out string error)
        {
            error = null;
            var data = new byte[count];
            long off = 0;
            var buf = new byte[1 << 20];
            while (off < count)
            {
                cancel.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buf.Length, count - off), got;
                EngineStatus st;
                try { st = s.ReadFile(e.Index, off, buf, want, out got); }
                catch (ObjectDisposedException) { error = "session closed"; return null; }
                if (st != EngineStatus.Ok) { error = st.ToString(); return null; }
                if (got <= 0) break;
                Buffer.BlockCopy(buf, 0, data, (int)off, got);
                off += got;
            }
            if (off < count) { error = "short read"; return null; }
            return data;
        }

        /// <summary>BOM -> that encoding; valid UTF-8 -> UTF-8; otherwise the system ANSI code page. Control characters become '.'.</summary>
        public static string DecodeText(byte[] d)
        {
            string s;
            if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) s = Encoding.UTF8.GetString(d, 3, d.Length - 3);
            else if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) s = Encoding.Unicode.GetString(d, 2, (d.Length - 2) & ~1);
            else if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) s = Encoding.BigEndianUnicode.GetString(d, 2, (d.Length - 2) & ~1);
            else
            {
                try { s = new UTF8Encoding(false, true).GetString(d, 0, TrimPartialUtf8(d)); }
                catch (DecoderFallbackException) { s = Encoding.Default.GetString(d); }
            }
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c < 32 && c != '\t' && c != '\r' && c != '\n' ? '.' : c);
            return sb.ToString();
        }

        // A 64 KB cut can split a multi-byte UTF-8 sequence; drop the incomplete tail so valid text is not misjudged.
        private static int TrimPartialUtf8(byte[] d)
        {
            int n = d.Length;
            for (int back = 1; back <= 3 && back <= n; back++)
            {
                byte b = d[n - back];
                if ((b & 0xC0) == 0x80) continue;                     // continuation byte
                int need = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 1;
                return need > back ? n - back : n;
            }
            return n;
        }
    }

    /// <summary>Reads basic facts and a text sample from PDF bytes without rendering. Hostile input is expected.</summary>
    public static class PdfInspector
    {
        private const int MaxStreams = 400;
        private const int MaxInflated = 32 * 1024 * 1024;
        private const int SampleChars = 4000;
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static PdfSummary Summarize(byte[] data)
        {
            var r = new PdfSummary { TextSample = string.Empty };
            string raw = Latin1.GetString(data);
            Match v = Regex.Match(raw.Length > 1024 ? raw.Substring(0, 1024) : raw, @"%PDF-(\d\.\d)");
            r.Version = v.Success ? v.Groups[1].Value : "?";
            r.EncryptedOrEncoded = raw.Contains("/Encrypt");

            var bodies = new List<string> { raw };
            var text = new StringBuilder();
            int inflatedTotal = 0, streams = 0;
            foreach (Match m in Regex.Matches(raw, @"stream\r?\n"))
            {
                if (++streams > MaxStreams || inflatedTotal > MaxInflated) break;
                int start = m.Index + m.Length;
                int end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
                if (end < 0) break;
                int dictStart = raw.LastIndexOf("<<", m.Index, Math.Min(m.Index, 2048), StringComparison.Ordinal);
                string dict = dictStart >= 0 ? raw.Substring(dictStart, m.Index - dictStart) : string.Empty;
                if (Regex.IsMatch(dict, @"/(Subtype\s*/Image|Type\s*/XObject|Type\s*/FontFile|Length1)")) continue;
                byte[] content;
                if (dict.Contains("/FlateDecode")) content = Inflate(data, start, end - start, MaxInflated - inflatedTotal);
                else if (!dict.Contains("/Filter")) { content = new byte[end - start]; Buffer.BlockCopy(data, start, content, 0, content.Length); }
                else continue;
                if (content == null) continue;
                inflatedTotal += content.Length;
                string body = Latin1.GetString(content);
                bodies.Add(body);                                   // object streams can hold the page tree / info dictionary
                if (text.Length < SampleChars && body.Contains("BT")) ExtractText(body, text);
            }

            int pages = 0, count = 0;
            foreach (string b in bodies)
            {
                pages += Regex.Matches(b, @"/Type\s*/Page(?![a-zA-Z])").Count;
                foreach (Match c in Regex.Matches(b, @"/Type\s*/Pages\b[^>]{0,400}?/Count\s+(\d+)|/Count\s+(\d+)[^>]{0,400}?/Type\s*/Pages\b"))
                {
                    int n;
                    string g = c.Groups[1].Success ? c.Groups[1].Value : c.Groups[2].Value;
                    if (int.TryParse(g, out n)) count = Math.Max(count, n);
                }
            }
            r.Pages = count > 0 ? count : pages;
            foreach (string b in bodies)
            {
                if (r.Title == null) r.Title = InfoString(b, "Title");
                if (r.Author == null) r.Author = InfoString(b, "Author");
                if (r.Producer == null) r.Producer = InfoString(b, "Producer");
            }
            string sample = text.ToString().Trim();
            int printable = sample.Count(ch => ch >= 32 || ch == '\n');
            r.TextSample = sample.Length > 0 && printable * 100 / sample.Length >= 90 && sample.Count(char.IsLetter) * 3 >= sample.Length
                ? (sample.Length > SampleChars ? sample.Substring(0, SampleChars) : sample) : string.Empty;
            return r;
        }

        private static byte[] Inflate(byte[] d, int start, int len, int limit)
        {
            if (len < 3 || limit <= 0) return null;
            try
            {
                using (var ms = new MemoryStream(d, start + 2, len - 2))           // skip the 2-byte zlib header
                using (var z = new DeflateStream(ms, CompressionMode.Decompress))
                using (var outp = new MemoryStream())
                {
                    var buf = new byte[65536];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0)
                    {
                        outp.Write(buf, 0, n);
                        if (outp.Length > limit) break;
                    }
                    return outp.ToArray();
                }
            }
            catch (InvalidDataException) { return null; }
            catch (IOException) { return null; }
        }

        // Literal strings shown by Tj / TJ / ' / " inside BT..ET blocks.
        private static void ExtractText(string body, StringBuilder sb)
        {
            int i = 0;
            bool inText = false;
            while (i < body.Length && sb.Length < SampleChars)
            {
                char c = body[i];
                if (c == '(' && inText) { sb.Append(ReadLiteral(body, ref i)); continue; }
                if (c == 'B' && i + 1 < body.Length && body[i + 1] == 'T' && Boundary(body, i, 2)) { inText = true; i += 2; continue; }
                if (c == 'E' && i + 1 < body.Length && body[i + 1] == 'T' && Boundary(body, i, 2)) { inText = false; sb.Append('\n'); i += 2; continue; }
                if (inText && (c == '\'' || c == '"' || (c == 'T' && i + 1 < body.Length && (body[i + 1] == '*' || body[i + 1] == 'd' || body[i + 1] == 'D')))) sb.Append(' ');
                i++;
            }
        }

        private static bool Boundary(string s, int i, int len)
        {
            bool before = i == 0 || char.IsWhiteSpace(s[i - 1]);
            bool after = i + len >= s.Length || char.IsWhiteSpace(s[i + len]);
            return before && after;
        }

        private static string ReadLiteral(string s, ref int i)
        {
            var sb = new StringBuilder();
            int depth = 0;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': break;
                        case 't': sb.Append(' '); break;
                        case '(': case ')': case '\\': sb.Append(n); break;
                        default:
                            if (n >= '0' && n <= '7')
                            {
                                int v = n - '0', k = 0;
                                while (k < 2 && i + 1 < s.Length && s[i + 1] >= '0' && s[i + 1] <= '7') { v = v * 8 + (s[++i] - '0'); k++; }
                                sb.Append((char)(v & 0xFF));
                            }
                            break;
                    }
                    continue;
                }
                if (c == '(') { if (depth++ > 0) sb.Append(c); continue; }
                if (c == ')') { if (--depth == 0) { i++; break; } sb.Append(c); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string InfoString(string body, string key)
        {
            Match m = Regex.Match(body, @"/" + key + @"\s*\(((?:\\.|[^\\)]){0,300})\)");
            if (m.Success)
            {
                int i = m.Groups[1].Index - 1;
                string v = ReadLiteral(body, ref i);
                if (v.Length >= 2 && v[0] == '\xFE' && v[1] == '\xFF') v = Encoding.BigEndianUnicode.GetString(Latin1.GetBytes(v.Substring(2)));
                return Clean(v);
            }
            m = Regex.Match(body, @"/" + key + @"\s*<([0-9A-Fa-f\s]{2,600})>");
            if (!m.Success) return null;
            string hex = Regex.Replace(m.Groups[1].Value, @"\s", "");
            if (hex.Length % 2 == 1) hex += "0";
            var bytes = new byte[hex.Length / 2];
            for (int k = 0; k < bytes.Length; k++) bytes[k] = Convert.ToByte(hex.Substring(k * 2, 2), 16);
            string s = bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2) : Latin1.GetString(bytes);
            return Clean(s);
        }

        private static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) if (c >= 32) sb.Append(c);
            string t = sb.ToString().Trim();
            return t.Length == 0 ? null : t;
        }
    }
}
