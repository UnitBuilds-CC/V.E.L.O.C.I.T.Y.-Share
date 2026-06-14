# Architectural Security Audit Report: JabuDemo Switch (XSwitch)

**Status:** RUTHLESS PRODUCTION AUDIT  
**Target System:** Polyglot Payment and Transaction Switch (C#, Rust, F#, Go)  
**Auditor:** Antigravity Security Team  
**Date:** June 11, 2026  

---

## 🎯 Executive Summary

The JabuDemo switch (XSwitch) achieves exceptional performance (82k+ RPS in-memory, 0 GC runs) by delegating low-level state transitions and cryptographic signing to an unmanaged Rust core. However, from a banking security standpoint, **the system contains critical vulnerabilities that make it unsafe for production deployment**. 

The current security model relies heavily on the assumption of a trusted network perimeter and memory-only state caches. If deployed in a live bank, these design choices would lead to **financial loss (double-spends), remote balance falsification, and trivial Denial of Service (DoS) attacks**.

This audit details these vulnerabilities, categorizes them by severity, and provides concrete remediation paths.

---

## 🛑 Summary of Findings

| ID | Finding Title | Severity | Impact |
| :--- | :--- | :--- | :--- |
| **SEC-01** | Transaction Replay & Double-Spend via Server Restart | **CRITICAL** | Direct Financial Loss |
| **SEC-02** | Unauthenticated Deposit API and Lack of Safe Signatures | **CRITICAL** | Fraudulent Fund Injection |
| **SEC-03** | Unbounded Memory Accumulation (OOM Denial of Service) | **HIGH** | System Failure / Crash |
| **SEC-04** | Exposed Administrative and Auditing Control Endpoints | **HIGH** | Unauthorized System Hijack |
| **SEC-05** | Plaintext Communication Default (No SSL/TLS) | **MEDIUM** | Man-in-the-Middle (MitM) |
| **SEC-06** | Floating-Point Tolerance and Rounding Exploitation | **MEDIUM** | Micro-Arbitrage Theft |
| **SEC-07** | Permanent Lockfile Stalling on Power Cuts (Go Edge) | **MEDIUM** | Hardware Terminal Block |

---

## 🔍 Detailed Vulnerability Reports

### SEC-01: Transaction Replay & Double-Spend via Server Restart
* **Severity:** **CRITICAL**
* **Target Component:** `SagaOrchestrator.cs` / `IdempotencyMiddleware.cs` / `LedgerEngine.cs`
* **Vulnerability Analysis:**
  The `IdempotencyMiddleware` and `SagaOrchestrator` maintain idempotency state and active saga sessions entirely in-memory using `ConcurrentDictionary` objects. 
  When the application server restarts, this memory is wiped. While the ledger state is replayed from the journal, **the engine does not maintain a set of historically processed transaction IDs**. 
  Consequently, if an attacker intercepts a successful payout request and resubmits it after a server restart, the switch will treat it as a brand-new transaction. It will debit the merchant and credit the wholesaler a second time using the same `SagaId` and `X-Idempotency-Key`.
* **Remediation:**
  1. The `LedgerEngine` must build and maintain a unique index of committed transaction IDs (e.g. `HashSet<string>`) during journal replay.
  2. `SubmitTransactionAsync` must check this transaction ID set and reject any incoming transaction containing a duplicate ID, regardless of memory cache state.

---

### SEC-02: Unauthenticated Deposit API and Lack of Safe Signatures
* **Severity:** **CRITICAL**
* **Target Component:** `Program.cs` (`/api/v1/deposit`) / `main.go`
* **Vulnerability Analysis:**
  The smart safe deposit API endpoint `/api/v1/deposit` accepts a JSON payload containing the deposit request parameters. However, it lacks **any form of authentication or cryptographic verification**. 
  An attacker with network access to the switch can make raw HTTP POST requests to this endpoint, pretending to be a physical safe (`SAFE_01`), and credit arbitrary amounts to a merchant's digital wallet without placing any physical cash inside a safe.
  The Go Edge client simply POSTs the JSON without signing the payload with a hardware-backed key.
* **Remediation:**
  1. Smart Safes must be provisioned with client certificates (Mutual TLS / mTLS) to authenticate their network identity.
  2. Every deposit request must be signed by the safe's hardware security module (HSM) or Trusted Platform Module (TPM) using an asymmetric key pair. The Central Switch must verify the signature against the safe's registered public key before committing the deposit.

---

### SEC-03: Unbounded Memory Accumulation (OOM Denial of Service)
* **Severity:** **HIGH**
* **Target Component:** `Middleware.cs` (`IdempotencyMiddleware`) / `SagaOrchestrator.cs`
* **Vulnerability Analysis:**
  - `IdempotencyGuard` caches response bytes for every transaction key indefinitely. There is no cache eviction policy (TTL or size limit).
  - `SagaOrchestrator` stores completed saga sessions in `_activeSagas` without ever removing them after settlement.
  An attacker can easily craft requests with unique random UUIDs for `X-Idempotency-Key` or `SagaId`. Under continuous load, the memory footprint will grow exponentially until the switch runs Out of Memory (OOM) and crashes.
* **Remediation:**
  1. Implement a Time-To-Live (TTL) eviction policy for the idempotency cache (e.g., 24 hours).
  2. Evict completed Sagas from `_activeSagas` once they reach a terminal state (`Succeeded`, `Failed`, or `Reversed`).
  3. Use a distributed, memory-bounded cache (such as Redis with a maxmemory-LRU policy) for production deployments.

---

### SEC-04: Exposed Administrative and Auditing Control Endpoints
* **Severity:** **HIGH**
* **Target Component:** `Program.cs` (`/api/v1/admin/*` and `/api/v1/audit/*`)
* **Vulnerability Analysis:**
  Administrative endpoints (like changing the validation engine, toggling `fsync`, or resetting performance modes) and sensitive auditing endpoints (dumping balances and reconciling ledger discrepancies) are exposed on the public HTTP port. They have no authentication middleware, allowing any network user to compromise system stability, degrade durability guarantees, or exfiltrate private financial data.
* **Remediation:**
  1. Bind administrative and auditing endpoints to a private, loopback-only port (e.g. `127.0.0.1:5001`) or a separate management network interface.
  2. Protect these routes with strict token-based authentication (JWT/OAuth2) restricted to administrators and auditors.

---

### SEC-05: Plaintext Communication Default (No SSL/TLS)
* **Severity:** **MEDIUM**
* **Target Component:** `main.go` / `Program.cs`
* **Vulnerability Analysis:**
  The Go Edge client defaults to using unencrypted HTTP (`http://localhost:5000` or arbitrary server URLs). Transactions traversing public cellular APNs are vulnerable to snooping, session hijacking, and active manipulation by MitM attackers.
* **Remediation:**
  Enforce HTTPS-only communication at both the Kestrel server configuration level and the Go client level. The Go client must refuse to connect if the server certificate validation fails.

---

### SEC-06: Floating-Point Tolerance and Rounding Exploitation
* **Severity:** **MEDIUM**
* **Target Component:** `Rules.fs` / `LedgerEngine.cs`
* **Vulnerability Analysis:**
  The validation engine uses double-precision floats (`double`) during FX position checks and allows a **2.5 cent rounding tolerance** (`difference > 2.5`). 
  Using floating-point variables for currency math can introduce precision drift. An attacker can exploit this 2.5 cent tolerance gap by routing thousands of micro-payouts, deliberately manipulating the exchange rate digits to skim fractional cents, accumulating significant risk-free profits (salami slicing attack).
* **Remediation:**
  1. Replace all floating-point math with fixed-point `decimal` types or perform all calculations using integer cents.
  2. Tighten the validation logic to enforce zero rounding tolerance, or log any non-zero rounding differences to an automated ledger variance account.

---

### SEC-07: Permanent Lockfile Stalling on Power Cuts (Go Edge)
* **Severity:** **MEDIUM**
* **Target Component:** `main.go` (`edge_journal.lock`)
* **Vulnerability Analysis:**
  The Go client implements a spin-lock by creating a file `edge_journal.lock` using exclusive file creation flags. If the hardware client loses power or crashes mid-write, the lock file remains on disk. On restart, the client will time out and crash indefinitely, causing a physical safe terminal lockout.
* **Remediation:**
  The Go client must write the current process ID (PID) into the lock file and check if that PID is still active on startup. If the file exists but the owner process is dead, the lock must be safely reclaimed.

---

## ⚖️ Final Assessment

The JabuDemo switch is an engineering showcase for high-speed, zero-allocation polyglot pipelines. However, in its current state, **it operates under a "happy path" security assumption**. To deploy this system safely in a bank, the development team must implement the remediation plan above to bridge the gap between low-latency optimization and critical financial security.
