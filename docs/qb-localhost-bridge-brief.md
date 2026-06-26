# QuickBooks Localhost Bridge — `QuoteAddIn` implementation brief

**Status:** ready to implement. The Python/webgui half is already built and merged
(in the `quote_modulev2` repo). This brief is the **.NET half** — the only thing
between today and "the centrally-hosted web quote module can send to QuickBooks."

**Canonical design (other repo, source of truth for the contract):**
`quote_modulev2/docs/superpowers/specs/2026-06-25-qb-localhost-bridge-design.md`.
This brief supersedes that spec's §7 reference code where they differ — it is
written against the **actual** `QuoteAddIn` code (verified 2026-06-25), which is
cleaner than the spec assumed.

---

## 1. Why this exists

The new web quote module (`quote_modulev2`) is hosted centrally on the app
server. QuickBooks sends must run inside an estimator's **open QB Desktop
session** (their seat) — which lives on their workstation, not the app server.
So the central webgui can't send server-side.

Solution: the estimator's **browser** (already on their workstation) POSTs the
final send to a `127.0.0.1` HTTP endpoint on that same machine. That endpoint —
the **bridge** — reuses the existing local QuickBooks plumbing to write to the
open seat. The browser is the router; its `localhost` is the machine with the
seat.

The webgui already does this: in `local-bridge` mode it stages centrally, then
POSTs the draft to `http://127.0.0.1:8788/submit-quote`. Right now that POST gets
connection-refused (no bridge) and the UI shows "QuickBooks bridge not
reachable." **This brief builds the listener that answers it.**

## 2. The good news: the bridge already exists as a CLI

`QuickBooksConnectorCli/Program.cs` **already does exactly what the bridge
needs** — it's what the Python connector spawns today:

1. Reads the `to_connector_payload()` JSON (handles `TransactionType` /
   `transaction_type` etc. via `NormalizeKey`).
2. `ParseQuoteUploadRequest` → `QBQuoteUploadRequest`.
3. Opens a `QuickBooksServiceConnection` (NetNamedPipe to
   `net.pipe://localhost/QuickBooksService`) and calls
   `Client.SubmitQuote(request)` → `QBStatusResponse<QBQuoteUploadResult>`.
4. `ToJsonResponse` → `{ StatusCode, StatusMessage, Data }`.

