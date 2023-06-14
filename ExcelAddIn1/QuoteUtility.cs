using Interop.QBFC14;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Security;
using Excel = Microsoft.Office.Interop.Excel;
using Microsoft.Office.Tools.Excel;
using Microsoft.Office.Tools.Excel.Extensions;
using System.Diagnostics;
using System.CodeDom;

namespace ExcelAddIn1
{
    /** 
     * <summary>
     * Class <c>QuoteUtility</c> drives the quote utility
     * </summary>
     */
    internal class QuoteUtility
    {
        /** 
         * <summary>
         * Property <c>AllItemList</c> contains all queried items
         * </summary>
         */
        public AllItemList AllItemList { get; set; }

        /**
         * <summary>
         * Field <c>customerName</c> is the current quote's customer
         * </summary>
         */
        private string customerName = "";

        /**
         * <summary>
         * Field <c>worksheet</c> is the Standard Quote worksheet
         * </summary>
         */
        private Worksheet worksheet = null;

        /**
         * <summary>
         * This method opens the connection to quickbooks, begins the session,
         * and drives the other quote functions
         * </summary>
         */ 
        public void RunQuoteUtility()
        {
            worksheet = Globals.Factory.GetVstoObject(
Globals.ThisAddIn.Application.ActiveWorkbook.Worksheets["Standard Quote"]);

            bool sessionBegun = false;
            bool connectionOpen = false;
            QBSessionManager sessionManager = null;

            try
            {
                sessionManager = new QBSessionManager();

                //Create the message set request object to hold our request
                IMsgSetRequest requestMsgSet = sessionManager.CreateMsgSetRequest("US", 14, 0);
                requestMsgSet.Attributes.OnError = ENRqOnError.roeContinue;

                sessionManager.OpenConnection("", "Sample Code from OSR");
                connectionOpen = true;
                sessionManager.BeginSession("", ENOpenMode.omDontCare);
                sessionBegun = true;

                //******* Add request functions here *******
                QueryCustomer(requestMsgSet, sessionManager);
                WalkItems();
                //******************************************

                sessionManager.EndSession();
                sessionBegun = false;
                sessionManager.CloseConnection();
                connectionOpen = false;
            }
            catch (Exception e)
            { // Cleanly close session and connection 
                MessageBox.Show(e.Message);
                if (sessionBegun)
                {
                    sessionManager.EndSession();
                }
                if (connectionOpen)
                {
                    sessionManager.CloseConnection();
                }
            }
        }

        /**
         * <summary>
         * This method adds a previously formed list of queried objects to the class
         * </summary>
         */
        public void AddList(ref AllItemList newList)
        {
            AllItemList = newList;
        }

        /**
         * <summary>
         * This method finds customer number from worksheet and sets <see cref="customerName"/> field
         * </summary>
         * <remarks>Modifies customerName, requestMsgSet</remarks>
         */
        private void QueryCustomer(IMsgSetRequest requestMsgSet, QBSessionManager sessionManager)
        {
            BuildCustomerQueryRq(requestMsgSet);

            IMsgSetResponse responseMsgSet = sessionManager.DoRequests(requestMsgSet);

            WalkCustomerAddRs(responseMsgSet);
        }

        /**
         * <summary>
         * ReGex customer ID from worksheet and add it to customer query
         * </summary>
         * <remarks>Modifies requestMsgSet</remarks>
         */
        private void BuildCustomerQueryRq(IMsgSetRequest requestMsgSet)
        {
            ICustomerQuery CustomerQueryRq = requestMsgSet.AppendCustomerQueryRq();

            //Set field value for Name
            CustomerQueryRq.ORCustomerListQuery.CustomerListFilter.ORNameFilter.NameFilter.MatchCriterion.SetValue(ENMatchCriterion.mcEndsWith);

            // five digit customer id
            Regex regex = new Regex(@"\d{5}$");

            string name = worksheet.Range["B11"].Value.ToString();
            Match match = regex.Match(name);
            name = match.Value;

            MessageBox.Show("Customer ID: " + name);

            CustomerQueryRq.ORCustomerListQuery.CustomerListFilter.ORNameFilter.NameFilter.Name.SetValue(name);
            CustomerQueryRq.IncludeRetElementList.Add("Name");
        }
        
