using System;
using System.Threading;

namespace QBRequestLibrary
{
    // QBFC and QBXMLRP both enter the native SDK in this process.
    public sealed class QuickBooksSessionGate : IDisposable
    {
        private static readonly SemaphoreSlim Available = new SemaphoreSlim(1, 1);
        private int released;

        private QuickBooksSessionGate() { }

        public static QuickBooksSessionGate Enter(CancellationToken cancellation = default(CancellationToken))
        {
            Available.Wait(cancellation);
            return new QuickBooksSessionGate();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                Available.Release();
        }
    }
}
