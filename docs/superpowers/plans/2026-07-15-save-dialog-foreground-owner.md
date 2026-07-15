# Save Dialog Foreground Owner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the ServiceHost Save As fallback modal to the browser window that initiated Export, Print, or request JSON.

**Architecture:** Capture the Win32 foreground-window handle immediately before starting the STA dialog thread. On that thread, validate and wrap the handle as `IWin32Window`; use it as the `SaveFileDialog` owner, with the current hidden topmost form retained only when the captured handle is unavailable or invalid.

**Tech Stack:** C# / .NET Framework 4.7.2, WinForms, user32.dll P/Invoke, NUnit 3, VS2022 MSBuild and vstest.console.

## Global Constraints

- The browser-native `showSaveFilePicker` path is unchanged.
- File validation, extension filters, cancellation, atomic writes, bridge authentication, and the HTTP contract remain unchanged.
- The host remains required for the ServiceHost fallback path.
- Do not introduce a globally topmost browser-owner path or close/retain the browser HWND.
- Preserve the current hidden topmost WinForms owner as the invalid-handle fallback.

---

### Task 1: Foreground window owner selection

**Files:**
- Create: `QuickBooksServiceHost/ForegroundWindowOwner.cs`
- Create: `QuickbooksIPCUnitTests/ForegroundWindowOwnerTests.cs`
- Modify: `QuickBooksServiceHost/QuickBooksServiceHost.csproj`
- Modify: `QuickbooksIPCUnitTests/QuickBooksServiceLibrary.Tests.csproj`

**Interfaces:**
- Produces: `ForegroundWindowOwner.CaptureHandle() -> IntPtr`
- Produces: `ForegroundWindowOwner.TryCreate(IntPtr) -> IWin32Window`
- Produces: `ForegroundWindowOwner.TryCreate(IntPtr, Func<IntPtr, bool>) -> IWin32Window` as the deterministic validation boundary.

- [ ] **Step 1: Add failing owner-selection tests and project wiring**

Create `ForegroundWindowOwnerTests.cs` with three NUnit tests:

```csharp
using System;
using NUnit.Framework;

namespace QuickBooksServiceHost
{
    [TestFixture]
    public class ForegroundWindowOwnerTests
    {
        [Test]
        public void TryCreate_ValidHandle_ReturnsOwnerWithSameHandle()
        {
            var handle = new IntPtr(1234);

            var owner = ForegroundWindowOwner.TryCreate(handle, candidate => candidate == handle);

            Assert.IsNotNull(owner);
            Assert.AreEqual(handle, owner.Handle);
        }

        [Test]
        public void TryCreate_ZeroHandle_ReturnsNullWithoutValidation()
        {
            bool validated = false;

            var owner = ForegroundWindowOwner.TryCreate(IntPtr.Zero, candidate =>
            {
                validated = true;
                return true;
            });

            Assert.IsNull(owner);
            Assert.IsFalse(validated);
        }

        [Test]
        public void TryCreate_InvalidHandle_ReturnsNull()
        {
            var owner = ForegroundWindowOwner.TryCreate(new IntPtr(1234), candidate => false);

            Assert.IsNull(owner);
        }
    }
}
```

Add the test file and link `ForegroundWindowOwner.cs` into the unit-test project; add `System.Windows.Forms` to its references. Add `ForegroundWindowOwner.cs` to the ServiceHost project.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" QuickbooksIPCUnitTests\QuickBooksServiceLibrary.Tests.csproj /p:Configuration=Debug /v:m
```

Expected: build fails because `QuickBooksServiceHost/ForegroundWindowOwner.cs` does not yet exist, proving the new tests require the missing production boundary.

- [ ] **Step 3: Implement the minimal Win32 owner wrapper**

Create `ForegroundWindowOwner.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuickBooksServiceHost
{
    internal static class ForegroundWindowOwner
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        internal static IntPtr CaptureHandle()
        {
            return GetForegroundWindow();
        }

        internal static IWin32Window TryCreate(IntPtr handle)
        {
            return TryCreate(handle, IsWindow);
        }

        internal static IWin32Window TryCreate(IntPtr handle, Func<IntPtr, bool> isWindow)
        {
            if (handle == IntPtr.Zero || !isWindow(handle))
            {
                return null;
            }

            return new WindowHandle(handle);
        }

        private sealed class WindowHandle : IWin32Window
        {
            internal WindowHandle(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; }
        }
    }
}
```

- [ ] **Step 4: Build and run the focused tests to verify GREEN**

Run the unit-test build, then:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\Extensions\TestPlatform\vstest.console.exe" QuickbooksIPCUnitTests\bin\Debug\QuickbooksIPCUnitTests.dll /TestCaseFilter:"FullyQualifiedName~ForegroundWindowOwnerTests"
```

Expected: 3 tests pass.

### Task 2: Use the browser HWND for Save As and release it

**Files:**
- Modify: `QuickBooksServiceHost/WorkstationSaveDialog.cs`
- Verify: `QuickBooksServiceHost/QuickBooksServiceHost.csproj`
