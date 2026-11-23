using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Web.Script.Serialization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace QuickBooksServiceHost
{
    internal sealed class OpportunityWatcher : IDisposable
    {
        private readonly string company;
        private readonly Uri endpoint;
        private readonly Uri checkEndpoint;
        private readonly DateTime since;
        private readonly HttpClient http;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly Thread thread;
        private string lastStatus;
        private static readonly Regex Marker = new Regex(@"(?<![A-Za-z0-9])WB-[a-f0-9]{32}(?![A-Za-z0-9])");

        public static OpportunityWatcher Start()
        {
            string file = Environment.GetEnvironmentVariable("QB_OPPORTUNITY_COMPANY");
            if (string.IsNullOrWhiteSpace(file)) return null;
            string url = Environment.GetEnvironmentVariable("QB_OPPORTUNITY_WORKBENCH");
            string token = Environment.GetEnvironmentVariable("QUOTE_MODULEV2_QB_CONVERSION_TOKEN");
            string start = Environment.GetEnvironmentVariable("QB_OPPORTUNITY_SINCE");
            Uri uri;
            DateTime date;
            if (!Path.IsPathRooted(file) || !Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(token) ||
                !DateTime.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date) || date.Year < 1900)
            {
                Console.Error.WriteLine("Opportunity sync disabled. Verify the company, Workbench URL, start date and token.");
                return null;
            }
            return new OpportunityWatcher(Path.GetFullPath(file), uri, token, date);
        }

        private OpportunityWatcher(string file, Uri uri, string token, DateTime date)
        {
            company = file;
            endpoint = new Uri(uri, "/api/opportunities/quickbooks-conversion");
            checkEndpoint = new Uri(uri, "/api/opportunities/quickbooks-check");
            since = date;
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = uri.Scheme == "https" });
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.Add("X-Workbench-Conversion-Token", token);
            thread = new Thread(Run) { IsBackground = true, Name = "QuickBooks opportunity sync" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Status(string message)
        {
            if (lastStatus == message) return;
            lastStatus = message;
            Console.WriteLine("Opportunity sync: " + message);
        }

        private void Run()
        {
            DateTime cursor = since;
            var elapsed = new Stopwatch();
            var serializer = new JavaScriptSerializer();
            do
            {
                try
                {
                    using (var response = http.GetAsync(checkEndpoint, stop.Token).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        var state = serializer.Deserialize<Dictionary<string, object>>(
                            response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                        long requested = Convert.ToInt64(state["requested"]);
                        bool pending = requested > Convert.ToInt64(state["completed"]);
                        if (elapsed.IsRunning && elapsed.Elapsed < TimeSpan.FromMinutes(5) && !pending)
                            continue;
                        DateTime latest = Scan(cursor);
                        using (var content = new StringContent(serializer.Serialize(new { completed = requested }),
                                                               Encoding.UTF8, "application/json"))
                        using (var receipt = http.PostAsync(checkEndpoint, content, stop.Token).GetAwaiter().GetResult())
                            receipt.EnsureSuccessStatusCode();
                        elapsed.Restart();
                        Console.WriteLine("Opportunity sync: completed " + (pending ? "requested" : "scheduled") + " check.");
                        cursor = latest > since ? latest.AddDays(-1) : since;
                    }
                    Status("connected");
                }
                catch (COMException) { Status("waiting for QuickBooks"); }
                catch (Exception) when (!stop.IsCancellationRequested)
                {
                    Status("sync unavailable; retrying");
                }
                catch (Exception) when (stop.IsCancellationRequested) { break; }
            } while (!stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(15)));
        }

        private DateTime Scan(DateTime from)
        {
            dynamic rp = Activator.CreateInstance(Type.GetTypeFromProgID("QBXMLRP2.RequestProcessor", true));
            string ticket = null;
            bool opened = false;
            try
            {
                rp.OpenConnection2("", "Workbench sample conversion test", 1);
                opened = true;
                ticket = rp.BeginSession("", 2);
                if (!string.Equals((string)rp.GetCurrentCompanyFileName(ticket), company,
                                   StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Configured company is not open.");
                string iterator = null;
                DateTime latest = from;
                do
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var query = new XElement("SalesOrderQueryRq",
                        new XAttribute("iterator", iterator == null ? "Start" : "Continue"),
                        iterator == null ? null : new XAttribute("iteratorID", iterator),
                        new XElement("MaxReturned", 100),
                        iterator == null ? new XElement("ModifiedDateRangeFilter",
                            new XElement("FromModifiedDate", from.ToString("yyyy-MM-dd'T'00:00:00", CultureInfo.InvariantCulture))) : null,
                        new XElement("IncludeLineItems", "true"));
                    string request = "<?xml version=\"1.0\"?><?qbxml version=\"13.0\"?>" +
                        new XElement("QBXML", new XElement("QBXMLMsgsRq", new XAttribute("onError", "stopOnError"), query));
                    var response = XDocument.Parse((string)rp.ProcessRequest(ticket, request))
                        .Descendants("SalesOrderQueryRs").Single();
                    string code = (string)response.Attribute("statusCode");
                    if (code != "0" && code != "1") throw new InvalidDataException("Order query failed.");
                    foreach (var order in response.Elements("SalesOrderRet"))
                    {
                        var modified = DateTimeOffset.Parse((string)order.Element("TimeModified"), CultureInfo.InvariantCulture).Date;
                        if (modified > latest) latest = modified;
                        Notify(order);
                    }
                    if (((int?)response.Attribute("iteratorRemainingCount") ?? 0) == 0) break;
                    iterator = (string)response.Attribute("iteratorID");
                    if (string.IsNullOrWhiteSpace(iterator)) throw new InvalidDataException("Missing iterator.");
                } while (true);
                return latest;
            }
            finally
            {
                try { if (ticket != null) rp.EndSession(ticket); }
                finally
                {
                    try { if (opened) rp.CloseConnection(); }
                    finally { Marshal.FinalReleaseComObject((object)rp); }
                }
            }
        }

        private void Notify(XElement order)
        {
            var ids = new HashSet<string>();
            foreach (var field in order.Elements("Memo").Concat(order.Descendants("SalesOrderLineRet").Elements("Desc")))
                foreach (Match match in Marker.Matches(field.Value)) ids.Add(match.Value);
            string txn = (string)order.Element("TxnID");
            if (ids.Count != 1 || string.IsNullOrEmpty(txn)) return;
            string key;
            using (var hash = SHA256.Create())
                key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    company.ToUpperInvariant() + "|" + txn))).Replace("-", "").ToLowerInvariant();
            string json = "{\"marker\":\"" + ids.Single() + "\",\"order_key\":\"" + key + "\"}";
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = http.PostAsync(endpoint, content, stop.Token).GetAwaiter().GetResult())
                response.EnsureSuccessStatusCode();
        }

        public void Dispose()
        {
            stop.Cancel();
            if (thread.Join(TimeSpan.FromSeconds(3)))
            {
                http.Dispose();
                stop.Dispose();
            }
        }
    }
}
