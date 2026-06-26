// QuickBooksServiceHost/QbBridge.cs
using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using QuickBooksConnectorCore;

namespace QuickBooksServiceHost
{
    /// <summary>
    /// The QuickBooks localhost bridge: an <see cref="HttpListener"/> bound to 127.0.0.1 that
    /// lets the centrally-hosted web quote module POST a final send from the estimator's browser
    /// into their open QuickBooks Desktop seat. It is just another local client of the same
    /// NetNamedPipe service this host already runs, so there is no service-lifetime entanglement:
    /// each request opens its own <see cref="QuickBooksServiceConnection"/> via the shared
    /// <see cref="SubmitQuoteHandler"/> — the exact pipeline the connector CLI uses.
    ///
    /// Security is defense-in-depth (financial write path): loopback-only bind, a CORS allowlist
    /// for the single configured app-server origin, and a shared <c>X-QB-Bridge-Token</c>.
    /// </summary>
    internal sealed class QbBridge
    {
        private readonly HttpListener _listener;
        private readonly QuoteBridgeRouter _router;

        private QbBridge(HttpListener listener, QuoteBridgeRouter router)
        {
            _listener = listener;
            _router = router;
        }

        /// <summary>
        /// Bind the loopback listener and start serving on background threads. Throws if the
        /// port cannot be bound; callers should treat a bridge failure as non-fatal to the host.
        /// </summary>
        internal static QbBridge Start()
        {
            int port = ReadInt("QB_BRIDGE_PORT", 8788);
            string origin = ReadString("QB_BRIDGE_ORIGIN", "http://APPSRV01:8742");
            string token = ReadString("QB_BRIDGE_TOKEN", string.Empty);

            var router = new QuoteBridgeRouter(origin, token, SubmitQuoteHandler.Handle);

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var bridge = new QbBridge(listener, router);
            ThreadPool.QueueUserWorkItem(_ => bridge.Loop());

            Console.WriteLine($"QuickBooks localhost bridge listening on http://127.0.0.1:{port}/ (origin: {origin}).");
            if (string.IsNullOrEmpty(token))
            {
                Console.Error.WriteLine("WARNING: QB_BRIDGE_TOKEN is not set; the bridge will reject every request with 403.");
            }

            return bridge;
        }

        internal void Stop()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
                // Best effort on shutdown.
            }
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    break; // listener stopped/disposed
                }

                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                string body = ReadBody(ctx.Request);
                string token = ctx.Request.Headers[QuoteBridgeRouter.TokenHeaderName];

                BridgeHttpResponse response = _router.Route(
                    ctx.Request.HttpMethod,
                    ctx.Request.Url.AbsolutePath,
                    token,
                    body);

                Write(ctx.Response, response);
            }
            catch (Exception ex)
            {
                try
                {
                    var res = ctx.Response;
                    res.StatusCode = 502;
                    res.ContentType = "application/json";
                    byte[] bytes = Encoding.UTF8.GetBytes(
                        "{\"kind\":\"bridge_error\",\"message\":\"" + Escape(ex.Message) + "\"}");
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                    res.Close();
                }
                catch
                {
                    // Nothing more we can do for this request.
                }
            }
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            if (!request.HasEntityBody)
            {
                return string.Empty;
            }

            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void Write(HttpListenerResponse res, BridgeHttpResponse response)
        {
            foreach (var header in response.Headers)
            {
                res.AddHeader(header.Key, header.Value);
            }

            res.StatusCode = response.StatusCode;

            if (response.Body == null)
            {
                res.Close();
                return;
            }

            res.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(response.Body);
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        private static string ReadString(string name, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                value = ConfigurationManager.AppSettings[name];
            }

            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static int ReadInt(string name, int fallback)
        {
            string value = ReadString(name, null);
            return int.TryParse(value, out int parsed) ? parsed : fallback;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }
    }
}
