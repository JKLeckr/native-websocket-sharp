// Copyright 2026 JKLeckr
// SPDX-License-Identifier: MPL-2.0

using System;
using Microsoft.Win32.SafeHandles;

namespace WebSocketSharp.Native;

internal sealed class NativeWebSocketHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeWebSocketHandle()
        : base(true)
    {
    }

    public NativeWebSocketHandle(IntPtr handle)
        : base(true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        WebSocketInterop.Destroy(handle);
        handle = IntPtr.Zero;
        return true;
    }
}
