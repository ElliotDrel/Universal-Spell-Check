use crate::store::wide;
use serde_json::Value;
use std::{
    cell::UnsafeCell,
    collections::VecDeque,
    ffi::c_void,
    ptr::{null, null_mut},
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};
use windows_sys::Win32::{Foundation::*, Networking::WinHttp::*, System::Threading::*};
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
#[derive(Debug)]
enum Signal {
    Completed(u32),
    Data(Vec<u8>),
    Error(u32),
}
struct State {
    event: HANDLE,
    queue: Mutex<VecDeque<Signal>>,
    buffer: UnsafeCell<[u8; 16384]>,
    body: Vec<u8>,
    headers: Zeroizing<Vec<u16>>,
}
// Only WinHTTP writes buffer; the callback copies its completed contents before signaling.
// Requests are driven serially by one owner; Arc retains all I/O memory until HANDLE_CLOSING.
unsafe impl Send for State {}
unsafe impl Sync for State {}
impl Drop for State {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.event);
        }
    }
}
unsafe extern "system" fn callback(
    _handle: *mut c_void,
    context: usize,
    status: u32,
    information: *mut c_void,
    length: u32,
) {
    if context == 0 {
        return;
    }
    let ptr = context as *const State;
    if status == WINHTTP_CALLBACK_STATUS_HANDLE_CLOSING {
        unsafe {
            drop(Arc::from_raw(ptr));
        }
        return;
    }
    let state = unsafe { &*ptr };
    let signal = match status {
        WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE
        | WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE => Some(Signal::Completed(status)),
        WINHTTP_CALLBACK_STATUS_READ_COMPLETE => {
            if length > 16384 || (length > 0 && information.is_null()) {
                Some(Signal::Error(ERROR_INVALID_DATA))
            } else {
                Some(Signal::Data(if length == 0 {
                    Vec::new()
                } else {
                    unsafe {
                        std::slice::from_raw_parts(information.cast::<u8>(), length as usize)
                            .to_vec()
                    }
                }))
            }
        }
        WINHTTP_CALLBACK_STATUS_REQUEST_ERROR => Some(Signal::Error(
            if !information.is_null()
                && length as usize >= std::mem::size_of::<WINHTTP_ASYNC_RESULT>()
            {
                unsafe { (*(information as *const WINHTTP_ASYNC_RESULT)).dwError }
            } else {
                ERROR_OPERATION_ABORTED
            },
        )),
        _ => None,
    };
    if let Some(signal) = signal {
        let mut queue = state.queue.lock().unwrap_or_else(|p| p.into_inner());
        queue.push_back(signal);
        unsafe {
            SetEvent(state.event);
        }
    }
}
struct Request {
    handle: Handle,
    state: Arc<State>,
    send_api_ms: f64,
    send_completion_wait_ms: f64,
}
impl Request {
    fn new(
        connection: *mut c_void,
        verb: &str,
        path: &str,
        body: Vec<u8>,
        key: Option<&str>,
        secure: bool,
    ) -> Result<Self, String> {
        unsafe {
            let handle = checked(WinHttpOpenRequest(
                connection,
                wide(verb).as_ptr(),
                wide(path).as_ptr(),
                null(),
                null(),
                null(),
                if secure { WINHTTP_FLAG_SECURE } else { 0 },
            ))?;
            let event = CreateEventW(null(), 0, 0, null());
            if event.is_null() {
                return Err(std::io::Error::last_os_error().to_string());
            }
            let prefix = "Content-Type: application/json; charset=utf-8\r\n";
            let capacity = prefix.encode_utf16().count()
                + key.map_or(0, |k| k.encode_utf16().count() + 24)
                + 1;
            let mut headers = Zeroizing::new(Vec::with_capacity(capacity));
            headers.extend(prefix.encode_utf16());
            if let Some(key) = key {
                headers.extend("Authorization: Bearer ".encode_utf16());
                headers.extend(key.encode_utf16());
                headers.extend("\r\n".encode_utf16());
            }
            headers.push(0);
            let state = Arc::new(State {
                event,
                queue: Mutex::new(VecDeque::new()),
                buffer: UnsafeCell::new([0; 16384]),
                body,
                headers,
            });
            let context = Arc::into_raw(state.clone()) as usize;
            if WinHttpSetOption(
                handle.0,
                WINHTTP_OPTION_CONTEXT_VALUE,
                (&context as *const usize).cast(),
                std::mem::size_of::<usize>() as u32,
            ) == 0
            {
                drop(Arc::from_raw(context as *const State));
                return Err(std::io::Error::last_os_error().to_string());
            }
            let previous = WinHttpSetStatusCallback(
                handle.0,
                Some(callback),
                WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE
                    | WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE
                    | WINHTTP_CALLBACK_STATUS_READ_COMPLETE
                    | WINHTTP_CALLBACK_STATUS_REQUEST_ERROR
                    | WINHTTP_CALLBACK_STATUS_HANDLE_CLOSING,
                0,
            );
            if previous.is_some_and(|f| f as usize == usize::MAX) {
                drop(Arc::from_raw(context as *const State));
                return Err(std::io::Error::last_os_error().to_string());
            }
            let request = Self {
                handle,
                state,
                send_api_ms: 0.,
                send_completion_wait_ms: 0.,
            };
            let redirect = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
            if WinHttpSetOption(
                request.handle.0,
                WINHTTP_OPTION_REDIRECT_POLICY,
                (&redirect as *const u32).cast(),
                4,
            ) == 0
            {
                return Err(std::io::Error::last_os_error().to_string());
            }
            Ok(request)
        }
    }
    fn accepted(ok: i32) -> Result<(), Failure> {
        if ok != 0 {
            return Ok(());
        }
        let error = unsafe { GetLastError() };
        if error == ERROR_IO_PENDING {
            Ok(())
        } else {
            Err(Failure::transport(error))
        }
    }
    fn wait(&self, deadline: Instant, expected: u32) -> Result<Option<Vec<u8>>, Failure> {
        loop {
            while let Some(signal) = self
                .state
                .queue
                .lock()
                .unwrap_or_else(|p| p.into_inner())
                .pop_front()
            {
                match signal {
                    Signal::Error(error) => return Err(Failure::transport(error)),
                    Signal::Completed(status) if status == expected => return Ok(None),
                    Signal::Data(bytes) if expected == WINHTTP_CALLBACK_STATUS_READ_COMPLETE => {
                        return Ok(Some(bytes));
                    }
                    _ => {}
                }
            }
            let remaining = deadline
                .checked_duration_since(Instant::now())
                .ok_or_else(|| Failure::transport(ERROR_WINHTTP_TIMEOUT))?;
            let ms = remaining.as_millis().clamp(1, u32::MAX as u128) as u32;
            let result = unsafe { WaitForSingleObject(self.state.event, ms) };
            if result == WAIT_TIMEOUT {
                return Err(Failure::transport(ERROR_WINHTTP_TIMEOUT));
            }
            if result != WAIT_OBJECT_0 {
                return Err(Failure::transport(unsafe { GetLastError() }));
            }
        }
    }
    fn diagnostics(&self) -> Value {
        unsafe {
            let mut times: WINHTTP_REQUEST_TIMES = std::mem::zeroed();
            times.cTimes = WinHttpRequestTimeLast as u32;
            let mut size = std::mem::size_of_val(&times) as u32;
            let times_ok = WinHttpQueryOption(
                self.handle.0,
                WINHTTP_OPTION_REQUEST_TIMES,
                (&mut times as *mut WINHTTP_REQUEST_TIMES).cast(),
                &mut size,
            ) != 0;
            let mut stats: WINHTTP_REQUEST_STATS = std::mem::zeroed();
            stats.cStats = WinHttpRequestStatLast as u32;
            let mut size = std::mem::size_of_val(&stats) as u32;
            let stats_ok = WinHttpQueryOption(
                self.handle.0,
                WINHTTP_OPTION_REQUEST_STATS,
                (&mut stats as *mut WINHTTP_REQUEST_STATS).cast(),
                &mut size,
            ) != 0;
            let mut protocol = 0u32;
            let mut size = 4;
            let protocol_ok = WinHttpQueryOption(
                self.handle.0,
                WINHTTP_OPTION_HTTP_PROTOCOL_USED,
                (&mut protocol as *mut u32).cast(),
                &mut size,
            ) != 0;
            let mut frequency = 0i64;
            let frequency_ok =
                windows_sys::Win32::System::Performance::QueryPerformanceFrequency(&mut frequency)
                    != 0
                    && frequency > 0;
            serde_json::json!({
                "qpc_frequency":if frequency_ok {Some(frequency)} else {None},
                "request_times_qpc":if times_ok {Some(&times.rgullTimes[..(times.cTimes as usize).min(times.rgullTimes.len())])} else {None},
                "request_stats_flags":if stats_ok {Some(stats.ullFlags)} else {None},
                "connection_request_index":if stats_ok {Some(stats.ulIndex)} else {None},
                "protocol_flags":if protocol_ok {Some(protocol)} else {None}
            })
        }
    }
    fn send(&mut self, deadline: Instant) -> Result<(), Failure> {
        let started = Instant::now();
        let accepted = unsafe {
            Self::accepted(WinHttpSendRequest(
                self.handle.0,
                self.state.headers.as_ptr(),
                (self.state.headers.len() - 1) as u32,
                self.state.body.as_ptr().cast(),
                self.state.body.len() as u32,
                self.state.body.len() as u32,
                Arc::as_ptr(&self.state) as usize,
            ))
        };
        self.send_api_ms = started.elapsed().as_secs_f64() * 1000.;
        accepted?;
        let started = Instant::now();
        let completed = self.wait(deadline, WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE);
        self.send_completion_wait_ms = started.elapsed().as_secs_f64() * 1000.;
        completed?;
        Ok(())
    }
    fn receive(&mut self, deadline: Instant) -> Result<u32, Failure> {
        unsafe {
            Self::accepted(WinHttpReceiveResponse(self.handle.0, null_mut()))?;
        }
        self.wait(deadline, WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE)?;
        unsafe {
            let mut status = 0u32;
            let mut length = 4;
            if WinHttpQueryHeaders(
                self.handle.0,
                WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                null(),
                (&mut status as *mut u32).cast(),
                &mut length,
                null_mut(),
            ) == 0
            {
                return Err(Failure::transport(GetLastError()));
            }
            Ok(status)
        }
    }
    fn read(&mut self, deadline: Instant) -> Result<Vec<u8>, Failure> {
        let mut body = Vec::new();
        loop {
            unsafe {
                Self::accepted(WinHttpReadData(
                    self.handle.0,
                    self.state.buffer.get().cast(),
                    16384,
                    null_mut(),
                ))?;
            }
            let bytes = self
                .wait(deadline, WINHTTP_CALLBACK_STATUS_READ_COMPLETE)?
                .unwrap_or_default();
            if bytes.is_empty() {
                break;
            }
            if body.len() + bytes.len() > 16 * 1024 * 1024 {
                return Err(Failure::permanent("OpenAI response exceeds 16 MiB."));
            }
            body.extend_from_slice(&bytes);
        }
        Ok(body)
    }
}
#[derive(Debug)]
struct Failure {
    message: String,
    retry: bool,
}
impl Failure {
    fn transport(code: u32) -> Self {
        Self {
            message: if code == ERROR_WINHTTP_TIMEOUT {
                "The OpenAI request timed out.".into()
            } else {
                format!("Windows HTTP error {code}")
            },
            retry: true,
        }
    }
    fn permanent(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            retry: false,
        }
    }
}
pub struct Client {
    connection: Handle,
    session: Handle,
}
// WinHTTP supports concurrent requests on an app-lifetime session/connection. Each Request owns its callback state.
unsafe impl Send for Client {}
unsafe impl Sync for Client {}
pub struct Response {
    pub body: Value,
    pub raw: String,
    pub status: u32,
    pub native: Value,
    pub send_ms: f64,
    pub send_api_ms: Option<f64>,
    pub send_completion_wait_ms: Option<f64>,
    pub wait_ms: f64,
    pub download_ms: f64,
    pub total_ms: f64,
    pub attempts: u32,
}
impl Client {
    pub fn new() -> Result<Self, String> {
        unsafe {
            let session = checked(WinHttpOpen(
                wide("UniversalSpellCheck/Rust").as_ptr(),
                WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                null(),
                null(),
                WINHTTP_FLAG_ASYNC,
            ))?;
            if WinHttpSetTimeouts(session.0, 5000, 5000, 30000, 30000) == 0 {
                return Err(std::io::Error::last_os_error().to_string());
            }
            let protocols = WINHTTP_PROTOCOL_FLAG_HTTP2;
            if WinHttpSetOption(
                session.0,
                WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL,
                (&protocols as *const u32).cast(),
                4,
            ) == 0
            {
                return Err(std::io::Error::last_os_error().to_string());
            }
            let connection = checked(WinHttpConnect(
                session.0,
                wide("api.openai.com").as_ptr(),
                443,
                0,
            ))?;
            Ok(Self {
                connection,
                session,
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
        let total = Instant::now();
        let (mut send_ms, mut wait_ms, mut download_ms) = (0., 0., 0.);
        for attempt in 1..=2 {
            match self.once(payload, key, &on_wait) {
                Ok(mut response) => {
                    send_ms += response.send_ms;
                    wait_ms += response.wait_ms;
                    download_ms += response.download_ms;
                    let retry =
                        response.status == 408 || response.status == 429 || response.status >= 500;
                    if !retry || attempt == 2 {
                        response.attempts = attempt;
                        response.send_ms = send_ms;
                        response.wait_ms = wait_ms;
                        response.download_ms = download_ms;
                        response.total_ms = total.elapsed().as_secs_f64() * 1000.;
                        return Ok(response);
                    }
                }
                Err(failure) => {
                    if !failure.retry || attempt == 2 {
                        return Err(failure.message);
                    }
                }
            }
            std::thread::sleep(Duration::from_millis(500));
        }
        unreachable!()
    }
    fn once(&self, payload: &Value, key: &str, on_wait: &impl Fn()) -> Result<Response, Failure> {
        let started = Instant::now();
        let deadline = started + Duration::from_secs(30);
        let body = serde_json::to_vec(payload).map_err(|e| Failure::permanent(e.to_string()))?;
        let mut request = Request::new(
            self.connection.0,
            "POST",
            "/v1/responses",
            body,
            Some(key),
            true,
        )
        .map_err(Failure::permanent)?;
        let sent = Instant::now();
        request.send(deadline)?;
        let send_ms = sent.elapsed().as_secs_f64() * 1000.;
        on_wait();
        let wait = Instant::now();
        let status = request.receive(deadline)?;
        let wait_ms = wait.elapsed().as_secs_f64() * 1000.;
        let download = Instant::now();
        let body = request.read(deadline)?;
        let download_ms = download.elapsed().as_secs_f64() * 1000.;
        let raw = String::from_utf8_lossy(&body).into_owned();
        let body = parse_body(status, &body)?;
        let native = request.diagnostics();
        Ok(Response {
            native,
            body,
            raw,
            status,
            send_ms,
            send_api_ms: Some(request.send_api_ms),
            send_completion_wait_ms: Some(request.send_completion_wait_ms),
            wait_ms,
            download_ms,
            total_ms: started.elapsed().as_secs_f64() * 1000.,
            attempts: 1,
        })
    }
    pub fn warm(&self) {
        let deadline = Instant::now() + Duration::from_secs(2);
        if let Ok(mut request) = Request::new(
            self.connection.0,
            "HEAD",
            "/v1/models",
            Vec::new(),
            None,
            true,
        ) && request.send(deadline).is_ok()
        {
            let _ = request.receive(deadline);
        }
        let _ = &self.session;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        io::{Read, Write},
        net::TcpListener,
    };
    fn local_request(
        delay: Duration,
        allow_disconnect: bool,
    ) -> (Request, Handle, Handle, std::thread::JoinHandle<()>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        listener.set_nonblocking(true).unwrap();
        let server = std::thread::spawn(move || {
            let accept_deadline = Instant::now() + Duration::from_secs(2);
            let mut stream = loop {
                match listener.accept() {
                    Ok((stream, _)) => break stream,
                    Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                        if Instant::now() > accept_deadline {
                            return;
                        }
                        std::thread::sleep(Duration::from_millis(1));
                    }
                    Err(_) => return,
                }
            };
            stream.set_nonblocking(false).unwrap();
            stream
                .set_read_timeout(Some(Duration::from_secs(2)))
                .unwrap();
            let mut received = Vec::new();
            let mut b = [0; 1024];
            while !received.windows(4).any(|s| s == b"\r\n\r\n") {
                let n = match stream.read(&mut b) {
                    Ok(n) => n,
                    Err(error)
                        if allow_disconnect
                            && matches!(
                                error.kind(),
                                std::io::ErrorKind::ConnectionReset
                                    | std::io::ErrorKind::ConnectionAborted
                                    | std::io::ErrorKind::TimedOut
                                    | std::io::ErrorKind::WouldBlock
                            ) =>
                    {
                        return;
                    }
                    Err(error) => panic!("Mock HTTP read failed: {error}"),
                };
                if n == 0 {
                    return;
                }
                received.extend_from_slice(&b[..n]);
            }
            let header_end = received.windows(4).position(|s| s == b"\r\n\r\n").unwrap() + 4;
            let headers = String::from_utf8_lossy(&received[..header_end]);
            let length = headers
                .lines()
                .find_map(|l| {
                    l.to_ascii_lowercase()
                        .strip_prefix("content-length:")
                        .and_then(|s| s.trim().parse::<usize>().ok())
                })
                .unwrap_or(0);
            while received.len() < header_end + length {
                let n = match stream.read(&mut b) {
                    Ok(n) => n,
                    Err(error)
                        if allow_disconnect
                            && matches!(
                                error.kind(),
                                std::io::ErrorKind::ConnectionReset
                                    | std::io::ErrorKind::ConnectionAborted
                                    | std::io::ErrorKind::TimedOut
                                    | std::io::ErrorKind::WouldBlock
                            ) =>
                    {
                        return;
                    }
                    Err(error) => panic!("Mock HTTP read failed: {error}"),
                };
                if n == 0 {
                    return;
                }
                received.extend_from_slice(&b[..n]);
            }
            std::thread::sleep(delay);
            let body = b"{\"output_text\":\"the test\"}";
            let header = format!(
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                body.len()
            );
            let _ = stream.write_all(header.as_bytes());
            let _ = stream.write_all(body);
        });
        unsafe {
            let session = checked(WinHttpOpen(
                wide("USC native test").as_ptr(),
                WINHTTP_ACCESS_TYPE_NO_PROXY,
                null(),
                null(),
                WINHTTP_FLAG_ASYNC,
            ))
            .unwrap();
            let connection = checked(WinHttpConnect(
                session.0,
                wide("127.0.0.1").as_ptr(),
                port,
                0,
            ))
            .unwrap();
            let request =
                Request::new(connection.0, "POST", "/test", b"{}".to_vec(), None, false).unwrap();
            (request, connection, session, server)
        }
    }
    #[test]
    fn native_async_reads_mock_http() {
        let (mut request, _connection, _session, server) = local_request(Duration::ZERO, false);
        let deadline = Instant::now() + Duration::from_secs(2);
        request.send(deadline).unwrap();
        assert_eq!(request.receive(deadline).unwrap(), 200);
        let bytes = request.read(deadline).unwrap();
        assert_eq!(
            serde_json::from_slice::<Value>(&bytes).unwrap()["output_text"],
            "the test"
        );
        drop(request);
        server.join().unwrap();
    }
    #[test]
    fn native_timeout_keeps_state_alive_until_handle_closes() {
        let (mut request, _connection, _session, server) =
            local_request(Duration::from_millis(200), true);
        let weak = Arc::downgrade(&request.state);
        let started = Instant::now();
        let deadline = started + Duration::from_millis(20);
        let outcome = request
            .send(deadline)
            .and_then(|_| request.receive(deadline).map(|_| ()));
        // Assert the deadline result, not OS scheduling latency under a concurrent CI build.
        assert_eq!(
            outcome.unwrap_err().message,
            "The OpenAI request timed out."
        );
        drop(request);
        let until = Instant::now() + Duration::from_secs(2);
        while weak.upgrade().is_some() && Instant::now() < until {
            std::thread::yield_now();
        }
        assert!(weak.upgrade().is_none());
        server.join().unwrap();
    }
}

fn parse_body(status: u32, body: &[u8]) -> Result<Value, Failure> {
    match serde_json::from_slice(body) {
        Ok(value) => Ok(value),
        Err(_) if !(200..300).contains(&status) => {
            Ok(serde_json::json!({"error":{"message":format!("OpenAI HTTP {status}")}}))
        }
        Err(_) => Err(Failure::permanent("OpenAI returned invalid JSON.")),
    }
}
#[cfg(test)]
mod response_tests {
    use super::*;
    #[test]
    fn gateway_errors_retain_status_and_success_requires_json() {
        assert!(parse_body(502, b"<html>bad gateway</html>").is_ok());
        assert!(parse_body(429, b"").is_ok());
        assert!(!parse_body(200, b"not JSON").unwrap_err().retry);
    }
}
