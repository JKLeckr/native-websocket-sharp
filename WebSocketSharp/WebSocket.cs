// Copyright 2026 JKLeckr
// SPDX-License-Identifier: MPL-2.0
//
// This file derived from websocket-sharp:
//
//   The MIT License (MIT)
//
//   Copyright (c) 2010-2026 sta.blockhead
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy
//   of this software and associated documentation files (the "Software"), to deal
//   in the Software without restriction, including without limitation the rights
//   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//   copies of the Software, and to permit persons to whom the Software is
//   furnished to do so, subject to the following conditions:
//
//   The above copyright notice and this permission notice shall be included in
//   all copies or substantial portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//   THE SOFTWARE.

using System;
using WebSocketSharp.Net;

namespace WebSocketSharp;

public class WebSocket : IDisposable
{
    private string[] _protocols;

    private volatile WebSocketState _readyState;

    private bool _secure;

    private ClientSslConfiguration _sslConfiguration;

    private Uri _uri;

    public bool IsAlive
    {
        get
        {
            // Original websocket-sharp treats IsAlive as stronger than ReadyState
            // and performs a ping-based liveness check. This stub preserves that
            // distinction by not pretending ReadyState alone is sufficient.
            //Ping(EmptyBytes);
            throw new NotImplementedException("Implement ping-based liveness semantics.");
        }
    }

    public bool IsSecure => _secure;

    public WebSocketState ReadyState => _readyState;

    public ClientSslConfiguration SslConfiguration
    {
        get
        {
            if (!_secure)
                throw new InvalidOperationException("This instance does not use a secure connection.");

            return _sslConfiguration ??= new ClientSslConfiguration(_uri.DnsSafeHost);
        }
    }

    public Uri Url => _uri;

    public event EventHandler<CloseEventArgs> OnClose;

    public event EventHandler<ErrorEventArgs> OnError;

    public event EventHandler<MessageEventArgs> OnMessage;

    public event EventHandler OnOpen;

    public WebSocket(string url, params string[] protocols)
    {
        if (url == null)
		{
			throw new ArgumentNullException("url");
		}
        if (url.Length == 0)
        {
            throw new ArgumentException("An empty string.", "url");
        }
        if (!tryCreateWebSocketUri(url, out _uri, out var text))
        {
            throw new ArgumentException(text, "url");
        }
        _protocols = protocols ?? [];
        _secure = string.Equals(_uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
        _readyState = WebSocketState.Connecting;
    }

    private static bool tryCreateWebSocketUri(string uriString, out Uri result, out string message)
    {
        result = null;
        message = null;
        Uri.TryCreate(uriString, UriKind.Absolute, out Uri uri);
        if (uri == null)
        {
            message = "An invalid URI string.";
            return false;
        }
        if (!uri.IsAbsoluteUri)
        {
            message = "A relative URI.";
            return false;
        }
        string scheme = uri.Scheme;
        if (!(scheme == "ws") && !(scheme == "wss"))
        {
            message = "The scheme part is not 'ws' or 'wss'.";
            return false;
        }
        int port = uri.Port;
        if (port == 0)
        {
            message = "The port part is zero.";
            return false;
        }
        if (uri.Fragment.Length > 0)
        {
            message = "It includes the fragment component.";
            return false;
        }
        result = ((port != -1) ? uri : new Uri(string.Format("{0}://{1}:{2}{3}", scheme, uri.Host, (scheme == "ws") ? 80 : 443, uri.PathAndQuery)));
        return true;
    }

    public void Close()
    {
        Close(1005, string.Empty);
    }

    public void Close(ushort code)
    {
        Close(code, string.Empty);
    }

    public void Close(ushort code, string reason)
    {
        throw new NotImplementedException("Implement synchronous connect and OnOpen/OnError/OnClose behavior.");
    }

    public void CloseAsync()
    {
        CloseAsync(1005, string.Empty);
    }

    public void CloseAsync(ushort code)
    {
        CloseAsync(code, string.Empty);
    }

    public void CloseAsync(ushort code, string reason)
    {
        throw new NotImplementedException("Implement async close worker behavior.");
    }

    public void Connect()
    {
        /*if (_readyState == WebSocketState.Closing)
		{
			string text2 = "The close process is in progress.";
			throw new InvalidOperationException(text2);
		}
		if (_retryCountForConnect > _maxRetryCountForConnect)
		{
			string text3 = "A series of reconnecting has failed.";
			throw new InvalidOperationException(text3);
		}
		if (connect())
		{
			open();
		}*/
        throw new NotImplementedException("Implement synchronous connect and OnOpen/OnError/OnClose behavior.");
    }

    public void ConnectAsync()
    {
        /*if (!_client)
		{
			string text = "This instance is not a client.";
			throw new InvalidOperationException(text);
		}
		if (_readyState == WebSocketState.Closing)
		{
			string text2 = "The close process is in progress.";
			throw new InvalidOperationException(text2);
		}
		if (_retryCountForConnect > _maxRetryCountForConnect)
		{
			string text3 = "A series of reconnecting has failed.";
			throw new InvalidOperationException(text3);
		}
		Func<bool> connector = connect;
		connector.BeginInvoke(delegate(IAsyncResult ar)
		{
			if (connector.EndInvoke(ar))
			{
				open();
			}
		}, null);*/
        throw new NotImplementedException("Implement async connect for MultiClient.NET on .NET40.");
    }

    public bool Ping()
    {
        throw new NotImplementedException("Implement ping with empty bytes");
    }

    public bool Ping(string message)
    {
        throw new NotImplementedException("Implement ping");
    }

    public void Send(string data)
    {
        throw new NotImplementedException("Implement UTF-8 text send semantics.");
    }

    public void SendAsync(string data, Action<bool> completed)
    {
        throw new NotImplementedException("Implement async UTF-8 text send and completion callback semantics.");
    }

    void IDisposable.Dispose()
    {
        // Fully implement this later
        //Close(1001, string.Empty);
    }

    // The helpers below are intentionally protected so a future implementation can
    // preserve websocket-sharp's event model while keeping transport internals separate.
    protected virtual void RaiseOnOpen()
    {
        OnOpen?.Invoke(this, EventArgs.Empty);
    }

    protected virtual void RaiseOnClose(CloseEventArgs e)
    {
        OnClose?.Invoke(this, e);
    }

    protected virtual void RaiseOnError(ErrorEventArgs e)
    {
        OnError?.Invoke(this, e);
    }

    protected virtual void RaiseOnMessage(MessageEventArgs e)
    {
        OnMessage?.Invoke(this, e);
    }
}
