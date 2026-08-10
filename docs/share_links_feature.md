# Share Links Feature: V.E.L.O.C.I.T.Y. Share

**Status:** Production Ready ✅  
**Module:** `ShareLinkManager.cs` (207 lines) + `SharePageGenerator.cs` (92 lines)  
**Tests:** 11 security tests in `ShareLinkSecurityTests.cs`  
**Last Updated:** August 2026

---

## Overview

Share Links allow users to create time-limited, optionally password-protected download URLs for files in the V.E.L.O.C.I.T.Y. Share system. The feature implements a secure token-based download flow that ensures passwords never appear in URLs, and includes brute-force protection to prevent unauthorized access.

---

## Architecture

```
┌──────────────┐     POST /api/share/link      ┌──────────────────┐
│  Web Dashboard│ ──────────────────────────────>│ ShareLinkManager │
│  (app.js)     │     {fileId, expiry, password} │                  │
│               │<────────────────────────────── │  CreateLink()    │
│               │     {shareId, shareUrl}        │                  │
└──────────────┘                                └──────────────────┘
                                                          │
┌──────────────┐     GET /s/{id}                 ┌────────▼─────────┐
│  Recipient    │ ──────────────────────────────> │ SharePageGenerator│
│  (Browser)    │                                 │                  │
│               │<──────────────────────────────  │  Password Page   │
│               │     HTML (branded dark UI)      │  or Download Page│
└──────────────┘                                └──────────────────┘
        │
        │  POST /s/{id}/verify {password}
        │<───────────────────────────────────────── {downloadToken}
        │
        │  GET /s/{id}/download?token=xxx
        │<───────────────────────────────────────── File stream
```

---

## Share Link Lifecycle

### 1. Creation

Share links are created via `POST /api/share/link` (authenticated, rate-limited):

```json
{
  "fileId": "abc123",
  "fileName": "enterprise-dataset.csv",
  "expiryHours": 24,
  "password": "optional-secret",
  "maxDownloads": 100
}
```

**Response:**
```json
{
  "shareId": "xK9mP2vLqR7w",
  "shareUrl": "https://share.example.com/s/xK9mP2vLqR7w",
  "fileName": "enterprise-dataset.csv",
  "fileSize": 52428800,
  "expiresAt": "2026-08-10T14:30:00Z",
  "maxDownloads": 100,
  "passwordProtected": true
}
```

**ID Generation:** 12-character, URL-safe Base64 encoded from 9 cryptographically random bytes (`RandomNumberGenerator.Fill`).

### 2. Password Hashing

When a password is provided:
1. A 16-byte random salt is generated
2. PBKDF2-HMAC-SHA256 is run for **100,000 iterations** via the Rust FFI (`VelocityShareCrypto.Pbkdf2Derive`)
3. The salt (16 bytes) and hash (32 bytes) are concatenated and stored as Base64
4. The raw password is never stored

### 3. Validation Flow

When a recipient visits `/s/{id}`:

| Step | Check | Result |
|------|-------|--------|
| 1 | Link exists in memory | 404 / expired page |
| 2 | Link not expired | Expired page |
| 3 | Download limit not reached | Expired page |
| 4 | Not brute-force locked | Locked page (shows expired) |
| 5 | Password required? | Show password page or download page |

### 4. One-Time Download Tokens

**Passwords never appear in URLs.** Instead:

1. Recipient submits password via `POST /s/{id}/verify`
2. Server validates password using constant-time comparison (`CryptographicOperations.FixedTimeEquals`)
3. On success, server issues a **one-time download token**:
   - 128-bit (16 bytes) cryptographically random
   - URL-safe Base64 encoded
   - Expires in **2 minutes**
   - Single-use (consumed on first download)
4. Recipient is redirected to `/s/{id}/download?token=xxx`
5. Server validates and consumes the token, then streams the file

### 5. Expiry & Cleanup

- Links auto-expire based on configured duration (1 hour to 7 days)
- Links auto-expire when download count reaches `maxDownloads`
- `CleanupExpired()` runs periodically to purge stale links and tokens from memory

---

