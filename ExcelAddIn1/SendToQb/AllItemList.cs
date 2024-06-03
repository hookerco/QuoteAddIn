using Interop.QBFC14;
using QBRequestLibrary;
using System;
using System.Collections.Generic;

namespace ExcelAddIn1
{
	public static class AllItemList
	{
		/**
		 * <summary>This method queries QuickBooks for the items list</summary>
		 */
		public static bool QueryItems(List<string[]> ItemList)
		{
			Connection conn = new Connection();

			try
			{
				conn.File = "";
				if (!Properties.Settings.Default.UseActiveQuickbook)
				{
					conn.File = Properties.Settings.Default.QuickbooksPath;
				}

				conn.Open();

				QBSessionManager sessionManager = conn.sessionManager;

				//Create the message set request object to hold our request
				IMsgSetRequest requestMsgSet = sessionManager.CreateMsgSetRequest("US", 14, 0);
				requestMsgSet.Attributes.OnError = ENRqOnError.roeContinue;

				IItemNonInventoryQuery itemQuery = requestMsgSet.AppendItemNonInventoryQueryRq();

				//Send the request and get the response from QuickBooks
				IMsgSetResponse responseMsgSet = sessionManager.DoRequests(requestMsgSet);

				WalkItemQueryRs(responseMsgSet, ItemList);

				conn.Close();

				return true;
			}

			catch (Exception)
			{
				//MessageBox.Show(e.Message);
				conn.Close();
				return false;
			}
		}

		/**
		 * <summary>This method ensures the response is valid</summary>
		 */
		private static void WalkItemQueryRs(IMsgSetResponse responseMsgSet, List<string[]> ItemList)
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
							PopulateItemList(ItemNonInventoryRet, ItemList);
						}
					}
				}
			}
		}

		/**
		 * <summary>This method adds each item and description to <see cref="Items"/></summary>
		 * <remarks>Modifies <see cref="Items"/></remarks>
		 */
		static private void PopulateItemList(IItemNonInventoryRetList ItemRetList, List<string[]> ItemList)
		{
			if (ItemRetList == null) return;

			for (int i = 0; i < ItemRetList.Count; ++i)
			{
				IItemNonInventoryRet ItemRet = ItemRetList.GetAt(i);

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

				ItemList.Add(itemN);

			}
		}

		/**
		 * <summary>This method searches through every item in <see cref="Items"/> to find 
		 * the part string</summary>
		 * <param name="part">part number to be searched</param>
		 * <returns>First instance of the string in format of [name, description]</returns>
		 */
		static public (string num, string desc) FindPart(string part, List<string[]> AllItemList)
		{
			string foundNum = ""; // Part number (serial)
			string foundDesc = ""; // Description
			bool found = false;

			for (int i = 0; i < AllItemList.Count; ++i)
			{
				if (AllItemList[i][1].Contains(part))
				{

					found = true;
					foundNum = AllItemList[i][0];
					foundDesc = AllItemList[i][1];
				}
				if (found == true) break;
			}
			
			return (foundNum, foundDesc);
		}

	}
}
