# V.E.L.O.C.I.T.Y. Share

V.E.L.O.C.I.T.Y. Share is a high-speed, secure, and resilient file transfer platform designed to transmit large enterprise payloads with client-side block encryption and integrity verification, backed by a native Rust core.

## Components
1. **Unmanaged Rust Cryptography Core (`velocity_share_ffi`)**:
   * Vectorized SHA-256 integrity hashing.
   * Block-level ChaCha20-Poly1305 stream cipher encryption.
2. **C# ASP.NET Core Signaling & Storage Backend (`VelocityShare.Server`)**:
   * P/Invoke bindings to the Rust FFI.
   * WebSocket Signaling endpoints to coordinate WebRTC SDP offers/answers.
   * REST endpoints for server-buffered dropsite uploads (fallback directory, local NAS).
3. **Command Center Web Client (`VelocityShare.Web`)**:
   * Glassmorphic dashboard in Obsidian dark mode.
   * HTML5 Canvas P2P connection visualizer.
   * SVG circular telemetry speed dials.
