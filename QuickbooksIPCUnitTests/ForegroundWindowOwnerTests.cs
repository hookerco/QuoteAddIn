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

            var owner = ForegroundWindowOwner.TryCreate(
                handle, candidate => candidate == handle);

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
            var owner = ForegroundWindowOwner.TryCreate(
                new IntPtr(1234), candidate => false);

            Assert.IsNull(owner);
        }
    }
}
