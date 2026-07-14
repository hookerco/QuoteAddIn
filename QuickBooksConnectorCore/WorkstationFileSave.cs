using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickBooksConnectorCore
{
    /// <summary>
    /// Validates workstation save requests and commits received file bytes atomically.
    /// The interactive host owns the SaveFileDialog; this class stays UI-free and testable.
    /// </summary>
    public static class WorkstationFileSave
    {
        public const int MaxFileBytes = 25 * 1024 * 1024;

        private static readonly HashSet<string> AllowedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "xlsx",
                "pdf",
                "json"
            };

        public static string NormalizeSuggestedName(string suggestedName, string extension)
        {
            string normalizedExtension = NormalizeExtension(extension);
            string rawName = suggestedName ?? string.Empty;
            int separator = Math.Max(
                rawName.LastIndexOf('/'), rawName.LastIndexOf('\\'));
            string name = separator >= 0 ? rawName.Substring(separator + 1) : rawName;
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            name = new string(name.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '-' : ch).ToArray())
                .TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "quote";
            }

            string suffix = "." + normalizedExtension;
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name += suffix;
            }

            int maxStem = Math.Max(1, 160 - suffix.Length);
            string stem = name.Substring(0, name.Length - suffix.Length);
            if (stem.Length > maxStem)
            {
                stem = stem.Substring(0, maxStem).TrimEnd('.', ' ');
            }
            return stem + suffix;
        }

        public static string NormalizeExtension(string extension)
        {
            string value = (extension ?? string.Empty).Trim().TrimStart('.');
            if (!AllowedExtensions.Contains(value))
            {
                throw new ArgumentException("Unsupported quote file extension.", nameof(extension));
            }
            return value.ToLowerInvariant();
        }

        public static void ValidateContent(byte[] content)
        {
            if (content == null || content.Length == 0)
            {
                throw new ArgumentException("Quote file content is empty.", nameof(content));
            }
            if (content.Length > MaxFileBytes)
            {
                throw new ArgumentException("Quote file exceeds the 25 MB workstation-save limit.", nameof(content));
            }
        }

        public static void WriteAtomically(string destination, byte[] content)
        {
            ValidateContent(content);
            string fullDestination = Path.GetFullPath(destination ?? string.Empty);
            string directory = Path.GetDirectoryName(fullDestination);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("The selected save directory does not exist.");
            }

            string temporary = Path.Combine(
                directory,
                "." + Path.GetFileNameWithoutExtension(fullDestination) + "-" +
                Guid.NewGuid().ToString("N") + Path.GetExtension(fullDestination));
            try
            {
                File.WriteAllBytes(temporary, content);
                if (File.Exists(fullDestination))
                {
                    File.Replace(temporary, fullDestination, null);
                }
                else
                {
                    File.Move(temporary, fullDestination);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
