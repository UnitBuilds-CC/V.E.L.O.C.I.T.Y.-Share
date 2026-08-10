# Mobile Client Architecture: V.E.L.O.C.I.T.Y. Share

**Status:** IMPLEMENTED ✅  
**Framework:** .NET MAUI (net10.0)  
**Targets:** Android, iOS, macOS, Windows  
**Last Updated:** August 2026  

---

## Overview

The V.E.L.O.C.I.T.Y. Share mobile client is a cross-platform .NET MAUI application that enables secure, automated folder synchronization between mobile devices and PC nodes. It shares the same Rust FFI cryptography layer as the server, ensuring consistent ChaCha20-Poly1305 encryption and SHA-256 integrity verification across all platforms.

---

## System Architecture

```
┌─────────────────────────────────────────────────┐
│              Mobile Device                       │
│                                                  │
│  ┌──────────┐  ┌──────────────┐  ┌───────────┐ │
│  │ MAUI UI  │──│ MainPage.cs  │──│ Rust FFI  │ │
│  │ (XAML)   │  │ (Code-behind)│  │ Crypto    │ │
│  └──────────┘  └──────┬───────┘  └───────────┘ │
│                       │                          │
│               ┌───────┴────────┐                │
│               │ FileSyncClient │                │
│               │ (WebSocket)    │                │
│               └───────┬────────┘                │
│                       │                          │
│               ┌───────┴────────┐                │
│               │ FileSystem     │                │
│               │ Watcher        │                │
│               └────────────────┘                │
└───────────────────────┬─────────────────────────┘
                        │ WebSocket Signaling
                        ▼
              ┌──────────────────┐
              │  V.E.L.O.C.I.T.Y │
              │  Share Server    │
              │  (ASP.NET Core)  │
              └────────┬─────────┘
                       │ WebSocket / WebRTC
                       ▼
              ┌──────────────────┐
              │  Peer PC Node    │
              │  (Web Dashboard) │
              └──────────────────┘
```

---

## Implementation Details

### Core Components

| File | Responsibility |
|------|---------------|
| `MainPage.xaml` | Premium dark UI with branded design system |
| `MainPage.xaml.cs` | UI logic, sync stats, connection indicators, log viewer |
| `FileSyncClient.cs` | WebSocket sync client with FileSystemWatcher |
| `VelocityShareCrypto.cs` | Rust FFI P/Invoke bindings (shared with server) |
| `Resources/Styles/Colors.xaml` | Brand color palette (matched to web frontend) |
| `Resources/Styles/Styles.xaml` | Global dark theme styles for all MAUI controls |

### FileSyncClient

The `FileSyncClient` class implements the mobile sync engine:

- **WebSocket Signaling**: Connects to the server's `/ws/share` endpoint
- **FileSystemWatcher**: Monitors local sync folder for changes (create, modify, delete, rename)
- **Debounced Processing**: 500ms debounce timer prevents redundant sync events
- **Catalog Tracking**: JSON metadata file (`.velocity_sync_metadata.json`) maps relative paths to SHA-256 hashes
- **Delta Detection**: Only syncs files whose hash has changed since last sync
- **Feedback Loop Prevention**: Temporarily disables FileSystemWatcher during remote writes
- **NDA Binary Protocol**: Uses compact 24-byte binary frames for efficient sync payloads

**Events exposed:**
- `OnLog(string message)` — Real-time log messages for UI display
- `OnStatusChanged(string status)` — Connection state changes (ACTIVE, CONNECTING, INACTIVE)
- `OnFileSynced(string fileName, long fileSize)` — File sync completion for stats tracking

### Rust FFI Integration

The mobile client uses the **same Rust FFI library** as the server:

```csharp
// VelocityShareCrypto.cs — identical P/Invoke bindings
[DllImport("velocity_share_ffi")]
public static extern int sha256_hash_chunk(byte* dataPtr, nuint dataLen, byte* hashOutPtr);

[DllImport("velocity_share_ffi")]
public static extern int encrypt_block_chacha(byte* keyPtr, byte* noncePtr, ...);

[DllImport("velocity_share_ffi")]
public static extern int decrypt_block_chacha(byte* keyPtr, byte* noncePtr, ...);
```

