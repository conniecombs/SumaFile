//! The media process. It answers the file service's requests on its standard
//! input and output until the input closes, then leaves at once: a decode
//! still running has nobody left to answer.

fn main() {
    sumafile_media::server::run(
        std::io::stdin().lock(),
        std::io::stdout(),
        sumafile_media::render::handle,
    );
    std::process::exit(0);
}
