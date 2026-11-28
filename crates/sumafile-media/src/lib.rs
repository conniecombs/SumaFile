//! SumaFile's media process. It decodes image formats Windows may have no
//! codec for and answers `thumbnail` and `preview` requests from the file
//! service on its standard input and output (see `sumafile-media-client`).
//!
//! It is a process of its own so that a file that crashes a decoder, hangs
//! it or exhausts memory costs the file service nothing.

pub mod decoders;
pub mod limits;
pub mod render;
pub mod server;
pub mod tonemap;

#[cfg(test)]
pub(crate) mod testing;
