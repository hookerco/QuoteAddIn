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

namespace ExcelAddIn1
{
    internal class QuoteUtility
    {
        public AllItemList allItemList { get; set; }
        private string customerName = "";

        private Worksheet worksheet = null;

        public void RunQuoteUtility()
        {
            worksheet = Globals.Factory.GetVstoObject(
Globals.ThisAddIn.Application.ActiveWorkbook.Worksheets["Standard Quote"]);

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

                //******* Add request functions here *******
                //query_customer(requestMsgSet, sessionManager);
                WalkItems();
                //******************************************

                //End the session and close the connection to QuickBooks
                sessionManager.EndSession();
                sessionBegun = false;
                sessionManager.CloseConnection();
                connectionOpen = false;
            }
            catch (Exception e)
            {
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

        public void AddList(ref AllItemList newList)
        {
            allItemList = newList;
        }

        // query_customer
        private void QueryCustomer(IMsgSetRequest requestMsgSet, QBSessionManager sessionManager)
        {
            BuildCustomerQueryRq(requestMsgSet);

            //Send the request and get the response from QuickBooks
            IMsgSetResponse responseMsgSet = sessionManager.DoRequests(requestMsgSet);

            WalkCustomerAddRs(responseMsgSet);
        }
        // query_customer::build the query
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
        // query_customer::check response
        private void WalkCustomerAddRs(IMsgSetResponse responseMsgSet)
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

        // WalkItems -> walk through each item in quote (not in mounting set, right?)
        public void WalkItems()
        {
            Regex regex = new Regex(@"BTI\sp/n\s.*$");
            // start of items
            int iterator = 22;

            // strings of the cells' values
            string colA = worksheet.Range["A" +  iterator].Text;
            string colB = worksheet.Range["B" + iterator].Text;

            // while the cells aren't empty (may have to change this depending on format)
            while (colA != "" || colB != "") {
                Match match = regex.Match(colB);

                if (match.Success && match.Value.Length > 15)
                {
                    // gets rid of "BTI p/n "
                    string partNum = match.Value.Substring(8);

                    // use AllListItem query method
                    string[] foundPart = allItemList.FindPart(partNum);

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

    internal class AllItemList
    {
        private List<string[]> items
        { get; } = new List<string[]>();


        // query_items
        public bool query_items()
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
        // query_items::Ensure response is okay?
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
        //query_items:: add each valid item to all items list
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

                //Console.WriteLine(itemN[1]);
                this.items.Add(itemN);

            }
        }

        public string[] FindPart(string part)
        {
            string[] foundPart = new string[2];
            foundPart[0] = "";
            foundPart[1] = "";
            bool found = false;

            for (int i = 0; i < items.Count; ++i)
            {
                if (items[i][1].Contains(part))
                {
                    
                    found = true;
                    foundPart[0] = items[i][0];
                    foundPart[1] = items[i][1];
                }
                if (found == true) break;
            }
            return foundPart;
        }

        public void Clear_List()
        {
            items.Clear();
        }
    }
}
