using System;
using System.Collections.Generic;
using NUnit.Framework;
using QuickBooksConnectorCore;
using QuickBooksIPCContracts;

namespace QuickbooksIPCUnitTests
{
    /// <summary>
    /// Covers the QuickBooks localhost bridge routing/security policy without a live socket or
    /// QuickBooks: the WCF submit is stubbed via the router's injected submit handler.
    /// </summary>
    [TestFixture]
    public class QuoteBridgeRouterTests
    {
        private const string Origin = "http://APPSRV01:8742";
        private const string Token = "s3cr3t-token";

        private static QuoteBridgeRouter RouterWith(Func<string, string> submitHandler)
        {
            return new QuoteBridgeRouter(Origin, Token, submitHandler);
        }

        private static QuoteBridgeRouter OkRouter()
        {
            // Stub the connector response as the bare {StatusCode,StatusMessage,Data} the CLI emits.
            return RouterWith(_ => "{\"StatusCode\":0,\"StatusMessage\":\"OK\",\"Data\":null}");
        }

        [Test]
        public void MissingToken_IsForbidden()
        {
            BridgeHttpResponse response = OkRouter().Route("POST", "/submit-quote", null, "{}");

            Assert.AreEqual(403, response.StatusCode);
            StringAssert.Contains("forbidden", response.Body);
        }

        [Test]
        public void BlankToken_IsForbidden()
        {
            BridgeHttpResponse response = OkRouter().Route("POST", "/submit-quote", "", "{}");

            Assert.AreEqual(403, response.StatusCode);
        }

        [Test]
        public void WrongToken_IsForbidden()
        {
            BridgeHttpResponse response = OkRouter().Route("POST", "/submit-quote", "nope", "{}");

            Assert.AreEqual(403, response.StatusCode);
        }

        [Test]
        public void BridgeWithNoConfiguredToken_RejectsEvenAMatchingEmptyHeader()
        {
            var router = new QuoteBridgeRouter(Origin, string.Empty, _ => "{}");

            BridgeHttpResponse response = router.Route("GET", "/ping", string.Empty, null);

            Assert.AreEqual(403, response.StatusCode);
        }

        [Test]
        public void Options_IsNoContentWithCorsHeaders_AndSkipsTokenCheck()
        {
            BridgeHttpResponse response = OkRouter().Route("OPTIONS", "/submit-quote", null, null);

            Assert.AreEqual(204, response.StatusCode);
            Assert.IsNull(response.Body);
            Assert.AreEqual(Origin, response.Headers["Access-Control-Allow-Origin"]);
            Assert.AreEqual("content-type, x-qb-bridge-token", response.Headers["Access-Control-Allow-Headers"]);
            Assert.AreEqual("GET, POST, OPTIONS", response.Headers["Access-Control-Allow-Methods"]);
        }

        [Test]
        public void CorsHeaders_ArePresentOnEveryResponse()
        {
            BridgeHttpResponse forbidden = OkRouter().Route("POST", "/submit-quote", null, "{}");

            Assert.AreEqual(Origin, forbidden.Headers["Access-Control-Allow-Origin"]);
            Assert.AreEqual("content-type, x-qb-bridge-token", forbidden.Headers["Access-Control-Allow-Headers"]);
            Assert.AreEqual("GET, POST, OPTIONS", forbidden.Headers["Access-Control-Allow-Methods"]);
        }

        [Test]
        public void Ping_WithValidToken_ReturnsContractVersion()
        {
            BridgeHttpResponse response = OkRouter().Route("GET", "/ping", Token, null);

            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual("{\"reply\":\"ok\",\"contract_version\":1}", response.Body);
        }

        [Test]
        public void SaveFile_WithoutValidToken_IsForbidden()
        {
            BridgeHttpResponse response = OkRouter().Route("POST", "/save-file", null, null);

            Assert.AreEqual(403, response.StatusCode);
        }

