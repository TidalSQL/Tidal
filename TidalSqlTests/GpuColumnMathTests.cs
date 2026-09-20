namespace TidalSqlTests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using TidalSqlLib;

    /// <summary>
    /// Tests for <see cref="GpuColumnMath"/>. These validate the ILGPU kernel against a plain LINQ
    /// reference. With no hardware GPU they exercise ILGPU's CPU accelerator (the transparent
    /// fallback), so kernel correctness is verified everywhere the suite runs.
    /// </summary>
    [TestClass]
    public class GpuColumnMathTests
    {
        private static double[] SampleColumn()
        {
            var rng = new Random(1234);
            return Enumerable.Range(0, 100_000).Select(_ => (double)rng.Next(0, 1000)).ToArray();
        }

        [TestMethod]
        public async Task SumAsync_MatchesLinq()
        {
            var column = SampleColumn();
            var expected = column.Sum();

            var actual = await GpuColumnMath.SumAsync(column);

            Assert.AreEqual(expected, actual, 1e-3);
        }

        [TestMethod]
        public async Task SumWhereAsync_MatchesLinq()
        {
            var column = SampleColumn();
            const double threshold = 500.0;
            var expected = column.Where(v => v > threshold).Sum();

            var actual = await GpuColumnMath.SumWhereAsync(column, ColumnPredicate.GreaterThan, threshold);

            Assert.AreEqual(expected, actual, 1e-3);
        }

        [TestMethod]
        public async Task CountWhereAsync_MatchesLinq()
        {
            var column = SampleColumn();
            const double threshold = 250.0;
            var expected = column.Count(v => v <= threshold);

            var actual = await GpuColumnMath.CountWhereAsync(column, ColumnPredicate.LessOrEqual, threshold);

            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public async Task EmptyColumn_ReturnsZero()
        {
            Assert.AreEqual(0.0, await GpuColumnMath.SumAsync(Array.Empty<double>()), 0.0);
            Assert.AreEqual(0L, await GpuColumnMath.CountWhereAsync(Array.Empty<double>(), ColumnPredicate.All, 0));
        }

        [TestMethod]
        public async Task Cancellation_IsHonoured()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                () => GpuColumnMath.SumAsync(SampleColumn(), cts.Token));
        }

        [TestMethod]
        public void AcceleratorName_IsReported()
        {
            // Whether or not a hardware GPU is present, an accelerator name is available.
            Assert.IsFalse(string.IsNullOrWhiteSpace(GpuColumnMath.AcceleratorName));
        }
    }
}
