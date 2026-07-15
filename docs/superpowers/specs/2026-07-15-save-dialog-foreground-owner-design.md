# Save Dialog Foreground Owner Design

Date: 2026-07-15

## Problem

The LAN quote workbench uses `QuickBooksServiceHost` as its workstation-local
Save As fallback. The host creates a topmost, invisible WinForms owner before
opening `SaveFileDialog`, but Windows can still place the common dialog behind
the browser because the background service-host process does not own the
foreground window.

The desired behavior is narrow: a Save As dialog initiated by Export, Print,
or request JSON should open above the browser window that initiated the save.
The browser-native `showSaveFilePicker` path is unchanged.

## Approaches Considered

1. **Use the current foreground window as the dialog owner (selected).** Capture
   the foreground HWND when the local `/save-file` request reaches the host and
   pass a lightweight `IWin32Window` wrapper to `SaveFileDialog.ShowDialog`.
   Windows then treats the dialog as modal to the browser and keeps it above
   that browser window.
2. **Force the ServiceHost owner globally topmost.** This is simpler, but a
   background process can steal focus from unrelated applications and leave
   the dialog above work that did not initiate the save.
3. **Add a browser extension or custom protocol.** This would provide stronger
   browser identity but introduces installation and maintenance complexity for
   behavior Windows already supports through an owner HWND.

## Design

`WorkstationSaveDialog.Save` captures `GetForegroundWindow()` immediately
before it creates the dedicated STA dialog thread. A small Win32 boundary
validates that the handle is non-zero and still identifies a real window.

On the STA thread:

- When the captured handle is valid, wrap it in an `IWin32Window` and call
  `dialog.ShowDialog(foregroundOwner)`. No global topmost flag or forced focus
  stealing is used in this path.
- When the handle is missing or becomes invalid, retain the existing hidden,
  taskbar-free, topmost WinForms owner as a safe fallback.

The file validation, extension filters, cancellation response, atomic write,
bridge authentication, and HTTP contract remain unchanged.

## Error Handling

Failure to obtain or validate a foreground handle is not an export failure; it
selects the existing fallback owner. Dialog failures continue through the
current `Workstation Save As failed: ...` exception boundary. The owner wrapper
does not retain or close the browser HWND.

## Testing

The foreground-owner selection is isolated from the dialog itself so tests can
cover:

- a valid foreground handle is selected;
- a zero or invalid handle selects the hidden-owner fallback;
- no owner-selection failure changes file validation or atomic-write behavior.

The existing 128-test connector suite remains the regression baseline. A live
smoke check uses the installed Release host and a real `/save-file` request to
confirm the Save As dialog appears above the browser. Release publishing must
continue through the shared installer, followed by installed-binary hash and
live endpoint verification.

## Scope Boundaries

This change does not alter browser-picker behavior, quote rendering, QuickBooks
staging, bridge-token handling, suggested filenames, or default save-directory
selection. It only changes which native window owns the ServiceHost fallback
dialog. The host remains required for that fallback path.
