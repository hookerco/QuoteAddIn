using System.Collections.Generic;
using ExcelAddIn1;
using NUnit.Framework;

namespace QuickBooksServiceLibrary.Tests
{
    /// <summary>
    /// Characterization tests for the Excel-side NumberGenerator. They pin the behavior the
    /// add-in ships TODAY, including the dense-set allocation bug, so the divergence from the
    /// server-side QuoteUploadItemResolver.GenerateNumber is proven in code before it is fixed.
    ///
    /// The bug: when the reserved set is exactly {0, 1, ..., n-1} (no gap to fill), Generate()
    /// adds n to the set, then re-reads Count (now n+1) to build the returned number. It hands
    /// out "1-000(n+1)" while reserving n, so the first free number is skipped. The server
    /// implementation returns the first free number "1-000n".
    /// </summary>
    [TestFixture]
    public class NumberGeneratorCharacterizationTests
    {
        [Test]
        public void Generate_FillsGapInReservedNumbers_MatchesServerBehavior()
        {
            var generator = new NumberGenerator(new SortedSet<int> { 0, 1, 5 });

            Assert.AreEqual("1-0002", generator.Generate());
        }

        [Test]
        public void Generate_FillsGapAtZero_MatchesServerBehavior()
        {
            var generator = new NumberGenerator(new SortedSet<int> { 1, 2 });

            Assert.AreEqual("1-0000", generator.Generate());
        }

        [Test]
        public void Generate_EmptyReservedSet_SkipsFirstNumber_DivergesFromServer()
        {
            var generator = new NumberGenerator(new SortedSet<int>());

            // Server behavior would be "1-0000"; the add-in skips it.
            Assert.AreEqual("1-0001", generator.Generate());
        }

        [Test]
        public void Generate_DenseReservedSet_SkipsFirstFreeNumber_DivergesFromServer()
        {
            var generator = new NumberGenerator(new SortedSet<int> { 0, 1, 2 });

            // Server behavior would be "1-0003"; the add-in reserves 3 but returns "1-0004".
            Assert.AreEqual("1-0004", generator.Generate());
        }

        [Test]
        public void Generate_DenseReservedSet_NeverIssuesTheNumberItSkipped()
        {
            var generator = new NumberGenerator(new SortedSet<int> { 0, 1, 2 });

            // Server behavior would be "1-0003" then "1-0004".
            Assert.AreEqual("1-0004", generator.Generate());
            Assert.AreEqual("1-0005", generator.Generate());
        }

        [Test]
        public void Generate_GapFillTurnsSetDense_SecondCallHitsTheDenseBug()
        {
            var generator = new NumberGenerator(new SortedSet<int> { 0, 2 });

            // First call fills the gap like the server would...
            Assert.AreEqual("1-0001", generator.Generate());
            // ...but the set is now dense {0,1,2}, so the second call skips "1-0003".
            Assert.AreEqual("1-0004", generator.Generate());
        }
    }
}
