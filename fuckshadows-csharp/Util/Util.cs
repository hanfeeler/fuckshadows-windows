﻿using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Fuckshadows.Controller;
using Microsoft.Win32;

namespace Fuckshadows.Util
{
    public struct BandwidthScaleInfo
    {
        public float value;
        public string unit_name;
        public long unit;

        public BandwidthScaleInfo(float value, string unit_name, long unit)
        {
            this.value = value;
            this.unit_name = unit_name;
            this.unit = unit;
        }
    }

    public static class Utils
    {
        private static string _tempPath = null;

        // return path to store temporary files
        public static string GetTempPath()
        {
            if (_tempPath == null) {
                try {
                    Directory.CreateDirectory(Path.Combine(Application.StartupPath, "fs_win_temp"));
                    // don't use "/", it will fail when we call explorer /select xxx/fs_win_temp\xxx.log
                    _tempPath = Path.Combine(Application.StartupPath, "fs_win_temp");
                } catch (Exception e) {
                    Logging.Error(e);
                    throw;
                }
            }
            return _tempPath;
        }

        // return a full path with filename combined which pointed to the temporary directory
        public static string GetTempPath(string filename) { return Path.Combine(GetTempPath(), filename); }

        public static string UnGzip(byte[] buf)
        {
            using (MemoryStream sb = new MemoryStream()) {
                using (MemoryStream stream = new MemoryStream(buf))
                using (GZipStream input = new GZipStream(stream, CompressionMode.Decompress, false))
                {
                    input.CopyTo(sb);
                }
                return System.Text.Encoding.UTF8.GetString(sb.ToArray());
            }
        }

        public static string FormatBandwidth(long n)
        {
            var result = GetBandwidthScale(n);
            return $"{result.value:0.##}{result.unit_name}";
        }

        public static string FormatBytes(long bytes)
        {
            const long K = 1024L;
            const long M = K * 1024L;
            const long G = M * 1024L;
            const long T = G * 1024L;
            const long P = T * 1024L;
            const long E = P * 1024L;

            if (bytes >= P * 990)
                return (bytes / (double) E).ToString("F5") + "EiB";
            if (bytes >= T * 990)
                return (bytes / (double) P).ToString("F5") + "PiB";
            if (bytes >= G * 990)
                return (bytes / (double) T).ToString("F5") + "TiB";
            if (bytes >= M * 990) {
                return (bytes / (double) G).ToString("F4") + "GiB";
            }
            if (bytes >= M * 100) {
                return (bytes / (double) M).ToString("F1") + "MiB";
            }
            if (bytes >= M * 10) {
                return (bytes / (double) M).ToString("F2") + "MiB";
            }
            if (bytes >= K * 990) {
                return (bytes / (double) M).ToString("F3") + "MiB";
            }
            if (bytes > K * 2) {
                return (bytes / (double) K).ToString("F1") + "KiB";
            }
            return bytes.ToString() + "B";
        }

        /// <summary>
        /// Return scaled bandwidth
        /// </summary>
        /// <param name="n">Raw bandwidth</param>
        /// <returns>
        /// The BandwidthScaleInfo struct
        /// </returns>
        public static BandwidthScaleInfo GetBandwidthScale(long n)
        {
            long scale = 1;
            float f = n;
            string unit = "B";
            if (f > 1024) {
                f = f / 1024;
                scale <<= 10;
                unit = "KiB";
            }
            if (f > 1024) {
                f = f / 1024;
                scale <<= 10;
                unit = "MiB";
            }
            if (f > 1024) {
                f = f / 1024;
                scale <<= 10;
                unit = "GiB";
            }
            if (f > 1024) {
                f = f / 1024;
                scale <<= 10;
                unit = "TiB";
            }
            return new BandwidthScaleInfo(f, unit, scale);
        }

        public static RegistryKey OpenRegKey(string name, bool writable, RegistryHive hive = RegistryHive.CurrentUser)
        {
            // we are building x86 binary for both x86 and x64, which will
            // cause problem when opening registry key
            // detect operating system instead of CPU
            if (name.IsNullOrEmpty()) throw new ArgumentException(nameof(name));
            try {
                RegistryKey userKey = RegistryKey.OpenBaseKey(hive,
                                                              Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32)
                                                 .OpenSubKey(name, writable);
                return userKey;
            } catch (ArgumentException ae) {
                MessageBox.Show("OpenRegKey: " + ae.ToString());
                return null;
            } catch (Exception e) {
                Logging.LogUsefulException(e);
                return null;
            }
        }

