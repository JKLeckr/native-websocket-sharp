// Copyright 2026 JKLeckr
// SPDX-License-Identifier: MPL-2.0

using System;
using System.IO;
#if NETSTANDARD2_0
using System.Runtime.InteropServices;
#endif

namespace WebSocketSharp.Native;

internal class NativeHelpers
{
    internal static RuntimePlatform GetRuntimePlatform()
    {
#if NETSTANDARD2_0
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimePlatform.Windows;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimePlatform.Mac;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimePlatform.Linux;
        }
#endif

        switch (Environment.OSVersion.Platform)
        {
            case PlatformID.Win32NT:
            case PlatformID.Win32S:
            case PlatformID.Win32Windows:
            case PlatformID.WinCE:
                return RuntimePlatform.Windows;
            case PlatformID.MacOSX:
                return RuntimePlatform.Mac;
            case PlatformID.Unix:
                return File.Exists("/System/Library/CoreServices/SystemVersion.plist")
                    ? RuntimePlatform.Mac
                    : RuntimePlatform.Linux;
            default:
                return RuntimePlatform.Windows;
        }
    }

    internal static RuntimeArchitecture GetRuntimeArchitecture()
    {
#if NETSTANDARD2_0
        switch (RuntimeInformation.ProcessArchitecture)
        {
            case Architecture.X86:
                return RuntimeArchitecture.X86;
            case Architecture.Arm64:
                return RuntimeArchitecture.Arm64;
            default:
                return RuntimeArchitecture.X64;
        }
#else
        if (IntPtr.Size == 4)
        {
            return RuntimeArchitecture.X86;
        }

        string architecture = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432")
            ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")
            ?? string.Empty).ToUpperInvariant();

        if (architecture.Contains("ARM64") || architecture.Contains("AARCH64"))
        {
            return RuntimeArchitecture.Arm64;
        }

        return RuntimeArchitecture.X64;
#endif
    }
}
