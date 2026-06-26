using System.Collections.Generic;

namespace QuickBooksConnectorCore
{
    /// <summary>
    /// A transport-agnostic HTTP response produced by <see cref="QuoteBridgeRouter"/>.
    /// The host's <c>HttpListener</c> wiring copies <see cref="Headers"/> and
    /// <see cref="Body"/> onto the real response; tests assert on it directly.
    /// </summary>
    public sealed class BridgeHttpResponse
    {
        public BridgeHttpResponse(int statusCode, string body, IReadOnlyDictionary<string, string> headers)
        {
            StatusCode = statusCode;
            Body = body;
            Headers = headers ?? new Dictionary<string, string>();
        }

        public int StatusCode { get; }

        /// <summary>JSON response body, or <c>null</c> for an empty (204) response.</summary>
        public string Body { get; }

        /// <summary>Headers (including CORS) to apply to every response.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }
    }
}
