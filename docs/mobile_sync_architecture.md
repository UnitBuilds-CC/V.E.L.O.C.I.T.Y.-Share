# Architectural Blueprint: V.E.L.O.C.I.T.Y. Share Mobile (Android/iOS)

This document outlines the architectural design for extending **V.E.L.O.C.I.T.Y. Share** to Android and iOS devices, enabling secure, automated background syncing of mobile galleries (DCIM), ebooks, documents, and other user data directly with a PC node or cloud instance.

---

## 1. System Architecture

To maintain the zero-trust, high-performance nature of V.E.L.O.C.I.T.Y. Share, the mobile app will function as a native peer, negotiating direct **WebRTC P2P Data Channels** with PC nodes and using the signaling hub as a coordinator or fallback dropsite.

```mermaid
graph TD
    subgraph Mobile Device (Android / iOS)
        Media[Gallery / DCIM / Ebooks / Docs]
        AppUI[Mobile Command Center UI]
        SyncWorker[Background Sync Worker]
        MobileFFI[Mobile Rust Crypto FFI]
    end

    subgraph PC Node
        LocalDir[Local Directory]
        PCServer[C# local server]
        PCBrowser[Dashboard UI]
    end

    subgraph Cloud Instance / Gateway
        SigServer[WebSocket Signaling Hub]
        CloudDrop[Encrypted Dumpsite Fallback]
    end

    SyncWorker -->|Read| Media
    SyncWorker -->|Frictionless P/Invoke| MobileFFI
    AppUI -->|Controls| SyncWorker

    %% Signaling Route
    PCServer <-->|Local WebSocket| PCBrowser
    PCBrowser <-->|WebSocket Signaling| SigServer
    SyncWorker <-->|WebSocket Signaling| SigServer

    %% Data Sync Route
    SyncWorker <-->|P2P WebRTC Data Channel| PCBrowser
    PCBrowser <-->|File Writes| PCServer
    SyncWorker -.->|Upload Fallback| CloudDrop
    PCServer -.->|Download Fallback| CloudDrop
```

---

## 2. Cross-Platform Technology Strategy

To maximize code reuse, stability, and speed of delivery, we recommend using **.NET MAUI (Multi-platform App UI)** or **Flutter with Rust Bindings**. 

### The .NET MAUI Advantage (Recommended)
Since the V.E.L.O.C.I.T.Y. Share backend is written in **ASP.NET Core (.NET 10)**, a .NET MAUI application offers unmatched architectural synergy:
1. **Shared Cryptography Layer**: The unmanaged C# P/Invoke bindings (`VelocityShareCrypto.cs`) can be copied directly into the mobile project.
2. **Shared Sync Logic**: The metadata compile and delta calculation algorithms (`FileSyncEngine.cs`) can be shared with minimal modifications.
3. **Rust FFI Reuse**: We can load the exact same compiled Rust FFI library natively on Android and iOS.

---

## 3. Cross-Compiling the Rust Cryptography Core

To preserve hardware-accelerated ChaCha20-Poly1305 and SHA-256 chunk hashing on mobile devices, the `velocity_share_ffi` crate will be compiled to native mobile dynamic/static libraries.

### Target Compilation Matrix:
* **Android**: Compiled using `cargo-ndk` to generate target architectures:
  * `arm64-v8a` (Modern Android devices)
  * `armeabi-v7a` (Older Android devices)
  * `x86_64` (Android Emulators)
  * Output: Packaged inside the Android app under `lib/` as `libvelocity_share_ffi.so`.
* **iOS**: Compiled using standard target toolchains (e.g. `aarch64-apple-ios` for devices, `aarch64-apple-ios-sim` for Apple Silicon simulators) and wrapped in a static archive framework.
  * Output: Packaged as `libvelocity_share_ffi.a` inside a Swift-linked framework.

---

## 4. OS-Level Directory Monitoring & Scoped Storage

Mobile operating systems enforce strict sandboxing and storage permissions. The app will interface with platform APIs to watch files:

### Android Implementation
* **Permissions**: Requires `READ_MEDIA_IMAGES` and `READ_MEDIA_VIDEO` (Android 13+) or `MANAGE_EXTERNAL_STORAGE` (for general ebooks/documents directory access).
* **FileSystem Monitoring**: Instead of standard file watchers, the app registers a `ContentObserver` on the Android `MediaStore` database to listen for additions to the Gallery or DCIM in real time.
* **Background Worker**: Implements `WorkManager` with a `PeriodicWorkRequest` configured to run when the device is **connected to Wi-Fi** and **charging**, executing a background catalog scan and P2P sync.

### iOS Implementation
* **Permissions**: Photo Library Access APIs (`PHPhotoLibrary`) and Documents/Ebooks directory sandboxes.
* **FileSystem Monitoring**: Registers as a delegate to the `PHPhotoLibraryChangeObserver` to capture new photos/videos immediately.
* **Background Worker**: Implements the `BackgroundTasks` framework (`BGProcessingTaskRequest`), requesting opportunistic background execution slots to negotiate connection and transfer metadata deltas.

---

## 5. Mobile UI Features

The mobile interface will replicate the obsidian-neon aesthetic in a compact mobile format:
1. **Live Roster**: View connected PC nodes and cloud dumpsites.
2. **Automated Folders**: Toggle switches to auto-sync "Camera Roll", "Ebooks", "Downloads", and "Voice Memos".
3. **Manual File Picker**: Send individual files to the PC with a single tap.
4. **Link Telemetry**: Compact circular dial meters showing transmission speeds, battery impact, and link latency.
