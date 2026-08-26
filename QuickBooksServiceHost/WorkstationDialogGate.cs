using System;

namespace QuickBooksServiceHost
{
    internal static class WorkstationDialogGate
    {
        private static readonly object SyncRoot = new object();

        internal static T Run<T>(Func<T> operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            lock (SyncRoot)
            {
                return operation();
            }
        }
    }
}
