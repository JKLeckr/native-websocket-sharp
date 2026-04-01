// Copyright 2026 JKLeckr
// SPDX-License-Identifier: MPL-2.0

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using WebSocketSharp.Native;

namespace WebSocketSharp;

public static class Logging
{
    public enum NativeLogLevel
    {
        Off = 0,
        Error = 1,
        Warn = 2,
        Info = 3,
        Debug = 4,
        Trace = 5
    }

    public delegate void NativeLogHandler(NativeLogLevel level, string message);

    private const string TraceEnvironmentVariable = "NWS_LOGGING";
    private const string TraceFileEnvironmentVariable = "NWS_LOG_FILE";
    private const string TraceMarkerFileName = "nativews.log.enable";
    private const string DefaultTraceFileName = "native-websocket-sharp.log";

    private static readonly object Sync = new();
    private static readonly NativeLogCallback NativeLogBridge = HandleNativeLog;

    private static NativeLogHandler _nativeLogger;
    private static NativeLogLevel _nativeLogVerbosity = NativeLogLevel.Off;
    private static bool _initialized;
    private static bool _nativeLoggingSupported;
    private static string _traceFilePath;

    internal static NativeLogHandler NativeLogger
    {
        get
        {
            lock (Sync)
            {
                return _nativeLogger;
            }
        }
        set
        {
            lock (Sync)
            {
                EnsureInitializedLocked();
                _nativeLogger = value;
                ApplyNativeLoggingLocked();
            }
        }
    }

    internal static NativeLogLevel NativeLogVerbosity
    {
        get
        {
            lock (Sync)
            {
                return _nativeLogVerbosity;
            }
        }
        set
        {
            lock (Sync)
            {
                EnsureInitializedLocked();
                _nativeLogVerbosity = value;
                ApplyNativeLoggingLocked();
            }
        }
    }

    internal static bool NativeLoggingSupported
    {
        get
        {
            lock (Sync)
            {
                return _nativeLoggingSupported;
            }
        }
    }

    internal static void EnsureInitialized()
    {
        lock (Sync)
        {
            EnsureInitializedLocked();
        }
    }

    internal static void Write(int socketId, string message)
    {
        Write("managed", socketId, message);
    }

    private static void EnsureInitializedLocked()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (IsTraceEnabled())
        {
            _traceFilePath = ResolveTraceFilePath();
            _nativeLogger ??= WriteNativeLog;
            if (_nativeLogVerbosity == NativeLogLevel.Off)
            {
                _nativeLogVerbosity = NativeLogLevel.Trace;
            }

            Write("managed", 0, "trace enabled file=" + _traceFilePath);
        }

        ApplyNativeLoggingLocked();
    }

    private static void ApplyNativeLoggingLocked()
    {
        try
        {
            WebSocketInterop.SetLogLevel((int)_nativeLogVerbosity);
            WebSocketInterop.SetLogHandler(_nativeLogger == null ? null : NativeLogBridge);
            _nativeLoggingSupported = true;
        }
        catch (Exception ex)
        {
            _nativeLoggingSupported = false;
            Write("managed", 0, "native logging unavailable: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool IsTraceEnabled()
    {
        string value = Environment.GetEnvironmentVariable(TraceEnvironmentVariable);
        if (!string.IsNullOrEmpty(value) &&
            !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return File.Exists(Path.Combine(GetBaseDirectory(), TraceMarkerFileName)) ||
               File.Exists(Path.Combine(Environment.CurrentDirectory, TraceMarkerFileName));
    }

    private static string ResolveTraceFilePath()
    {
        string value = Environment.GetEnvironmentVariable(TraceFileEnvironmentVariable);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        return Path.Combine(GetBaseDirectory(), DefaultTraceFileName);
    }

    private static string GetBaseDirectory()
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        return string.IsNullOrEmpty(baseDirectory) ? Environment.CurrentDirectory : baseDirectory;
    }

    private static void HandleNativeLog(int level, IntPtr message)
    {
        NativeLogHandler logger;
        lock (Sync)
        {
            logger = _nativeLogger;
        }

        if (logger == null)
        {
            return;
        }

        try
        {
            string text = message == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(message);
            logger(ClampLogLevel(level), text ?? string.Empty);
        }
        catch
        {
        }
    }

    private static NativeLogLevel ClampLogLevel(int level)
    {
        if (level <= (int)NativeLogLevel.Off)
        {
            return NativeLogLevel.Off;
        }
        if (level >= (int)NativeLogLevel.Trace)
        {
            return NativeLogLevel.Trace;
        }

        return (NativeLogLevel)level;
    }

    private static void WriteNativeLog(NativeLogLevel level, string message)
    {
        Write("native/" + level, 0, message);
    }

    private static void Write(string source, int socketId, string message)
    {
        string traceFilePath;
        lock (Sync)
        {
            if (!_initialized)
            {
                EnsureInitializedLocked();
            }
            traceFilePath = _traceFilePath;
        }

        if (string.IsNullOrEmpty(traceFilePath))
        {
            return;
        }

        string id = socketId == 0 ? "-" : socketId.ToString();
        string line = $"{DateTime.UtcNow:O} [{source}] [ws {id}] [thread {Thread.CurrentThread.ManagedThreadId}] {message ?? string.Empty}";

        try
        {
            lock (Sync)
            {
                File.AppendAllText(traceFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
