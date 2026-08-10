# V.E.L.O.C.I.T.Y. Software Suite Roadmap

**Last Updated:** August 2026  

This document outlines the product strategy and architectural vision for the **V.E.L.O.C.I.T.Y.** software suite. All systems leverage the ultra-low latency, zero-allocation native Rust core for maximum performance.

---

## Product Status Overview

| Product | Status | Version | Notes |
|---------|--------|---------|-------|
| **V.E.L.O.C.I.T.Y. Share** | ✅ **Production Ready** | 1.0 | Full P2P file transfer platform |
| **V.E.L.O.C.I.T.Y. Messenger** | 🟡 In Development | — | P2P VoIP calling pipeline |
| **V.E.L.O.C.I.T.Y. Stream** | 🔵 Planned | — | Live streaming platform |
| **V.E.L.O.C.I.T.Y. Store** | 🔵 Planned | — | Cloud storage platform |
| **V.E.L.O.C.I.T.Y. Remote** | 🔵 Planned | — | Remote desktop system |
| **V.E.L.O.C.I.T.Y. Play** | 🔵 Planned | — | Music streaming service |

---

## 1. V.E.L.O.C.I.T.Y. Share — ✅ Production Ready

**Status:** Fully implemented, security hardened, production deployed.

The secure file transfer platform is complete with:
- Custom VCTP (UDP-based) transport protocol with BBR-style congestion control
- Native Rust FFI cryptography (ChaCha20-Poly1305, SHA-256, PBKDF2)
- WebRTC P2P data channels with WebSocket signaling fallback
- Folder synchronization engine with delta detection
- Share links with password protection, expiry, brute-force protection, and one-time download tokens
- Premium dark-theme web dashboard with telemetry dials and network visualization
- Cross-platform .NET MAUI mobile client with matching UI
- Prometheus metrics, health checks, Docker deployment
- 53 passing tests, comprehensive benchmark suites
- Full production security hardening (see [architectural_security_audit.md](architectural_security_audit.md))

**Documentation:**
- [README.md](../README.md) — Full project documentation
- [architectural_security_audit.md](architectural_security_audit.md) — Security audit report
- [vctp_protocol_design.md](vctp_protocol_design.md) — VCTP protocol specification
- [share_links_feature.md](share_links_feature.md) — Share links feature documentation
- [benchmark_suite.md](benchmark_suite.md) — Benchmark suite documentation
- [mobile_sync_architecture.md](mobile_sync_architecture.md) — Mobile client architecture
- [walkthrough.md](walkthrough.md) — Technical implementation walkthrough

---

## 2. V.E.L.O.C.I.T.Y. Stream (Twitch Competitor) — 🔵 Planned

* **Objective:** Build a next-generation live streaming platform offering sub-second glass-to-glass latency and high-fidelity video/audio broadcasting.
* **Core Technology:**
  * Native RTMP/SRT/WebRTC ingestion pipelines managed directly by the Rust ring buffer matrix.
  * Zero-copy packet forwarding from ingest nodes to regional edge caches.
  * Real-time transcode acceleration utilizing GPU/Hardware encoders managed by lock-free thread pools.
* **Aesthetic Direction:** Dynamic neon-accented dark interface, overlay personalization, and live chat visualization.

---

## 3. V.E.L.O.C.I.T.Y. Store (Google Drive Competitor) — 🔵 Planned

* **Objective:** Create a secure, distributed cloud storage platform featuring instantaneous file synchronization, encryption, and collaboration.
* **Core Technology:**
  * Client-side zero-knowledge encryption using AES-GCM-256 with keys held solely by the user.
  * Multi-threaded file chunking and hash validation mapped to the native unmanaged pipeline.
  * Delta-sync algorithms to transmit only binary diffs rather than entire files.
  * Distributed caching layers for near-zero delay download speeds.
* **Aesthetic Direction:** Frosted glass visual files drawer, intuitive drag-and-drop file dropzones, and interactive utilization analytics.

---

## 4. V.E.L.O.C.I.T.Y. Remote (AnyDesk Competitor) — 🔵 Planned

* **Objective:** Develop an ultra-low latency remote desktop connection and control system.
* **Core Technology:**
  * Proprietary screen capture and compression codec utilizing NVENC/AMF/VAAPI hardware-assisted encoding.
  * Lock-free frame buffer chunking directly routed to a UDP/WebRTC data channel.
  * Custom input packet dispatching to minimize mouse/keyboard input lag.
  * Dynamic network adaptation to scale bitrate dynamically based on ping spikes.
* **Aesthetic Direction:** High-fidelity, overlay-less viewports, fluid connection animations, and seamless dark mode configuration consoles.

---

## 5. V.E.L.O.C.I.T.Y. Play (Spotify Competitor) — 🔵 Planned

* **Objective:** Deliver a premium, high-fidelity music streaming service for audiophiles.
* **Core Technology:**
  * FLAC/Opus audio streaming buffers powered by the native ring buffer to guarantee gapless playback.
  * Client-side audio processing filters (EQ, spatial audio virtualization).
  * Fast metadata caching and offline playlist encryption.
* **Aesthetic Direction:** Vibrant, dynamic ambient background lighting that shifts to match album art colors, neon progress timelines, and smooth hover micro-animations on albums/tracks.

---

## Shared Technology Foundation

All V.E.L.O.C.I.T.Y. products share:
- **Rust FFI Core**: Hardware-accelerated ChaCha20-Poly1305, SHA-256, PBKDF2
- **Obsidian-Neon Aesthetic**: Dark theme with green/cyan accent palette
- **Zero-Allocation Hot Paths**: Memory-mapped files, stack-allocated nonces, pointer-based crypto
- **WebSocket Signaling**: Consistent P2P handshake protocol across all products
- **Docker Deployment**: Multi-stage builds, non-root containers, health checks
