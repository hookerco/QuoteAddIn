using System;

namespace QuickBooksConnectorCore
{
    /// <summary>
    /// Thrown when a submit-quote payload cannot be parsed into a
    /// <see cref="QuickBooksIPCContracts.QBQuoteUploadRequest"/>. Callers map this to a
    /// client error (CLI exit code 2 / bridge HTTP 400) rather than a server fault.
    /// </summary>
    public class QuoteRequestParseException : Exception
    {
        public QuoteRequestParseException(string message)
            : base(message)
        {
        }
    }
}