        public static bool IsTcpFastOpenSupported()
        {
#if ZERO
            const string subkey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            using (var ndpKey = OpenRegKey(subkey, false, RegistryHive.LocalMachine))
            {
                try
                {
                    if (ndpKey == null) return false;
                    var currentVersion = double.Parse(ndpKey.GetValue("CurrentVersion").ToString());
                    var currentBuild = int.Parse(ndpKey.GetValue("CurrentBuild").ToString());
                    if (currentVersion >= 6.3 && currentBuild >= 14393) return true;
                    else return false;
                }
                catch (Exception e)
                {
                    return false;
                }
            }
#else
            return Environment.OSVersion.Version.Major >= 10
                && Environment.OSVersion.Version.Build >= 14393;
#endif
        }

        #region BufferBlockCopy Wrapper

        /*
         * Usage: enable [allow unsafe code] and define INCLUDE_UNSAFE
         */
        private const int BufferBlockCopyThreshold = 1024;
#if INCLUDE_UNSAFE
        private const int UnmanagedThreshold = 128;
#endif

        /// <summary>
        /// Copy bytes effectively, if you are sure length is less than or equal to
        /// BufferBlockCopyThreshold, use the corresponding method directly
        /// </summary>
        /// <param name="src"></param>
        /// <param name="srcOff"></param>
        /// <param name="dst"></param>
        /// <param name="dstOff"></param>
        /// <param name="length"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PerfByteCopy(byte[] src, int srcOff, byte[] dst, int dstOff, int length)
        {
#if INCLUDE_UNSAFE
            if (length >= UnmanagedThreshold) {
                unsafe {
                    fixed (byte* srcPtr = &src[srcOff]) {
                        fixed (byte* dstPtr = &dst[dstOff]) {
                            CopyMemory(srcPtr, dstPtr, length);
                        }
                    }
                }
            } else {
#endif
                if (length >= BufferBlockCopyThreshold) {
                    Buffer.BlockCopy(src, srcOff, dst, dstOff, length);
                } else {
                    Array.Copy(src, srcOff, dst, dstOff, length);
                }
#if INCLUDE_UNSAFE
            }
#endif
        }

#if INCLUDE_UNSAFE

        /// <summary>
        ///     Copy data from <paramref name="srcPtr" /> into <paramref name="dstPtr" />.
        /// </summary>
        /// <remarks>
        ///     If <paramref name="srcPtr" /> or <paramref name="dstPtr" /> are not originally for byte-oriented data,
        ///     length will need to be adjusted accordingly, e.g. UInt32 pointer vs. byte pointer = 4x length.
        ///     Method auto-optimises for word size (32/64-bit) on the machine ISA.
        /// </remarks>
        /// <param name="srcPtr">Pointer to source of data.</param>
        /// <param name="dstPtr">Pointer to destination for data.</param>
        /// <param name="length">Length/quantity of data to copy, in bytes.</param>
        public static unsafe void CopyMemory(byte* srcPtr, byte* dstPtr, int length)
        {
            const int u32Size = sizeof(UInt32);
            const int u64Size = sizeof(UInt64);

            byte* srcEndPtr = srcPtr + length;

            if (IntPtr.Size == u32Size) {
                // 32-bit
                while (srcPtr + u64Size <= srcEndPtr) {
                    * (UInt32*) dstPtr = * (UInt32*) srcPtr;
                    dstPtr += u32Size;
                    srcPtr += u32Size;
                    * (UInt32*) dstPtr = * (UInt32*) srcPtr;
                    dstPtr += u32Size;
                    srcPtr += u32Size;
                }
            } else if (IntPtr.Size == u64Size) {
                // 64-bit
                const int u128Size = sizeof(UInt64) * 2;
                while (srcPtr + u128Size <= srcEndPtr) {
                    * (UInt64*) dstPtr = * (UInt64*) srcPtr;
                    dstPtr += u64Size;
                    srcPtr += u64Size;
                    * (UInt64*) dstPtr = * (UInt64*) srcPtr;
                    dstPtr += u64Size;
                    srcPtr += u64Size;
                }
                if (srcPtr + u64Size <= srcEndPtr) {
                    * (UInt64*) dstPtr ^= * (UInt64*) srcPtr;
                    dstPtr += u64Size;
                    srcPtr += u64Size;
                }
            }

            if (srcPtr + u32Size <= srcEndPtr) {
                * (UInt32*) dstPtr = * (UInt32*) srcPtr;
                dstPtr += u32Size;
                srcPtr += u32Size;
            }

            if (srcPtr + sizeof(UInt16) <= srcEndPtr) {
                * (UInt16*) dstPtr = * (UInt16*) srcPtr;
                dstPtr += sizeof(UInt16);
                srcPtr += sizeof(UInt16);
            }

            if (srcPtr + 1 <= srcEndPtr) {
                * dstPtr = * srcPtr;
            }
        }
#endif

        #endregion
    }
}