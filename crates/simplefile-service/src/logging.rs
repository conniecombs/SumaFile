//! Sends `log` records to the error stream.
//!
//! The app writes every line of the service's error stream to `service.log`,
//! with the time. Without a logger installed, the warnings of the transfer
//! engine (a retried attempt, a record recovery kept, a file whose times
//! could not be set) were dropped before they got there.

use log::{Level, LevelFilter, Log, Metadata, Record};

struct ErrorStream;

static LOGGER: ErrorStream = ErrorStream;

/// Warnings and errors. A retried attempt is a warning; the debug records
/// are one per file and would fill the log.
const MAX_LEVEL: LevelFilter = LevelFilter::Warn;

impl Log for ErrorStream {
    fn enabled(&self, metadata: &Metadata<'_>) -> bool {
        metadata.level() <= MAX_LEVEL
    }

    fn log(&self, record: &Record<'_>) {
        if self.enabled(record.metadata()) {
            eprintln!("{}", line(record.level(), &record.args().to_string()));
        }
    }

    fn flush(&self) {}
}

/// One record as it is written: on one line, so that the time the app puts
/// before each line of the stream stands before the whole record.
fn line(level: Level, message: &str) -> String {
    let label = match level {
        Level::Error => "error",
        Level::Warn => "warning",
        Level::Info => "info",
        Level::Debug => "debug",
        Level::Trace => "trace",
    };
    let message = message.replace(['\r', '\n'], " ");
    format!("simplefile-service {label}: {message}")
}

/// Installs the logger. Called once when the service starts; a second call
/// changes nothing.
pub fn install() {
    if log::set_logger(&LOGGER).is_ok() {
        log::set_max_level(MAX_LEVEL);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_record_is_one_line_that_names_the_service_and_the_level() {
        assert_eq!(
            line(
                Level::Warn,
                "Transfer attempt 2: Read source [a.bin]: timed out"
            ),
            "simplefile-service warning: Transfer attempt 2: Read source [a.bin]: timed out"
        );
        assert_eq!(
            line(Level::Error, "first\r\nsecond"),
            "simplefile-service error: first  second"
        );
    }

    #[test]
    fn warnings_and_errors_are_written_and_the_per_file_records_are_not() {
        let enabled = |level| LOGGER.enabled(&Metadata::builder().level(level).build());
        assert!(enabled(Level::Error));
        assert!(enabled(Level::Warn));
        assert!(!enabled(Level::Info));
        assert!(!enabled(Level::Debug));
    }

    #[test]
    fn installing_twice_is_harmless() {
        install();
        install();
        assert_eq!(log::max_level(), MAX_LEVEL);
    }
}
