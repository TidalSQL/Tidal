namespace TidalSqlLib
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using ILGPU;
    using ILGPU.Runtime;

    /// <summary>Comparison used when filtering a numeric column.</summary>
    public enum ColumnPredicate
    {
        /// <summary>Include every element (no filter).</summary>
        All = 0,

        /// <summary>Include elements strictly greater than the threshold.</summary>
        GreaterThan = 1,

        /// <summary>Include elements greater than or equal to the threshold.</summary>
        GreaterOrEqual = 2,

        /// <summary>Include elements strictly less than the threshold.</summary>
        LessThan = 3,

        /// <summary>Include elements less than or equal to the threshold.</summary>
        LessOrEqual = 4,

        /// <summary>Include elements equal to the threshold.</summary>
        Equal = 5,
    }

    /// <summary>
    /// GPU-accelerated column aggregates (SUM / filtered SUM / COUNT) over a numeric column, built on
    /// ILGPU. A single accelerator is created once and reused: a hardware GPU when one is present
    /// (see <see cref="GpuDetector"/>), otherwise ILGPU's CPU accelerator as a transparent fallback,
    /// so the same kernel runs everywhere and the API works without a GPU. All operations are exposed
    /// as <see cref="Task"/>s that honour a <see cref="CancellationToken"/>.
    /// </summary>
    public static class GpuColumnMath
    {
        // The maximum number of GPU threads launched for a reduction. Each thread grid-strides over
        // the column accumulating a partial sum; the small partial array is then reduced on the host.
        private const int MaxThreads = 4096;

        private static readonly object Gate = new();

        private static bool initialized;
        private static Context? context;
        private static Accelerator? accelerator;
        private static Action<Index1D, ArrayView<double>, ArrayView<double>, double, int, int>? kernel;
        private static bool isHardwareGpu;

        /// <summary>True when the reused accelerator is a physical GPU rather than the CPU fallback.</summary>
        public static bool IsGpuAccelerated
        {
            get
            {
                EnsureInitialized();
                return isHardwareGpu;
            }
        }

        /// <summary>The name of the accelerator in use (e.g. the GPU name, or the CPU device).</summary>
        public static string AcceleratorName
        {
            get
            {
                EnsureInitialized();
                return accelerator?.Name ?? "none";
            }
        }

        /// <summary>Sums every element of the column.</summary>
        public static Task<double> SumAsync(double[] column, CancellationToken cancellationToken = default) =>
            Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Reduce(column, 0.0, (int)ColumnPredicate.All, addValue: true);
                },
                cancellationToken);

        /// <summary>Sums the elements of the column that satisfy <paramref name="predicate"/>.</summary>
        public static Task<double> SumWhereAsync(
            double[] column,
            ColumnPredicate predicate,
            double threshold,
            CancellationToken cancellationToken = default) =>
            Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Reduce(column, threshold, (int)predicate, addValue: true);
                },
                cancellationToken);

        /// <summary>Counts the elements of the column that satisfy <paramref name="predicate"/>.</summary>
        public static Task<long> CountWhereAsync(
            double[] column,
            ColumnPredicate predicate,
            double threshold,
            CancellationToken cancellationToken = default) =>
            Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return (long)Math.Round(Reduce(column, threshold, (int)predicate, addValue: false));
                },
                cancellationToken);

        private static double Reduce(double[] column, double threshold, int predicate, bool addValue)
        {
            if (column == null)
            {
                throw new ArgumentNullException(nameof(column));
            }

            if (column.Length == 0)
            {
                return 0.0;
            }

            EnsureInitialized();

            // ILGPU failed to initialise (no usable accelerator at all): compute on the host directly.
            if (accelerator == null || kernel == null)
            {
                return CpuReduce(column, threshold, predicate, addValue);
            }

            // The accelerator/stream is not safe for concurrent launches; serialise GPU work.
            lock (Gate)
            {
                var threads = (int)Math.Min(column.Length, MaxThreads);
                using var deviceColumn = accelerator.Allocate1D(column);
                using var devicePartials = accelerator.Allocate1D<double>(threads);

                kernel!(threads, deviceColumn.View, devicePartials.View, threshold, predicate, addValue ? 1 : 0);
                accelerator.Synchronize();

                var partials = devicePartials.GetAsArray1D();
                var sum = 0.0;
                foreach (var value in partials)
                {
                    sum += value;
                }

                return sum;
            }
        }

        // Grid-stride partial reduction: each thread sums a disjoint slice of the column into its own
        // partial slot, so no atomics or inter-thread synchronisation are required.
        private static void ReduceKernel(
            Index1D index,
            ArrayView<double> data,
            ArrayView<double> partials,
            double threshold,
            int predicate,
            int addValue)
        {
            var stride = (int)partials.Length;
            var sum = 0.0;

            for (var i = (long)index.X; i < data.Length; i += stride)
            {
                var value = data[i];
                var pass = Matches(value, threshold, predicate);
                if (pass)
                {
                    sum += addValue == 1 ? value : 1.0;
                }
            }

            partials[index] = sum;
        }

        private static bool Matches(double value, double threshold, int predicate)
        {
            if (predicate == 1)
            {
                return value > threshold;
            }

            if (predicate == 2)
            {
                return value >= threshold;
            }

            if (predicate == 3)
            {
                return value < threshold;
            }

            if (predicate == 4)
            {
                return value <= threshold;
            }

            if (predicate == 5)
            {
                return value == threshold;
            }

            return true;
        }

        private static double CpuReduce(double[] column, double threshold, int predicate, bool addValue)
        {
            var sum = 0.0;
            foreach (var value in column)
            {
                if (Matches(value, threshold, predicate))
                {
                    sum += addValue ? value : 1.0;
                }
            }

            return sum;
        }

        private static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            lock (Gate)
            {
                if (initialized)
                {
                    return;
                }

                try
                {
                    context = Context.CreateDefault();

                    // Prefer a hardware GPU; ILGPU returns its CPU device when none is available.
                    var device = context.GetPreferredDevice(preferCPU: false);
                    accelerator = device.CreateAccelerator(context);
                    isHardwareGpu = accelerator.AcceleratorType != AcceleratorType.CPU;

                    kernel = accelerator.LoadAutoGroupedStreamKernel<
                        Index1D, ArrayView<double>, ArrayView<double>, double, int, int>(ReduceKernel);
                }
                catch
                {
                    // Leave accelerator/kernel null so callers transparently use the host fallback.
                    accelerator?.Dispose();
                    context?.Dispose();
                    accelerator = null;
                    context = null;
                    kernel = null;
                    isHardwareGpu = false;
                }

                initialized = true;
            }
        }
    }
}
