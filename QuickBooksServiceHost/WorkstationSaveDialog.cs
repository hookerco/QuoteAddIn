using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using QuickBooksConnectorCore;

namespace QuickBooksServiceHost
{
    internal sealed class WorkstationSaveResult
    {
        internal WorkstationSaveResult(bool cancelled, string filename)
        {
            Cancelled = cancelled;
            Filename = filename;
        }

        internal bool Cancelled { get; }
        internal string Filename { get; }
    }

    /// <summary>Owns the interactive Save As dialog on the estimator workstation.</summary>
    internal static class WorkstationSaveDialog
    {
        internal static WorkstationSaveResult Save(
            byte[] content,
            string suggestedName,
            string extension)
        {
            WorkstationFileSave.ValidateContent(content);
            string normalizedExtension = WorkstationFileSave.NormalizeExtension(extension);
            string normalizedName = WorkstationFileSave.NormalizeSuggestedName(
                suggestedName, normalizedExtension);
            WorkstationSaveResult result = null;
            Exception failure = null;
            IntPtr foregroundWindow = ForegroundWindowOwner.CaptureHandle();

            var thread = new Thread(() =>
            {
                try
                {
                    using (var dialog = new SaveFileDialog())
                    {
                        dialog.Title = "Save Quote File";
                        dialog.FileName = normalizedName;
                        dialog.DefaultExt = normalizedExtension;
                        dialog.AddExtension = true;
                        dialog.OverwritePrompt = true;
                        dialog.CheckPathExists = true;
                        dialog.InitialDirectory = WorkstationSaveFolder.Read() ?? string.Empty;
                        dialog.ValidateNames = true;
                        dialog.RestoreDirectory = true;
                        dialog.Filter = FilterFor(normalizedExtension);

                        DialogResult dialogResult;
                        IWin32Window foregroundOwner =
                            ForegroundWindowOwner.TryCreate(foregroundWindow);
                        if (foregroundOwner != null)
                        {
                            dialogResult = dialog.ShowDialog(foregroundOwner);
                        }
                        else
                        {
                            using (var owner = new Form())
                            {
                                owner.TopMost = true;
                                owner.ShowInTaskbar = false;
                                owner.StartPosition = FormStartPosition.CenterScreen;
                                owner.Width = 1;
                                owner.Height = 1;
                                owner.Opacity = 0;
                                owner.Show();
                                owner.Activate();

                                dialogResult = dialog.ShowDialog(owner);
                            }
                        }

                        if (dialogResult != DialogResult.OK)
                        {
                            result = new WorkstationSaveResult(true, null);
                            return;
                        }

                        if (!string.Equals(
                            Path.GetExtension(dialog.FileName),
                            "." + normalizedExtension,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "The selected filename has the wrong extension.");
                        }

                        WorkstationFileSave.WriteAtomically(dialog.FileName, content);
                        result = new WorkstationSaveResult(
                            false, Path.GetFileName(dialog.FileName));
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new InvalidOperationException(
                    "Workstation Save As failed: " + failure.Message, failure);
            }
            return result ?? throw new InvalidOperationException(
                "Workstation Save As did not return a result.");
        }
        internal static WorkstationSaveResult ChooseFolder()
        {
            WorkstationSaveResult result = null;
            Exception failure = null;
            IntPtr foregroundWindow = ForegroundWindowOwner.CaptureHandle();

            var thread = new Thread(() =>
            {
                try
                {
                    using (var dialog = new FolderBrowserDialog())
                    {
                        dialog.Description = "Choose the default quote save folder";
                        dialog.ShowNewFolderButton = true;
                        dialog.SelectedPath = WorkstationSaveFolder.Read() ?? string.Empty;

                        DialogResult dialogResult;
                        IWin32Window foregroundOwner =
                            ForegroundWindowOwner.TryCreate(foregroundWindow);
                        if (foregroundOwner != null)
                        {
                            dialogResult = dialog.ShowDialog(foregroundOwner);
                        }
                        else
                        {
                            using (var owner = new Form())
                            {
                                owner.TopMost = true;
                                owner.ShowInTaskbar = false;
                                owner.StartPosition = FormStartPosition.CenterScreen;
                                owner.Width = 1;
                                owner.Height = 1;
                                owner.Opacity = 0;
                                owner.Show();
                                owner.Activate();
                                dialogResult = dialog.ShowDialog(owner);
                            }
                        }

                        if (dialogResult != DialogResult.OK)
                        {
                            result = new WorkstationSaveResult(true, null);
                            return;
                        }

                        WorkstationSaveFolder.Write(dialog.SelectedPath);
                        result = new WorkstationSaveResult(
                            false, WorkstationSaveFolder.DisplayName(dialog.SelectedPath));
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                throw new InvalidOperationException(
                    "Workstation folder selection failed: " + failure.Message, failure);
            }
            return result ?? throw new InvalidOperationException(
                "Workstation folder selection did not return a result.");
        }

        private static string FilterFor(string extension)
        {
            switch (extension)
            {
                case "xlsx":
                    return "Excel workbook|*.xlsx";
                case "pdf":
                    return "PDF document|*.pdf";
                case "json":
                    return "JSON request|*.json";
                default:
                    throw new ArgumentException("Unsupported quote file extension.");
            }
        }
    }
}
