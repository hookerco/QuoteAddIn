---
name: verify
description: Drive the QuickBooks quote pipeline end-to-end against a live (dummy) company file - service host + connector CLI + QBFC recon
---

# Verifying the quote pipeline against live QuickBooks

Preconditions: QuickBooks Desktop 2021 (32-bit) open with a **dummy** company file
(e.g. `C:\Users\chooker\Documents\Quickbooks\BTI_dummy_copy.qbw` - a production copy,
so the app "Proto-CAM QB Library" is already authorized inside the file). Submitting
quotes WRITES items/estimates - never point this at the real company file.

## Drive the service surface

1. Build Debug (AnyCPU), then start the host (takes ~75s to warm up the QB connection):
   `QuickBooksServiceHost\bin\Debug\QuickBooksServiceHost.exe` (background).
   It serves `net.pipe://localhost/QuickBooksService` + an HTTP bridge on 127.0.0.1:8788.
   Make sure no other host instance is running first (`Get-Process QuickBooksServiceHost`).
2. `QuickBooksConnectorCli\bin\Debug\QuickBooksConnectorCli.exe ping` → `{"status":"ok","reply":"Pong"}`.
3. Pipe a QBQuoteUploadRequest JSON to `QuickBooksConnectorCli.exe submit-quote` (stdin).
   Accepted keys are case/underscore-insensitive. Minimal estimate payload:
   `{"TransactionType":"Estimate","QuoteNumber":"CLV-1","DueDate":"2026-07-21","Customer":{"Name":"<QB customer FullName>","PO":"..."},"Lines":[{"Description":"PART, desc","Quantity":1,"Rate":10.0,"OverrideNumber":""}]}`
   - **QuoteNumber must be <= 11 chars** (QB RefNumber limit; longer → StatusCode 3070).
   - Customer FullName must exist in the file (customer names look like `A1SPEEDOMO12260`).
   - SalesOrder requires DueDate; Estimate doesn't.
   - Items are created BEFORE the estimate/sales order is added - a failed txn can still
     have created items (resubmits then match them by description, CreatedItem=false).
   - CLI exits 0 even when the service returns a failure; read StatusCode from the JSON body.
4. Kill the host when done (`Stop-Process -Name QuickBooksServiceHost`).

## Read-only recon/inspection via QBFC COM

QBFC14 is 32-bit COM: run scripts with
`C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -File <script.ps1>`.
Working examples from a past verification (recon + post-write checks): the session
scratchpad scripts `qb-recon.ps1` / `qb-verify.ps1` pattern:

- `New-Object -ComObject QBFC14.QBSessionManager`; `OpenConnection("", "Proto-CAM QB Library")`
  (same AppName as the service → no new authorization dialog); `BeginSession("", 2)`.
- ALWAYS set `$req.Attributes.OnError = 1` on the message set or DoRequests throws
  "Missing 'onError' attribute".
- Item queries default to ACTIVE ONLY. The service's GetAllItems uses ActiveStatus=asAll
  and also queries **ItemService** in addition to ItemNonInventory - inactive and service
  items still reserve their 1-XXXX numbers. For a faithful first-free-number prediction set
  `...ListWithClassFilter.ActiveStatus.SetValue(2)` and include both item types.
- Query specific items by name: `$q.ORListQueryWithOwnerIDAndClass.FullNameList.Add($name)`
  (no intermediate `.ListQuery`).
- Estimates: `AppendEstimateQueryRq`, filter
  `ORTxnQuery.TxnFilter.EntityFilter.OREntityFilter.FullNameList.Add($customer)`,
  `IncludeLineItems.SetValue($true)`; lines via `OREstimateLineRetList.GetAt(i).EstimateLineRet`.

## What the Excel leg needs (not covered above)

The add-in GUI path (SalesOrderWorksheet Send button) requires the VSTO add-in loaded in
Excel. The ClickOnce-installed add-in is the PUBLISHED version, not the working tree -
verifying uncommitted add-in changes means installing the Debug VSTO, which can conflict
with the production install. Prefer verifying the shared resolution logic through the
service surface (same linked source) + unit tests for the Excel bridge.
