using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace QuickBooksConnectorCore
{
    /// <summary>
    /// Pure routing + security policy for the QuickBooks localhost bridge, independent of any
    /// HTTP transport. The host (<c>HttpListener</c>) feeds it the request method, path, token
    /// header, and body, and writes back the <see cref="BridgeHttpResponse"/>.
    ///
    /// Policy (contract_version 1):
    ///   - CORS headers are echoed on every response so a preflighted POST from the configured
    ///     app-server origin (and only that origin) can drive the bridge.
    ///   - <c>OPTIONS</c> short-circuits to 204 (preflight) before any token check.
    ///   - Every other route requires the shared <c>X-QB-Bridge-Token</c>; a missing, empty, or
    ///     mismatched token is 403.
    ///   - <c>GET /ping</c> → 200 {"reply":"ok","contract_version":1}.
    ///   - <c>POST /submit-quote</c> → 200 {"response": &lt;bare conn response&gt;}; a malformed
    ///     body is 400, a bridge/transport failure is 502. QuickBooks success *and* failure are
    ///     both 200 (the non-zero StatusCode carries the failure).
    /// </summary>
    public sealed class QuoteBridgeRouter
    {
        public const int ContractVersion = 1;
        public const string TokenHeaderName = "X-QB-Bridge-Token";

        private static readonly JavaScriptSerializer JsonSerializer = new JavaScriptSerializer();

        private readonly string _origin;
        private readonly string _token;
        private readonly Func<string, string> _submitHandler;

        /// <param name="origin">The single allowed app-server origin echoed as Access-Control-Allow-Origin.</param>
        /// <param name="token">The shared secret required in the <c>X-QB-Bridge-Token</c> header.</param>
        /// <param name="submitHandler">Maps a request body to the bare connector response JSON
        /// (see <see cref="SubmitQuoteHandler.Handle(string)"/>); may throw
        /// <see cref="QuoteRequestParseException"/> for a malformed body.</param>
        public QuoteBridgeRouter(string origin, string token, Func<string, string> submitHandler)
        {
            _origin = origin ?? string.Empty;
            _token = token ?? string.Empty;
            _submitHandler = submitHandler ?? throw new ArgumentNullException(nameof(submitHandler));
        }

        public BridgeHttpResponse Route(string method, string path, string token, string body)
        {
            var headers = CorsHeaders();

            if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                return new BridgeHttpResponse(204, null, headers);
            }

            if (string.IsNullOrEmpty(_token) || !string.Equals(token, _token, StringComparison.Ordinal))
            {
                return Json(403, headers, ErrorBody("forbidden", "bad or missing token"));
            }

            if (path == "/ping")
            {
                return Json(200, headers, "{\"reply\":\"ok\",\"contract_version\":" + ContractVersion + "}");
            }

            if (path == "/submit-quote" && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string connResponseJson = _submitHandler(body);
                    return Json(200, headers, "{\"response\":" + connResponseJson + "}");
                }
                catch (QuoteRequestParseException ex)
                {
                    return Json(400, headers, ErrorBody("bad_request", ex.Message));
                }
                catch (Exception ex)
                {
                    return Json(502, headers, ErrorBody("bridge_error", ex.Message));
                }
            }

            return Json(404, headers, ErrorBody("not_found", "unknown route"));
        }

        private Dictionary<string, string> CorsHeaders()
        {
            return new Dictionary<string, string>
            {
                { "Access-Control-Allow-Origin", _origin },
                { "Access-Control-Allow-Headers", "content-type, x-qb-bridge-token" },
                { "Access-Control-Allow-Methods", "GET, POST, OPTIONS" }
            };
        }

        private static BridgeHttpResponse Json(int statusCode, IReadOnlyDictionary<string, string> headers, string body)
        {
            return new BridgeHttpResponse(statusCode, body, headers);
        }

        private static string ErrorBody(string kind, string message)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                { "kind", kind },
                { "message", message ?? string.Empty }
            });
        }
    }
}
