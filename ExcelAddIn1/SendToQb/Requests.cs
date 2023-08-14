using Interop.QBFC14;
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
		internal static string QueryCustomer(Connection conn, string name)
		{

			IMsgSetRequest requestMsgSet = conn.sessionManager.CreateMsgSetRequest("US", 14, 0);
			requestMsgSet.Attributes.OnError = ENRqOnError.roeContinue;

			QBSessionManager sessionManager = conn.sessionManager;

			BuildCustomerQueryRq(requestMsgSet, name);

			IMsgSetResponse responseMsgSet = sessionManager.DoRequests(requestMsgSet);

			return WalkCustomerAddRs(responseMsgSet);
		}

		private static void BuildCustomerQueryRq(IMsgSetRequest requestMsgSet, string name)
		{
			ICustomerQuery CustomerQueryRq = requestMsgSet.AppendCustomerQueryRq();

			CustomerQueryRq.ORCustomerListQuery.CustomerListFilter.ORNameFilter.NameFilter.MatchCriterion.SetValue(ENMatchCriterion.mcEndsWith);

			CustomerQueryRq.ORCustomerListQuery.CustomerListFilter.ORNameFilter.NameFilter.Name.SetValue(name);
			CustomerQueryRq.IncludeRetElementList.Add("Name");
		}

		private static string WalkCustomerAddRs(IMsgSetResponse responseMsgSet)
		{

			if (responseMsgSet == null) return null;
			IResponseList responseList = responseMsgSet.ResponseList;
			if (responseList == null) return null;

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
							return CustomerRet.GetAt(i).Name.GetValue();
						}
					}
				}
			}
			return null;
		}
	}
}