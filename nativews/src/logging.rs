// Copyright 2026 JKLeckr
// SPDX-License-Identifier: MPL-2.0

use std::ffi::CString;
use std::os::raw::c_char;
use std::sync::Mutex;
use std::sync::atomic::{AtomicI32, Ordering};

pub(crate) const LOG_ERROR: i32 = 1;
pub(crate) const LOG_WARN: i32 = 2;
pub(crate) const LOG_INFO: i32 = 3;
pub(crate) const LOG_DEBUG: i32 = 4;
pub(crate) const LOG_TRACE: i32 = 5;

pub(super) static LOG_LEVEL: AtomicI32 = AtomicI32::new(0);
pub(super) static LOG_HANDLER: Mutex<Option<NativeLogCallback>> = Mutex::new(None);

pub(super) type NativeLogCallback = extern "C" fn(i32, *const c_char);

pub(crate) fn set_log_level(level: i32) {
    LOG_LEVEL.store(level.clamp(0, LOG_TRACE), Ordering::Relaxed);
}

pub(crate) fn log_native(level: i32, message: String) {
    if level <= 0 || level > LOG_LEVEL.load(Ordering::Relaxed) {
        return;
    }

    let handler = match LOG_HANDLER.lock() {
        Ok(slot) => *slot,
        Err(_) => None,
    };

    let Some(handler) = handler else {
        return;
    };

    let sanitized = message.replace('\0', " ");
    if let Ok(c_message) = CString::new(sanitized) {
        handler(level, c_message.as_ptr());
    }
}
