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

            // Pre-loads item list
            isLoaded = allItemList.QueryItems();
        }

        private void button1_Click(object sender, RibbonControlEventArgs e)
        {
            if (!isLoaded)
            {
                MessageBox.Show("Items not loaded, Now trying to load items. Open QuickBooks if not yet open");
                isLoaded = allItemList.QueryItems();
            }
            else
            {

                QuoteUtility quoteUtility = new QuoteUtility();
                //Literally just adds the list of items
                quoteUtility.AddList(ref allItemList);
                quoteUtility.RunQuoteUtility();
            }
        }
    }
}
