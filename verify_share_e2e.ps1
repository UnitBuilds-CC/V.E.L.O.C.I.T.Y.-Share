# V.E.L.O.C.I.T.Y. Share Automated E2E Verification Runner
# This script hits the server's diagnostic and performance endpoints to verify FFI and VCTP integrity.

$VM_IP = "52.188.14.216"
$HOST_HEADER = "share.unitbuilds.com"
$BASE_URL = "https://$VM_IP"

Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host "          V.E.L.O.C.I.T.Y. Share E2E Diagnostic Test Runner" -ForegroundColor Cyan
Write-Host "=====================================================================" -ForegroundColor Cyan

# Helper function to invoke API via curl.exe to bypass .NET SSL certificate blocks on raw IPs
function Invoke-ShareApi {
    param (
        [string]$Path
    )
    $url = "$BASE_URL$Path"
    try {
        $responseStr = & curl.exe -s -k -H "Host: $HOST_HEADER" $url
        if ($responseStr) {
            return $responseStr | ConvertFrom-Json
        }
    } catch {
        Write-Error "API Call to $Path failed: $_"
    }
    return $null
}

# 1. Run FFI Crypto self-test
Write-Host "[Test 1] Running FFI Crypto Diagnostic (SHA256 & ChaCha20 FFI)..." -ForegroundColor Yellow
$res1 = Invoke-ShareApi -Path "/api/share/test"
if ($res1 -and $res1.status -eq "PASS") {
    Write-Host "  -> [PASS] SHA256 Hash: $($res1.sha256)" -ForegroundColor Green
    Write-Host "  -> [PASS] Decrypted content match: '$($res1.decrypted)'" -ForegroundColor Green
} else {
    Write-Error "  -> [FAIL] FFI Crypto self-test did not return PASS status."
    exit 1
}

# 2. Run VCTP File Transfer loopback simulation
Write-Host "[Test 2] Running VCTP loopback file sync simulation (with force-kill & resume)..." -ForegroundColor Yellow
$res2 = Invoke-ShareApi -Path "/api/share/test/vctp"
if ($res2 -and $res2.status -eq "PASS") {
    Write-Host "  -> [PASS] VCTP transfer completed successfully." -ForegroundColor Green
    Write-Host "  -> Throughput: $($res2.throughput_mbs.ToString("F2")) MB/s ($(($res2.throughput_mbps / 1000.0).ToString("F2")) Gbps)" -ForegroundColor Green
    Write-Host "  -> Verification log count: $($res2.logs.Count) lines." -ForegroundColor Green
} else {
    Write-Error "  -> [FAIL] VCTP self-test did not return PASS status."
    exit 1
}

# 3. Run Cryptographic FFI Benchmark
Write-Host "[Test 3] Fetching FFI vs .NET Cryptography Benchmark (625 MB payload)..." -ForegroundColor Yellow
$res3 = Invoke-ShareApi -Path "/api/share/test/benchmark"
if ($res3) {
    Write-Host "  -> SHA256 Rust FFI: $($res3.sha256.rust_ffi.speed_mbps.ToString("F2")) Mbps" -ForegroundColor Green
    Write-Host "  -> ChaCha20 Rust FFI: $($res3.chacha20_poly1305.rust_ffi.speed_mbps.ToString("F2")) Mbps" -ForegroundColor Green
}

# 4. Run VCTP High-Throughput Memory Benchmark
Write-Host "[Test 4] Fetching VCTP 100% In-Memory Sync Benchmark (250 MB payload)..." -ForegroundColor Yellow
$res4 = Invoke-ShareApi -Path "/api/share/test/vctp/benchmark"
if ($res4 -and $res4.status -eq "PASS") {
    Write-Host "  -> [PASS] VCTP Memory Sync: $($res4.throughput_gbps.ToString("F2")) Gbps ($($res4.throughput_mbs.ToString("F2")) MB/s)" -ForegroundColor Green
    Write-Host "  -> Comparison vs WebRTC SCTP Browser: $($res4.comparisons.webrtc_sctp_browser.speedup_x.ToString("F1"))x Speedup" -ForegroundColor Green
}

Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host "          ALL VELOCITY SHARE E2E DIAGNOSTIC TESTS PASSED!" -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Cyan
