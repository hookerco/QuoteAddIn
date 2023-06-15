using Microsoft.Office.Tools.Ribbon;
using System.Windows.Forms;

namespace ExcelAddIn1
{

    public partial class QBRibbon
    {
        private bool isLoaded = false;
        // allItemList must be loaded separately because a QuoteUtility object needs access to the current worksheet before it exists
        AllItemList allItemList = new AllItemList();
        private void Ribbon1_Load(object sender, RibbonUIEventArgs e)
        {
            OpenFileDialog1.FileName = Properties.Settings.Default.QuickbooksPath;
            ChooseFileButton.SuperTip = OpenFileDialog1.FileName;
            QuickBooksActiveToggle.Checked = Properties.Settings.Default.UseActiveQuickbook;

            // Pre-loads item list
            // Only if active Quickbook is opened, otherwise it would take too long
            if (Properties.Settings.Default.UseActiveQuickbook)
            {
                isLoaded = allItemList.QueryItems();
            }
        }

        private void button1_Click(object sender, RibbonControlEventArgs e)
        {
            if (!isLoaded)
            {
                isLoaded = allItemList.QueryItems();
            }

            QuoteUtility quoteUtility = new QuoteUtility();
            //Literally just adds the list of items
            quoteUtility.AddList(ref allItemList);
            quoteUtility.RunQuoteUtility();
        }

        private void chooseFile_Click_1(object sender, RibbonControlEventArgs e)
        {
            DialogResult result = OpenFileDialog1.ShowDialog();

            if (result == DialogResult.OK)
            {
                string filePath = OpenFileDialog1.FileName;

                Properties.Settings.Default.QuickbooksPath = filePath;

                ChooseFileButton.SuperTip = OpenFileDialog1.FileName;

                Properties.Settings.Default.Save();
            }
        }

        private void QuickBooksActiveToggle_Click(object sender, RibbonControlEventArgs e)
        {
            if (QuickBooksActiveToggle.Checked)
            {
                Properties.Settings.Default.UseActiveQuickbook = true;
            }
            else
            {
                Properties.Settings.Default.UseActiveQuickbook = false;
            }

            Properties.Settings.Default.Save();
        }
    }
}
