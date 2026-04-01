// Copyright 2026 JKLeckr
// SPDX-License-Identifier: MPL-2.0

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WebSocketSharp.Native;

internal static class NativeLibLoader
{
    private const string Windows32Library = "nativews-win32.dll";
    private const string Windows64Library = "nativews-win64.dll";
    private const string WindowsArm64Library = "nativews-winarm64.dll";
    private const string Linux64Library = "nativews-linux-amd64.so";
    private const string LinuxArm64Library = "nativews-linux-arm64.so";
    private const string MacLibrary = "nativews-macos-universal.dylib";

    private static readonly object Sync = new();
    private static NativeFunctionTable _functions;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeResult nws_client_create_delegate(byte[] urlPtr, ulong urlLen, out IntPtr client);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void nws_client_destroy_delegate(IntPtr client);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeResult nws_client_abort_delegate(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeResult nws_client_connect_delegate(NativeWebSocketHandle client);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeResult nws_client_close_delegate(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeResult nws_client_send_text_delegate(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeResult nws_client_send_binary_delegate(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeResult nws_client_ping_delegate(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeResult nws_client_poll_event_delegate(NativeWebSocketHandle client, int timeoutMs, out NativeEventRaw nativeEvent);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void nws_event_clear_delegate(ref NativeEventRaw nativeEvent);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void nws_set_log_handler_delegate(NativeLogCallback handler);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void nws_set_log_level_delegate(int level);

    private sealed class NativeFunctionTable
    {
        public IntPtr ModuleHandle;
        public string LibraryPath;
        public nws_client_create_delegate Create;
        public nws_client_destroy_delegate Destroy;
        public nws_client_abort_delegate Abort;
        public nws_client_connect_delegate Connect;
        public nws_client_close_delegate Close;
        public nws_client_send_text_delegate SendText;
        public nws_client_send_binary_delegate SendBinary;
        public nws_client_ping_delegate Ping;
        public nws_client_poll_event_delegate PollEvent;
        public nws_event_clear_delegate ClearEvent;
        public nws_set_log_handler_delegate SetLogHandler;
        public nws_set_log_level_delegate SetLogLevel;
    }

    internal static NativeResult Create(byte[] urlPtr, ulong urlLen, out IntPtr client)
    {
        return GetFunctions().Create(urlPtr, urlLen, out client);
    }

    internal static void Destroy(IntPtr client)
    {
        GetFunctions().Destroy(client);
    }

    internal static NativeResult Abort(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen)
    {
        return GetFunctions().Abort(client, code, reasonPtr, reasonLen);
    }

    internal static NativeResult Connect(NativeWebSocketHandle client)
    {
        return GetFunctions().Connect(client);
    }

    internal static NativeResult Close(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen)
    {
        return GetFunctions().Close(client, code, reasonPtr, reasonLen);
    }

    internal static NativeResult SendText(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen)
    {
        return GetFunctions().SendText(client, dataPtr, dataLen);
    }

    internal static NativeResult SendBinary(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen)
    {
        return GetFunctions().SendBinary(client, dataPtr, dataLen);
    }

    internal static NativeResult Ping(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen)
    {
        return GetFunctions().Ping(client, dataPtr, dataLen);
    }

    internal static NativeResult PollEvent(NativeWebSocketHandle client, int timeoutMs, out NativeEventRaw nativeEvent)
    {
        return GetFunctions().PollEvent(client, timeoutMs, out nativeEvent);
    }

    internal static void ClearEvent(ref NativeEventRaw nativeEvent)
    {
        GetFunctions().ClearEvent(ref nativeEvent);
    }

    internal static void SetLogHandler(NativeLogCallback handler)
    {
        NativeFunctionTable functions = GetFunctions();
        if (functions.SetLogHandler == null)
        {
            throw CreateMissingExportException(functions.LibraryPath, "nws_set_log_handler");
        }

        functions.SetLogHandler(handler);
    }

    internal static void SetLogLevel(int level)
    {
        NativeFunctionTable functions = GetFunctions();
        if (functions.SetLogLevel == null)
        {
            throw CreateMissingExportException(functions.LibraryPath, "nws_set_log_level");
        }

        functions.SetLogLevel(level);
    }

    private static NativeFunctionTable GetFunctions()
    {
        NativeFunctionTable functions = _functions;
        if (functions != null)
        {
            return functions;
        }

        lock (Sync)
        {
            _functions ??= LoadFunctions();
            return _functions;
        }
    }

    private static NativeFunctionTable LoadFunctions()
    {
        return NativeHelpers.GetRuntimePlatform() switch
        {
            RuntimePlatform.Windows => LoadWindowsFunctions(),
            RuntimePlatform.Mac => LoadMacFunctions(),
            _ => NativeHelpers.GetRuntimeArchitecture() == RuntimeArchitecture.Arm64
                                ? LoadLinuxArm64Functions()
                                : LoadLinux64Functions(),
        };

    }

    private static NativeFunctionTable LoadWindowsFunctions()
    {
        string libraryPath = Path.Combine(GetNativeLibraryDirectory(), GetWindowsLibraryName());
        if (!File.Exists(libraryPath))
        {
            throw new DllNotFoundException(
                "The native websocket library could not be found at '" + libraryPath + "'.");
        }

        IntPtr moduleHandle = LoadLibrary(libraryPath);
        if (moduleHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new DllNotFoundException(
                "The native websocket library could not be loaded from '" + libraryPath +
                "' (LoadLibrary error " + error + ").");
        }

        return new NativeFunctionTable
        {
            ModuleHandle = moduleHandle,
            LibraryPath = libraryPath,
            Create = GetDelegate<nws_client_create_delegate>(moduleHandle, "nws_client_create", libraryPath),
            Destroy = GetDelegate<nws_client_destroy_delegate>(moduleHandle, "nws_client_destroy", libraryPath),
            Abort = GetDelegate<nws_client_abort_delegate>(moduleHandle, "nws_client_abort", libraryPath),
            Connect = GetDelegate<nws_client_connect_delegate>(moduleHandle, "nws_client_connect", libraryPath),
            Close = GetDelegate<nws_client_close_delegate>(moduleHandle, "nws_client_close", libraryPath),
            SendText = GetDelegate<nws_client_send_text_delegate>(moduleHandle, "nws_client_send_text", libraryPath),
            SendBinary = GetDelegate<nws_client_send_binary_delegate>(moduleHandle, "nws_client_send_binary", libraryPath),
            Ping = GetDelegate<nws_client_ping_delegate>(moduleHandle, "nws_client_ping", libraryPath),
            PollEvent = GetDelegate<nws_client_poll_event_delegate>(moduleHandle, "nws_client_poll_event", libraryPath),
            ClearEvent = GetDelegate<nws_event_clear_delegate>(moduleHandle, "nws_event_clear", libraryPath),
            SetLogHandler = GetOptionalDelegate<nws_set_log_handler_delegate>(moduleHandle, "nws_set_log_handler"),
            SetLogLevel = GetOptionalDelegate<nws_set_log_level_delegate>(moduleHandle, "nws_set_log_level")
        };
    }

    private static NativeFunctionTable LoadLinux64Functions()
    {
        return new NativeFunctionTable
        {
            LibraryPath = Linux64Library,
            Create = Linux64NLib.nws_client_create,
            Destroy = Linux64NLib.nws_client_destroy,
            Abort = Linux64NLib.nws_client_abort,
            Connect = Linux64NLib.nws_client_connect,
            Close = Linux64NLib.nws_client_close,
            SendText = Linux64NLib.nws_client_send_text,
            SendBinary = Linux64NLib.nws_client_send_binary,
            Ping = Linux64NLib.nws_client_ping,
            PollEvent = Linux64NLib.nws_client_poll_event,
            ClearEvent = Linux64NLib.nws_event_clear,
            SetLogHandler = Linux64NLib.nws_set_log_handler,
            SetLogLevel = Linux64NLib.nws_set_log_level
        };
    }

    private static NativeFunctionTable LoadLinuxArm64Functions()
    {
        return new NativeFunctionTable
        {
            LibraryPath = LinuxArm64Library,
            Create = LinuxArm64NLib.nws_client_create,
            Destroy = LinuxArm64NLib.nws_client_destroy,
            Abort = LinuxArm64NLib.nws_client_abort,
            Connect = LinuxArm64NLib.nws_client_connect,
            Close = LinuxArm64NLib.nws_client_close,
            SendText = LinuxArm64NLib.nws_client_send_text,
            SendBinary = LinuxArm64NLib.nws_client_send_binary,
            Ping = LinuxArm64NLib.nws_client_ping,
            PollEvent = LinuxArm64NLib.nws_client_poll_event,
            ClearEvent = LinuxArm64NLib.nws_event_clear,
            SetLogHandler = LinuxArm64NLib.nws_set_log_handler,
            SetLogLevel = LinuxArm64NLib.nws_set_log_level
        };
    }

    private static NativeFunctionTable LoadMacFunctions()
    {
        return new NativeFunctionTable
        {
            LibraryPath = MacLibrary,
            Create = MacNLib.nws_client_create,
            Destroy = MacNLib.nws_client_destroy,
            Abort = MacNLib.nws_client_abort,
            Connect = MacNLib.nws_client_connect,
            Close = MacNLib.nws_client_close,
            SendText = MacNLib.nws_client_send_text,
            SendBinary = MacNLib.nws_client_send_binary,
            Ping = MacNLib.nws_client_ping,
            PollEvent = MacNLib.nws_client_poll_event,
            ClearEvent = MacNLib.nws_event_clear,
            SetLogHandler = MacNLib.nws_set_log_handler,
            SetLogLevel = MacNLib.nws_set_log_level
        };
    }

    private static string GetNativeLibraryDirectory()
    {
        string assemblyLocation = typeof(NativeLibLoader).Assembly.Location;
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            string directory = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrEmpty(directory))
            {
                return directory;
            }
        }

        return AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
    }

    private static string GetWindowsLibraryName()
    {
        return NativeHelpers.GetRuntimeArchitecture() switch
        {
            RuntimeArchitecture.X86 => Windows32Library,
            RuntimeArchitecture.Arm64 => WindowsArm64Library,
            _ => Windows64Library,
        };
    }

    private static T GetDelegate<T>(IntPtr moduleHandle, string exportName, string libraryPath)
        where T : class
    {
        IntPtr procAddress = GetProcAddress(moduleHandle, exportName);
        if (procAddress == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException(
                "The native websocket library '" + libraryPath +
                "' does not export '" + exportName + "'.");
        }

        return (T)(object)Marshal.GetDelegateForFunctionPointer(procAddress, typeof(T));
    }

    private static T GetOptionalDelegate<T>(IntPtr moduleHandle, string exportName)
        where T : class
    {
        IntPtr procAddress = GetProcAddress(moduleHandle, exportName);
        return procAddress == IntPtr.Zero
            ? null
            : (T)(object)Marshal.GetDelegateForFunctionPointer(procAddress, typeof(T));
    }

    private static EntryPointNotFoundException CreateMissingExportException(string libraryPath, string exportName)
    {
        return new EntryPointNotFoundException(
            $"The native websocket library '{libraryPath}' does not export '{exportName}'."
        );
    }

    private static class Linux64NLib
    {
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_create")]
        internal static extern NativeResult nws_client_create(byte[] urlPtr, ulong urlLen, out IntPtr client);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_destroy")]
        internal static extern void nws_client_destroy(IntPtr client);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_abort")]
        internal static extern NativeResult nws_client_abort(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_connect")]
        internal static extern NativeResult nws_client_connect(NativeWebSocketHandle client);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_close")]
        internal static extern NativeResult nws_client_close(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_send_text")]
        internal static extern NativeResult nws_client_send_text(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_send_binary")]
        internal static extern NativeResult nws_client_send_binary(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_ping")]
        internal static extern NativeResult nws_client_ping(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_poll_event")]
        internal static extern NativeResult nws_client_poll_event(NativeWebSocketHandle client, int timeoutMs, out NativeEventRaw nativeEvent);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_event_clear")]
        internal static extern void nws_event_clear(ref NativeEventRaw nativeEvent);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_set_log_handler")]
        internal static extern void nws_set_log_handler(NativeLogCallback handler);
        [DllImport(Linux64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_set_log_level")]
        internal static extern void nws_set_log_level(int level);
    }

    private static class LinuxArm64NLib
    {
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_create")]
        internal static extern NativeResult nws_client_create(byte[] urlPtr, ulong urlLen, out IntPtr client);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_destroy")]
        internal static extern void nws_client_destroy(IntPtr client);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_abort")]
        internal static extern NativeResult nws_client_abort(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_connect")]
        internal static extern NativeResult nws_client_connect(NativeWebSocketHandle client);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_close")]
        internal static extern NativeResult nws_client_close(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_send_text")]
        internal static extern NativeResult nws_client_send_text(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_send_binary")]
        internal static extern NativeResult nws_client_send_binary(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_ping")]
        internal static extern NativeResult nws_client_ping(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_poll_event")]
        internal static extern NativeResult nws_client_poll_event(NativeWebSocketHandle client, int timeoutMs, out NativeEventRaw nativeEvent);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_event_clear")]
        internal static extern void nws_event_clear(ref NativeEventRaw nativeEvent);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_set_log_handler")]
        internal static extern void nws_set_log_handler(NativeLogCallback handler);
        [DllImport(LinuxArm64Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_set_log_level")]
        internal static extern void nws_set_log_level(int level);
    }

    private static class MacNLib
    {
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_create")]
        internal static extern NativeResult nws_client_create(byte[] urlPtr, ulong urlLen, out IntPtr client);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_destroy")]
        internal static extern void nws_client_destroy(IntPtr client);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_abort")]
        internal static extern NativeResult nws_client_abort(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_connect")]
        internal static extern NativeResult nws_client_connect(NativeWebSocketHandle client);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_close")]
        internal static extern NativeResult nws_client_close(NativeWebSocketHandle client, ushort code, byte[] reasonPtr, ulong reasonLen);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_send_text")]
        internal static extern NativeResult nws_client_send_text(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_send_binary")]
        internal static extern NativeResult nws_client_send_binary(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_ping")]
        internal static extern NativeResult nws_client_ping(NativeWebSocketHandle client, byte[] dataPtr, ulong dataLen);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_client_poll_event")]
        internal static extern NativeResult nws_client_poll_event(NativeWebSocketHandle client, int timeoutMs, out NativeEventRaw nativeEvent);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_event_clear")]
        internal static extern void nws_event_clear(ref NativeEventRaw nativeEvent);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_set_log_handler")]
        internal static extern void nws_set_log_handler(NativeLogCallback handler);
        [DllImport(MacLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nws_set_log_level")]
        internal static extern void nws_set_log_level(int level);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
}
