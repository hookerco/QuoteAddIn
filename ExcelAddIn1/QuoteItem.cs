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
		double GetRate();
		void SetRate(double value);
		int GetQuantity();
		void SetQuantity(int value);
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
		protected double _rate;
		protected int _quantity;

		public string GetNumber() => _number;
		public void SetNumber(string value) => _number = value;
		public string GetDescription() => _description;
		public void SetDescription(string value) => _description = value;
		public double GetRate() => _rate;
		public void SetRate(double value) => _rate = value;
		public int GetQuantity() => _quantity;
		public void SetQuantity(int value) => _quantity = value;
	}
}
