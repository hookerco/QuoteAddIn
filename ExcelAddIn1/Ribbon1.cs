using Interop.QBFC14;
using Microsoft.Office.Tools.Ribbon;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace ExcelAddIn1
{

	public partial class QBRibbon
	{
		private void Ribbon1_Load(object sender, RibbonUIEventArgs e)
		{
			//OpenFileDialog1.FileName = Properties.Settings.Default.QuickbooksPath;
			//ChooseFileButton.SuperTip = OpenFileDialog1.FileName;
			//QuickBooksActiveToggle.Checked = Properties.Settings.Default.UseActiveQuickbook;
		}

		private void SalesOrderButton_Click(object sender, RibbonControlEventArgs e)
		{
			Driver.Run();
		}

		//private void ChooseFile_Click(object sender, RibbonControlEventArgs e)
		//{
		//	DialogResult result = OpenFileDialog1.ShowDialog();

		//	if (result == DialogResult.OK)
		//	{
		//		string filePath = OpenFileDialog1.FileName;

		//		Properties.Settings.Default.QuickbooksPath = filePath;

		//		ChooseFileButton.SuperTip = OpenFileDialog1.FileName;

		//		Properties.Settings.Default.Save();
		//	}
		//}

		//private void QuickBooksActiveToggle_Click(object sender, RibbonControlEventArgs e)
		//{
		//	if (QuickBooksActiveToggle.Checked)
		//	{
		//		Properties.Settings.Default.UseActiveQuickbook = true;
		//	}
		//	else
		//	{
		//		Properties.Settings.Default.UseActiveQuickbook = false;
		//	}

		//	Properties.Settings.Default.Save();
		//}

		private void CreateButton_Click(object sender, RibbonControlEventArgs e)
		{
			QuoteBuilder.Create();
		}

		private void AddButton_Click(object sender, RibbonControlEventArgs e)
		{
			string filePath;
			DialogResult result = QuoteOpenFileDialog.ShowDialog();
			if (result == DialogResult.OK)
			{
				filePath = QuoteOpenFileDialog.FileName;

				QuoteBuilder.Add(filePath);
			}
			
		}
	}
}
