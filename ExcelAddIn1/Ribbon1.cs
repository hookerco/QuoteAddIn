using Microsoft.Office.Tools.Ribbon;
using Excel = Microsoft.Office.Interop.Excel;
using Microsoft.Office.Tools.Excel;
using Microsoft.Office.Tools.Excel.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Interop.QBFC14;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ExcelAddIn1
{
    public partial class QBRibbon
    {
        private void Ribbon1_Load(object sender, RibbonUIEventArgs e)
        {
            
        }

        private void button1_Click(object sender, RibbonControlEventArgs e)
        {
            MessageBox.Show("Pressed!");
            query_customer();
        }

        public void query_customer()
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

                BuildCustomerQueryRq(requestMsgSet);

                //Send the request and get the response from QuickBooks
                IMsgSetResponse responseMsgSet = sessionManager.DoRequests(requestMsgSet);

                //End the session and close the connection to QuickBooks
                sessionManager.EndSession();
                sessionBegun = false;
                sessionManager.CloseConnection();
                connectionOpen = false;

                WalkCustomerAddRs(responseMsgSet);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
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
        void BuildCustomerQueryRq(IMsgSetRequest requestMsgSet)
        {
            ICustomerQuery CustomerQueryRq = requestMsgSet.AppendCustomerQueryRq();

            //Set field value for Name
            CustomerQueryRq.ORCustomerListQuery.CustomerListFilter.ORNameFilter.NameFilter.MatchCriterion.SetValue(ENMatchCriterion.mcEndsWith);

            Worksheet worksheet = Globals.Factory.GetVstoObject(
Globals.ThisAddIn.Application.ActiveWorkbook.Worksheets["Standard Quote"]);

            // five digit customer id
            Regex regex = new Regex(@"\d{5}$");

            string name = worksheet.Range["B11"].Value.ToString();
            Match match = regex.Match(name);
            name = match.Value;

            MessageBox.Show("Customer ID: " + name);

            CustomerQueryRq.ORCustomerListQuery.CustomerListFilter.ORNameFilter.NameFilter.Name.SetValue(name);
        }




        static void WalkCustomerAddRs(IMsgSetResponse responseMsgSet)
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
                            //WalkCustomerRet(CustomerRet);
                            MessageBox.Show($"Found {CustomerRet.GetAt(i).Name.GetValue()} as a customer");
                        }
                    }
                }
            }
        }
    }
}
