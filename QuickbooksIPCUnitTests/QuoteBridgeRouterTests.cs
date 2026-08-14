using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
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
        private const string SubmitQuoteGoldenSha256 =
            "a01f356449385cd17697a3fb6ef2b73eb06b80295fa3ba28fdd3396dc5e809fc";

        private static QuoteBridgeRouter RouterWith(Func<string, string> submitHandler)
        {
            return new QuoteBridgeRouter(Origin, Token, submitHandler);
        }

        private static QuoteBridgeRouter OkRouter()
        {
            // Stub the connector response as the bare {StatusCode,StatusMessage,Data} the CLI emits.
            return RouterWith(_ => "{\"StatusCode\":0,\"StatusMessage\":\"OK\",\"Data\":null}");
        }

        private static QuoteBridgeRouter QuoteNumberAdminRouter(
            Func<string> reconciliationHandler,
            Func<string> companyIdentityHandler)
        {
            return new QuoteBridgeRouter(
                Origin,
                Token,
                _ => "{}",
                () => "{}",
                _ => "{}",
                reconciliationHandler,
                companyIdentityHandler);
        }

        [TestCase("/quote-number-reconciliation")]
        [TestCase("/quickbooks-company-identity")]
        public void QuoteNumberAdmin_WrongTokenIsForbidden(string path)
        {
            BridgeHttpResponse response = QuoteNumberAdminRouter(
                () => throw new AssertionException("handler must not run"),
                () => throw new AssertionException("handler must not run"))
                .Route("GET", path, "wrong-token", null);

            Assert.AreEqual(403, response.StatusCode);
            StringAssert.DoesNotContain("handler must not run", response.Body);
        }

        [TestCase("POST", "/quote-number-reconciliation")]
        [TestCase("POST", "/quickbooks-company-identity")]
        public void QuoteNumberAdmin_OnlyGetIsAllowed(string method, string path)
        {
            BridgeHttpResponse response = QuoteNumberAdminRouter(
                () => throw new AssertionException("handler must not run"),
                () => throw new AssertionException("handler must not run"))
                .Route(method, path, Token, null);

            Assert.AreEqual(404, response.StatusCode);
            StringAssert.DoesNotContain("handler must not run", response.Body);
        }

        [Test]
        public void QuoteNumberAdmin_ReconciliationReturnsStrictSanitizedSchemaAndNumericEstimates()
        {
            string fingerprint = new string('a', 64);
            string json = QuoteNumberAdminHandler.HandleReconciliation(
                () => new QBStatusResponse<string>
                {
                    StatusCode = 0,
                    StatusMessage = @"invented C:\Sensitive\Company.qbw customer Acme",
                    Data = fingerprint
                },
                () => new QBStatusResponse<List<QBEstimateReference>>
                {
                    StatusCode = 0,
                    StatusMessage = "invented raw QBFC error",
                    Data = new List<QBEstimateReference>
                    {
                        new QBEstimateReference { Reference = "120050", TransactionId = "TXN-1" },
                        new QBEstimateReference { Reference = "TEST-ABC123", TransactionId = "TXN-2" },
                        new QBEstimateReference { Reference = "12A", TransactionId = "TXN-3" }
                    }
                },
                "synthetic-user");

            var payload = new JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(json);
            CollectionAssert.AreEquivalent(
                new[] { "schema_version", "windows_username", "company_fingerprint", "estimates" },
                payload.Keys);
            Assert.AreEqual(1, payload["schema_version"]);
            Assert.AreEqual("synthetic-user", payload["windows_username"]);
            Assert.AreEqual(fingerprint, payload["company_fingerprint"]);

            var estimates = (ArrayList)payload["estimates"];
            Assert.AreEqual(1, estimates.Count);
            var estimate = (Dictionary<string, object>)estimates[0];
            CollectionAssert.AreEquivalent(
                new[] { "reference", "transaction_id" }, estimate.Keys);
            Assert.AreEqual("120050", estimate["reference"]);
            Assert.AreEqual("TXN-1", estimate["transaction_id"]);
            StringAssert.DoesNotContain("Company.qbw", json);
            StringAssert.DoesNotContain("customer Acme", json);
            StringAssert.DoesNotContain("QBFC error", json);
        }

        [Test]
        public void QuoteNumberAdmin_CompanyIdentityReturnsOnlyBoundedIdentityFields()
        {
            string fingerprint = new string('b', 64);
            string json = QuoteNumberAdminHandler.HandleCompanyIdentity(
                () => new QBStatusResponse<string>
                {
                    StatusCode = 0,
                    StatusMessage = @"invented C:\Sensitive\Company.qbw",
                    Data = fingerprint
                },
                "synthetic-user");

            var payload = new JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(json);
            CollectionAssert.AreEquivalent(
                new[] { "schema_version", "windows_username", "company_fingerprint" },
                payload.Keys);
            Assert.AreEqual(1, payload["schema_version"]);
            Assert.AreEqual("synthetic-user", payload["windows_username"]);
            Assert.AreEqual(fingerprint, payload["company_fingerprint"]);
            StringAssert.DoesNotContain("Company.qbw", json);
        }

        [Test]
        public void QuoteNumberAdmin_ReconciliationRejectsCompanyChangeDuringScan()
        {
            int identityReads = 0;
            TestDelegate action = () => QuoteNumberAdminHandler.HandleReconciliation(
                () => new QBStatusResponse<string>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = ++identityReads == 1 ? new string('a', 64) : new string('b', 64)
                },
                () => new QBStatusResponse<List<QBEstimateReference>>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = new List<QBEstimateReference>()
                },
                "synthetic-user");

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(action);
            Assert.AreEqual(
                "QuickBooks quote-number reconciliation is unavailable.",
                error.Message);
        }

        [TestCase("/quote-number-reconciliation", "QuickBooks quote-number reconciliation is unavailable.")]
        [TestCase("/quickbooks-company-identity", "QuickBooks company identity is unavailable.")]
        public void QuoteNumberAdmin_FailuresUseFixedMessages(string path, string expectedMessage)
        {
            var router = QuoteNumberAdminRouter(
                () => throw new InvalidOperationException(
                    @"invented C:\Sensitive\Company.qbw customer Acme QBFC detail"),
                () => throw new InvalidOperationException(
                    @"invented C:\Sensitive\Company.qbw customer Acme QBFC detail"));

            BridgeHttpResponse response = router.Route("GET", path, Token, null);

            Assert.AreEqual(502, response.StatusCode);
            StringAssert.Contains(expectedMessage, response.Body);
            StringAssert.DoesNotContain("Company.qbw", response.Body);
            StringAssert.DoesNotContain("customer Acme", response.Body);
            StringAssert.DoesNotContain("QBFC detail", response.Body);
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

        [TestCase("GET", "/save-folder")]
        [TestCase("POST", "/save-folder/choose")]
        [TestCase("POST", "/save-folder/reset")]
        public void SaveFolder_WithValidToken_AuthorizesTransportHandler(
            string method,
            string path)
        {
            BridgeHttpResponse response = OkRouter().Route(method, path, Token, null);

            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual("{\"status\":\"authorized\"}", response.Body);
        }

        [Test]
        public void SaveFolder_WithoutValidToken_IsForbidden()
        {
            BridgeHttpResponse response = OkRouter().Route("GET", "/save-folder", null, null);

            Assert.AreEqual(403, response.StatusCode);
        }

        [Test]
        public void CommercialTerms_WithValidToken_ReturnsSanitizedCatalog()
        {
            var constructor = typeof(QuoteBridgeRouter).GetConstructor(new[]
            {
                typeof(string), typeof(string), typeof(Func<string, string>), typeof(Func<string>)
            });
            Assert.IsNotNull(constructor, "QuoteBridgeRouter must accept a commercial-terms handler");
            var router = (QuoteBridgeRouter)constructor.Invoke(new object[]
            {
                Origin,
                Token,
                new Func<string, string>(_ => "{}"),
                new Func<string>(() => "{\"schema_version\":1,\"credit_terms\":[\"Prepaid\"]}")
            });

            BridgeHttpResponse response = router.Route("GET", "/commercial-terms", Token, null);

            Assert.AreEqual(200, response.StatusCode);
            Assert.AreEqual(
                "{\"schema_version\":1,\"credit_terms\":[\"Prepaid\"]}",
                response.Body);
        }

        [Test]
        public void CommercialTerms_FailureDoesNotExposeRawQuickBooksError()
        {
            var router = new QuoteBridgeRouter(
                Origin,
                Token,
                _ => "{}",
                () => throw new InvalidOperationException(
                    "invented sensitive company-file path and connection detail"));

            BridgeHttpResponse response = router.Route(
                "GET", "/commercial-terms", Token, null);

            Assert.AreEqual(502, response.StatusCode);
            StringAssert.Contains("QuickBooks commercial terms are unavailable.", response.Body);
            StringAssert.DoesNotContain("company-file", response.Body);
            StringAssert.DoesNotContain("connection detail", response.Body);
        }

        [Test]
        public void CommercialTermsCatalog_SerializesOnlyNormalizedNamesAndRefreshTime()
        {
            Type handler = typeof(SubmitQuoteHandler).Assembly.GetType(
                "QuickBooksConnectorCore.CommercialTermsHandler");
            Assert.IsNotNull(handler, "CommercialTermsHandler must exist");
            var serialize = handler.GetMethod("SerializeCatalog");
            Assert.IsNotNull(serialize, "CommercialTermsHandler.SerializeCatalog must exist");

            string json = (string)serialize.Invoke(null, new object[]
            {
                new[] { " Net 30 ", "", "net 30", "Prepaid" },
                new[] { "UPS Ground", " Customer Pickup ", null },
                new DateTime(2026, 8, 13, 18, 0, 0, DateTimeKind.Utc)
            });

            Assert.AreEqual(
                "{\"schema_version\":1,\"credit_terms\":[\"Net 30\",\"Prepaid\"]," +
                "\"shipping_methods\":[\"Customer Pickup\",\"UPS Ground\"]," +
                "\"refreshed_at\":\"2026-08-13T18:00:00.0000000Z\"}",
                json);
        }

        [Test]
        public void CommercialTermsHandler_ReturnsSanitizedCatalogFromServiceResponse()
        {
            Func<QBStatusResponse<QBCommercialTermsCatalog>> fetch = () =>
                new QBStatusResponse<QBCommercialTermsCatalog>
                {
                    StatusCode = 0,
                    StatusMessage = "invented raw status must not cross bridge",
                    Data = new QBCommercialTermsCatalog
                    {
                        CreditTerms = new List<string> { "Prepaid" },
                        ShippingMethods = new List<string> { "UPS Ground" },
                        RefreshedAtUtc = new DateTime(
                            2026, 8, 13, 18, 0, 0, DateTimeKind.Utc)
                    }
                };
            var handle = typeof(CommercialTermsHandler).GetMethod(
                "Handle",
                new[] { typeof(Func<QBStatusResponse<QBCommercialTermsCatalog>>) });
            Assert.IsNotNull(handle, "CommercialTermsHandler.Handle(fetch) must exist");

            string json = (string)handle.Invoke(null, new object[] { fetch });

            Assert.AreEqual(
                "{\"schema_version\":1,\"credit_terms\":[\"Prepaid\"]," +
                "\"shipping_methods\":[\"UPS Ground\"]," +
                "\"refreshed_at\":\"2026-08-13T18:00:00.0000000Z\"}",
                json);
            StringAssert.DoesNotContain("raw status", json);
        }

        [Test]
        public void CustomerCommercialTerms_WithValidTokenReturnsOnlySanitizedTerms()
        {
            var router = new QuoteBridgeRouter(
                Origin,
                Token,
                _ => "{}",
                () => "{}",
                _ => "{\"schema_version\":1,\"credit_terms\":\"Net 30\"}");

            BridgeHttpResponse response = router.Route(
                "POST",
                "/customer-commercial-terms",
                Token,
                "{\"account_number\":\"EX-1042\",\"refresh\":true}");

            Assert.AreEqual(200, response.StatusCode);
            StringAssert.Contains("Net 30", response.Body);
            StringAssert.DoesNotContain("EX-1042", response.Body);
        }

        [Test]
        public void CustomerCommercialTerms_BlankAccountIsBadRequest()
        {
            var router = new QuoteBridgeRouter(
                Origin,
                Token,
                _ => "{}",
                () => "{}",
                CustomerCommercialTermsHandler.Handle);

            BridgeHttpResponse response = router.Route(
                "POST",
                "/customer-commercial-terms",
                Token,
                "{\"account_number\":\"\"}");

            Assert.AreEqual(400, response.StatusCode);
        }

        [Test]
        public void CustomerCommercialTermsHandler_SuppressesRawServiceStatus()
        {
            Func<string, bool, QBStatusResponse<QBCustomerCommercialTerms>> fetch =
                (account, refresh) => new QBStatusResponse<QBCustomerCommercialTerms>
                {
                    StatusCode = 0,
                    StatusMessage = "invented raw company detail",
                    Data = new QBCustomerCommercialTerms
                    {
                        CreditTerms = " Net 30 "
                    }
                };

            string json = CustomerCommercialTermsHandler.Handle(
                "EX-1042", true, fetch);

            Assert.AreEqual(
                "{\"schema_version\":1,\"credit_terms\":\"Net 30\"}",
                json);
            StringAssert.DoesNotContain("company detail", json);
            StringAssert.DoesNotContain("EX-1042", json);
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
        public void QuoteIdentity_SubmitQuoteSerializesReturnedTransactionIdentity()
        {
            string json = SubmitQuoteHandler.Handle(
                "{}",
                _ => new QBStatusResponse<QBQuoteUploadResult>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = new QBQuoteUploadResult
                    {
                        TransactionType = QBQuoteTransactionType.SalesOrder,
                        QuoteNumber = "120050",
                        TransactionId = "TXN-SO-1",
                        AssignedReference = "SO-9001",
                        Lines = new List<QBQuoteUploadResolvedLine>()
                    }
                });

            StringAssert.Contains("\"TransactionId\":\"TXN-SO-1\"", json);
            StringAssert.Contains("\"AssignedReference\":\"SO-9001\"", json);
        }

        [Test]
        public void SubmitQuote_RealHandlerAndRouterMatchVersionedGoldenContract()
        {
            string goldenPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Fixtures",
                "submit_quote_success_v1.json");
            string golden = File.ReadAllText(goldenPath).Trim();
            using (SHA256 sha = SHA256.Create())
            {
                string hash = BitConverter.ToString(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(golden)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
                Assert.AreEqual(SubmitQuoteGoldenSha256, hash);
            }

            string bare = null;
            Func<QBQuoteUploadRequest, QBStatusResponse<QBQuoteUploadResult>> submit =
                _ => new QBStatusResponse<QBQuoteUploadResult>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = new QBQuoteUploadResult
                    {
                        TransactionType = QBQuoteTransactionType.Estimate,
                        CustomerName = "Invented Customer",
                        QuoteNumber = "120050",
                        TransactionId = "SYNTH-TXN-1",
                        AssignedReference = "120050",
                        Lines = new List<QBQuoteUploadResolvedLine>()
                    }
                };
            var router = RouterWith(body =>
            {
                bare = SubmitQuoteHandler.Handle(body, submit);
                return bare;
            });

            BridgeHttpResponse response = router.Route(
                "POST", "/submit-quote", Token, "{}");

            Assert.AreEqual(golden, bare);
            Assert.AreEqual("{\"response\":" + golden + "}", response.Body);
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