        /**
         * <summary>
         * Changes <see cref="customerName"/> field to response from query
         * </summary>
         * <remarks>Modifies <see cref="customerName"/></remarks>
         */
        private void WalkCustomerAddRs(IMsgSetResponse responseMsgSet)
        {

            if (responseMsgSet == null) return;
            IResponseList responseList = responseMsgSet.ResponseList;
            if (responseList == null) return;
            
            for (int i = 0; i < responseList.Count; i++)
            {
                IResponse response = responseList.GetAt(i);
                if (response.StatusCode >= 0)
                {
                    if (response.Detail != null)
                    {
                        //make sure the response is the type we're expecting
                        ENResponseType responseType = (ENResponseType)response.Type.GetValue();
                        if (responseType == ENResponseType.rtCustomerQueryRs)
                        {
                            //upcast to more specific type here, this is safe because we checked with response.Type check above
                            ICustomerRetList CustomerRet = (ICustomerRetList)response.Detail;

                            /* GetAt(i)? Is there one value in CustomerRet per response? 
                             * Seems like it should have its own iterator */
                            MessageBox.Show($"Found {CustomerRet.GetAt(i).Name.GetValue()} as a customer");
                            customerName = CustomerRet.GetAt(i).Name.GetValue();
                        }
                    }
                }
            }
        }

        /**
         * <summary>
         * In worksheet, sees if parts in "Standard Quote" are located in any item descriptions from <see cref="AllItemList"/>
         * </summary>
         * <remarks>Takes much longer in WinForms projects than in Excel VSTO Add-In. 
         * Runs <see cref="String.Contains(string)"/> on thousands of items for multiple parts.</remarks>
         */
        public void WalkItems()
        {
            Regex regex = new Regex(@"BTI\sp/n\s.*$");
            // start of items
            int iterator = 22;

            // strings of the cells' values
            string colA = worksheet.Range["A" + iterator].Text;
            string colB = worksheet.Range["B" + iterator].Text;

            // while the cells aren't empty (may have to change this depending on format)
            while (colA != "" || colB != "")
            {
                Match match = regex.Match(colB);

                if (match.Success && match.Value.Length > 15)
                {
                    // gets rid of "BTI p/n "
                    string partNum = match.Value.Substring(8);

                    // use AllListItem query method
                    string[] foundPart = AllItemList.FindPart(partNum);

                    if (foundPart[0] != "")
                    {
                        MessageBox.Show($"Item: '{foundPart[0]}' found with description '{foundPart[1]}'");
                    }
                    else
                    {
                        MessageBox.Show($"Item: '{partNum}' not found");
                    }
                }

                // Go to next cell(s)
                iterator++;
                colA = worksheet.Range["A" + iterator].Text;
                colB = worksheet.Range["B" + iterator].Text;
            }
        }
    }
    
    /**
     * <summary>
     * Class <c>AllItemList</c> queries and holds queried items
     * </summary>
     */
    internal class AllItemList
    {
        /**
         * <summary>Field <c>items</c> is the list of the queried items</summary>
         */
        private List<string[]> Items
        { get; } = new List<string[]>();


        /**
         * This method queries QuickBooks for the items list
         */
        public bool QueryItems()
        {
            bool sessionBegun = false;
            bool connectionOpen = false;
            QBSessionManager sessionManager = null;

            try
            {
                //Create the session Manager object
                sessionManager = new QBSessionManager();

                //Create the message set request object to hold our request
                IMsgSetRequest requestMsgSet = sessionManager.CreateMsgSetRequest("US", 14, 0);
                requestMsgSet.Attributes.OnError = ENRqOnError.roeContinue;

                //Connect to QuickBooks and begin a session
                sessionManager.OpenConnection("", "Sample Code from OSR");
                connectionOpen = true;
                sessionManager.BeginSession("", ENOpenMode.omDontCare);
                sessionBegun = true;

                IItemNonInventoryQuery itemQuery = requestMsgSet.AppendItemNonInventoryQueryRq();

                //Send the request and get the response from QuickBooks
                IMsgSetResponse responseMsgSet = sessionManager.DoRequests(requestMsgSet);

                //End the session and close the connection to QuickBooks
                sessionManager.EndSession();
                sessionBegun = false;
                sessionManager.CloseConnection();
                connectionOpen = false;

                WalkItemQueryRs(responseMsgSet);
                return true;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
                if (sessionBegun)
                {
                    sessionManager.EndSession();
                }
                if (connectionOpen)
                {
                    sessionManager.CloseConnection();
                }
                return false;
            }
        }
        
