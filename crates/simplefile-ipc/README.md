# simplefile-ipc

Protocol constants for [SumaFile](https://github.com/conniecombs/SumaFile), a Windows file manager whose WinUI host,
Rust service and plugin backends talk length-prefixed JSON-RPC 2.0 over named pipes.

The crate holds what both sides of a pipe must agree on, and no transport:

- the protocol and JSON-RPC versions, and the frame size limit (`MAX_FRAME_BYTES`);
- `frame`: the 4-byte little-endian length prefix of every frame (`encode_frame`, `decode_length`);
- `rpc`: the JSON-RPC request, response, error and notification shapes;
- the binary frame magic and version (`BINARY_FRAME_MAGIC`, `BINARY_FRAME_VERSION`);
- the error codes (`ERR_INVALID_REQUEST`, `ERR_METHOD_NOT_FOUND`, `ERR_INVALID_PARAMS`, `ERR_APPLICATION`, ...) and
  the error message prefixes the host recognises (`CONFLICT:`, ...);
- the method names and binary frame kinds generated from the schemas in
  [`ipc/schema`](https://github.com/conniecombs/SumaFile/tree/main/ipc/schema).

Plugin backends normally use it through [`sumafile-plugin`](https://github.com/conniecombs/SumaFile/tree/main/crates/sumafile-plugin),
which builds on it and implements the pipe server.

The crate keeps the `simplefile` name SumaFile had before it was renamed; the name is part of the protocol's history,
not a separate product.

## License

MIT
