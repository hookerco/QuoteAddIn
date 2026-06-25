using ExcelAddIn1.SendToQb;
using Interop.QBFC14;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Forms.VisualStyles;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using QuickBooksIPCContracts;
using System.CodeDom;
using System.ServiceModel.Configuration;
using System.Diagnostics;
using System.Security.Cryptography;

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

			QBConnector qbConnector = new QBConnector();
			var response = qbConnector.Client.GetAllItems();
			if (response.StatusCode != 0)
			{
				return false;
			}

			foreach (var item in response.Data)
			{
				IQuoteItem quoteItem = new BaseQuoteItem();
				// Upper because some of the same item have different capitalizations in quickbooks
				quoteItem.SetNumber(item.Number.ToUpper());
				quoteItem.SetDescription(item.Description.ToUpper());
				quoteItem.SetIsActive(item.Active);
                itemList.Add(quoteItem);
			}

			return true;
		}

		/**
		 * <summary>This method searches through every item in <see cref="Items"/> to find 
		 * the BTI part string</summary>
		 * <param name="part">BTI part number to be searched</param>
		 * <returns>First instance of the string in format of [name, description]</returns>
		 */
		public string FindMPN(string part)
		{
			return FindMPN(part, "");
		}

		public string FindMPN(string part, string preferredDescriptionPartNumber)
		{
			part = part.ToUpper(); // Upper because some of the same item have different capitalizations in quickbooks
			List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>();

			for (int i = 0; i < itemList.Count; ++i)
			{
				if (DescriptionContainsLookup(itemList[i].GetDescription(), part) && IsOurPartNum(itemList[i].GetNumber()))
				{
					if (itemList[i].GetIsActive() == false)
                    {
						Debug.WriteLine("Inactive part #: " + itemList[i].GetNumber());
						continue;
                    }
					candidates.Add(new ItemLookupCandidate(itemList[i].GetNumber(), itemList[i].GetDescription(), i));
				}
			}
			
			return ItemLookupCandidateSelector.SelectBestItemNumber(candidates, part, preferredDescriptionPartNumber);
		}

		// same as above but with Serialized number
		public string FindSerialNumber(string part)
		{
			part = part.ToUpper(); // Upper because some of the same item have different capitalizations in quickbooks
            string foundNum = ""; // Part number (serial)
			string foundDesc = ""; // Description
			bool found = false;

			for (int i = 0; i < itemList.Count; ++i)
			{
                if (itemList[i].GetIsActive() == false)
                {
                    Debug.WriteLine("Inactive part #: " + itemList[i].GetNumber());
                    continue;
                }
                if (itemList[i].GetNumber() == part && !IsOurPartNum(part)) 
				{
					Debug.WriteLine("Not part #: " + part);
				}
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

		// Wiper lookups collapse the key to a bare EDP number, which is then matched against item
		// descriptions. Match a numeric key as a whole number so EDP "3819" does not match inside a
		// longer run of digits such as "38190" or another item's "EDP#13819".
		private static bool DescriptionContainsLookup(string description, string lookup)
		{
			if (string.IsNullOrEmpty(lookup))
			{
				return false;
			}

			if (Regex.IsMatch(lookup, @"^\d+$"))
			{
				return Regex.IsMatch(description ?? "", @"(?<!\d)" + Regex.Escape(lookup) + @"(?!\d)");
			}

			return (description ?? "").Contains(lookup);
		}

		private bool IsOurPartNum(string part)
		{
			Regex rgx = new Regex(@"^(?:\d-)?\d+$"); // if 1 or 2-dash number or just plain number. 
			if (rgx.IsMatch(part))
            {
                return true;
            }
            if (part.ToUpper().Contains("SPEC"))
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
