using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuickBooksConnectorCore;

namespace QuickbooksIPCUnitTests
{
    [TestFixture]
    public class WorkstationFileSaveTests
    {
        [Test]
        public void NormalizeSuggestedName_RemovesPathAndUnsafeCharacters()
        {
            string name = WorkstationFileSave.NormalizeSuggestedName(
                @"..\bad:name?.xlsx", "xlsx");

            Assert.AreEqual("bad-name-.xlsx", name);
        }

        [Test]
        public void NormalizeSuggestedName_RejectsUnsupportedExtension()
        {
            Assert.Throws<ArgumentException>(() =>
                WorkstationFileSave.NormalizeSuggestedName("quote.exe", "exe"));
        }

        [Test]
        public void WriteAtomically_ReplacesDestinationAndCleansTemporaryFile()
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "quote-save-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string destination = Path.Combine(directory, "quote.pdf");
                File.WriteAllBytes(destination, new byte[] { 1 });

                WorkstationFileSave.WriteAtomically(
                    destination, new byte[] { 2, 3, 4 });

                CollectionAssert.AreEqual(new byte[] { 2, 3, 4 }, File.ReadAllBytes(destination));
                Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
