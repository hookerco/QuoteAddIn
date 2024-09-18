using QBRequestLibrary;

namespace ExcelAddIn1
{
	/** 
	 * <summary>
	 * Class <c>QuoteUtility</c> drives the quote utility
	 * </summary>
	 */
	internal static class Requests
	{

		/**
		 * <summary>
		 * This method finds customer number from worksheet
		 * </summary>
		 * <remarks>Modifies requestMsgSet</remarks>
		 * <returns>string as customer Name</returns>
		 */
		internal static string QueryCustomer(string name)
		{
			CustomerQueryRequest request = new CustomerQueryRequest(name);
			CustomerQueryResponse response = request.SendRequest();
			return response.Name;
		}
	}
}