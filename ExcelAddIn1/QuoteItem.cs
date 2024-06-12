using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExcelAddIn1
{
	internal interface IQuoteItem
	{
		string GetNumber();
		void SetNumber(string value);
		string GetDescription();
		void SetDescription(string value);
		string GetRate();
		void SetRate(string value);
		string GetQuantity();
		void SetQuantity(string value);
	}

	internal interface IQuote
	{
		string GetQuoteNumber();
		void SetQuoteNumber(int value);
		string GetCustomer();
		void SetCustomer(string value);
		List<IQuoteItem> GetItems();
		void SetItems(List<IQuoteItem> value);
	}

	internal class BaseQuote : IQuoteItem
	{
		protected string _number;
		protected string _description;
		protected string _rate;
		protected string _quantity;

		public string GetNumber() => _number;
		public void SetNumber(string value) => _number = value;
		public string GetDescription() => _description;
		public void SetDescription(string value) => _description = value;
		public string GetRate() => _rate;
		public void SetRate(string value) => _rate = value;
		public string GetQuantity() => _quantity;
		public void SetQuantity(string value) => _quantity = value;
	}
}