**The bridge is that pipeline, driven by an HTTP request body instead of stdin,
and writing to the HTTP response instead of stdout.** The WCF op is
`SubmitQuote` (`QuickBooksIPCContracts/ServiceContracts.cs:18`) — do **not** use
`AddEstimate`/`AddOrder` (those are the Excel ribbon's lower-level path) and do
**not** refactor `SendRequest.SendOrder` (irrelevant to this path).

### Recommended refactor (DRY)
Extract the reusable pieces of `QuickBooksConnectorCli/Program.cs` — namely
`ParseQuoteUploadRequest` (+ its `Parse*`/`Get*`/`NormalizeKey` helpers),
`ToJsonResponse`/`ToJsonResult`, and the `QuickBooksServiceConnection` class —
into a shared type, e.g. `QuickBooksConnectorCore.SubmitQuoteHandler`, in a
library both projects reference (`QBRequestLibrary` is a fine home, or a new
small project). Then:
- the CLI's `RunSubmitQuote()` calls `SubmitQuoteHandler.Handle(jsonString)`;
- the bridge handler calls the **same** `SubmitQuoteHandler.Handle(jsonString)`.

If extraction is too invasive for a first cut, duplicating the handful of static
methods into the host is acceptable — but the shared path is the goal so the CLI
and bridge can never diverge.

## 3. Where it lives

Add the `HttpListener` to **`QuickBooksServiceHost/Program.cs`** — the always-on
host process (the "service host exe" that already runs locally and hosts the
NetNamedPipe service). Start the listener on a background thread before the
existing `shutdownEvent.WaitOne()`, and stop it on shutdown. Each request opens
its own `QuickBooksServiceConnection` (same as the CLI) — the host serves the
pipe and the bridge is just another local client of it, so no service-lifetime
entanglement.

## 4. The contract (`contract_version: 1`) — do not drift

The Python side is pinned by `quote_modulev2/tests/quickbooks/test_contract.py`
and produced by the frontend `qbConnectorPayload(draft)` mapper. The bridge MUST
accept this and only this request shape, and return the response shape below.

### Endpoints (on `http://127.0.0.1:<port>`, default port 8788)
- `GET /ping` → `200 {"reply":"ok","contract_version":1}` (token-checked).
- `POST /submit-quote` → `200 {"response": { StatusCode, StatusMessage, Data }}`.
- `OPTIONS` on both → CORS preflight (§5).

### Request body (POST /submit-quote) — exactly what the CLI parses today
```json
{
  "TransactionType": "Estimate",
  "QuoteNumber": "26-1042",
  "CustomerAccountNumber": "11375",
  "CustomerName": "Acme Inc.",
  "CustomerPO": "PO-77",
  "DueDate": "2026-07-15",
  "Lines": [
    {"Description": "BB/123, bend block", "Quantity": 1, "Rate": 250.0, "OverrideNumber": ""}
  ]
}
```
(`ParseQuoteUploadRequest` already accepts this verbatim, including the
`/Date(...)` and ISO `DueDate` forms.)

### Response body — WRAP the CLI's response in `{"response": ...}`
The webgui frontend reads `data.response?.StatusMessage` (it expects the same
envelope the Python `/api/quickbooks/send` returns: `{"response": <conn resp>}`).
The CLI emits the **bare** `{StatusCode, StatusMessage, Data}` — so the bridge
must wrap it:
```json
{ "response": { "StatusCode": 0, "StatusMessage": "OK", "Data": { ... } } }
```
Return HTTP `200` for both QB success and QB failure (the non-zero `StatusCode`
+ `StatusMessage` carry the failure; the UI shows the message either way).
Return `4xx` only for bridge-level failures (bad/missing token → 403; malformed
body → 400).

## 5. Security (financial write path — defense in depth)

- **Loopback bind:** `HttpListener` prefix `http://127.0.0.1:<port>/` only. Never
  a LAN interface.
- **CORS allowlist:** echo `Access-Control-Allow-Origin: <app-server origin>`
  (the single configured origin, e.g. `http://APPSRV01:8742`),
  `Access-Control-Allow-Headers: content-type, x-qb-bridge-token`,
  `Access-Control-Allow-Methods: GET, POST, OPTIONS`. The webgui's JSON POST is
  preflighted, so a foreign website can't drive the bridge.
- **Shared token:** require header `X-QB-Bridge-Token` to equal a configured
  secret; `403` on mismatch or empty. The **same** secret is set on the app
  server as `QUOTE_MODULEV2_QB_BRIDGE_TOKEN` (the webgui injects it into the page
  via `/api/bootstrap`, and the browser sends it back as `X-QB-Bridge-Token`).

## 6. Configuration (must match the app server)

Read from `app.config`/env on the host:
- `QB_BRIDGE_PORT` (default `8788`) — must equal the app server's
  `QUOTE_MODULEV2_QB_BRIDGE_URL` port.
- `QB_BRIDGE_ORIGIN` — the app-server origin, e.g. `http://APPSRV01:8742`.
- `QB_BRIDGE_TOKEN` — must equal the app server's
  `QUOTE_MODULEV2_QB_BRIDGE_TOKEN`.

## 7. Reference implementation (drop into `QuickBooksServiceHost`)

```csharp
// QuickBooksServiceHost/QbBridge.cs
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace QuickBooksServiceHost
{
    internal static class QbBridge
    {
        static readonly string Origin =
            Environment.GetEnvironmentVariable("QB_BRIDGE_ORIGIN") ?? "http://APPSRV01:8742";
        static readonly string Token =
            Environment.GetEnvironmentVariable("QB_BRIDGE_TOKEN") ?? "";
        static readonly int Port =
            int.TryParse(Environment.GetEnvironmentVariable("QB_BRIDGE_PORT"), out var p) ? p : 8788;

        internal static HttpListener Start()
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            ThreadPool.QueueUserWorkItem(_ => Loop(listener));
            return listener; // caller stops it on shutdown
        }

        static void Loop(HttpListener listener)
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { break; }
                ThreadPool.QueueUserWorkItem(__ => Handle(ctx));
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            res.AddHeader("Access-Control-Allow-Origin", Origin);
            res.AddHeader("Access-Control-Allow-Headers", "content-type, x-qb-bridge-token");
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

            if (ctx.Request.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

            if (string.IsNullOrEmpty(Token) || ctx.Request.Headers["X-QB-Bridge-Token"] != Token)
            { Write(res, 403, "{\"kind\":\"forbidden\",\"message\":\"bad or missing token\"}"); return; }

            try
            {
                var path = ctx.Request.Url.AbsolutePath;
                if (path == "/ping")
                { Write(res, 200, "{\"reply\":\"ok\",\"contract_version\":1}"); return; }

                if (path == "/submit-quote" && ctx.Request.HttpMethod == "POST")
                {
                    string body;
                    using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();

                    // SAME pipeline the CLI's submit-quote uses (extract & share it):
                    //   parse -> QuickBooksServiceConnection.SubmitQuote -> ToJsonResponse
                    // Returns the bare {StatusCode,StatusMessage,Data} JSON string.
                    string connResponseJson = SubmitQuoteHandler.Handle(body);

                    Write(res, 200, "{\"response\":" + connResponseJson + "}");
                    return;
                }
                Write(res, 404, "{\"kind\":\"not_found\",\"message\":\"unknown route\"}");
            }
            catch (Exception ex)
            { Write(res, 502, "{\"kind\":\"bridge_error\",\"message\":" + JsonString(ex.Message) + "}"); }
        }

        static void Write(HttpListenerResponse res, int code, string json)
        {
            res.StatusCode = code; res.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            res.OutputStream.Write(bytes, 0, bytes.Length); res.Close();
        }

        static string JsonString(string s) =>
            new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(s ?? "");
    }
}
```

`SubmitQuoteHandler.Handle(body)` is the extracted CLI logic (§2): parse the JSON
to `QBQuoteUploadRequest`, `SubmitQuote` over the NetNamedPipe, return
`ToJsonResponse(...)` serialized — i.e. the body of the CLI's `RunSubmitQuote`
minus the stdin/stdout. Reuse it from `QuickBooksConnectorCli` too so they can't
drift.

Wire-up in `QuickBooksServiceHost/Program.cs` (inside the `try`, after
`host.Open();`, before `shutdownEvent.WaitOne();`):
```csharp
HttpListener bridge = QbBridge.Start();
// ... existing shutdownEvent.WaitOne();
bridge.Stop();
```

## 8. Verification

- **Unit tests** (in `QuickbooksIPCUnitTests` or a new test project):
  - Missing/blank/wrong `X-QB-Bridge-Token` → 403.
  - `OPTIONS` → 204 with the three CORS headers and the configured origin.
  - A valid body routes to `SubmitQuoteHandler` and the response is wrapped as
    `{"response": ...}`. (Mock/stub the WCF `SubmitQuote` so this needs no live
    QuickBooks.)
- **Manual QB smoke** (needs QuickBooks open on the box): start the service host
  with the env vars set, then from the **webgui in `local-bridge` mode**: stage a
  quote and click Send → confirm an Estimate/SalesOrder appears in QuickBooks and
  the UI shows the `StatusMessage`. A `curl` equivalent:
  ```
  curl -X POST http://127.0.0.1:8788/submit-quote ^
    -H "Content-Type: application/json" -H "X-QB-Bridge-Token: <token>" ^
    --data "@sample-connector-payload.json"
  ```
- **CORS-from-browser** check: with the bridge up, the webgui's on-load
  `GET /ping` should no longer show "bridge not reachable."

## 9. Out of scope

- The webgui side (done, merged in `quote_modulev2`).
- The Excel ribbon send path (`SalesOrderWorksheet` / `SendRequest.SendOrder`) —
  untouched; it keeps working in parallel.
- Auth beyond the loopback bind + CORS allowlist + token.
- The Excel-add-in QB-send **audit drop** (separate design:
  `quote_modulev2/docs/superpowers/specs/2026-06-25-excel-addin-qb-send-audit-design.md`).

## 10. Definition of done

The webgui in `local-bridge` mode, with the service host running and QuickBooks
open, stages and sends a quote that lands in QuickBooks — and a foreign origin or
a missing token cannot. The CLI and bridge share one `SubmitQuote` code path.