**Cross-compilation targets:**
- Android: `arm64-v8a`, `armeabi-v7a`, `x86_64` → `libvelocity_share_ffi.so`
- iOS: `aarch64-apple-ios` → `libvelocity_share_ffi.a`

---

## UI Design System

The mobile UI matches the web frontend's premium dark theme with consistent branding.

### Color Palette (Colors.xaml)

| Token | Value | Usage |
|-------|-------|-------|
| `BgBase` | `#0a0c12` | Page background |
| `BgSidebar` | `#0d1018` | Header background |
| `BgCard` | `#0e121c` | Card backgrounds |
| `BgInput` | `#1a1e2e` | Input fields, borders |
| `Green` | `#00ff66` | Primary accent, active states |
| `Cyan` | `#00e5ff` | Secondary accent, peer ID |
| `Amber` | `#f59e0b` | Warnings, sync icon |
| `Red` | `#ef4444` | Errors, inactive states |
| `TextPrimary` | `#f1f5f9` | Main text |
| `TextSecondary` | `#94a3b8` | Secondary text |
| `TextMuted` | `#64748b` | Labels, hints |

### UI Sections

1. **Header Bar** — Logo circle + brand title + connection status pill (colored dot + label)
2. **Your ID Card** — Prominent peer ID display with copy-to-clipboard button
3. **Sync Configuration Card** — Server URL, local path (with Browse dialog), target peer ID
4. **Sync Status Card** — Color-coded status badge + 3-column stats grid (files synced, data sent, uptime)
5. **Activity Log Card** — Styled dark terminal with event counter and color-coded entries
6. **Action Button** — Large 56px sync toggle (green → red color swap)
7. **Footer** — Subtle branding text

### Responsive Behavior

- Connection dot changes color: Red (offline) → Amber (connecting) → Green (secure)
- Status badge updates in real-time with matching colors
- Sync stats update live: file count, data transferred, uptime timer
- Log entries are color-coded by type (errors = red, sync client = cyan, other = green)
- Auto-scroll to latest log entry

---

## Cross-Platform Considerations

### Android
- **Permissions**: `READ_MEDIA_IMAGES`, `READ_MEDIA_VIDEO` (Android 13+)
- **Background Sync**: `WorkManager` with `PeriodicWorkRequest` (Wi-Fi + charging constraints)
- **Storage**: `ContentObserver` on `MediaStore` for real-time gallery monitoring

### iOS
- **Permissions**: `PHPhotoLibrary` access for photos/videos
- **Background Sync**: `BGProcessingTaskRequest` for opportunistic background execution
- **Storage**: Scoped sandbox directories via `FileSystem.AppDataDirectory`

### Windows
- **Storage**: Standard file system access via `FileSystem.AppDataDirectory`
- **Default Path**: `Path.Combine(FileSystem.AppDataDirectory, "Sync")`

---

## Build & Deployment

```powershell
# Build for Windows
dotnet build VelocityShare.Mobile -f net10.0-windows10.0.19041.0

# Build for Android
dotnet build VelocityShare.Mobile -f net10.0-android

# Build for iOS (macOS required)
dotnet build VelocityShare.Mobile -f net10.0-ios
```

**Build Status:** ✅ 0 errors, 0 warnings

---

## Future Enhancements

| Feature | Priority | Description |
|---------|----------|-------------|
| Native File Picker | High | Platform-specific folder picker UI instead of text prompt |
| Peer Discovery | High | Automatic detection of available peers on the network |
| Transfer History | Medium | Persistent log of synced files with re-sync capability |
| Share Link UI | Medium | Create and manage share links from mobile |
| Background Sync Service | High | OS-level background worker for continuous sync |
| Push Notifications | Medium | Alert on incoming file transfers |
| Battery Optimization | Low | Adaptive sync frequency based on battery level |
