﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ParsecVDisplay
{
    internal static class ParsecVDD
    {
        public const string NAME = "Parsec Virtual Display";

        public const string DISPLAY_ID = "PSCCDD0";
        public const string DISPLAY_NAME = "ParsecVDA";

        public const string ADAPTER = "Parsec Virtual Display Adapter";
        public const string ADAPTER_GUID = "{00b41627-04c4-429e-a26e-0265cf50c8fa}";

        public const string HARDWARE_ID = @"Root\Parsec\VDA";
        public const string CLASS_GUID = "{4d36e968-e325-11ce-bfc1-08002be10318}";

        static IntPtr VddHandle;

        // actually 16 devices could be created per adapter
        // so just use a half to avoid plugging lag
        public static int MAX_DISPLAYS => 8;

        public static bool Init()
        {
            if (Device.OpenHandle(ADAPTER_GUID, out VddHandle))
            {
                Ping();
                return true;
            }

            return false;
        }

        public static void Uninit()
        {
            Device.CloseHandle(VddHandle);
        }

        public static List<Display> GetDisplays(out bool noMonitors)
        {
            var displays = Display.GetAllDisplays();
            noMonitors = displays.Count == 0;

            displays = displays.FindAll(d => d.DisplayName
                .Equals(DISPLAY_ID, StringComparison.OrdinalIgnoreCase));

            noMonitors = displays.Count == 0 && noMonitors;
            return displays;
        }

        public static List<Display> GetDisplays()
        {
            return GetDisplays(out var _);
        }

        public static Device.Status QueryStatus()
        {
            return Device.QueryStatus(CLASS_GUID, HARDWARE_ID);
        }

        public static bool QueryVersion(out string version)
        {
            if (Core.IoControl(VddHandle, Core.IOCTL_VERSION, null, out int vernum, 100))
            {
                int major = 0;
                int minor = vernum & 0xFFFF;
                version = $"{major}.{minor}";
                return true;
            }
            else
            {
                version = "0.???";
                return false;
            }
        }

        public static bool AddDisplay(out int index)
        {
            if (Core.IoControl(VddHandle, Core.IOCTL_ADD, null, out index, 5000))
            {
                Ping();
                return true;
            }

            return false;
        }

        public static bool RemoveDisplay(int index)
        {
            var input = new byte[2];
            input[1] = (byte)(index & 0xFF);

            if (Core.IoControl(VddHandle, Core.IOCTL_REMOVE, input, 1000))
            {
                Ping();
                return true;
            }

            return false;
        }

        public static void Ping()
        {
            Core.IoControl(VddHandle, Core.IOCTL_UPDATE, null, 1000);
        }

        static unsafe class Core
        {
            public const uint IOCTL_ADD = 0x22E004;
            public const uint IOCTL_REMOVE = 0x22A008;
            public const uint IOCTL_UPDATE = 0x22A00C;
            public const uint IOCTL_VERSION = 0x22E010;

            // new code in driver v0.45
            // relates to IOCTL_UPDATE and per display state
            // but unused in Parsec app
            public const uint IOCTL_UNKNOWN1 = 0x22A014;

            static bool IoControl(IntPtr handle, uint code, byte[] input, int* result, int timeout)
            {
                var InBuffer = new byte[32];
                var Overlapped = new Native.OVERLAPPED();

                if (input != null && input.Length > 0)
                {
                    Array.Copy(input, InBuffer, Math.Min(input.Length, InBuffer.Length));
                }

                fixed (byte* buffer = InBuffer)
                {
                    int outputLength = result != null ? sizeof(int) : 0;
                    Overlapped.hEvent = Native.CreateEvent(null, false, false, null);

                    Native.DeviceIoControl(handle, code,
                        buffer, InBuffer.Length,
                        result, outputLength,
                        null, ref Overlapped);

                    bool success = Native.GetOverlappedResultEx(handle, ref Overlapped,
                        out var NumberOfBytesTransferred, timeout, false);

                    if (Overlapped.hEvent != IntPtr.Zero)
                        Native.CloseHandle(Overlapped.hEvent);

                    return success;
                }
            }

            public static bool IoControl(IntPtr handle, uint code, byte[] input, int timeout)
            {
                return IoControl(handle, code, input, null, timeout);
            }

            public static bool IoControl(IntPtr handle, uint code, byte[] input, out int result, int timeout)
            {
                int output;
                bool success = IoControl(handle, code, input, &output, timeout);
                result = output;
                return success;
            }
        }

        public static IList<Display.Mode> GetCustomDisplayModes()
        {
            var list = new List<Display.Mode>();

            using (var vdd = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Parsec\\vdd", RegistryKeyPermissionCheck.ReadSubTree))
            {
                if (vdd != null)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        using (var index = vdd.OpenSubKey($"{i}", RegistryKeyPermissionCheck.ReadSubTree))
                        {
                            if (index != null)
                            {
                                var width = index.GetValue("width");
                                var height = index.GetValue("height");
                                var hz = index.GetValue("hz");

                                if (width != null && height != null && hz != null)
                                {
                                    list.Add(new Display.Mode
                                    {
                                        Width = Convert.ToUInt16(width),
                                        Height = Convert.ToUInt16(height),
                                        Hz = Convert.ToUInt16(hz),
                                    });
                                }
                            }
                        }
                    }
                }
            }

            return list;
        }

        // Requires admin perm
        public static void SetCustomDisplayModes(List<Display.Mode> modes)
        {
            using (var vdd = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Parsec\\vdd", RegistryKeyPermissionCheck.ReadWriteSubTree))
            {
                if (vdd != null)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        using (var index = vdd.CreateSubKey($"{i}", RegistryKeyPermissionCheck.ReadWriteSubTree))
                        {
                            if (i >= modes.Count && index != null)
                            {
                                index.Dispose();
                                vdd.DeleteSubKey($"{i}");
                            }
                            else if (index != null)
                            {
                                index.SetValue("width", modes[i].Width, RegistryValueKind.DWord);
                                index.SetValue("height", modes[i].Height, RegistryValueKind.DWord);
                                index.SetValue("hz", modes[i].Hz, RegistryValueKind.DWord);
                            }
                        }
                    }
                }
            }
        }

        public enum ParentGPU
        {
            Auto = 0,
            NVIDIA = 0x10DE,
            AMD = 0x1002,
        }

        public static ParentGPU GetParentGPU()
        {
            using (var parameters = Registry.LocalMachine.OpenSubKey(
                "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\WUDF\\Services\\ParsecVDA\\Parameters",
                RegistryKeyPermissionCheck.ReadSubTree))
            {
                if (parameters != null)
                {
                    object value = parameters.GetValue("PreferredRenderAdapterVendorId");
                    if (value != null)
                    {
                        return (ParentGPU)Convert.ToInt32(value);
                    }
                }
            }

            return ParentGPU.Auto;
        }

        // Requires admin perm
        public static void SetParentGPU(ParentGPU kind)
        {
            using (var parameters = Registry.LocalMachine.OpenSubKey(
                "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\WUDF\\Services\\ParsecVDA\\Parameters",
                RegistryKeyPermissionCheck.ReadWriteSubTree))
            {
                if (parameters != null)
                {
                    if (kind == ParentGPU.Auto)
                    {
                        parameters.DeleteValue("PreferredRenderAdapterVendorId", false);
                    }
                    else
                    {
                        parameters.SetValue("PreferredRenderAdapterVendorId",
                            (uint)kind, RegistryValueKind.DWord);
                    }
                }
            }
        }

        static unsafe class Native
        {
            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeviceIoControl(
                IntPtr device, uint code,
                void* lpInBuffer, int nInBufferSize,
                void* lpOutBuffer, int nOutBufferSize,
                void* lpBytesReturned,
                ref OVERLAPPED lpOverlapped
            );

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetOverlappedResultEx(
                IntPtr handle,
                ref OVERLAPPED lpOverlapped,
                out uint lpNumberOfBytesTransferred,
                int dwMilliseconds,
                [MarshalAs(UnmanagedType.Bool)] bool bAlertable
            );

            [StructLayout(LayoutKind.Sequential)]
            public struct OVERLAPPED
            {
                public IntPtr Internal;
                public IntPtr InternalHigh;
                public IntPtr Pointer;
                public IntPtr hEvent;
            }

            [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateEvent(
                void* lpEventAttributes,
                [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
                [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
                string lpName
            );

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}