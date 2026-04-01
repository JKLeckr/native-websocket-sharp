// Copyright 2026 JKLeckr
// SPDX-License-Identifier: MPL-2.0

#![allow(non_camel_case_types)]

mod logging;
use logging::*;

use std::collections::VecDeque;
use std::io;
use std::net::{TcpStream, ToSocketAddrs};
use std::ptr;
use std::slice;
use std::sync::{Condvar, Mutex};
use std::time::{Duration, Instant};

use tungstenite::error::Error as WsError;
use tungstenite::extensions::{ExtensionsConfig, compression::deflate::DeflateConfig};
use tungstenite::handshake::MidHandshake;
use tungstenite::handshake::client::ClientHandshake;
use tungstenite::http::Uri;
use tungstenite::protocol::frame::coding::CloseCode;
use tungstenite::protocol::frame::{CloseFrame, Utf8Bytes};
use tungstenite::protocol::{Message, WebSocketConfig};
use tungstenite::stream::MaybeTlsStream;
use tungstenite::{HandshakeError, WebSocket, client_tls_with_config};

type NativeWebSocket = WebSocket<MaybeTlsStream<TcpStream>>;
type NativeHandshake = MidHandshake<ClientHandshake<MaybeTlsStream<TcpStream>>>;

const WAIT_QUANTUM_MS: u64 = 10;
const CONNECT_TIMEOUT_MS: u64 = 5000;
const DEFAULT_CLOSE_CODE: u16 = 1005;

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum nws_error_kind {
    NWS_ERR_CONNECT_FAILED = 1,
    NWS_ERR_TLS_FAILED = 2,
    NWS_ERR_IO = 3,
    NWS_ERR_PROTOCOL = 4,
    NWS_ERR_TIMEOUT = 5,
    NWS_ERR_INTERNAL = 6,
    NWS_ERR_UNKNOWN = -1,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct nws_event {
    pub kind: i32,
    pub message_kind: i32,
    pub error_kind: i32,
    pub close_code: u16,
    pub close_was_clean: u8,
    pub data_ptr: *const u8,
    pub data_len: u64,
}

impl Default for nws_event {
    fn default() -> Self {
        Self {
            kind: 0,
            message_kind: 0,
            error_kind: 0,
            close_code: 0,
            close_was_clean: 0,
            data_ptr: ptr::null(),
            data_len: 0,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum nws_event_kind {
    NWS_EVENT_OPEN = 1,
    NWS_EVENT_CLOSE = 2,
    NWS_EVENT_MESSAGE = 3,
    NWS_EVENT_ERROR = 4,
    NWS_EVENT_PONG = 5,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum nws_message_kind {
    NWS_MSG_TEXT = 1,
    NWS_MSG_BINARY = 2,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum nws_result {
    NWS_OK = 0,
    NWS_TIMEOUT = 1,
    NWS_INVALID_STATE = 2,
    NWS_INVALID_ARGUMENT = 3,
    NWS_NOT_OPEN = 4,
    NWS_DISPOSED = 5,
    NWS_INTERNAL_ERROR = 6,
    NWS_UNKNOWN = -1,
}

fn event_name(kind: nws_event_kind) -> &'static str {
    match kind {
        nws_event_kind::NWS_EVENT_OPEN => "open",
        nws_event_kind::NWS_EVENT_CLOSE => "close",
        nws_event_kind::NWS_EVENT_MESSAGE => "message",
        nws_event_kind::NWS_EVENT_ERROR => "error",
        nws_event_kind::NWS_EVENT_PONG => "pong",
    }
}

enum State {
    Created,
    Handshaking(NativeHandshake),
    Open(NativeWebSocket),
    Closed,
}

pub struct Client {
    signal: Condvar,
    inner: Mutex<InnerClient>,
}

struct CloseContext {
    code: u16,
    reason: String,
    was_clean: bool,
}

struct InnerClient {
    url: String,
    uri: Uri,
    state: State,
    connect_requested: bool,
    pending_messages: VecDeque<Message>,
    pending_close: Option<PendingClose>,
    needs_flush: bool,
    events: VecDeque<OwnedEvent>,
    close_reported: bool,
    close_context: Option<CloseContext>,
}

struct OwnedEvent {
    kind: nws_event_kind,
    message_kind: Option<nws_message_kind>,
    error_kind: Option<nws_error_kind>,
    close_code: u16,
    close_was_clean: bool,
    data: Vec<u8>,
}

struct PendingClose {
    code: u16,
    reason: String,
}

impl OwnedEvent {
    fn open() -> Self {
        Self {
            kind: nws_event_kind::NWS_EVENT_OPEN,
            message_kind: None,
            error_kind: None,
            close_code: 0,
            close_was_clean: false,
            data: Vec::new(),
        }
    }

    fn message(kind: nws_message_kind, data: Vec<u8>) -> Self {
        Self {
            kind: nws_event_kind::NWS_EVENT_MESSAGE,
            message_kind: Some(kind),
            error_kind: None,
            close_code: 0,
            close_was_clean: false,
            data,
        }
    }

    fn error(kind: nws_error_kind, data: Vec<u8>) -> Self {
        Self {
            kind: nws_event_kind::NWS_EVENT_ERROR,
            message_kind: None,
            error_kind: Some(kind),
            close_code: 0,
            close_was_clean: false,
            data,
        }
    }

    fn close(code: u16, was_clean: bool, data: Vec<u8>) -> Self {
        Self {
            kind: nws_event_kind::NWS_EVENT_CLOSE,
            message_kind: None,
            error_kind: None,
            close_code: code,
            close_was_clean: was_clean,
            data,
        }
    }

    fn pong(data: Vec<u8>) -> Self {
        Self {
            kind: nws_event_kind::NWS_EVENT_PONG,
            message_kind: None,
            error_kind: None,
            close_code: 0,
            close_was_clean: false,
            data,
        }
    }

    fn into_ffi(self, out_event: &mut nws_event) {
        *out_event = nws_event::default();
        out_event.kind = self.kind as i32;
        out_event.message_kind = self.message_kind.map_or(0, |kind| kind as i32);
        out_event.error_kind = self.error_kind.map_or(0, |kind| kind as i32);
        out_event.close_code = self.close_code;
        out_event.close_was_clean = u8::from(self.close_was_clean);
        if self.data.is_empty() {
            return;
        }

        let boxed = self.data.into_boxed_slice();
        out_event.data_len = boxed.len() as u64;
        let raw_data: *mut [u8] = Box::into_raw(boxed);
        out_event.data_ptr = raw_data as *mut u8 as *const u8;
    }
}

impl InnerClient {
    fn new(url: String) -> Result<Self, nws_result> {
        let uri = parse_uri(&url)?;

        Ok(Self {
            url,
            uri,
            state: State::Created,
            connect_requested: false,
            pending_messages: VecDeque::new(),
            pending_close: None,
            needs_flush: false,
            events: VecDeque::new(),
            close_reported: false,
            close_context: None,
        })
    }

    fn is_open(&self) -> bool {
        matches!(self.state, State::Open(_))
    }

    fn request_connect(&mut self) -> nws_result {
        log_native(LOG_DEBUG, "request_connect".to_owned());
        match self.state {
            State::Created if !self.connect_requested => {
                self.connect_requested = true;
                log_native(LOG_DEBUG, "request_connect accepted".to_owned());
                nws_result::NWS_OK
            }
            _ => {
                log_native(
                    LOG_WARN,
                    "request_connect rejected invalid state".to_owned(),
                );
                nws_result::NWS_INVALID_STATE
            }
        }
    }

    fn request_message(&mut self, message: Message) -> nws_result {
        if !self.is_open() || self.pending_close.is_some() {
            log_native(
                LOG_WARN,
                "request_message rejected not open or closing".to_owned(),
            );
            return nws_result::NWS_NOT_OPEN;
        }
        let description = match &message {
            Message::Text(text) => format!("request_message text bytes={}", text.len()),
            Message::Binary(data) => format!("request_message binary bytes={}", data.len()),
            Message::Ping(data) => format!("request_message ping bytes={}", data.len()),
            Message::Pong(data) => format!("request_message pong bytes={}", data.len()),
            Message::Close(_) => "request_message close".to_owned(),
            Message::Frame(_) => "request_message frame".to_owned(),
        };
        log_native(LOG_DEBUG, description);
        self.pending_messages.push_back(message);
        nws_result::NWS_OK
    }

    fn request_close(&mut self, code: u16, reason: String) -> nws_result {
        log_native(
            LOG_DEBUG,
            format!("request_close code={code} reason_len={}", reason.len()),
        );
        match self.state {
            State::Created => {
                self.connect_requested = false;
                self.state = State::Closed;
                self.record_close(code, reason, false);
                nws_result::NWS_OK
            }
            State::Handshaking(_) | State::Open(_) => {
                if self.pending_close.is_none() {
                    self.pending_close = Some(PendingClose { code, reason });
                }
                nws_result::NWS_OK
            }
            State::Closed => nws_result::NWS_INVALID_STATE,
        }
    }

    fn abort(&mut self, code: u16, reason: String) {
        log_native(
            LOG_WARN,
            format!("abort code={code} reason_len={}", reason.len()),
        );
        self.connect_requested = false;
        self.pending_messages.clear();
        self.pending_close = None;
        self.needs_flush = false;
        self.state = State::Closed;
        self.record_close(code, reason, false);
    }

    fn drive_once(&mut self, max_wait: Duration) {
        if !self.events.is_empty() {
            return;
        }

        match &mut self.state {
            State::Created => {
                if self.connect_requested && !max_wait.is_zero() {
                    self.start_connect();
                }
            }
            State::Handshaking(_) => {
                if let Some(close) = self.pending_close.take() {
                    self.state = State::Closed;
                    self.record_close(close.code, close.reason, false);
                } else {
                    self.drive_handshake();
                }
            }
            State::Open(_) => self.drive_open(),
            State::Closed => {}
        }
    }

    fn drive_handshake(&mut self) {
        let current = match std::mem::replace(&mut self.state, State::Closed) {
            State::Handshaking(midh) => midh,
            other => {
                self.state = other;
                return;
            }
        };

        match current.handshake() {
            Ok((socket, _)) => {
                log_native(
                    LOG_INFO,
                    "drive_handshake complete; opening socket".to_owned(),
                );
                self.state = State::Open(socket);
                self.events.push_back(OwnedEvent::open());
            }
            Err(HandshakeError::Interrupted(mid)) => {
                log_native(LOG_TRACE, "drive_handshake interrupted".to_owned());
                self.state = State::Handshaking(mid);
            }
            Err(HandshakeError::Failure(err)) => {
                log_native(LOG_ERROR, format!("drive_handshake failed: {err}"));
                self.record_error(map_ws_error_kind(&err, true), err.to_string());
                self.state = State::Closed;
            }
        }
    }

    fn drive_open(&mut self) {
        let mut socket = match std::mem::replace(&mut self.state, State::Closed) {
            State::Open(socket) => socket,
            other => {
                self.state = other;
                return;
            }
        };
        let mut keep_socket = true;

        if let Some(close) = self.pending_close.take() {
            log_native(
                LOG_DEBUG,
                format!(
                    "drive_open sending close code={} reason_len={}",
                    close.code,
                    close.reason.len()
                ),
            );
            let frame = CloseFrame {
                code: CloseCode::from(close.code),
                reason: Utf8Bytes::from(close.reason.clone()),
            };
            self.close_context = Some(CloseContext {
                code: close.code,
                reason: close.reason,
                was_clean: false,
            });
            match socket.close(Some(frame)) {
                Ok(()) => self.needs_flush = true,
                Err(err) if is_would_block_error(&err) => {
                    log_native(LOG_TRACE, "drive_open close would block".to_owned());
                    self.needs_flush = true
                }
                Err(err) => {
                    log_native(LOG_ERROR, format!("drive_open close failed: {err}"));
                    keep_socket = false;
                    self.handle_terminal_error(err, false);
                }
            }
        }

        while keep_socket && self.pending_close.is_none() {
            let Some(message) = self.pending_messages.pop_front() else {
                break;
            };
            match socket.write(message.clone()) {
                Ok(()) => {
                    log_native(LOG_TRACE, "drive_open write accepted".to_owned());
                    self.needs_flush = true
                }
                Err(WsError::WriteBufferFull(message)) => {
                    log_native(LOG_WARN, "drive_open write buffer full".to_owned());
                    self.pending_messages.push_front(*message);
                    self.needs_flush = true;
                    break;
                }
                Err(err) if is_would_block_error(&err) => {
                    log_native(LOG_TRACE, "drive_open write would block".to_owned());
                    self.pending_messages.push_front(message);
                    self.needs_flush = true;
                    break;
                }
                Err(err) => {
                    log_native(LOG_ERROR, format!("drive_open write failed: {err}"));
                    keep_socket = false;
                    self.handle_terminal_error(err, false);
                }
            }
        }

        if keep_socket && self.needs_flush {
            match socket.flush() {
                Ok(()) => {
                    log_native(LOG_TRACE, "drive_open flush complete".to_owned());
                    self.needs_flush = false
                }
                Err(err) if is_would_block_error(&err) => {
                    log_native(LOG_TRACE, "drive_open flush would block".to_owned());
                }
                Err(err) => {
                    log_native(LOG_ERROR, format!("drive_open flush failed: {err}"));
                    keep_socket = false;
                    self.handle_terminal_error(err, false);
                }
            }
        }

        if keep_socket {
            match socket.read() {
                Ok(message) => {
                    log_native(LOG_TRACE, "drive_open read message".to_owned());
                    self.handle_incoming_message(message);
                    if matches!(
                        self.events.back().map(|event| event.kind),
                        Some(nws_event_kind::NWS_EVENT_CLOSE)
                    ) {
                        self.needs_flush = true;
                    }
                }
                Err(err) if is_would_block_error(&err) => {}
                Err(WsError::ConnectionClosed) => {
                    log_native(LOG_INFO, "drive_open connection closed".to_owned());
                    keep_socket = false;
                    self.finalize_close(true);
                }
                Err(err) => {
                    log_native(LOG_ERROR, format!("drive_open read failed: {err}"));
                    keep_socket = false;
                    self.handle_terminal_error(err, false);
                }
            }
        }

        self.state = if keep_socket {
            State::Open(socket)
        } else {
            State::Closed
        };
    }

    fn finalize_close(&mut self, was_clean_close: bool) {
        if self.close_reported {
            return;
        }
        let (code, reason, clean) = match self.close_context.take() {
            Some(close) => (close.code, close.reason, close.was_clean || was_clean_close),
            None => (DEFAULT_CLOSE_CODE, String::new(), was_clean_close),
        };
        self.record_close(code, reason, clean);
    }

    fn handle_incoming_message(&mut self, message: Message) {
        match message {
            Message::Text(text) => {
                log_native(LOG_DEBUG, format!("incoming text bytes={}", text.len()));
                self.events.push_back(OwnedEvent::message(
                    nws_message_kind::NWS_MSG_TEXT,
                    text.as_bytes().to_vec(),
                ));
            }
            Message::Binary(data) => {
                log_native(LOG_DEBUG, format!("incoming binary bytes={}", data.len()));
                self.events.push_back(OwnedEvent::message(
                    nws_message_kind::NWS_MSG_BINARY,
                    data.to_vec(),
                ));
            }
            Message::Pong(data) => {
                log_native(LOG_DEBUG, format!("incoming pong bytes={}", data.len()));
                self.events.push_back(OwnedEvent::pong(data.to_vec()));
            }
            Message::Ping(_) => {
                log_native(LOG_DEBUG, "incoming ping".to_owned());
                self.needs_flush = true;
            }
            Message::Close(frame) => {
                let (code, reason) = close_frame_parts(frame);
                log_native(
                    LOG_INFO,
                    format!("incoming close code={code} reason_len={}", reason.len()),
                );
                self.record_close(code, reason, true);
            }
            Message::Frame(_) => {}
        }
    }

    fn handle_terminal_error(&mut self, err: WsError, was_clean_close: bool) {
        log_native(
            LOG_ERROR,
            format!("terminal error clean={was_clean_close}: {err}"),
        );
        if !matches!(err, WsError::ConnectionClosed) {
            self.record_error(map_ws_error_kind(&err, false), err.to_string());
        }
        self.finalize_close(was_clean_close);
    }

    fn record_close(&mut self, code: u16, reason: String, was_clean: bool) {
        log_native(
            LOG_INFO,
            format!(
                "record_close code={code} clean={was_clean} reason_len={}",
                reason.len()
            ),
        );
        self.close_context = Some(CloseContext {
            code,
            reason: reason.clone(),
            was_clean,
        });
        if self.close_reported {
            return;
        }
        self.close_reported = true;
        self.events
            .push_back(OwnedEvent::close(code, was_clean, reason.into_bytes()));
    }

    fn record_error(&mut self, kind: nws_error_kind, message: String) {
        log_native(
            LOG_ERROR,
            format!("record_error kind={kind:?} message={message}"),
        );
        self.events
            .push_back(OwnedEvent::error(kind, message.into_bytes()));
    }

    fn start_connect(&mut self) {
        log_native(LOG_INFO, format!("start_connect url={}", self.url));
        self.connect_requested = false;

        let stream = match connect_tcp(&self.uri, Duration::from_millis(CONNECT_TIMEOUT_MS)) {
            Ok(stream) => stream,
            Err(err) => {
                log_native(LOG_ERROR, format!("connect_tcp failed: {err}"));
                self.record_error(map_connect_error_kind(&err), err.to_string());
                self.state = State::Closed;
                return;
            }
        };
        log_native(LOG_DEBUG, "connect_tcp complete".to_owned());

        if let Err(err) = stream.set_nodelay(true) {
            log_native(LOG_ERROR, format!("set_nodelay failed: {err}"));
            self.record_error(nws_error_kind::NWS_ERR_IO, err.to_string());
            self.state = State::Closed;
            return;
        }

        let secure = matches!(self.uri.scheme_str(), Some("wss"));
        log_native(LOG_DEBUG, format!("start_connect secure={secure}"));
        if !secure && let Err(err) = stream.set_nonblocking(true) {
            log_native(LOG_ERROR, format!("set_nonblocking plain failed: {err}"));
            self.record_error(nws_error_kind::NWS_ERR_IO, err.to_string());
            self.state = State::Closed;
            return;
        }

        match client_tls_with_config(self.url.clone(), stream, Some(websocket_config()), None) {
            Ok((mut socket, _)) => {
                log_native(LOG_INFO, "client_tls_with_config complete".to_owned());
                if secure && let Err(err) = set_socket_nonblocking(socket.get_mut()) {
                    log_native(LOG_ERROR, format!("set_nonblocking secure failed: {err}"));
                    self.record_error(nws_error_kind::NWS_ERR_IO, err.to_string());
                    self.state = State::Closed;
                    return;
                }
                log_native(LOG_INFO, "start_connect open event queued".to_owned());
                self.state = State::Open(socket);
                self.events.push_back(OwnedEvent::open());
            }
            Err(HandshakeError::Interrupted(mid)) => {
                log_native(
                    LOG_WARN,
                    format!("client_tls_with_config interrupted secure={secure}"),
                );
                if secure {
                    self.record_error(
                        nws_error_kind::NWS_ERR_INTERNAL,
                        "Secure handshake unexpectedly returned an interrupted state.".to_owned(),
                    );
                    self.state = State::Closed;
                } else {
                    self.state = State::Handshaking(mid);
                }
            }
            Err(HandshakeError::Failure(err)) => {
                log_native(LOG_ERROR, format!("client_tls_with_config failed: {err}"));
                self.record_error(map_ws_error_kind(&err, true), err.to_string());
                self.state = State::Closed;
            }
        }
    }
}

fn client_from_ptr<'a>(client: *mut Client) -> Result<&'a Client, nws_result> {
    if client.is_null() {
        return Err(nws_result::NWS_DISPOSED);
    }
    Ok(unsafe { &*client })
}

fn close_frame_parts(frame: Option<CloseFrame>) -> (u16, String) {
    match frame {
        Some(frame) => (u16::from(frame.code), frame.reason.to_string()),
        None => (DEFAULT_CLOSE_CODE, String::new()),
    }
}

fn connect_tcp(uri: &Uri, timeout: Duration) -> io::Result<TcpStream> {
    let host = uri.host().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "The URL does not include a host name.",
        )
    })?;
    let port = uri.port_u16().unwrap_or_else(|| match uri.scheme_str() {
        Some("wss") => 443,
        _ => 80,
    });

    let addrs = (host, port).to_socket_addrs()?;
    let mut last_error = io::Error::new(io::ErrorKind::NotFound, "No socket addresses resolved.");
    for addr in addrs {
        match TcpStream::connect_timeout(&addr, timeout) {
            Ok(stream) => return Ok(stream),
            Err(err) => last_error = err,
        }
    }
    Err(last_error)
}

fn decode_utf8(ptr: *const u8, len: u64) -> Result<String, nws_result> {
    let bytes = decode_bytes(ptr, len)?;
    std::str::from_utf8(&bytes)
        .map(str::to_owned)
        .map_err(|_| nws_result::NWS_INVALID_ARGUMENT)
}

fn decode_bytes(ptr: *const u8, len: u64) -> Result<Vec<u8>, nws_result> {
    if len == 0 {
        return Ok(Vec::new());
    }
    if ptr.is_null() {
        return Err(nws_result::NWS_INVALID_ARGUMENT);
    }
    let len = usize::try_from(len).map_err(|_| nws_result::NWS_INVALID_ARGUMENT)?;
    let data = unsafe { slice::from_raw_parts(ptr, len) };
    Ok(data.to_vec())
}

fn is_would_block_error(err: &WsError) -> bool {
    matches!(err, WsError::Io(io_err) if io_err.kind() == io::ErrorKind::WouldBlock)
}

fn map_connect_error_kind(err: &io::Error) -> nws_error_kind {
    if let Some(code) = err.raw_os_error() {
        match code {
            61 | 111 | 10061 => return nws_error_kind::NWS_ERR_CONNECT_FAILED,
            110 | 10060 => return nws_error_kind::NWS_ERR_TIMEOUT,
            _ => {}
        }
    }

    match err.kind() {
        io::ErrorKind::TimedOut => nws_error_kind::NWS_ERR_TIMEOUT,
        _ => nws_error_kind::NWS_ERR_CONNECT_FAILED,
    }
}

fn map_ws_error_kind(err: &WsError, during_connect: bool) -> nws_error_kind {
    match err {
        WsError::Tls(_) => nws_error_kind::NWS_ERR_TLS_FAILED,
        WsError::Io(io_err) => {
            if during_connect {
                map_connect_error_kind(io_err)
            } else {
                match io_err.kind() {
                    io::ErrorKind::TimedOut => nws_error_kind::NWS_ERR_TIMEOUT,
                    _ => nws_error_kind::NWS_ERR_IO,
                }
            }
        }
        WsError::Protocol(_)
        | WsError::Utf8(_)
        | WsError::Capacity(_)
        | WsError::AttackAttempt
        | WsError::Http(_)
        | WsError::HttpFormat(_) => nws_error_kind::NWS_ERR_PROTOCOL,
        WsError::Url(_) => nws_error_kind::NWS_ERR_CONNECT_FAILED,
        WsError::WriteBufferFull(_) => nws_error_kind::NWS_ERR_IO,
        WsError::ConnectionClosed | WsError::AlreadyClosed => nws_error_kind::NWS_ERR_INTERNAL,
    }
}

fn parse_uri(url: &str) -> Result<Uri, nws_result> {
    let uri: Uri = url.parse().map_err(|_| nws_result::NWS_INVALID_ARGUMENT)?;
    match uri.scheme_str() {
        Some("ws") | Some("wss") => {}
        _ => return Err(nws_result::NWS_INVALID_ARGUMENT),
    }
    if uri.host().is_none() {
        return Err(nws_result::NWS_INVALID_ARGUMENT);
    }
    Ok(uri)
}

fn pop_event_locked(inner: &mut InnerClient, out_event: &mut nws_event) -> nws_result {
    match inner.events.pop_front() {
        Some(event) => {
            log_native(
                LOG_DEBUG,
                format!(
                    "pop_event kind={} close_code={} clean={} bytes={}",
                    event_name(event.kind),
                    event.close_code,
                    event.close_was_clean,
                    event.data.len()
                ),
            );
            event.into_ffi(out_event);
            nws_result::NWS_OK
        }
        None => nws_result::NWS_TIMEOUT,
    }
}

fn set_socket_nonblocking(stream: &mut MaybeTlsStream<TcpStream>) -> io::Result<()> {
    match stream {
        MaybeTlsStream::Plain(socket) => socket.set_nonblocking(true),
        MaybeTlsStream::Rustls(socket) => socket.get_mut().set_nonblocking(true),
        _ => Err(io::Error::other(
            "Unsupported TLS stream variant for nonblocking mode.",
        )),
    }
}

fn websocket_config() -> WebSocketConfig {
    let mut config = WebSocketConfig::default();
    let mut extensions = ExtensionsConfig::default();
    extensions.permessage_deflate = Some(DeflateConfig::default());
    config.extensions = extensions;
    config
}

#[unsafe(no_mangle)]
pub extern "C" fn nws_set_log_handler(handler: Option<NativeLogCallback>) {
    if let Ok(mut slot) = LOG_HANDLER.lock() {
        *slot = handler;
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn nws_set_log_level(level: i32) {
    set_log_level(level);
    log_native(
        LOG_INFO,
        format!("native log level set to {}", level.clamp(0, LOG_TRACE)),
    );
}

#[unsafe(no_mangle)]
/// # Safety
///
/// `url_ptr` must point to `url_len` readable bytes for the duration of the call.
/// `out_client` must be a valid writable pointer. The returned client handle must
/// later be released exactly once with [`nws_client_destroy`].
pub unsafe extern "C" fn nws_client_create(
    url_ptr: *const u8,
    url_len: u64,
    out_client: *mut *mut Client,
) -> nws_result {
    if out_client.is_null() {
        return nws_result::NWS_INVALID_ARGUMENT;
    }
    let url = match decode_utf8(url_ptr, url_len) {
        Ok(url) => url,
        Err(result) => return result,
    };
    log_native(LOG_INFO, format!("nws_client_create url={url}"));
    let inner = match InnerClient::new(url) {
        Ok(inner) => inner,
        Err(result) => {
            log_native(
                LOG_ERROR,
                format!("nws_client_create invalid url result={result:?}"),
            );
            return result;
        }
    };

    let client = Box::new(Client {
        signal: Condvar::new(),
        inner: Mutex::new(inner),
    });

    unsafe {
        *out_client = Box::into_raw(client);
    }
    log_native(LOG_INFO, "nws_client_create ok".to_owned());
    nws_result::NWS_OK
}

#[unsafe(no_mangle)]
/// # Safety
///
/// `client` must be a handle previously returned by [`nws_client_create`] that
/// has not already been destroyed. No further calls may use the handle after this returns.
pub unsafe extern "C" fn nws_client_destroy(client: *mut Client) {
    if client.is_null() {
        log_native(LOG_DEBUG, "nws_client_destroy null".to_owned());
        return;
    }
    log_native(LOG_INFO, "nws_client_destroy".to_owned());
    unsafe {
        drop(Box::from_raw(client));
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn nws_client_abort(
    client: *mut Client,
    code: u16,
    reason_ptr: *const u8,
    reason_len: u64,
) -> nws_result {
    log_native(
        LOG_WARN,
        format!("nws_client_abort code={code} reason_len={reason_len}"),
    );
    let client = match client_from_ptr(client) {
        Ok(client) => client,
        Err(result) => return result,
    };
    let reason = match decode_utf8(reason_ptr, reason_len) {
        Ok(reason) => reason,
        Err(result) => return result,
    };
    let mut inner = match client.inner.lock() {
        Ok(inner) => inner,
        Err(_) => return nws_result::NWS_INTERNAL_ERROR,
    };

    inner.abort(code, reason);
    client.signal.notify_all();
    log_native(LOG_WARN, "nws_client_abort ok".to_owned());
    nws_result::NWS_OK
}

#[unsafe(no_mangle)]
pub extern "C" fn nws_client_connect(client: *mut Client) -> nws_result {
    log_native(LOG_INFO, "nws_client_connect".to_owned());
    let client = match client_from_ptr(client) {
        Ok(client) => client,
        Err(result) => return result,
    };
    let mut inner = match client.inner.lock() {
        Ok(inner) => inner,
        Err(_) => return nws_result::NWS_INTERNAL_ERROR,
    };

    let result = inner.request_connect();
    if result == nws_result::NWS_OK {
        client.signal.notify_one();
    }
    log_native(LOG_INFO, format!("nws_client_connect result={result:?}"));
    result
}

#[unsafe(no_mangle)]
pub extern "C" fn nws_client_close(
    client: *mut Client,
    code: u16,
    reason_ptr: *const u8,
    reason_len: u64,
) -> nws_result {
    log_native(
        LOG_INFO,
        format!("nws_client_close code={code} reason_len={reason_len}"),
    );
    let client = match client_from_ptr(client) {
        Ok(client) => client,
        Err(result) => return result,
    };
    let reason = match decode_utf8(reason_ptr, reason_len) {
        Ok(reason) => reason,
        Err(result) => return result,
    };
    let mut inner = match client.inner.lock() {
        Ok(inner) => inner,
        Err(_) => return nws_result::NWS_INTERNAL_ERROR,
    };

    let result = inner.request_close(code, reason);
    if result == nws_result::NWS_OK {
        client.signal.notify_one();
    }
    log_native(LOG_INFO, format!("nws_client_close result={result:?}"));
    result
}

#[unsafe(no_mangle)]
pub extern "C" fn nws_client_send_text(
    client: *mut Client,
    data_ptr: *const u8,
    data_len: u64,
) -> nws_result {
    log_native(LOG_DEBUG, format!("nws_client_send_text bytes={data_len}"));
    let client = match client_from_ptr(client) {
        Ok(client) => client,
        Err(result) => return result,
    };
    let text = match decode_utf8(data_ptr, data_len) {
        Ok(text) => text,
        Err(result) => return result,
    };
    let mut inner = match client.inner.lock() {
        Ok(inner) => inner,
        Err(_) => return nws_result::NWS_INTERNAL_ERROR,
    };

    let result = inner.request_message(Message::Text(text.into()));
    if result == nws_result::NWS_OK {
        client.signal.notify_one();
    }
    log_native(LOG_DEBUG, format!("nws_client_send_text result={result:?}"));
    result
}

#[unsafe(no_mangle)]
pub extern "C" fn nws_client_send_binary(
    client: *mut Client,
    data_ptr: *const u8,
    data_len: u64,
) -> nws_result {
    log_native(
        LOG_DEBUG,
        format!("nws_client_send_binary bytes={data_len}"),
    );
    let client = match client_from_ptr(client) {
        Ok(client) => client,
        Err(result) => return result,
    };
    let data = match decode_bytes(data_ptr, data_len) {
        Ok(data) => data,
        Err(result) => return result,
    };
    let mut inner = match client.inner.lock() {
        Ok(inner) => inner,
        Err(_) => return nws_result::NWS_INTERNAL_ERROR,
    };

    let result = inner.request_message(Message::Binary(data.into()));
    if result == nws_result::NWS_OK {
        client.signal.notify_one();
    }
    log_native(
        LOG_DEBUG,
        format!("nws_client_send_binary result={result:?}"),
    );
    result
}

#[unsafe(no_mangle)]
pub extern "C" fn nws_client_ping(
    client: *mut Client,
    data_ptr: *const u8,
    data_len: u64,
) -> nws_result {
    log_native(LOG_DEBUG, format!("nws_client_ping bytes={data_len}"));
    let client = match client_from_ptr(client) {
        Ok(client) => client,
        Err(result) => return result,
    };
    let data = match decode_bytes(data_ptr, data_len) {
        Ok(data) => data,
        Err(result) => return result,
    };
    let mut inner = match client.inner.lock() {
        Ok(inner) => inner,
        Err(_) => return nws_result::NWS_INTERNAL_ERROR,
    };

    let result = inner.request_message(Message::Ping(data.into()));
    if result == nws_result::NWS_OK {
        client.signal.notify_one();
    }
    log_native(LOG_DEBUG, format!("nws_client_ping result={result:?}"));
    result
}

#[unsafe(no_mangle)]
/// # Safety
///
/// `client` must be a live handle returned by [`nws_client_create`]. `out_event`
/// must be a valid writable pointer. At most one thread may poll a given client handle.
pub unsafe extern "C" fn nws_client_poll_event(
    client: *mut Client,
    timeout_ms: i32,
    out_event: *mut nws_event,
) -> nws_result {
    if out_event.is_null() || timeout_ms < 0 {
        log_native(
            LOG_WARN,
            "nws_client_poll_event invalid argument".to_owned(),
        );
        return nws_result::NWS_INVALID_ARGUMENT;
    }
    let client = match client_from_ptr(client) {
        Ok(client) => client,
        Err(result) => return result,
    };

    let deadline = Instant::now() + Duration::from_millis(timeout_ms as u64);
    let mut guard = match client.inner.lock() {
        Ok(guard) => guard,
        Err(_) => return nws_result::NWS_INTERNAL_ERROR,
    };

    loop {
        let out_event_ref = unsafe { &mut *out_event };
        if !guard.events.is_empty() {
            return pop_event_locked(&mut guard, out_event_ref);
        }

        let now = Instant::now();
        if timeout_ms == 0 || now >= deadline {
            *out_event_ref = nws_event::default();
            return nws_result::NWS_TIMEOUT;
        }

        let remaining = deadline.saturating_duration_since(now);
        guard.drive_once(remaining);
        if !guard.events.is_empty() {
            return pop_event_locked(&mut guard, out_event_ref);
        }

        let sleep_for = remaining.min(Duration::from_millis(WAIT_QUANTUM_MS));
        match client.signal.wait_timeout(guard, sleep_for) {
            Ok((next_guard, _)) => guard = next_guard,
            Err(_) => return nws_result::NWS_INTERNAL_ERROR,
        }
    }
}

#[unsafe(no_mangle)]
/// # Safety
///
/// `event` must be a valid writable pointer. It may contain buffers previously
/// allocated by this library, and must not be cleared concurrently.
pub unsafe extern "C" fn nws_event_clear(event: *mut nws_event) {
    if event.is_null() {
        return;
    }
    let event = unsafe { &mut *event };
    if !event.data_ptr.is_null() && event.data_len > 0 {
        let len = event.data_len as usize;
        let slice_ptr = ptr::slice_from_raw_parts_mut(event.data_ptr as *mut u8, len);
        unsafe {
            drop(Box::from_raw(slice_ptr));
        }
    }
    *event = nws_event::default();
}

#[cfg(test)]
mod tests {
    use super::*;

    use std::net::TcpListener;
    use std::thread;

    use tungstenite::{Message as WsMessage, accept_with_config};

    fn poll_until(client: *mut Client, wanted_kind: nws_event_kind) -> OwnedEvent {
        let mut event = nws_event::default();
        for _ in 0..200 {
            let result = unsafe { nws_client_poll_event(client, 50, &mut event) };
            if result == nws_result::NWS_OK {
                let owned = read_event(&event);
                unsafe { nws_event_clear(&mut event) };
                if owned.kind == wanted_kind {
                    return owned;
                }
            }
        }
        panic!("timed out waiting for event {:?}", wanted_kind);
    }

    fn read_event(event: &nws_event) -> OwnedEvent {
        let data = if event.data_ptr.is_null() || event.data_len == 0 {
            Vec::new()
        } else {
            unsafe { slice::from_raw_parts(event.data_ptr, event.data_len as usize).to_vec() }
        };

        OwnedEvent {
            kind: match event.kind {
                1 => nws_event_kind::NWS_EVENT_OPEN,
                2 => nws_event_kind::NWS_EVENT_CLOSE,
                3 => nws_event_kind::NWS_EVENT_MESSAGE,
                4 => nws_event_kind::NWS_EVENT_ERROR,
                5 => nws_event_kind::NWS_EVENT_PONG,
                other => panic!("unexpected event kind {other}"),
            },
            message_kind: match event.message_kind {
                1 => Some(nws_message_kind::NWS_MSG_TEXT),
                2 => Some(nws_message_kind::NWS_MSG_BINARY),
                0 => None,
                other => panic!("unexpected message kind {other}"),
            },
            error_kind: match event.error_kind {
                0 => None,
                1 => Some(nws_error_kind::NWS_ERR_CONNECT_FAILED),
                2 => Some(nws_error_kind::NWS_ERR_TLS_FAILED),
                3 => Some(nws_error_kind::NWS_ERR_IO),
                4 => Some(nws_error_kind::NWS_ERR_PROTOCOL),
                5 => Some(nws_error_kind::NWS_ERR_TIMEOUT),
                6 => Some(nws_error_kind::NWS_ERR_INTERNAL),
                other => panic!("unexpected error kind {other}"),
            },
            close_code: event.close_code,
            close_was_clean: event.close_was_clean != 0,
            data,
        }
    }

    #[test]
    fn create_rejects_invalid_url() {
        let mut client = ptr::null_mut();
        let result = unsafe { nws_client_create(b"http://example.com".as_ptr(), 18, &mut client) };
        assert_eq!(result, nws_result::NWS_INVALID_ARGUMENT);
        assert!(client.is_null());
    }

    #[test]
    fn connect_send_receive_and_close() {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind test listener");
        let addr = listener.local_addr().expect("listener addr");

        let server = thread::spawn(move || {
            let (stream, _) = listener.accept().expect("accept client");
            let mut socket =
                accept_with_config(stream, Some(websocket_config())).expect("accept websocket");
            let message = socket.read().expect("read client message");
            assert_eq!(message, WsMessage::Text("hello".into()));
            socket
                .write(WsMessage::Text("world".into()))
                .expect("write reply frame");
            socket.flush().expect("flush reply");
            socket.close(None).expect("close socket");
        });

        let url = format!("ws://127.0.0.1:{}/ws", addr.port());
        let mut client = ptr::null_mut();
        assert_eq!(
            unsafe { nws_client_create(url.as_ptr(), url.len() as u64, &mut client) },
            nws_result::NWS_OK
        );
        assert!(!client.is_null());

        assert_eq!(nws_client_connect(client), nws_result::NWS_OK);
        let open = poll_until(client, nws_event_kind::NWS_EVENT_OPEN);
        assert_eq!(open.kind, nws_event_kind::NWS_EVENT_OPEN);

        assert_eq!(
            nws_client_send_text(client, b"hello".as_ptr(), 5),
            nws_result::NWS_OK
        );
        let message = poll_until(client, nws_event_kind::NWS_EVENT_MESSAGE);
        assert_eq!(message.message_kind, Some(nws_message_kind::NWS_MSG_TEXT));
        assert_eq!(String::from_utf8(message.data).unwrap(), "world");

        let close = poll_until(client, nws_event_kind::NWS_EVENT_CLOSE);
        assert_eq!(close.kind, nws_event_kind::NWS_EVENT_CLOSE);

        unsafe { nws_client_destroy(client) };
        server.join().expect("join test server");
    }

    #[test]
    fn map_connect_error_kind_treats_refused_as_connect_failed() {
        let refused = io::Error::from_raw_os_error(10061);
        let timeout = io::Error::from_raw_os_error(10060);

        assert_eq!(
            map_connect_error_kind(&refused),
            nws_error_kind::NWS_ERR_CONNECT_FAILED
        );
        assert_eq!(
            map_connect_error_kind(&timeout),
            nws_error_kind::NWS_ERR_TIMEOUT
        );
    }
}
