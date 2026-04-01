// Copyright 2026 JKLeckr
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WebSocketSharp.Native;

internal static class WebSocketInterop {
    private static NativeResult Create(byte[] url, out NativeWebSocketHandle client) {
        NativeResult result = NativeLibLoader.Create(url, (ulong)url.Length, out IntPtr handle);
        client = result == NativeResult.Ok && handle != IntPtr.Zero
            ? new NativeWebSocketHandle(handle)
            : null;
        return result;
    }

    public static NativeResult Create(string url, out NativeWebSocketHandle client) =>
        Create(Encoding.UTF8.GetBytes(url), out client);

    public static NativeResult Connect(NativeWebSocketHandle client) =>
        NativeLibLoader.Connect(client);

    public static NativeResult Abort(NativeWebSocketHandle client, string reason, ushort code)
    {
        byte[] bytes = EncodeNullable(reason);
        return NativeLibLoader.Abort(client, code, bytes, (ulong)bytes.Length);
    }

    public static NativeResult Close(NativeWebSocketHandle client, string reason, ushort code)
    {
        byte[] bytes = EncodeNullable(reason);
        return NativeLibLoader.Close(client, code, bytes, (ulong)bytes.Length);
    }

    public static void Destroy(IntPtr client)
    {
        if (client == IntPtr.Zero) return;
        NativeLibLoader.Destroy(client);
    }

    public static NativeResult SendText(NativeWebSocketHandle client, byte[] data) =>
        NativeLibLoader.SendText(client, data, (ulong)data.Length);

    public static NativeResult SendBinary(NativeWebSocketHandle client, byte[] data) =>
        NativeLibLoader.SendBinary(client, data, (ulong)data.Length);

    public static NativeResult Ping(NativeWebSocketHandle client, byte[] data) =>
        NativeLibLoader.Ping(client, data, (ulong)data.Length);

    public static NativeResult PollEvent(NativeWebSocketHandle client, int timeoutMs, out NativeEvent nativeEvent)
    {
        var result = NativeLibLoader.PollEvent(client, timeoutMs, out NativeEventRaw raw);

        if (result != NativeResult.Ok)
        {
            nativeEvent = default;
            return result;
        }

        try
        {
            nativeEvent = new NativeEvent
            {
                Kind = (NativeEventKind)raw.kind,
                MessageKind = (NativeMessageKind)raw.message_kind,
                ErrorKind = (NativeErrorKind)raw.error_kind,
                CloseCode = raw.close_code,
                CloseWasClean = raw.close_was_clean != 0,
                Data = CopyBytes(raw.data_ptr, raw.data_len)
            };
        }
        finally
        {
            ClearEvent(ref raw);
        }

        return result;
    }

    private static void ClearEvent(ref NativeEventRaw nativeEvent) =>
        NativeLibLoader.ClearEvent(ref nativeEvent);

    public static void SetLogHandler(NativeLogCallback handler) =>
        NativeLibLoader.SetLogHandler(handler);

    public static void SetLogLevel(int level) =>
        NativeLibLoader.SetLogLevel(level);

    private static byte[] CopyBytes(IntPtr dataPtr, ulong dataLen)
    {
        if (dataPtr == IntPtr.Zero || dataLen == 0)
        {
            return [];
        }

        if (dataLen > int.MaxValue)
        {
            throw new InvalidOperationException("Native payload is too large for managed allocation.");
        }

        byte[] buffer = new byte[(int)dataLen];
        Marshal.Copy(dataPtr, buffer, 0, buffer.Length);
        return buffer;
    }

    private static byte[] EncodeNullable(string text)
    {
        return string.IsNullOrEmpty(text) ? [] : Encoding.UTF8.GetBytes(text);
    }
}
