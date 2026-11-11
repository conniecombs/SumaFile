//! Deterministic, real wall-clock cloud/NAS latency on otherwise successful I/O.
//! Test-only wrappers keep actual streaming / pacing / progress code in the path.
use std::io::{self, Read, Write};
use std::time::Duration;

pub struct DelayedIo<T> {
    pub inner: T,
    pub delay: bool,
    reads: usize,
    writes: usize,
}

impl<T> DelayedIo<T> {
    pub fn new(inner: T, delay: bool) -> Self {
        Self {
            inner,
            delay,
            reads: 0,
            writes: 0,
        }
    }
}
impl<T: Read> Read for DelayedIo<T> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        let size = buffer.len().min(64 * 1024);
        let read = self.inner.read(&mut buffer[..size])?;
        if self.delay && read > 0 {
            // Cloud cold-read latency, followed by uneven chunk delivery. A
            // fresh file repeats this pattern rather than assuming a warm cache.
            let ms = [1100, 80, 900, 120][self.reads % 4];
            std::thread::sleep(Duration::from_millis(ms));
        }
        self.reads += 1;
        Ok(read)
    }
}
impl<T: Write> Write for DelayedIo<T> {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        if self.delay && !buffer.is_empty() {
            let ms = [90, 850, 70, 160][self.writes % 4];
            std::thread::sleep(Duration::from_millis(ms));
            self.writes += 1;
        }
        self.inner.write(buffer)
    }
    fn flush(&mut self) -> io::Result<()> {
        if self.delay {
            std::thread::sleep(Duration::from_millis(1200));
        }
        self.inner.flush()
    }
}