## Brute-Force Protection

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| Max failed attempts | 5 | Prevents casual guessing |
| Lockout duration | 15 minutes | Blocks sustained attacks |
| Scope | Per-link | One link's lockout doesn't affect others |
| Recovery | Automatic after lockout expires | Failed counter resets |
| Success reset | Failed counter resets on correct password | Prevents lockout from intermittent typos |

**Attack scenario:** An attacker trying to guess a 6-character password (2.18 billion combinations) would need ~435 million years at 5 attempts per 15 minutes per link.

---

## Share Link Pages

### Password Page (`SharePageGenerator.GeneratePasswordPage`)

- Branded dark UI matching the main dashboard
- CSS variables: `#0a0c12` background, `#00ff66` accent, `#00e5ff` secondary
- Password input field with submit button
- Inline JavaScript posts to `/s/{id}/verify`
- On success: redirects to download URL with token
- On failure: shows error message (generic, no information leakage)

### Download Page (`SharePageGenerator.GenerateDownloadPage`)

- File name (HTML-encoded for XSS prevention)
- File size (human-readable format)
- Expiry timestamp
- Downloads remaining counter
- Green download button with hover animation
- Branded footer

### Expired Page (`SharePageGenerator.GenerateExpiredPage`)

- Clean "link expired or invalid" message
- No information about whether the link ever existed
- Branded styling consistent with other pages

---

## API Endpoints

| Method | Endpoint | Auth | Rate Limited | Description |
|--------|----------|------|:---:|-------------|
| POST | `/api/share/link` | API Key | ✅ | Create a share link |
| GET | `/api/share/links` | API Key | ✅ | List active share link count |
| GET | `/s/{id}` | — | ✅ | Share link page (password or download) |
| POST | `/s/{id}/verify` | — | ✅ | Verify password, get download token |
| GET | `/s/{id}/download` | — | ✅ | Download file (token required for protected links) |

---

## Security Controls

| Control | Implementation |
|---------|---------------|
| Password hashing | PBKDF2-HMAC-SHA256, 100K iterations, Rust FFI |
| Password comparison | `CryptographicOperations.FixedTimeEquals` (constant-time) |
| Download tokens | 128-bit random, 2-min expiry, single-use |
| Brute-force protection | 5 attempts → 15-min lockout per link |
| XSS prevention | `HtmlEncode` on all filenames in generated pages |
| Rate limiting | All 5 endpoints rate-limited (100 req/min per IP) |
| No password in URLs | Tokens replace passwords in query strings |
| No information disclosure | Generic expired page for all invalid/locked/expired links |
| ID generation | 9 bytes from `RandomNumberGenerator.Fill`, URL-safe Base64 |

---

## Test Coverage

All 11 tests in `ShareLinkSecurityTests.cs`:

| Test | What It Verifies |
|------|-----------------|
| `CreateLink_NoPassword_ValidatesWithoutPassword` | Unprotected links work without password |
| `CreateLink_WithPassword_RequiresPassword` | Protected links reject missing password |
| `ExpiredLink_ReturnsNull` | Expired links are rejected |
| `BruteForce_LockoutAfter5FailedAttempts` | 5 wrong passwords → lockout |
| `BruteForce_LockoutResetsAfterDuration` | Lockout clears after 15 minutes |
| `BruteForce_SuccessResetsFailedCount` | Correct password resets counter |
| `DownloadToken_IsUniquePerCall` | Each token is different |
| `DownloadToken_CannotBeReused` | Token consumed on first use |
| `DownloadToken_ExpiredTokenRejected` | Expired tokens rejected |
| `MaxDownloads_EnforcedCorrectly` | Download limit works |
| `CleanupExpired_RemovesStaleLinks` | Cleanup purges expired links |

---

## Configuration

Share link parameters can be configured per-creation via the web dashboard modal:

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| Expiry | 24 hours | 1h – 168h (7 days) | How long the link remains valid |
| Password | None | Any string | Optional password protection |
| Max Downloads | 100 | 1 – 10,000 | Maximum number of downloads allowed |

---

## Related Documentation

- [architectural_security_audit.md](architectural_security_audit.md) — Full security audit
- [walkthrough.md](walkthrough.md) — Technical implementation walkthrough
- [README.md](../README.md) — Project overview and API reference
