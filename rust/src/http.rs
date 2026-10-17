use crate::store::wide;
use serde_json::Value;
use std::{
    ffi::c_void,
    ptr::{null, null_mut},
    time::Instant,
};
use windows_sys::Win32::Networking::WinHttp::*;
use zeroize::Zeroizing;
struct Handle(*mut c_void);
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe {
            WinHttpCloseHandle(self.0);
        }
    }
}
fn checked(raw: *mut c_void) -> Result<Handle, String> {
    if raw.is_null() {
        Err(std::io::Error::last_os_error().to_string())
    } else {
        Ok(Handle(raw))
    }
}
fn check(ok: i32) -> Result<(), String> {
    if ok == 0 {
        Err(std::io::Error::last_os_error().to_string())
    } else {
        Ok(())
    }
}
pub struct Client {
    session: Handle,
    connection: Handle,
}
pub struct Response {
    pub body: Value,
    pub status: u32,
    pub send_ms: f64,
    pub wait_ms: f64,
    pub download_ms: f64,
    pub total_ms: f64,
    pub attempts: u32,
}
impl Client {
    pub fn new() -> Result<Self, String> {
        unsafe {
            let session = checked(WinHttpOpen(
                wide("UniversalSpellCheck/0.13 Rust").as_ptr(),
                WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                null(),
                null(),
                0,
            ))?;
            check(WinHttpSetTimeouts(session.0, 5000, 5000, 30000, 30000))?;
            let protocols = WINHTTP_PROTOCOL_FLAG_HTTP2;
            check(WinHttpSetOption(
                session.0,
                WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL,
                (&protocols as *const u32).cast(),
                4,
            ))?;
            let connection = checked(WinHttpConnect(
                session.0,
                wide("api.openai.com").as_ptr(),
                443,
                0,
            ))?;
            Ok(Self {
                session,
                connection,
            })
        }
    }
    pub fn request(
        &self,
        payload: &Value,
        key: &str,
        on_wait: impl Fn(),
    ) -> Result<Response, String> {
        if key.trim().is_empty() || key == "offline-replay-no-network" {
            return Err(
                "A saved API key is needed; offline placeholders cannot access the network.".into(),
            );
        }
        let mut last = None;
        for attempt in 1..=3 {
            let result = self.once(payload, key, &on_wait);
            let retry = match &result {
                Ok(response) => {
                    response.status == 408 || response.status == 429 || response.status >= 500
                }
                Err(_) => true,
            };
            if !retry || attempt == 3 {
                return result.map(|mut r| {
                    r.attempts = attempt;
                    r
                });
            }
            last = Some(result);
            std::thread::sleep(std::time::Duration::from_millis(250 * (attempt as u64)));
        }
        last.unwrap()
    }
    fn once(&self, payload: &Value, key: &str, on_wait: &impl Fn()) -> Result<Response, String> {
        unsafe {
            let started = Instant::now();
            let data = serde_json::to_vec(payload).map_err(|e| e.to_string())?;
            let request = checked(WinHttpOpenRequest(
                self.connection.0,
                wide("POST").as_ptr(),
                wide("/v1/responses").as_ptr(),
                null(),
                null(),
                null(),
                WINHTTP_FLAG_SECURE,
            ))?;
            let redirect = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
            check(WinHttpSetOption(
                request.0,
                WINHTTP_OPTION_REDIRECT_POLICY,
                (&redirect as *const u32).cast(),
                4,
            ))?;
            let header = Zeroizing::new(wide(&format!(
                "Content-Type: application/json; charset=utf-8\r\nAuthorization: Bearer {key}\r\n"
            )));
            let sent = Instant::now();
            check(WinHttpSendRequest(
                request.0,
                header.as_ptr(),
                (header.len() - 1) as u32,
                data.as_ptr().cast(),
                data.len() as u32,
                data.len() as u32,
                0,
            ))?;
            let send_ms = sent.elapsed().as_secs_f64() * 1000.;
            on_wait();
            let wait = Instant::now();
            check(WinHttpReceiveResponse(request.0, null_mut()))?;
            let wait_ms = wait.elapsed().as_secs_f64() * 1000.;
            let mut status = 0u32;
            let mut len = 4;
            check(WinHttpQueryHeaders(
                request.0,
                WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                null(),
                (&mut status as *mut u32).cast(),
                &mut len,
                null_mut(),
            ))?;
            let download = Instant::now();
            let mut body = Vec::new();
            loop {
                let mut buffer = [0u8; 16384];
                let mut read = 0;
                check(WinHttpReadData(
                    request.0,
                    buffer.as_mut_ptr().cast(),
                    buffer.len() as u32,
                    &mut read,
                ))?;
                if read == 0 {
                    break;
                }
                if body.len() + read as usize > 16 * 1024 * 1024 {
                    return Err("Response exceeds the 16 MiB limit.".into());
                }
                body.extend_from_slice(&buffer[..read as usize]);
                if started.elapsed().as_secs() >= 30 {
                    return Err("The OpenAI request timed out.".into());
                }
            }
            let body = serde_json::from_slice(&body).map_err(|_| "OpenAI returned invalid JSON")?;
            Ok(Response {
                body,
                status,
                send_ms,
                wait_ms,
                download_ms: download.elapsed().as_secs_f64() * 1000.,
                total_ms: started.elapsed().as_secs_f64() * 1000.,
                attempts: 1,
            })
        }
    }
    pub fn warm(&self) {
        unsafe {
            if let Ok(request) = checked(WinHttpOpenRequest(
                self.connection.0,
                wide("HEAD").as_ptr(),
                wide("/v1/models").as_ptr(),
                null(),
                null(),
                null(),
                WINHTTP_FLAG_SECURE,
            )) {
                let _ = WinHttpSetTimeouts(request.0, 2000, 2000, 2000, 2000);
                if WinHttpSendRequest(request.0, null(), 0, null(), 0, 0, 0) != 0 {
                    WinHttpReceiveResponse(request.0, null_mut());
                }
            }
        }
        let _ = &self.session;
    }
}
