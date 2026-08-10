# V.E.L.O.C.I.T.Y. Share

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Protocol](https://img.shields.io/badge/Protocol-V.C.T.P.-orange.svg)](#)
[![Speedup vs WebRTC](https://img.shields.io/badge/Speedup%20vs%20WebRTC-8.7x-brightgreen.svg)](#)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](#)
[![Rust FFI](https://img.shields.io/badge/Crypto-Rust%20FFI-orange.svg)](#)
[![CI/CD](https://github.com/UnitBuilds-CC/V.E.L.O.C.I.T.Y.-Share/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/UnitBuilds-CC/V.E.L.O.C.I.T.Y.-Share/actions/workflows/ci-cd.yml)

**V.E.L.O.C.I.T.Y. Share** is a secure, high-speed, and highly resilient file transfer platform designed to transmit large enterprise payloads. It integrates a custom UDP-based transport protocol (VCTP) with native Rust cryptography, block-level parallelization, and intelligent congestion control to maximize network link utilization.

---

## Features

### Core Transfer Engine
- **VCTP-Backed Folder Synchronization**: Direct UDP-based file transfer with WebSocket signaling
- **RTT-Aware NACK Deduplication**: Reduces redundant retransmissions by up to 90% on lossy networks
- **Adaptive AIMD Congestion Pacing**: Dynamically scales throughput based on real-time link quality
- **ChaCha20-Poly1305 Encryption**: Native Rust FFI for high-performance authenticated encryption
- **Zero-Copy Architecture**: Memory-mapped file transfers for maximum throughput
- **NDA Binary Protocol**: Compact 24-byte binary frame protocol replacing JSON overhead on the hot path

### Share Links
- **Time-Limited Downloads**: Create shareable links with configurable expiry (1 hour to 7 days)
- **Password Protection**: PBKDF2-HMAC-SHA256 hashed passwords (100K iterations via Rust FFI)
- **Download Limits**: Cap the number of downloads per link (1 to 10,000)
- **Brute-Force Protection**: 5 failed password attempts triggers 15-minute lockout per link
- **One-Time Download Tokens**: Passwords never appear in URLs — short-lived tokens instead
- **Styled Download Pages**: Branded password and download pages matching the dark UI theme

### Production Hardening

- **Cloud Storage Retry Logic**: S3 and Azure Blob providers use exponential backoff with jitter (3 retries) for all operations
- **TOCTOU Prevention**: ShareLinkManager uses atomic CAS loop for concurrent download counting
- **Memory-Safe Streaming**: Multi-chunk downloads stream directly to response body (no MemoryStream buffering)
- **Certificate Validation**: Mobile client enforces exact host matching and SSL policy in production
- **Concurrency Safety**: FileSyncEngine uses reference counting for concurrent remote changes, Interlocked for 64-bit stats
- **Journal Consistency**: SyncChangeJournal protects all reads with write lock for SQLite consistency
- **Cleanup Grace Period**: Orphan cleanup skips directories with activity within 24 hours

### Security
- **All Endpoints Rate-Limited**: Fixed window rate limiter (100 req/min per IP) on every endpoint
- **API Key Authentication**: All admin endpoints enforce constant-time API key validation
- **Comprehensive Security Headers**: CSP, HSTS, Permissions-Policy, X-Frame-Options, X-Content-Type-Options, Referrer-Policy
- **Path Traversal Prevention**: Regex validation + sandbox boundary enforcement on all file operations
- **No Information Disclosure**: Generic error messages in production, server-side logging of details
- **Non-Root Docker Container**: Runs as unprivileged `velocityshare` user
- **Kestrel Hardening**: Connection limits, body size caps, header limits, hidden server version
- **Request Timeouts**: 60-second default timeout policy

### Monitoring & Observability
- **Prometheus Metrics**: `/metrics` endpoint with real-time counters (admin-only in production)
- **Health Checks**: `/health` endpoint for container orchestration (Docker, Kubernetes)
- **JSON Structured Logging**: Production-grade log aggregation format with file logging fallback
- **Benchmark Suites**: BenchmarkDotNet (.NET) + Criterion (Rust) for crypto performance validation

### User Interfaces
- **Web Dashboard**: Premium dark-theme SPA with telemetry dials, network matrix visualization, drag-and-drop, keyboard shortcuts, and full accessibility (ARIA, skip-nav)
- **Mobile Client**: .NET MAUI cross-platform app with branded dark UI, sync stats, activity log, and Rust FFI crypto
- **Share Pages**: Standalone download pages for password-protected and public share links

---

## Quick Start

### Prerequisites
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (LTS)
- [Rust toolchain](https://rustup.rs/) (for FFI crypto library)

### Development

```powershell
# Clone the repository
git clone https://github.com/UnitBuilds-CC/V.E.L.O.C.I.T.Y.-Share.git
cd V.E.L.O.C.I.T.Y.-Share

# Build Rust FFI (Windows)
cd velocity_share_ffi
cargo build --release
copy target\release\velocity_share_ffi.dll ..\VelocityShare.Server\

# Run the server
cd ..\VelocityShare.Server
dotnet run --launch-profile https
```

### Production (Docker)

```bash
# Build the Docker image
docker build -t velocity-share:latest .

# Run the container
docker run -d \
  --name velocity-share \
  -p 5000:5000 \
  -v /path/to/uploads:/app/wwwroot/uploads \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e AdminCredentials__ApiKey=your-secret-api-key \
  -e Cors__AllowedOrigins__0=https://yourdomain.com \
  velocity-share:latest
```

### Docker Compose

```yaml
version: '3.8'
services:
  velocity-share:
    image: ghcr.io/unitbuilds-cc/velocity-share:latest
    ports:
      - "5000:5000"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - AdminCredentials__ApiKey=your-secret-api-key
      - Cors__AllowedOrigins__0=https://yourdomain.com
    volumes:
      - uploads:/app/wwwroot/uploads
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5000/health"]
      interval: 30s
      timeout: 3s
      retries: 3

volumes:
  uploads:
```

---

## Configuration

### appsettings.Production.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Cors": {
    "AllowedOrigins": [ "https://yourdomain.com" ],
    "AllowedMethods": [ "GET", "POST" ],
    "AllowedHeaders": [ "Content-Type", "Authorization", "X-API-Key", "X-WS-Token" ]
  },
  "AdminCredentials": {
    "ApiKey": "your-secret-api-key"
  }
}
```

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `ASPNETCORE_ENVIRONMENT` | Runtime environment | `Production` |
| `AdminCredentials__ApiKey` | API key for admin endpoints | *(required in production)* |
| `Cors__AllowedOrigins__0` | Allowed CORS origin | `https://share.unitbuilds.com` |
| `ASPNETCORE_URLS` | Server listening URLs | `http://+:5000` |

---

## API Endpoints

### Public Endpoints

| Method | Endpoint | Rate Limited | Description |
|--------|----------|:---:|-------------|
| GET | `/health` | — | Health check for container orchestration |
| GET | `/` | — | Web dashboard (SPA) |
| GET | `/api/share/auth/status` | ✅ | Returns whether authentication is required |
| GET | `/api/share/peers` | ✅ | Lists count of online peers (no IDs exposed) |

### Authenticated Endpoints (API Key Required)

| Method | Endpoint | Rate Limited | Description |
|--------|----------|:---:|-------------|
| POST | `/api/share/auth/verify` | ✅ | Validates an API key |
| POST | `/api/share/sync/start` | ✅ | Start folder synchronization |
| POST | `/api/share/sync/stop` | ✅ | Stop folder synchronization |
| POST | `/api/share/dumpsite` | ✅ | Configure custom dumpsite (NAS, cloud mock) |
| GET | `/api/share/dumpsite` | ✅ | Get dumpsite configuration |
| POST | `/api/share/upload` | ✅ | Upload file chunk (server-buffered fallback) |
| GET | `/api/share/download` | ✅ | Download file chunk |
| POST | `/api/share/link` | ✅ | Create a shareable download link |
| GET | `/api/share/links` | ✅ | List active share link count |
| GET | `/metrics` | ✅ | Prometheus metrics (admin-only in production) |

### Share Link Endpoints (Public)

| Method | Endpoint | Rate Limited | Description |
|--------|----------|:---:|-------------|
| GET | `/s/{id}` | ✅ | Share link page (download or password prompt) |
| POST | `/s/{id}/verify` | ✅ | Verify password, receive one-time download token |
| GET | `/s/{id}/download` | ✅ | Download file (requires token for protected links) |

### WebSocket

| Protocol | Endpoint | Description |
|----------|----------|-------------|
| WS | `/ws/share` | WebSocket signaling for WebRTC P2P handshake |

### Development-Only Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/share/test` | Rust FFI crypto self-test |
| GET | `/api/share/test/benchmark` | Cryptographic engine benchmarks (Rust FFI vs .NET) |
| GET | `/api/share/test/vctp` | VCTP integrity, speed, and interruptibility tests |
| GET | `/api/share/test/vctp/benchmark` | VCTP in-memory transport pipeline benchmarks |

---

## Architecture

### Project Structure

```
VelocityShare/
├── VelocityShare.Server/           # ASP.NET Core 10.0 server
│   ├── Program.cs                  # Main entry point (2400+ lines, all endpoints)
│   ├── ShareLinkManager.cs         # Share link lifecycle, brute-force protection, tokens
│   ├── SharePageGenerator.cs       # HTML generation for share link pages
│   ├── FileSyncEngine.cs           # Folder synchronization with FileSystemWatcher
│   ├── Sync/                       # Sync subsystem
│   │   ├── ISyncStorageProvider.cs # Storage provider interface
│   │   ├── LocalSyncStorageProvider.cs  # Local filesystem storage
│   │   ├── S3SyncStorageProvider.cs     # Amazon S3 with retry logic
│   │   ├── AzureBlobSyncStorageProvider.cs # Azure Blob with retry logic
│   │   ├── SyncRateLimiter.cs      # Bandwidth/CPU/IOPS throttling
│   │   ├── AdaptiveSyncScheduler.cs # Dynamic debounce scheduler
│   │   ├── SyncLatencyTracker.cs   # Rolling latency metrics
│   │   ├── SyncThrottleConfig.cs   # Profile-based throttle presets
│   │   └── SyncChangeJournal.cs    # SQLite persistent change log
│   ├── MetricsMiddleware.cs        # Prometheus-compatible metrics collection
│   ├── PathValidation.cs           # Path traversal prevention utilities
│   ├── VelocityShareCrypto.cs      # Rust FFI P/Invoke bindings
│   ├── NdaSignaling.cs             # NDA binary protocol (24-byte frames)
│   ├── VelocityShareCustomTransport.cs  # VCTP protocol engine
│   ├── appsettings.json            # Development configuration
│   ├── appsettings.Production.json # Production configuration
│   └── wwwroot/                    # Static web assets
│       ├── index.html              # SPA shell
│       ├── index.css               # Design system (dark theme, CSS variables)
│       └── app.js                  # Frontend logic (WebSocket, WebRTC, UI)
├── VelocityShare.Mobile/           # .NET MAUI mobile client
│   ├── MainPage.xaml               # Premium dark UI (Border cards, brand colors)
│   ├── MainPage.xaml.cs            # Sync stats, connection indicators, log viewer
│   ├── FileSyncClient.cs           # WebSocket sync client with Rust FFI crypto
│   └── VelocityShareCrypto.cs      # Mobile Rust FFI bindings (shared)
├── VelocityShare.Tests/            # xUnit test suite (116 tests)
│   ├── ShareLinkSecurityTests.cs   # Brute-force, download tokens, share link tests
│   ├── ProductionHardeningTests.cs # CAS loop, journal consistency, concurrency
│   ├── DeepCoverageTests.cs        # Cloud retry, cert validation, NDA protocol
│   └── ...                         # Additional test files
├── VelocityShare.Benchmarks/       # BenchmarkDotNet suite
│   ├── Sha256Benchmarks.cs         # SHA-256 Rust FFI vs .NET native
│   └── ChaCha20Benchmarks.cs       # ChaCha20-Poly1305 Rust FFI vs .NET native
├── VelocityShare.E2ETest/          # End-to-end integration tests
├── velocity_share_ffi/             # Rust cryptography FFI crate
│   ├── src/lib.rs                  # SHA-256, ChaCha20-Poly1305, PBKDF2
│   ├── benches/crypto_bench.rs     # Criterion benchmark suite (26 cases)
│   └── Cargo.toml
├── docs/                           # Documentation
│   ├── architectural_security_audit.md  # Production security audit report
│   ├── vctp_protocol_design.md          # VCTP protocol specification
│   ├── mobile_sync_architecture.md      # Mobile client architecture
│   ├── walkthrough.md                   # Technical implementation walkthrough
│   ├── share_links_feature.md          # Share links feature documentation
│   ├── benchmark_suite.md              # Benchmark suite documentation
│   └── velocity_suite_roadmap.md       # Suite-wide product roadmap
├── Dockerfile                      # Multi-stage build (Rust + .NET)
└── .github/workflows/              # CI/CD pipeline
```

### Technology Stack

| Layer | Technology |
|-------|-----------|
| **Server** | ASP.NET Core 10.0 |
| **Cryptography** | Rust FFI (ChaCha20-Poly1305, SHA-256, PBKDF2) |
| **Transport** | Custom VCTP (UDP-based) + WebRTC Data Channels |
| **Signaling** | WebSocket (`/ws/share`) |
| **Mobile** | .NET MAUI (Android, iOS, macOS, Windows) |
| **Frontend** | Vanilla JS SPA with CSS design system |
| **Testing** | xUnit (116 tests), BenchmarkDotNet, Criterion |
| **Container** | Docker multi-stage build, non-root user |
| **Metrics** | Prometheus-compatible `/metrics` endpoint |

---

## Performance Benchmarks

### Cryptographic Engine (Rust FFI vs .NET Native)

*Measured August 2026 — .NET 10.0.2, X64 RyuJIT AVX2, Windows 11, BenchmarkDotNet v0.14.0*

| Operation | Implementation | Mean (64KB) | Throughput | Comparison |
|-----------|---------------|------------|------------|------------|
| SHA-256 | Rust FFI | 32,541 ns | ~1.88 GB/s | Parity with .NET |
| SHA-256 | .NET Native | 31,731 ns | ~1.93 GB/s | Baseline |
| ChaCha20-Poly1305 Encrypt | Rust FFI | 31,147 ns | **1.96 GB/s** | **5.26x faster** |
| ChaCha20-Poly1305 Encrypt | .NET Native | 163,897 ns | 378 MB/s | Baseline |
| ChaCha20-Poly1305 Decrypt | Rust FFI | 35,046 ns | **1.77 GB/s** | **4.01x faster** |
| ChaCha20-Poly1305 Decrypt | .NET Native | 140,468 ns | 442 MB/s | Baseline |
| PBKDF2 (100K iter) | Rust FFI | 9.994 ms | — | ~Parity |
| PBKDF2 (100K iter) | .NET Native | 10.406 ms | — | Baseline |

### VCTP Throughput Comparisons

*In-memory loopback benchmark, 250 MB payload, .NET 10.0.2, Windows 11*

```
[WebRTC SCTP]   ██ (37.5 MB/s) | VCTP is 8.7x faster
[Aspera FASP]   █████ (75 MB/s) | VCTP is 4.3x faster
[SFTP/HTTPS]    ████████████████ (250 MB/s) | VCTP is 1.3x faster
[V.C.T.P.]      ████████████████████████████████████████████ (326.18 MB/s)
```

### VCTP In-Memory Pipeline

- **Throughput**: 326.18 MB/s (2.55 Gbps) over loopback
- **Duration**: 0.766 seconds for 250 MB
- **WebRTC Speedup**: 8.7x faster than SCTP data channels
- **Aspera Speedup**: 4.3x faster than FASP
- **Pipeline Overhead**: 1553.95 μs/MB (~10 ms total for 250 MB)

---

## Security

For the full security audit report, see [docs/architectural_security_audit.md](docs/architectural_security_audit.md).

### Defense in Depth

| Layer | Protection |
|-------|-----------|
| **Network** | Rate limiting (100 req/min/IP), CORS restrictions, Kestrel connection limits |
| **Transport** | HSTS (1 year, includeSubDomains), HTTPS enforcement in production |
| **Headers** | CSP, X-Frame-Options: DENY, Permissions-Policy, Referrer-Policy, X-Content-Type-Options |
| **Authentication** | API key validation with constant-time comparison (`CryptographicOperations.FixedTimeEquals`) |
| **Share Links** | PBKDF2-HMAC-SHA256 password hashing, brute-force lockout (5 attempts → 15 min), one-time download tokens |
| **File I/O** | Path traversal prevention, sandbox boundary enforcement, regex input validation |
| **Error Handling** | Generic error messages in production, server-side exception logging |
| **Container** | Non-root user, health checks, minimal attack surface |

### Security Headers (Production)

```
Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline' ...
Strict-Transport-Security: max-age=31536000; includeSubDomains
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), ...
Referrer-Policy: no-referrer
```

---

## Testing

```powershell
# Run all 116 tests
dotnet test

# Run security-specific tests
dotnet test --filter "FullyQualifiedName~ShareLinkSecurityTests"

# Run production hardening tests
dotnet test --filter "FullyQualifiedName~ProductionHardeningTests"

# Run benchmarks
dotnet run -p VelocityShare.Benchmarks -c Release

# Rust FFI benchmarks
cd velocity_share_ffi
cargo bench
```

---

## CI/CD Pipeline

The GitHub Actions workflow automatically:
1. Builds the Rust FFI library
2. Compiles the .NET solution
3. Runs health checks and E2E tests
4. Publishes Docker images to GitHub Container Registry

Push to `main` triggers a full build and Docker image publish.

---

## Documentation

| Document | Description |
|----------|-------------|
| [docs/architectural_security_audit.md](docs/architectural_security_audit.md) | Production security audit report |
| [docs/vctp_protocol_design.md](docs/vctp_protocol_design.md) | VCTP protocol specification |
| [docs/share_links_feature.md](docs/share_links_feature.md) | Share links feature documentation |
| [docs/benchmark_suite.md](docs/benchmark_suite.md) | Benchmark suite documentation |
| [docs/mobile_sync_architecture.md](docs/mobile_sync_architecture.md) | Mobile client architecture |
| [docs/walkthrough.md](docs/walkthrough.md) | Technical implementation walkthrough |
| [docs/velocity_suite_roadmap.md](docs/velocity_suite_roadmap.md) | Suite-wide product roadmap |

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
