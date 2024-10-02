using ExcelAddIn1.SendToQb;
using System;

namespace ExcelAddIn1
{
    
    /// <summary>
    /// This class contains methods for making requests to QuickBooks.
    /// </summary>
    internal static class Requests
    {
        /// <summary>
        /// This method queries the customer name from the worksheet.
        /// </summary>
        /// <param name="name">The name of the customer.</param>
        /// <returns>The customer name as a string.</returns>
        internal static string QueryCustomer(string name)
        {
            QBConnector qBConnector = new QBConnector();
            return qBConnector.Client.GetCustomer(name).Name;
        }
    }
}