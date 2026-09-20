namespace TidalSqlLib
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Management;
    using System.Runtime.Versioning;

    /// <summary>A display adapter reported by the operating system.</summary>
    /// <param name="Name">The adapter's product name (e.g. "NVIDIA GeForce RTX 4090").</param>
    /// <param name="Vendor">The adapter vendor (e.g. "NVIDIA", "Advanced Micro Devices, Inc.").</param>
    /// <param name="MemoryBytes">Best-effort dedicated video memory in bytes; 0 when unknown.</param>
    /// <param name="IsSynthetic">
    /// True for virtual/fallback adapters that are not real GPUs (VM display, Basic Display Adapter,
    /// remote-desktop mirror drivers, etc.). These do not offer usable graphics/compute acceleration.
    /// </param>
    public sealed record GpuAdapter(string Name, string Vendor, long MemoryBytes, bool IsSynthetic);

    /// <summary>The result of a GPU probe: the adapters found and a few convenience flags.</summary>
    public sealed class GpuInfo
    {
        /// <summary>All display adapters the OS reported, including synthetic/virtual ones.</summary>
        public IReadOnlyList<GpuAdapter> Adapters { get; init; } = Array.Empty<GpuAdapter>();

        /// <summary>True when an NVIDIA CUDA-capable GPU was confirmed via <c>nvidia-smi</c>.</summary>
        public bool HasNvidiaGpu { get; init; }

        /// <summary>True when at least one non-synthetic (physical) display adapter was found.</summary>
        public bool HasPhysicalGpu => this.Adapters.Any(a => !a.IsSynthetic) || this.HasNvidiaGpu;

        /// <summary>The physical adapters only (synthetic/virtual entries removed).</summary>
        public IEnumerable<GpuAdapter> PhysicalAdapters => this.Adapters.Where(a => !a.IsSynthetic);
    }

    /// <summary>
    /// Detects whether the current machine has a usable GPU. On Windows this reads
    /// <c>Win32_VideoController</c> via WMI and filters out virtual/fallback adapters; on all
    /// platforms it also probes <c>nvidia-smi</c> for a definitive CUDA-capable signal, and falls
    /// back to <c>lspci</c> (Linux) / <c>system_profiler</c> (macOS) when WMI is unavailable.
    /// A reported display adapter is not necessarily a compute-capable GPU, so callers that need
    /// GPU compute should prefer <see cref="GpuInfo.HasNvidiaGpu"/>.
    /// </summary>
    public static class GpuDetector
    {
        // Substrings that identify a virtual, remote, or fallback adapter rather than real hardware.
        private static readonly string[] SyntheticMarkers =
        {
            "Microsoft Basic Display",
            "Microsoft Remote Display",
            "Hyper-V Video",
            "Remote Desktop",
            "RDP",
            "VMware SVGA",
            "VirtualBox",
            "QXL",
            "Virtual Display",
            "Parsec",
            "DameWare",
            "Mirror Driver",
        };

        /// <summary>Probes the machine and returns the adapters found plus convenience flags.</summary>
        public static GpuInfo Detect()
        {
            var adapters = new List<GpuAdapter>();

            if (OperatingSystem.IsWindows())
            {
                adapters.AddRange(QueryWindows());
            }
            else if (OperatingSystem.IsLinux())
            {
                adapters.AddRange(QueryLinux());
            }
            else if (OperatingSystem.IsMacOS())
            {
                adapters.AddRange(QueryMacOs());
            }

            return new GpuInfo
            {
                Adapters = adapters,
                HasNvidiaGpu = ProbeNvidiaSmi(),
            };
        }

        /// <summary>True for adapter names that denote a virtual/fallback device, not real hardware.</summary>
        public static bool IsSyntheticName(string name) =>
            !string.IsNullOrWhiteSpace(name) &&
            SyntheticMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

        [SupportedOSPlatform("windows")]
        private static IEnumerable<GpuAdapter> QueryWindows()
        {
            var results = new List<GpuAdapter>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, AdapterCompatibility, AdapterRAM FROM Win32_VideoController");

                foreach (var mo in searcher.Get().OfType<ManagementBaseObject>())
                {
                    var name = mo["Name"]?.ToString()?.Trim() ?? "(unknown)";
                    var vendor = mo["AdapterCompatibility"]?.ToString()?.Trim() ?? string.Empty;

                    // AdapterRAM is a 32-bit value that saturates near 4 GB and is often 0 on VMs;
                    // it is best-effort only.
                    long memory = 0;
                    if (mo["AdapterRAM"] is uint ram)
                    {
                        memory = ram;
                    }

                    results.Add(new GpuAdapter(name, vendor, memory, IsSyntheticName(name)));
                }
            }
            catch
            {
                // WMI can be unavailable or blocked; treat as "no adapters discovered".
            }

            return results;
        }

        private static IEnumerable<GpuAdapter> QueryLinux()
        {
            var output = RunProcess("lspci", string.Empty);
            if (output == null)
            {
                yield break;
            }

            foreach (var line in output.Split('\n'))
            {
                // Example: "01:00.0 VGA compatible controller: NVIDIA Corporation GA102 [GeForce RTX 3090]"
                if (line.IndexOf("VGA compatible controller", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("3D controller", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("Display controller", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var colon = line.IndexOf(':', line.IndexOf(':') + 1);
                var name = colon >= 0 && colon + 1 < line.Length ? line[(colon + 1)..].Trim() : line.Trim();
                yield return new GpuAdapter(name, VendorFromName(name), 0, IsSyntheticName(name));
            }
        }

        private static IEnumerable<GpuAdapter> QueryMacOs()
        {
            var output = RunProcess("system_profiler", "SPDisplaysDataType");
            if (output == null)
            {
                yield break;
            }

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Chipset Model:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = trimmed["Chipset Model:".Length..].Trim();
                    yield return new GpuAdapter(name, VendorFromName(name), 0, IsSyntheticName(name));
                }
            }
        }

        private static bool ProbeNvidiaSmi()
        {
            var output = RunProcess("nvidia-smi", "--query-gpu=name --format=csv,noheader");
            return !string.IsNullOrWhiteSpace(output);
        }

        private static string VendorFromName(string name)
        {
            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                return "NVIDIA";
            }

            if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
            {
                return "AMD";
            }

            if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                return "Intel";
            }

            return string.Empty;
        }

        /// <summary>
        /// Runs a console tool and returns its standard output, or null when the tool is missing,
        /// exits non-zero, or does not complete within the timeout. Never throws.
        /// </summary>
        private static string? RunProcess(string fileName, string arguments, int timeoutMs = 4000)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return null;
                }

                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return null;
                }

                return process.ExitCode == 0 ? output : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
