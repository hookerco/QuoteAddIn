# Direct QuickBooks cache refresh performance

Verified locally on September 10, 2026 against the user's open old dummy company, which still contains real company data. No company records were written, exported, or retained. The comparison kept responses in process memory and emitted only timings, counts, and equality results.

## Result

The optimized complete refresh took **3.885 seconds** on a background MTA worker, matching service-host startup. An earlier synchronous run took 3.971 seconds. Item data became available after a 0.444-second item request, following a 0.334-second connection setup.

The principal delay was traversal of QBFC COM response properties, not the QuickBooks item query itself. A first baseline measured 70.836 seconds extracting items and 51.505 seconds extracting customer terms. The following paired comparison then processed each identical response through both the original converter and the new XML reader:

| Dataset | QB query | Original extraction | XML transfer | Managed parsing plus equality check | Records | Exact field equality |
|---|---:|---:|---:|---:|---:|---|
| Non-inventory and service items | 372 ms | 67,089 ms | 5 ms | 36 ms | 6,082 | Yes |
| Credit terms and shipping methods | 90 ms | 177 ms | <1 ms | <1 ms | 32 | Yes |
| Customer commercial terms | 3,458 ms | 38,577 ms | 3 ms | 15 ms | 2,445 | Yes |

Equality covers item number, description, active status, customer account number, credit terms, shipping account, term/method names, list order and status code. The generated refresh timestamp is intentionally excluded.

In the final full-refresh worker run, the customer query itself took 2.927 seconds and customer conversion took 19 ms. These are measurements of this open test company and workstation, not a guarantee for the production company or other machines.

## Changes

- Transfer each cache response once with `IMsgSetResponse.ToXMLString()`, then parse locally using the standard .NET XML libraries. Preserve both item types, inactive items, description choices and public customer custom fields. No XML is logged or saved.
- Validate expected response types/counts and status codes before publishing. Reject malformed/incomplete responses and partial failures; accept a valid SDK empty-list result.
- Use one short-lived QuickBooks session for all three refresh stages, with sequential requests on the same worker. Write requests continue to own separate sessions and retain company verification.
- Serialize overlapping item reads and coalesce overlapping full-refresh calls. An item invalidation during a read prevents publication of that obsolete snapshot. Item snapshots now belong to the service instance rather than a static dictionary.
- Close sessions deterministically and idempotently. Remove request/connection finalizers that could call COM during garbage collection; a failed connection reproduced a finalizer crash before this fix.
- Log connection, request construction, query, conversion and close timings without response records.

This does not introduce a shared server cache, change the startup/hourly/manual refresh schedule, or create an external-edit notification policy. Direct edits in QuickBooks become visible on the next refresh, as before. Existing successful snapshots remain available during ordinary background refreshes and failed refreshes.

## Verification and rerun

- All **242 unit tests passed**, including XML mapping/error cases, session ownership/cleanup, overlapping cache misses and invalidation during a read.
- Service-host Release build succeeded using the existing signing configuration.
- The live paired comparison and full refresh used read-only queries. Only timing logs and aggregate comparison output remain in `.test-logs/cache-performance/`.
- Pre-existing estimate lookup fixes in the working tree were preserved.

With QuickBooks open and signed in, run from the repository in the same interactive user session:

```powershell
& "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File .\Measure-QuickBooksCacheRefresh.ps1
```

The script uses the Release build by default, creates no listener or automatic refresh loop, and writes timing logs locally. `-BuildDirectory` can select another built or installed host directory. Rebuild first when measuring source changes.

The optimized binaries were published to the shared `service_host_installation` payload on September 10, 2026 at 13:45:25 UTC. On September 18, the shared and locally installed `QBRequestLibrary.dll` and `QuickBooksIPCService.dll` hashes matched, and the installed request library contained the new cache parser and shared-session classes. Other workstation installations were reported by the user, but were not individually inspected.

The published manifest labels this build with commit `8b464a9f1bde36e90c7e6bbf854d7c9ecfe66240`, although the optimization and estimate lookup changes were still uncommitted when published. The deployment script copies Release binaries and defaults the release ID to Git `HEAD`; it does not require a clean working tree. That historical commit label alone therefore does not identify all source changes in the published binaries.
