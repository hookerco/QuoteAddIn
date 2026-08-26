using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace QuickBooksServiceHost
{
    [TestFixture]
    public class WorkstationDialogGateTests
    {
        [Test]
        public void Run_DoesNotOverlapDialogOperations()
        {
            using (var firstEntered = new ManualResetEvent(false))
            using (var releaseFirst = new ManualResetEvent(false))
            using (var secondAttempted = new ManualResetEvent(false))
            using (var secondEntered = new ManualResetEvent(false))
            {
                Task<int> first = Task.Run(() => WorkstationDialogGate.Run(() =>
                {
                    firstEntered.Set();
                    releaseFirst.WaitOne();
                    return 1;
                }));
                Assert.IsTrue(firstEntered.WaitOne(TimeSpan.FromSeconds(2)));

                Task<int> second = Task.Run(() =>
                {
                    secondAttempted.Set();
                    return WorkstationDialogGate.Run(() =>
                    {
                        secondEntered.Set();
                        return 2;
                    });
                });
                Assert.IsTrue(secondAttempted.WaitOne(TimeSpan.FromSeconds(2)));
                Assert.IsFalse(secondEntered.WaitOne(TimeSpan.FromMilliseconds(100)));

                releaseFirst.Set();
                Assert.IsTrue(secondEntered.WaitOne(TimeSpan.FromSeconds(2)));
                Assert.AreEqual(1, first.Result);
                Assert.AreEqual(2, second.Result);
            }
        }
    }
}
