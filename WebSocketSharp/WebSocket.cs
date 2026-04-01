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
using System.Collections.Generic;
using System.IO;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using WebSocketSharp.Net;
using WebSocketSharp.Native;

namespace WebSocketSharp;

public class WebSocket : IDisposable
{
    private static readonly byte[] EmptyBytes = [];
    private static readonly TimeSpan DefaultWaitTime = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan PingCacheWindow = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5.0);
    private static int _lastId;

    private readonly ManualResetEvent _closeCompleted;
    private readonly ManualResetEvent _connectCompleted;
    private readonly object _forMessageEventQueue;
    private readonly object _forPing;
    private readonly object _forSend;
    private readonly object _forState;
    private readonly Queue<MessageEventArgs> _messageEventQueue;
    private readonly ManualResetEvent _openEventCompleted;
    private readonly ManualResetEvent _pongReceived;

    private NativeWebSocketHandle _nativeClient;
    private DateTime _lastPongUtc;
    private Thread _pollThread;
    private bool _pollThreadStarted;
    //private string[] _protocols;
    private volatile WebSocketState _readyState;
    private readonly bool _secure;
    private ClientSslConfiguration _sslConfiguration;
    private readonly Uri _uri;
    private bool _disposed;
    private bool _connectSucceeded;
    private bool _closeReported;
    private bool _openEventPending;
    private bool _messageDispatching;
    private readonly int _id;
    private TimeSpan _waitTime;

    public bool IsAlive => ping(EmptyBytes);

    public bool IsSecure => _secure;

    public WebSocketState ReadyState => _readyState;

    // This is stubbed as tungstenite does not handle this (it automatically uses TLS 1.3 and 1.2 as needed)
    public ClientSslConfiguration SslConfiguration
    {
        get
        {
            if (!_secure)
            {
                throw new InvalidOperationException("This instance does not use a secure connection.");
            }

            return _sslConfiguration ??= new ClientSslConfiguration(_uri.DnsSafeHost);
        }
    }

    public Uri Url => _uri;

    public static Logging.NativeLogHandler NativeLogger
    {
        get => Logging.NativeLogger;
        set => Logging.NativeLogger = value;
    }

    public static Logging.NativeLogLevel NativeLogVerbosity
    {
        get => Logging.NativeLogVerbosity;
        set => Logging.NativeLogVerbosity = value;
    }

    public static bool NativeLoggingSupported => Logging.NativeLoggingSupported;

    public TimeSpan WaitTime
    {
        get => _waitTime;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException("value", "Zero or less.");
            }

            lock (_forState)
            {
                if (_readyState == WebSocketState.Closed)
                {
                    _waitTime = value;
                }
            }
        }
    }

    public event EventHandler<CloseEventArgs> OnClose;

    public event EventHandler<ErrorEventArgs> OnError;

    public event EventHandler<MessageEventArgs> OnMessage;

    public event EventHandler OnOpen;

    public WebSocket(string url, params string[] protocols)
    {
        _id = Interlocked.Increment(ref _lastId);
        Logging.EnsureInitialized();
        log_trace("constructor begin url=" + (url ?? "<null>"));

        if (url == null)
        {
            throw new ArgumentNullException("url");
        }

        if (url.Length == 0)
        {
            throw new ArgumentException("An empty string.", "url");
        }

        if (!TryCreateWebSocketUri(url, out _uri, out var uriMessage))
        {
            throw new ArgumentException(uriMessage, "url");
        }

        if (protocols != null && protocols.Length != 0)
        {
            if (!CheckProtocols(protocols, out var protocolsMessage))
            {
                throw new ArgumentException(protocolsMessage, "protocols");
            }
        }

        //_protocols = protocols ?? [];
        _secure = string.Equals(_uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
        _readyState = WebSocketState.Closed;
        _closeCompleted = new ManualResetEvent(true);
        _connectCompleted = new ManualResetEvent(false);
        _messageEventQueue = new Queue<MessageEventArgs>();
        _forMessageEventQueue = ((System.Collections.ICollection)_messageEventQueue).SyncRoot;
        _openEventCompleted = new ManualResetEvent(true);
        _forPing = new object();
        _forSend = new object();
        _forState = new object();
        _pongReceived = new ManualResetEvent(false);
        _lastPongUtc = DateTime.MinValue;
        _waitTime = DefaultWaitTime;

        NativeResult result = WebSocketInterop.Create(_uri.ToString(), out _nativeClient);
        if (result != NativeResult.Ok || _nativeClient == null || _nativeClient.IsInvalid)
        {
            log_trace("constructor native create failed result=" + result);
            throw new InvalidOperationException("The native websocket client could not be created.");
        }

        log_trace("constructor complete secure=" + _secure + " state=" + _readyState);
    }

    public void Close()
    {
        close(1005, string.Empty);
    }

    public void Close(ushort code)
    {
        ValidateCloseCode(code);
        close(code, string.Empty);
    }

    public void Close(ushort code, string reason)
    {
        ValidateCloseCode(code);
        ValidateCloseReason(code, reason);
        close(code, reason ?? string.Empty);
    }

    public void CloseAsync()
    {
        closeAsync(1005, string.Empty);
    }

    public void CloseAsync(ushort code)
    {
        ValidateCloseCode(code);
        closeAsync(code, string.Empty);
    }

    public void CloseAsync(ushort code, string reason)
    {
        ValidateCloseCode(code);
        ValidateCloseReason(code, reason);
        closeAsync(code, reason ?? string.Empty);
    }

    public void Connect()
    {
        ThrowIfDisposed();

        if (connect())
        {
            return;
        }
    }

    public void ConnectAsync()
    {
        ThrowIfDisposed();
        ValidateConnectStart();

        QueueBackground(delegate
        {
            try
            {
                Connect();
            }
            catch
            {
            }
        });
    }

    public bool Ping()
    {
        return ping(EmptyBytes);
    }

    public bool Ping(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return ping(EmptyBytes);
        }

        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(message);
        }
        catch (Exception)
        {
            throw new ArgumentException("It could not be UTF-8-encoded.", "message");
        }

        if (bytes.Length > 125)
        {
            throw new ArgumentOutOfRangeException("message", "Its size is greater than 125 bytes.");
        }

        return ping(bytes);
    }

    public void Send(string data)
    {
        if (data == null)
        {
            throw new ArgumentNullException("data");
        }

        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(data);
        }
        catch (Exception)
        {
            throw new ArgumentException("It could not be UTF-8-encoded.", "data");
        }

        send(bytes, isBinary: false);
    }

    public void SendAsync(string data, Action<bool> completed)
    {
        if (data == null)
        {
            throw new ArgumentNullException("data");
        }

        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(data);
        }
        catch (Exception)
        {
            throw new ArgumentException("It could not be UTF-8-encoded.", "data");
        }

        ValidateSendState();
        QueueBackground(delegate
        {
            bool success = false;
            try
            {
                send(bytes, isBinary: false);
                success = true;
            }
            catch
            {
                success = false;
            }

            completed?.Invoke(success);
        });
    }

    public void Dispose()
    {
        log_trace("Dispose begin state=" + _readyState + " disposed=" + _disposed);
        Dispose(true);
        GC.SuppressFinalize(this);
        log_trace("Dispose end state=" + _readyState + " disposed=" + _disposed);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            log_trace("Dispose(" + disposing + ") ignored; already disposed");
            return;
        }

        if (!disposing)
        {
            _disposed = true;
            return;
        }

        try
        {
            if (_readyState == WebSocketState.Open || _readyState == WebSocketState.Connecting)
            {
                log_trace("Dispose closing active socket state=" + _readyState);
                close(1001, string.Empty);
            }
            else if (_readyState == WebSocketState.Closing && !_closeCompleted.WaitOne(_waitTime))
            {
                log_trace("Dispose force closing timed out closing socket");
                forceClose(1001, string.Empty);
            }
        }
        catch (Exception ex)
        {
            log_trace("Dispose swallowed close exception " + ex.GetType().Name + ": " + ex.Message);
        }

        _disposed = true;

        Thread pollThread = null;
        lock (_forState)
        {
            pollThread = _pollThread;
        }

        if (pollThread != null && pollThread.IsAlive && pollThread != Thread.CurrentThread)
        {
            log_trace("Dispose joining poll thread");
            pollThread.Join(1000);
            log_trace("Dispose poll thread join complete alive=" + pollThread.IsAlive);
        }

        destroyNativeClient();
    }

    protected virtual void RaiseOnOpen() => OnOpen?.Invoke(this, EventArgs.Empty);

    protected virtual void RaiseOnClose(CloseEventArgs e) => OnClose?.Invoke(this, e);

    protected virtual void RaiseOnError(ErrorEventArgs e) => OnError?.Invoke(this, e);

    protected virtual void RaiseOnMessage(MessageEventArgs e) => OnMessage?.Invoke(this, e);

    private void clearMessageEventQueue()
    {
        lock (_forMessageEventQueue)
        {
            _messageEventQueue.Clear();
            _messageDispatching = false;
        }
    }

    private void dispatchMessageEvents()
    {
        while (true)
        {
            MessageEventArgs e;
            lock (_forMessageEventQueue)
            {
                if (_openEventPending || _messageEventQueue.Count == 0 || _readyState != WebSocketState.Open)
                {
                    if (_readyState != WebSocketState.Open)
                    {
                        _messageEventQueue.Clear();
                    }

                    _messageDispatching = false;
                    return;
                }

                e = _messageEventQueue.Dequeue();
            }

            log_trace("raising OnMessage");
            try
            {
                RaiseOnMessage(e);
            }
            catch (Exception ex)
            {
                log_trace("OnMessage threw " + ex.GetType().Name + ": " + ex.Message);
                raiseOnErrorSafely(new ErrorEventArgs("An error has occurred during an OnMessage event.", ex));
            }
            log_trace("OnMessage returned");
        }
    }

    private void dispatchOpenEvent()
    {
        try
        {
            log_trace("raising OnOpen");
            try
            {
                RaiseOnOpen();
            }
            catch (Exception ex)
            {
                log_trace("OnOpen threw " + ex.GetType().Name + ": " + ex.Message);
                raiseOnErrorSafely(new ErrorEventArgs("An error has occurred during the OnOpen event.", ex));
            }
            log_trace("OnOpen returned");

            bool startMessageDispatcher = false;
            lock (_forMessageEventQueue)
            {
                _openEventPending = false;
                if (!_messageDispatching && _messageEventQueue.Count != 0 && _readyState == WebSocketState.Open)
                {
                    _messageDispatching = true;
                    startMessageDispatcher = true;
                }
            }

            if (startMessageDispatcher)
            {
                log_trace("starting OnMessage dispatcher");
                QueueBackground(dispatchMessageEvents);
            }
        }
        finally
        {
            _openEventCompleted.Set();
        }
    }

    private void enqueueMessageEvent(MessageEventArgs e)
    {
        bool startDispatcher = false;
        lock (_forMessageEventQueue)
        {
            _messageEventQueue.Enqueue(e);
            if (!_openEventPending && !_messageDispatching && _readyState == WebSocketState.Open)
            {
                _messageDispatching = true;
                startDispatcher = true;
            }
        }

        if (startDispatcher)
        {
            log_trace("starting OnMessage dispatcher");
            QueueBackground(dispatchMessageEvents);
        }
    }

    private void raiseOnCloseSafely(CloseEventArgs e)
    {
        try
        {
            RaiseOnClose(e);
        }
        catch (Exception ex)
        {
            log_trace("OnClose threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void raiseOnErrorSafely(ErrorEventArgs e)
    {
        try
        {
            RaiseOnError(e);
        }
        catch (Exception ex)
        {
            log_trace("OnError threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool CheckProtocols(string[] protocols, out string message)
    {
        message = null;
        for (int i = 0; i < protocols.Length; i++)
        {
            string protocol = protocols[i];
            if (string.IsNullOrEmpty(protocol) || !IsToken(protocol))
            {
                message = "It contains a value that is not a token.";
                return false;
            }

            for (int j = i + 1; j < protocols.Length; j++)
            {
                if (protocols[j] == protocol)
                {
                    message = "It contains a value twice.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsCloseStatusCode(ushort code)
    {
        return code > 999 && code < 5000;
    }

    private static bool IsToken(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c < ' ' || c > '~')
            {
                return false;
            }

            switch (c)
            {
                case '(':
                case ')':
                case '<':
                case '>':
                case '@':
                case ',':
                case ';':
                case ':':
                case '\\':
                case '"':
                case '/':
                case '[':
                case ']':
                case '?':
                case '=':
                case '{':
                case '}':
                case ' ':
                case '\t':
                    return false;
            }
        }

        return true;
    }

    private static bool TryCreateWebSocketUri(string uriString, out Uri result, out string message)
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
        if (scheme != "ws" && scheme != "wss")
        {
            message = "The scheme part is not 'ws' or 'wss'.";
            return false;
        }

        if (uri.Port == 0)
        {
            message = "The port part is zero.";
            return false;
        }

        if (uri.Fragment.Length > 0)
        {
            message = "It includes the fragment component.";
            return false;
        }

        if (uri.Port != -1)
        {
            result = uri;
            return true;
        }

        result = new Uri(string.Format("{0}://{1}:{2}{3}", scheme, uri.Host, scheme == "ws" ? 80 : 443, uri.PathAndQuery));
        return true;
    }

    private static void QueueBackground(ThreadStart action)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            action();
        });
    }

    private void log_trace(string message)
    {
        Logging.Write(_id, message);
    }

    private void close(ushort code, string reason)
    {
        ThrowIfDisposed();
        log_trace("close begin code=" + code + " reasonLength=" + (reason == null ? 0 : reason.Length) + " state=" + _readyState);

        if (_readyState == WebSocketState.Closed)
        {
            log_trace("close ignored; already closed");
            return;
        }

        if (_readyState == WebSocketState.Closing)
        {
            log_trace("close ignored; already closing");
            return;
        }

        NativeResult result;
        lock (_forState)
        {
            if (_readyState == WebSocketState.Closed || _readyState == WebSocketState.Closing)
            {
                return;
            }

            _closeCompleted.Reset();
            _readyState = WebSocketState.Closing;
            startPollingLoop();
            NativeWebSocketHandle client = getNativeClient();
            result = client == null
                ? NativeResult.Disposed
                : WebSocketInterop.Close(client, reason, code);
            log_trace("native close result=" + result);
        }

        if (result == NativeResult.Ok)
        {
            if (!_closeCompleted.WaitOne(_waitTime))
            {
                log_trace("close timed out after " + _waitTime.TotalMilliseconds + "ms; forcing close");
                forceClose(code, reason ?? string.Empty);
            }
            log_trace("close complete state=" + _readyState);
            return;
        }

        if (result == NativeResult.InvalidState || result == NativeResult.Disposed)
        {
            log_trace("close finalizing after native result=" + result);
            finalizeClosedState(new CloseEventArgs(code, reason ?? string.Empty, false), raiseEvent: false);
            return;
        }

        log_trace("close throwing result=" + result);
        throw CreateCommandException(result, "An error has occurred while attempting to close.");
    }

    private void closeAsync(ushort code, string reason)
    {
        ThrowIfDisposed();
        QueueBackground(delegate
        {
            try
            {
                close(code, reason);
            }
            catch
            {
            }
        });
    }

    private bool connect()
    {
        if (_readyState == WebSocketState.Open)
        {
            log_trace("connect ignored; already open");
            return true;
        }

        ValidateConnectStart();
        log_trace("connect begin state=" + _readyState + " uri=" + _uri);

        NativeResult result;
        lock (_forState)
        {
            _connectSucceeded = false;
            _connectCompleted.Reset();
            _closeCompleted.Reset();
            _closeReported = false;
            _readyState = WebSocketState.Connecting;
            startPollingLoop();
            NativeWebSocketHandle client = getNativeClient();
            result = client == null
                ? NativeResult.Disposed
                : WebSocketInterop.Connect(client);
            log_trace("native connect result=" + result);
        }

        if (result != NativeResult.Ok)
        {
            _readyState = WebSocketState.Closed;
            log_trace("connect throwing native result=" + result);
            throw CreateCommandException(result, "An error has occurred while attempting to connect.");
        }

        log_trace("connect waiting for native open/error");
        _connectCompleted.WaitOne();
        if (_connectSucceeded)
        {
            _openEventCompleted.WaitOne();
        }
        log_trace("connect wait complete succeeded=" + _connectSucceeded + " state=" + _readyState);
        return _connectSucceeded;
    }

    private void destroyNativeClient()
    {
        NativeWebSocketHandle handle = null;
        lock (_forState)
        {
            if (_nativeClient == null)
            {
                log_trace("destroyNativeClient ignored; no native handle");
                return;
            }

            handle = _nativeClient;
            _nativeClient = null;
        }

        log_trace("destroyNativeClient disposing native handle");
        handle.Dispose();
    }

    private void finalizeClosedState(CloseEventArgs closeEvent, bool raiseEvent)
    {
        bool shouldRaise = false;
        lock (_forState)
        {
            _readyState = WebSocketState.Closed;
            _connectSucceeded = false;
            _connectCompleted.Set();
            _closeCompleted.Set();
            _lastPongUtc = DateTime.MinValue;
            if (raiseEvent && !_closeReported)
            {
                _closeReported = true;
                shouldRaise = true;
            }
        }

        clearMessageEventQueue();
        log_trace("finalizeClosedState code=" + closeEvent.Code + " wasClean=" + closeEvent.WasClean + " raise=" + shouldRaise);
        if (shouldRaise)
        {
            log_trace("raising OnClose");
            raiseOnCloseSafely(closeEvent);
            log_trace("OnClose returned");
        }
    }

    private void forceClose(ushort code, string reason)
    {
        log_trace("forceClose code=" + code + " reasonLength=" + (reason == null ? 0 : reason.Length));
        abortNativeClient(code, reason);
        finalizeClosedState(new CloseEventArgs(code, reason ?? string.Empty, false), raiseEvent: true);
    }

    private void abortNativeClient(ushort code, string reason)
    {
        NativeWebSocketHandle handle;
        lock (_forState)
        {
            handle = getNativeClient();
        }

        if (handle != null)
        {
            NativeResult result = WebSocketInterop.Abort(handle, reason, code);
            log_trace("native abort result=" + result);
        }
        else
        {
            log_trace("native abort skipped; no handle");
        }
    }

    private NativeWebSocketHandle getNativeClient()
    {
        NativeWebSocketHandle handle = _nativeClient;
        return handle == null || handle.IsInvalid || handle.IsClosed ? null : handle;
    }

    private bool hasNativeClient() => getNativeClient() != null;

    private Exception CreateCommandException(NativeResult result, string defaultMessage)
    {
        return result switch
        {
            NativeResult.InvalidState => new InvalidOperationException(defaultMessage),
            NativeResult.NotOpen => new InvalidOperationException("The current state of the connection is not Open."),
            NativeResult.Disposed => new ObjectDisposedException(GetType().FullName),
            NativeResult.InvalidArgument => new ArgumentException(defaultMessage),
            NativeResult.Timeout => new TimeoutException(defaultMessage),
            _ => new InvalidOperationException(defaultMessage),
        };
    }

    private Exception CreateErrorException(NativeErrorKind kind, string message)
    {
        return kind switch
        {
            NativeErrorKind.Timeout => new TimeoutException(message),
            NativeErrorKind.TlsFailed => new AuthenticationException(message),
            NativeErrorKind.Io or NativeErrorKind.ConnectFailed => new IOException(message),
            _ => new InvalidOperationException(message),
        };
    }

    private void handleNativeClose(NativeEvent nativeEvent)
    {
        string reason = decodeString(nativeEvent.Data);
        log_trace("native event close code=" + nativeEvent.CloseCode + " wasClean=" + nativeEvent.CloseWasClean + " reasonLength=" + reason.Length);
        finalizeClosedState(new CloseEventArgs(nativeEvent.CloseCode, reason, nativeEvent.CloseWasClean), raiseEvent: true);
    }

    private void handleNativeError(NativeEvent nativeEvent)
    {
        string message = decodeString(nativeEvent.Data);
        Exception exception = CreateErrorException(nativeEvent.ErrorKind, message);
        log_trace("native event error kind=" + nativeEvent.ErrorKind + " state=" + _readyState + " message=" + message);

        lock (_forState)
        {
            if (_readyState == WebSocketState.Connecting)
            {
                _readyState = WebSocketState.Closed;
                _connectSucceeded = false;
                _connectCompleted.Set();
                _closeCompleted.Set();
            }
        }

        log_trace("raising OnError");
        raiseOnErrorSafely(new ErrorEventArgs(message, exception));
        log_trace("OnError returned");
    }

    private void handleNativeEvent(NativeEvent nativeEvent)
    {
        log_trace("handleNativeEvent kind=" + nativeEvent.Kind + " state=" + _readyState);
        switch (nativeEvent.Kind)
        {
            case NativeEventKind.Open:
                _openEventCompleted.Reset();
                lock (_forMessageEventQueue)
                {
                    _openEventPending = true;
                }
                lock (_forState)
                {
                    _readyState = WebSocketState.Open;
                    _connectSucceeded = true;
                    _connectCompleted.Set();
                }
                log_trace("native event open; starting OnOpen dispatcher");
                QueueBackground(dispatchOpenEvent);
                return;

            case NativeEventKind.Close:
                handleNativeClose(nativeEvent);
                return;

            case NativeEventKind.Message:
                if (nativeEvent.MessageKind == NativeMessageKind.Text)
                {
                    log_trace("native event text message bytes=" + (nativeEvent.Data == null ? 0 : nativeEvent.Data.Length));
                    enqueueMessageEvent(new MessageEventArgs(decodeString(nativeEvent.Data)));
                }
                else
                {
                    log_trace("native event binary message bytes=" + (nativeEvent.Data == null ? 0 : nativeEvent.Data.Length));
                    enqueueMessageEvent(new MessageEventArgs(Opcode.Binary, nativeEvent.Data ?? []));
                }
                return;

            case NativeEventKind.Error:
                handleNativeError(nativeEvent);
                return;

            case NativeEventKind.Pong:
                log_trace("native event pong bytes=" + (nativeEvent.Data == null ? 0 : nativeEvent.Data.Length));
                _lastPongUtc = DateTime.UtcNow;
                _pongReceived.Set();
                return;
        }
    }

    private bool hasRecentPong()
    {
        DateTime lastPongUtc = _lastPongUtc;
        return lastPongUtc != DateTime.MinValue && DateTime.UtcNow - lastPongUtc <= PingCacheWindow;
    }

    private bool ping(byte[] payload)
    {
        ThrowIfDisposed();
        log_trace("ping begin bytes=" + (payload == null ? 0 : payload.Length) + " state=" + _readyState);

        if (_readyState != WebSocketState.Open)
        {
            log_trace("ping false; state=" + _readyState);
            return false;
        }

        if (hasRecentPong())
        {
            log_trace("ping true; recent pong");
            return true;
        }

        lock (_forPing)
        {
            if (_readyState != WebSocketState.Open)
            {
                return false;
            }

            if (hasRecentPong())
            {
                return true;
            }

            _pongReceived.Reset();
            NativeWebSocketHandle client = getNativeClient();
            if (client == null)
            {
                log_trace("ping false; no native handle");
                return false;
            }

            NativeResult result = WebSocketInterop.Ping(client, payload);
            if (result != NativeResult.Ok)
            {
                log_trace("native ping result=" + result);
                return false;
            }

            bool pong = _pongReceived.WaitOne(PingTimeout);
            log_trace("ping wait complete pong=" + pong);
            return pong;
        }
    }

    private void pollLoop()
    {
        log_trace("pollLoop start");
        try
        {
            while (true)
            {
                NativeWebSocketHandle client = getNativeClient();
                if (client == null)
                {
                    log_trace("pollLoop exit; no native handle");
                    return;
                }

                NativeResult result = WebSocketInterop.PollEvent(client, 50, out NativeEvent nativeEvent);
                if (result == NativeResult.Ok)
                {
                    log_trace("native poll event kind=" + nativeEvent.Kind);
                    handleNativeEvent(nativeEvent);
                }
                else if (result != NativeResult.Timeout)
                {
                    log_trace("native poll failure result=" + result);
                    handlePollFailure(result);
                    return;
                }

                if (_readyState == WebSocketState.Closed)
                {
                    log_trace("pollLoop exit; ready state closed");
                    return;
                }
            }
        }
        finally
        {
            log_trace("pollLoop finally");
            lock (_forState)
            {
                _pollThreadStarted = false;
                _pollThread = null;
            }

            if (_readyState == WebSocketState.Closed)
            {
                destroyNativeClient();
            }
        }
    }

    private void handlePollFailure(NativeResult result)
    {
        Exception exception = CreateCommandException(result, "The native websocket poller failed.");
        string message = exception.Message;

        log_trace("handlePollFailure result=" + result + " message=" + message);
        raiseOnErrorSafely(new ErrorEventArgs(message, exception));
        finalizeClosedState(new CloseEventArgs(1006, string.Empty, false), raiseEvent: true);
    }

    private void send(byte[] data, bool isBinary)
    {
        ThrowIfDisposed();
        ValidateSendState();
        log_trace("send begin kind=" + (isBinary ? "binary" : "text") + " bytes=" + (data == null ? 0 : data.Length) + " state=" + _readyState);

        lock (_forSend)
        {
            ValidateSendState();

            NativeWebSocketHandle client = getNativeClient() ?? throw new ObjectDisposedException(GetType().FullName);

            NativeResult result = isBinary
                ? WebSocketInterop.SendBinary(client, data)
                : WebSocketInterop.SendText(client, data);
            log_trace("native send result=" + result);

            if (result != NativeResult.Ok)
            {
                throw CreateCommandException(result, "The message could not be sent.");
            }
        }
    }

    private string decodeString(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(data);
    }

    private void startPollingLoop()
    {
        lock (_forState)
        {
            if (_pollThreadStarted || !hasNativeClient())
            {
                log_trace("startPollingLoop skipped started=" + _pollThreadStarted + " hasClient=" + hasNativeClient());
                return;
            }

            Thread pollThread = new(pollLoop)
            {
                IsBackground = true,
                Name = "websocket-sharp-native-poll"
            };
            _pollThread = pollThread;
            _pollThreadStarted = true;
            pollThread.Start();
            log_trace("startPollingLoop started thread");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private void ValidateCloseCode(ushort code)
    {
        if (!IsCloseStatusCode(code))
        {
            throw new ArgumentOutOfRangeException("code", "Less than 1000 or greater than 4999.");
        }

        if (code == 1011)
        {
            throw new ArgumentException("1011 cannot be used.", "code");
        }
    }

    private void ValidateCloseReason(ushort code, string reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return;
        }

        if (code == 1005)
        {
            throw new ArgumentException("1005 cannot be used.", "code");
        }

        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(reason);
        }
        catch (Exception)
        {
            throw new ArgumentException("It could not be UTF-8-encoded.", "reason");
        }

        if (bytes.Length > 123)
        {
            throw new ArgumentOutOfRangeException("reason", "Its size is greater than 123 bytes.");
        }
    }

    private void ValidateConnectStart()
    {
        if (_readyState == WebSocketState.Closing)
        {
            throw new InvalidOperationException("The close process is in progress.");
        }

        if (_readyState == WebSocketState.Connecting)
        {
            throw new InvalidOperationException("The connection is already in progress.");
        }
    }

    private void ValidateSendState()
    {
        if (_readyState != WebSocketState.Open)
        {
            throw new InvalidOperationException("The current state of the connection is not Open.");
        }

        if (!hasNativeClient())
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }
}
