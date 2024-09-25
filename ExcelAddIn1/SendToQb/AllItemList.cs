using Interop.QBFC14;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Forms.VisualStyles;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace ExcelAddIn1
{
	public class AllItemList
	{
		private List<IQuoteItem> itemList = new List<IQuoteItem>();

		public void Add(IQuoteItem item)
		{
			itemList.Add(item);
		}

		/**
		 * <summary>This method queries QuickBooks for the items list</summary>
		 */
		public bool QueryItems()
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

				WalkItemQueryRs(responseMsgSet, itemList);

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
		private static void WalkItemQueryRs(IMsgSetResponse responseMsgSet, List<IQuoteItem> ItemList)
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
		static private void PopulateItemList(IItemNonInventoryRetList ItemRetList, List<IQuoteItem> ItemList)
		{
			if (ItemRetList == null) return;

			for (int i = 0; i < ItemRetList.Count; ++i)
			{
				IItemNonInventoryRet ItemRet = ItemRetList.GetAt(i);

				// Some items are SalesOrPurchase, some are SalesAndPurchase? Try catch wouldn't fix it
				// Adds non-inventory items' names and descriptions to memory (this.items)
				BaseQuoteItem itemN = new BaseQuoteItem();
				itemN.SetNumber(ItemRet.Name.GetValue());
				itemN.SetDescription("");



				if (ItemRet.ORSalesPurchase.SalesOrPurchase != null)
				{
					if (ItemRet.ORSalesPurchase.SalesOrPurchase.Desc != null)
					{
                        itemN.SetDescription(ItemRet.ORSalesPurchase.SalesOrPurchase.Desc.GetValue());
					}

				}
				else if (ItemRet.ORSalesPurchase.SalesAndPurchase != null)
				{
					if (ItemRet.ORSalesPurchase.SalesAndPurchase.SalesDesc != null)
					{
						itemN.SetDescription(ItemRet.ORSalesPurchase.SalesAndPurchase.SalesDesc.GetValue());
					}
				}

				ItemList.Add(itemN);

			}
		}

		/**
		 * <summary>This method searches through every item in <see cref="Items"/> to find 
		 * the BTI part string</summary>
		 * <param name="part">BTI part number to be searched</param>
		 * <returns>First instance of the string in format of [name, description]</returns>
		 */
		public string FindMPN(string part)
		{
			string foundNum = ""; // Part number (serial)
			string foundDesc = ""; // Description
			bool found = false;

			for (int i = 0; i < itemList.Count; ++i)
			{
				if (itemList[i].GetDescription().Contains(part) && IsOurPartNum(itemList[i].GetNumber()))
				{
					found = true;
					foundNum = itemList[i].GetNumber();
					foundDesc = itemList[i].GetDescription();
				}
				if (found == true) break;
			}
			
			return foundNum;
		}

		// same as above but with Serialized number
		public string FindSerialNumber(string part)
		{
			string foundNum = ""; // Part number (serial)
			string foundDesc = ""; // Description
			bool found = false;

			for (int i = 0; i < itemList.Count; ++i)
			{
				if (itemList[i].GetNumber() == part && IsOurPartNum(part))
				{
					found = true;
					foundNum = itemList[i].GetNumber();
					foundDesc = itemList[i].GetDescription();
				}
				if (found == true) break;
			}

			return foundNum;
		}

		private bool IsOurPartNum(string part)
		{
			Regex rgx = new Regex(@"^(?:\d-)?\d+$"); // if 1 or 2-dash number or just plain number. 
			if (rgx.IsMatch(part))
            {
                return true;
            }
            if (part.ToLower().Contains("spec"))
            {
                return true;
            }

			return false;
		}

		internal void GetNumberSet(ref SortedSet<int> sortedNumberSet)
		{
			foreach (IQuoteItem item in itemList)
			{
				string partNumString = item.GetNumber();
				string pattern = @"^1-(?<number>\d+).*?";
				Match match = Regex.Match(partNumString, pattern);

				if (match.Success)
				{
					partNumString = match.Groups["number"].Value;
					int partNum = int.Parse(partNumString);
					sortedNumberSet.Add(partNum);
				}
			}
		}
	}
}
