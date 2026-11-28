//! The video process. It answers the file service's requests on its standard
//! input and output until the input closes, then leaves at once.

use std::io::Write;
use sumafile_media_client::protocol::{write_message, ResponseHeader};

fn main() {
    // Say that the process is running before reading anything. The file
    // service waits for this: a process that ends first never started, and
    // no file is blamed for it.
    let mut output = std::io::stdout();
    if write_message(&mut output, &ResponseHeader::ready(), &[]).is_err() || output.flush().is_err()
    {
        std::process::exit(1);
    }

    sumafile_media::server::run(std::io::stdin().lock(), output, sumafile_video::handle);
    std::process::exit(0);
}
