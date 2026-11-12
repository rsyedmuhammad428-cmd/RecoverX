using System;
using System.IO;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    /// <summary>
    /// Safety rule: recovered files must never be written to the volume being scanned. Uses the native engine's volume
    /// identity check when available (handles mount points), otherwise compares drive letters.
    /// </summary>
    public sealed class DestinationValidator : IDestinationValidator
    {
        private readonly IEngineClient _engine;

        public DestinationValidator(IEngineClient engine) { _engine = engine; }

        public DestinationVerdict Validate(string sourceRoot, string destinationDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory)) return DestinationVerdict.NotFound;
            bool exists;
            try { exists = Directory.Exists(destinationDirectory); } catch (Exception) { exists = false; }
            if (!exists) return DestinationVerdict.NotFound;

            if (_engine.IsAvailable)
            {
                try { return _engine.ValidateDestination(sourceRoot, destinationDirectory); }
                catch (EngineException) { /* fall through to the managed check */ }
            }
            return ByDriveLetter(sourceRoot, destinationDirectory);
        }

        /// <summary>Managed fallback: same drive letter means same volume.</summary>
        public static DestinationVerdict ByDriveLetter(string sourceRoot, string destinationDirectory)
        {
            char s, d;
            if (!TryLetter(sourceRoot, out s)) return DestinationVerdict.Unverifiable;
            if (destinationDirectory != null && destinationDirectory.StartsWith(@"\\", StringComparison.Ordinal)
                && !destinationDirectory.StartsWith(@"\\?\", StringComparison.Ordinal)
                && !destinationDirectory.StartsWith(@"\\.\", StringComparison.Ordinal))
                return DestinationVerdict.Safe;          // network share
            if (!TryLetter(destinationDirectory, out d)) return DestinationVerdict.Unverifiable;
            return s == d ? DestinationVerdict.SameVolume : DestinationVerdict.Safe;
        }

        private static bool TryLetter(string path, out char letter)
        {
            letter = '\0';
            if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':') return false;
            char c = char.ToUpperInvariant(path[0]);
            if (c < 'A' || c > 'Z') return false;
            letter = c;
            return true;
        }
    }
}
