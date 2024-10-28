using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using QuickBooksIPCContracts;

namespace ExcelAddIn1
{
	public interface IQuoteItem
	{
		string GetNumber();
		void SetNumber(string value);
		string GetDescription();
		void SetDescription(string value);
		double GetRate();
		void SetRate(double value);
		int GetQuantity();
		void SetQuantity(int value);
		bool GetIsActive();
        void SetIsActive(bool value);
    }

	public interface IQuote
	{
		string GetQuoteNumber();
		void SetQuoteNumber(int value);
		string GetCustomer();
		void SetCustomer(string value);
		List<IQuoteItem> GetItems();
		void SetItems(List<IQuoteItem> value);
	}

	public class BaseQuoteItem : IQuoteItem
	{
		protected string _number;
		protected string _description;
		protected double _rate;
		protected int _quantity;
        protected bool _isActive;

        public string GetNumber() => _number;
		public void SetNumber(string value) => _number = value;
		public string GetDescription() => _description;
		public void SetDescription(string value) => _description = value;
		public double GetRate() => _rate;
		public void SetRate(double value) => _rate = value;
		public int GetQuantity() => _quantity;
		public void SetQuantity(int value) => _quantity = value;
        public bool GetIsActive() => _isActive;
        public void SetIsActive(bool value) => _isActive = value;
    }

    public class BaseQuote : IQuote
    {
        private string _quoteNumber;
        private string _customer;
        private List<IQuoteItem> _items;

        public string GetQuoteNumber() => _quoteNumber;
        public void SetQuoteNumber(int value) => _quoteNumber = value.ToString();
        public string GetCustomer() => _customer;
        public void SetCustomer(string value) => _customer = value;
        public List<IQuoteItem> GetItems() => _items;
        public void SetItems(List<IQuoteItem> value) => _items = value;
    }
}