        /**
         * <summary>This method ensures the response is valid.</summary>
         */
        private void WalkItemQueryRs(IMsgSetResponse responseMsgSet)
        {

            if (responseMsgSet == null) return;
            IResponseList responseList = responseMsgSet.ResponseList;
            if (responseList == null) return;
            //if we sent only one request, there is only one response, we'll walk the list for this sample
            for (int i = 0; i < responseList.Count; i++)
            {
                IResponse response = responseList.GetAt(i);
                //check the status code of the response, 0=ok, >0 is warning
                if (response.StatusCode >= 0)
                {
                    //the request-specific response is in the details, make sure we have some
                    if (response.Detail != null)
                    {
                        //make sure the response is the type we're expecting
                        ENResponseType responseType = (ENResponseType)response.Type.GetValue();
                        if (responseType == ENResponseType.rtItemNonInventoryQueryRs)
                        {
                            //upcast to more specific type here, this is safe because we checked with response.Type check above
                            IItemNonInventoryRetList ItemNonInventoryRet = (IItemNonInventoryRetList)response.Detail;
                            WalkItemRet(ItemNonInventoryRet);
                        }
                    }
                }
            }
        }
        
        /**
         * <summary>This method adds each item and description to <see cref="Items"/></summary>
         * <remarks>Modifies <see cref="Items"/></remarks>
         */
        private void WalkItemRet(IItemNonInventoryRetList ItemRetList)
        {
            if (ItemRetList == null) return;

            IItemNonInventoryRet ItemRet = null;

            for (int i = 0; i < ItemRetList.Count; ++i)
            {
                ItemRet = ItemRetList.GetAt(i);

                // Some items are SalesOrPurchase, some are SalesAndPurchase? Try catch wouldn't fix it
                // Adds non-inventory items' names and descriptions to memory (this.items)
                string[] itemN = new string[2];
                itemN[0] = ItemRet.Name.GetValue();
                itemN[1] = "";



                if (ItemRet.ORSalesPurchase.SalesOrPurchase != null)
                {
                    if (ItemRet.ORSalesPurchase.SalesOrPurchase.Desc != null)
                    {
                        itemN[1] = ItemRet.ORSalesPurchase.SalesOrPurchase.Desc.GetValue();
                    }

                }
                else if (ItemRet.ORSalesPurchase.SalesAndPurchase != null)
                {
                    if (ItemRet.ORSalesPurchase.SalesAndPurchase.SalesDesc != null)
                    {
                        itemN[1] = ItemRet.ORSalesPurchase.SalesAndPurchase.SalesDesc.GetValue();
                    }
                }

                this.Items.Add(itemN);

            }
        }

        /**
         * <summary>This method searches through every item in <see cref="Items"/> to find 
         * the part string</summary>
         * <param name="part">part number to be searched</param>
         * <returns>First instance of the string in format of [name, description]</returns>
         */
        public string[] FindPart(string part)
        {
            string[] foundPart = new string[2];
            foundPart[0] = "";
            foundPart[1] = "";
            bool found = false;

            for (int i = 0; i < Items.Count; ++i)
            {
                if (Items[i][1].Contains(part))
                {

                    found = true;
                    foundPart[0] = Items[i][0];
                    foundPart[1] = Items[i][1];
                }
                if (found == true) break;
            }
            return foundPart;
        }

        /**
         * <summary>Empties <see cref="Items"/></summary>
         */
        public void Clear()
        {
            Items.Clear();
        }
    }
}
