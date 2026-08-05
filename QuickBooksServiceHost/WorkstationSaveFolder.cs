using System;
using System.IO;

namespace QuickBooksServiceHost
{
    internal static class WorkstationSaveFolder
    {
        private static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BTI",
            "QuoteModule",
            "save-folder.txt");

        internal static string Read()
        {
            return Read(StatePath);
        }

        internal static string Read(string statePath)
        {
            if (!File.Exists(statePath))
            {
                return null;
            }
            string folder = File.ReadAllText(statePath).Trim();
            return Directory.Exists(folder) ? folder : null;
        }

        internal static void Write(string folder)
        {
            Write(StatePath, folder);
        }

        internal static void Write(string statePath, string folder)
        {
            if (!Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException("The selected quote save folder no longer exists.");
            }
            string stateDirectory = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(stateDirectory))
            {
                Directory.CreateDirectory(stateDirectory);
            }
            File.WriteAllText(statePath, folder);
        }

        internal static void Clear()
        {
            Clear(StatePath);
        }

        internal static void Clear(string statePath)
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }

        internal static string DisplayName(string folder)
        {
            string trimmed = (folder ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? folder : name;
        }
    }
}
