# Installer Transaction Remediation Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development to implement this plan task-by-task. This work executes inline in the already isolated `codex/service-host-auto-update` worktree.

**Goal:** Make installer publication reads non-destructive and make every post-stop failure restore and restart the retained installed release.

**Architecture:** `Invoke-ServiceHostInstall` remains the public seam. Under the shared installer/manager mutex it neutralizes the task, secures `StatePath`, snapshots one fixed `Previous` release, stages the candidate into a new GUID directory, and verifies all bytes before stopping the host. Explicit phase flags drive one locked rollback routine; task starts and health checks occur only after mutex release.

**Tech Stack:** Windows PowerShell 5.1-compatible PowerShell, temp-directory behavioral tests, injected filesystem/process/task/mutex actions.

## Global Constraints

- Implement remediation phase 2 only from `809ed26`.
- Never exercise live Scheduled Tasks, service-host processes, deployment shares, or machine install/state paths.
- Keep the phase-1 mutex, task-neutralization, identity-binding, and secure non-reparse state-tree guarantees.
- Stage every manifest runtime file, manager, and candidate manifest before host stop.
- Retain exactly one `StatePath\Previous` on success; consume it when rollback restores the old release.
- Preserve the primary exception and store rollback diagnostics separately.

---

### Task 1: Transaction test harness and RED coverage

**Files:**
- Modify: `InstallServiceHostScript.Tests.ps1`

**Interfaces:**
- Extend `New-InjectedActions` with `MovePath(source, destination)` and event/call tracking.
- Extend `Invoke-FixtureInstall` to pass `MovePathAction` when supported.
- Add scenario `transactional-install` covering corrupt/missing stage input, stale `Previous`, swap/state/task/start failures, success ordering, and retained-release idempotency.

- [ ] Add temp-tree setup helpers that install an identifiable old runtime, old manager, and old manifest.
- [ ] Add assertions that pre-stop failures leave those three canonical surfaces byte-identical, make zero stop calls, and leave no task.
- [ ] Add assertions that each post-stop failure restores old bytes, re-registers the old task plan, and restarts exactly the expected path.
- [ ] Add primary/rollback dual-failure diagnostics and the exact `stage -> stop -> move -> promote -> register -> release -> start` ordering assertion.
- [ ] Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\InstallServiceHostScript.Tests.ps1 -Scenario transactional-install` and confirm the missing `MovePathAction`/transaction behavior fails.

### Task 2: Verified staging and retained previous release

**Files:**
- Modify: `install_service_host.ps1`

**Interfaces:**
- Add optional `MovePathAction(source, destination)` to `Invoke-ServiceHostInstall`.
- Extend per-invocation state with `Phase`, `StageRoot`, `PreviousPath`, `HadPreviousRuntime`, `HadPreviousManager`, `HadPreviousManifest`, `CurrentMoved`, `PromotionHappened`, and `RollbackPrepared`.

- [ ] Move source/manifest validation inside the first locked mutation after task neutralization.
- [ ] Secure and fail-fast remove the single retained `Previous`, recreate it, and copy old manager/manifest snapshots before stop.
- [ ] Create `StatePath\<guid-N>\Payload`, copy all manifest files plus manager and manifest, and verify target lengths/SHA-256 before stop.
- [ ] Run the focused scenario and confirm pre-stop cases pass before continuing.

### Task 3: Swap, rollback, and post-lock verification

**Files:**
- Modify: `install_service_host.ps1`
- Modify: `InstallServiceHostScript.Tests.ps1`

**Interfaces:**
- Locked rollback neutralizes the task, stops only expected-path hosts, removes `InstallPath` only when candidate promotion completed, restores `Previous\Payload` and state snapshots, registers the known old task plan, and marks rollback ready for restart.
- Post-lock verification starts the selected task and accepts only a process whose executable path exactly equals `InstallPath\QuickBooksServiceHost.exe`.

- [ ] Stop and bounded-wait only after successful staging, move current install to `Previous\Payload`, promote staged payload/state, register the new task, release, start, and verify.
- [ ] On locked swap/register failure, perform rollback while still locked, then release and start/verify the restored host.
- [ ] On post-lock start/health failure, reacquire the same mutex, rollback, release, and start/verify the restored host.
- [ ] Attach any rollback exception as `Exception.Data['RollbackFailure']` without replacing the primary exception.
- [ ] Run focused scenario under both shells until green.

### Task 4: Regression verification and delivery

**Files:**
- Create ignored: `.superpowers/sdd/remediation-2-report.md`

- [ ] Run all Deploy, Installer, and Manager `-Scenario all` suites under Windows PowerShell 5.1 and PowerShell 7.
- [ ] Run `git diff --check`.
- [ ] Commit only the phase-2 plan, installer, and installer tests.
- [ ] Record RED/GREEN evidence, verification output, and residual concerns in the ignored report.
- [ ] Confirm `git status --short` is empty.
