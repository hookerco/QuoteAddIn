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

            var thread = new Thread(() =>
            {
                try
                {
                    using (var dialog = new SaveFileDialog())
                    using (var owner = new Form())
                    {
                        dialog.Title = "Save Quote File";
                        dialog.FileName = normalizedName;
                        dialog.DefaultExt = normalizedExtension;
                        dialog.AddExtension = true;
                        dialog.OverwritePrompt = true;
                        dialog.CheckPathExists = true;
                        dialog.ValidateNames = true;
                        dialog.RestoreDirectory = true;
                        dialog.Filter = FilterFor(normalizedExtension);

                        owner.TopMost = true;
                        owner.ShowInTaskbar = false;
                        owner.StartPosition = FormStartPosition.CenterScreen;
                        owner.Width = 1;
                        owner.Height = 1;
                        owner.Opacity = 0;
                        owner.Show();
                        owner.Activate();

                        if (dialog.ShowDialog(owner) != DialogResult.OK)
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
