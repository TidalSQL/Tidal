namespace ParqBaseTests
{
    using System.Linq;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using ParqBaseLib;

    /// <summary>
    /// Tests for <see cref="GpuDetector"/>. GPU presence is environment-specific (CI/VMs usually have
    /// none), so these assert the probe runs safely and classifies adapters consistently rather than
    /// asserting a particular device exists.
    /// </summary>
    [TestClass]
    public class GpuDetectorTests
    {
        [TestMethod]
        public void Detect_DoesNotThrow_AndReturnsResult()
        {
            var info = GpuDetector.Detect();

            Assert.IsNotNull(info);
            Assert.IsNotNull(info.Adapters);

            // HasPhysicalGpu is consistent with the adapter classification / NVIDIA probe.
            var expected = info.Adapters.Any(a => !a.IsSynthetic) || info.HasNvidiaGpu;
            Assert.AreEqual(expected, info.HasPhysicalGpu);

            // PhysicalAdapters never contains a synthetic entry.
            Assert.IsFalse(info.PhysicalAdapters.Any(a => a.IsSynthetic));
        }

        [TestMethod]
        public void IsSyntheticName_FlagsVirtualAdapters()
        {
            Assert.IsTrue(GpuDetector.IsSyntheticName("Microsoft Hyper-V Video"));
            Assert.IsTrue(GpuDetector.IsSyntheticName("Microsoft Basic Display Adapter"));
            Assert.IsTrue(GpuDetector.IsSyntheticName("Microsoft Remote Display Adapter"));
        }

        [TestMethod]
        public void IsSyntheticName_DoesNotFlagRealGpus()
        {
            Assert.IsFalse(GpuDetector.IsSyntheticName("NVIDIA GeForce RTX 4090"));
            Assert.IsFalse(GpuDetector.IsSyntheticName("AMD Radeon RX 7900 XTX"));
            Assert.IsFalse(GpuDetector.IsSyntheticName("Intel Arc A770"));
        }
    }
}