        [Test]
        public void SaveFile_WithValidToken_AuthorizesTransportHandler()
        {
            BridgeHttpResponse response = OkRouter().Route("POST", "/save-file", Token, null);

            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual("{\"status\":\"authorized\"}", response.Body);
        }

        [Test]
        public void SubmitQuote_WithValidToken_WrapsConnectorResponse()
        {
            string capturedBody = null;
            var router = RouterWith(body =>
            {
                capturedBody = body;
                return "{\"StatusCode\":0,\"StatusMessage\":\"OK\",\"Data\":null}";
            });

            BridgeHttpResponse response = router.Route("POST", "/submit-quote", Token, "{\"QuoteNumber\":\"26-1042\"}");

            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual("{\"response\":{\"StatusCode\":0,\"StatusMessage\":\"OK\",\"Data\":null}}", response.Body);
            Assert.AreEqual("{\"QuoteNumber\":\"26-1042\"}", capturedBody);
        }

        [Test]
        public void SubmitQuote_RoutesThroughSharedHandler_OnRealContractPayload()
        {
            // Wire the router to the SAME SubmitQuoteHandler the CLI uses, stubbing only the WCF call.
            QBQuoteUploadRequest received = null;
            Func<QBQuoteUploadRequest, QBStatusResponse<QBQuoteUploadResult>> submit = request =>
            {
                received = request;
                return new QBStatusResponse<QBQuoteUploadResult>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = new QBQuoteUploadResult
                    {
                        TransactionType = QBQuoteTransactionType.Estimate,
                        CustomerName = "Acme Inc.",
                        QuoteNumber = "26-1042",
                        Lines = new List<QBQuoteUploadResolvedLine>()
                    }
                };
            };
            var router = RouterWith(body => SubmitQuoteHandler.Handle(body, submit));

            const string payload =
                "{\"TransactionType\":\"Estimate\",\"QuoteNumber\":\"26-1042\"," +
                "\"CustomerAccountNumber\":\"11375\",\"CustomerName\":\"Acme Inc.\"," +
                "\"CustomerPO\":\"PO-77\",\"DueDate\":\"2026-07-15\"," +
                "\"Lines\":[{\"Description\":\"BB/123, bend block\",\"Quantity\":1,\"Rate\":250.0,\"OverrideNumber\":\"\"}]}";

            BridgeHttpResponse response = router.Route("POST", "/submit-quote", Token, payload);

            Assert.AreEqual(200, response.StatusCode);
            StringAssert.StartsWith("{\"response\":", response.Body);
            StringAssert.Contains("\"StatusMessage\":\"OK\"", response.Body);
            Assert.IsNotNull(received);
            Assert.AreEqual("26-1042", received.QuoteNumber);
            Assert.AreEqual(QBQuoteTransactionType.Estimate, received.TransactionType);
            Assert.AreEqual(1, received.Lines.Count);
        }

        [Test]
        public void SubmitQuote_MalformedBody_IsBadRequest()
        {
            Func<QBQuoteUploadRequest, QBStatusResponse<QBQuoteUploadResult>> submit =
                _ => throw new AssertionException("submit must not be reached for a malformed body");
            var router = RouterWith(body => SubmitQuoteHandler.Handle(body, submit));

            BridgeHttpResponse response = router.Route("POST", "/submit-quote", Token, "this is not json {");

            Assert.AreEqual(400, response.StatusCode);
            StringAssert.Contains("bad_request", response.Body);
        }

        [Test]
        public void SubmitQuote_BridgeTransportFailure_IsBadGateway()
        {
            var router = RouterWith(_ => throw new InvalidOperationException("pipe is down"));

            BridgeHttpResponse response = router.Route("POST", "/submit-quote", Token, "{}");

            Assert.AreEqual(502, response.StatusCode);
            StringAssert.Contains("bridge_error", response.Body);
        }

        [Test]
        public void UnknownRoute_IsNotFound()
        {
            BridgeHttpResponse response = OkRouter().Route("GET", "/nope", Token, null);

            Assert.AreEqual(404, response.StatusCode);
            StringAssert.Contains("not_found", response.Body);
        }
    }
}
