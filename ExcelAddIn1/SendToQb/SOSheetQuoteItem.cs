namespace ExcelAddIn1
{
	internal class SOSheetQuoteItem : IQuoteItem
	{
		IQuoteItem internalQuote;
		string _override;

		// Gives both Excel Interface and Data
		public SOSheetQuoteItem(IQuoteItem quoteItem, int row)
		{
			internalQuote = quoteItem;
			Row = row;
		}

		public int Row { get; set; }

		// Mirrors the resolver's override semantics (trimmed; whitespace-only counts as no
		// override) so the audit record's number matches what QuickBooks actually received.
		public string GetInputNumber() => string.IsNullOrWhiteSpace(GetOverride()) ? GetNumber() : GetOverride().Trim();

		// Sets Excel Interface
		public string GetNumber() { return internalQuote.GetNumber(); }
		public void SetNumber(string value) { internalQuote.SetNumber(value); }
		public string GetDescription() { return internalQuote.GetDescription(); }
		public void SetDescription(string value) { internalQuote.SetDescription(value); }
		public double GetRate() { return internalQuote.GetRate(); }
		public void SetRate(double value) { internalQuote.SetRate(value); }
		public int GetQuantity() { return internalQuote.GetQuantity(); }
		public void SetQuantity(int value) { internalQuote.SetQuantity(value); }
		public string GetOverride() { return _override; }
		public void SetOverride(string value) { _override = value; }
		public bool GetIsActive() { return internalQuote.GetIsActive(); }
		public void SetIsActive(bool value) { internalQuote.SetIsActive(value); }
	}
}
