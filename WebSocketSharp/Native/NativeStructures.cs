// Copyright 2026 JKLeckr
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Runtime.InteropServices;

namespace WebSocketSharp.Native;

internal enum NativeResult
{
    Ok = 0,
    Timeout = 1,
    InvalidState = 2,
    InvalidArgument = 3,
    NotOpen = 4,
    Disposed = 5,
    InternalError = 6,
    Unknown = -1
}

internal enum NativeErrorKind
{
    ConnectFailed = 1,
    TlsFailed = 2,
    Io = 3,
    Protocol = 4,
    Timeout = 5,
    Internal = 6,
    Unknown = -1
}

internal enum NativeEventKind
{
    Open = 1,
    Close = 2,
    Message = 3,
    Error = 4,
    Pong = 5
}

internal enum NativeMessageKind
{
    Text = 1,
    Binary = 2
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void NativeLogCallback(int level, IntPtr message);

internal struct NativeEvent
{
    public NativeEventKind Kind;
    public NativeMessageKind MessageKind;
    public NativeErrorKind ErrorKind;
    public ushort CloseCode;
    public bool CloseWasClean;
    public byte[] Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEventRaw
{
    public int kind;
    public int message_kind;
    public int error_kind;
    public ushort close_code;
    public byte close_was_clean;
    public IntPtr data_ptr;
    public ulong data_len;
}
