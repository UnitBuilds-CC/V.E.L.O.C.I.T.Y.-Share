use criterion::{black_box, criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};
use rand::RngCore;
use velocity_share_ffi::*;

/// Helper: call sha256_hash_chunk via the FFI boundary
unsafe fn hash_via_ffi(data: &[u8]) -> [u8; 32] {
    let mut hash = [0u8; 32];
    let rc = sha256_hash_chunk(data.as_ptr(), data.len(), hash.as_mut_ptr());
    assert_eq!(rc, 0);
    hash
}

/// Helper: encrypt via FFI
unsafe fn encrypt_via_ffi(data: &mut [u8], key: &[u8; 32], nonce: &[u8; 12]) -> [u8; 16] {
    let mut tag = [0u8; 16];
    let rc = encrypt_block_chacha(
        key.as_ptr(),
        nonce.as_ptr(),
        data.as_mut_ptr(),
        data.len(),
        tag.as_mut_ptr(),
    );
    assert_eq!(rc, 0);
    tag
}

/// Helper: decrypt via FFI
unsafe fn decrypt_via_ffi(data: &mut [u8], key: &[u8; 32], nonce: &[u8; 12], tag: &[u8; 16]) {
    let rc = decrypt_block_chacha(
        key.as_ptr(),
        nonce.as_ptr(),
        data.as_mut_ptr(),
        data.len(),
        tag.as_ptr(),
    );
    assert_eq!(rc, 0);
}

fn bench_sha256(c: &mut Criterion) {
    let mut group = c.benchmark_group("SHA-256 (Rust FFI)");

    for size in [64, 1024, 4096, 65536, 1_048_576] {
        let data = {
            let mut buf = vec![0u8; size];
            rand::thread_rng().fill_bytes(&mut buf);
            buf
        };

        group.throughput(Throughput::Bytes(size as u64));
        group.bench_with_input(BenchmarkId::from_parameter(format!("{}B", size)), &data, |b, data| {
            b.iter(|| unsafe { hash_via_ffi(black_box(data)) });
        });
    }
    group.finish();
}

fn bench_bulk_hash(c: &mut Criterion) {
    let mut group = c.benchmark_group("Bulk Hash (single FFI call)");
    let chunk_size = 65536u32; // 64KB chunks
    let chunk_counts = [1, 4, 16, 64];

    for &count in &chunk_counts {
        // Build length-prefixed chunk buffer
        let mut buf = Vec::new();
        let mut chunks_data = Vec::new();
        for _ in 0..count {
            let mut chunk = vec![0u8; chunk_size as usize];
            rand::thread_rng().fill_bytes(&mut chunk);
            chunks_data.push(chunk);
        }
        for chunk in &chunks_data {
            buf.extend_from_slice(&(chunk.len() as u32).to_le_bytes());
            buf.extend_from_slice(chunk);
        }

        let total_bytes = count as u64 * chunk_size as u64;
        group.throughput(Throughput::Bytes(total_bytes));
        group.bench_with_input(
            BenchmarkId::from_parameter(format!("{}x{}KB", count, chunk_size / 1024)),
            &buf,
            |b, buf| {
                let mut hashes = vec![0u8; count as usize * 32];
                b.iter(|| unsafe {
                    let rc = bulk_hash_chunks(
                        black_box(buf.as_ptr()),
                        buf.len(),
                        count,
                        hashes.as_mut_ptr(),
                    );
                    assert_eq!(rc, 0);
                });
            },
        );
    }
    group.finish();
}

fn bench_verify_integrity(c: &mut Criterion) {
    let mut group = c.benchmark_group("Verify Chunk Integrity");

    for size in [1024, 65536, 1_048_576] {
        let data = {
            let mut buf = vec![0u8; size];
            rand::thread_rng().fill_bytes(&mut buf);
            buf
        };
        let hash = unsafe { hash_via_ffi(&data) };

        group.throughput(Throughput::Bytes(size as u64));
        group.bench_function(BenchmarkId::from_parameter(format!("{}B", size)), |b| {
            b.iter(|| unsafe {
                verify_chunk_integrity(black_box(data.as_ptr()), data.len(), hash.as_ptr())
            });
        });
    }
    group.finish();
}

fn bench_chacha20_encrypt(c: &mut Criterion) {
    let mut group = c.benchmark_group("ChaCha20-Poly1305 Encrypt");

    for size in [1024, 65536, 1_048_576] {
        let mut data = vec![0u8; size];
        rand::thread_rng().fill_bytes(&mut data);
        let mut key = [0u8; 32];
        rand::thread_rng().fill_bytes(&mut key);
        let mut nonce = [0u8; 12];
        rand::thread_rng().fill_bytes(&mut nonce);

        group.throughput(Throughput::Bytes(size as u64));
        group.bench_function(BenchmarkId::from_parameter(format!("{}B", size)), |b| {
            b.iter(|| {
                let mut working = data.clone();
                unsafe { encrypt_via_ffi(&mut working, &key, &nonce) }
            });
        });
    }
    group.finish();
}

fn bench_chacha20_decrypt(c: &mut Criterion) {
    let mut group = c.benchmark_group("ChaCha20-Poly1305 Decrypt");

    for size in [1024, 65536, 1_048_576] {
        let plaintext = {
            let mut buf = vec![0u8; size];
            rand::thread_rng().fill_bytes(&mut buf);
            buf
        };
        let mut key = [0u8; 32];
        rand::thread_rng().fill_bytes(&mut key);
        let mut nonce = [0u8; 12];
        rand::thread_rng().fill_bytes(&mut nonce);

        // Pre-encrypt to get ciphertext + tag
        let mut ciphertext = plaintext.clone();
        let tag = unsafe { encrypt_via_ffi(&mut ciphertext, &key, &nonce) };

        group.throughput(Throughput::Bytes(size as u64));
        group.bench_function(BenchmarkId::from_parameter(format!("{}B", size)), |b| {
            b.iter(|| {
                let mut working = ciphertext.clone();
                unsafe { decrypt_via_ffi(&mut working, &key, &nonce, &tag) }
            });
        });
    }
    group.finish();
}

fn bench_pbkdf2(c: &mut Criterion) {
    let mut group = c.benchmark_group("PBKDF2-HMAC-SHA256");
    let password = b"share-link-password-12345";
    let salt = b"velocity-share-salt";

    for iterations in [10_000, 100_000, 600_000] {
        group.bench_with_input(
            BenchmarkId::from_parameter(format!("{}it", iterations)),
            &iterations,
            |b, &iters| {
                let mut derived = [0u8; 32];
                b.iter(|| unsafe {
                    let rc = pbkdf2_derive(
                        black_box(password.as_ptr()),
                        password.len(),
                        salt.as_ptr(),
                        salt.len(),
                        iters,
                        derived.as_mut_ptr(),
                        32,
                    );
                    assert_eq!(rc, 0);
                });
            },
        );
    }
    group.finish();
}

fn bench_roundtrip(c: &mut Criterion) {
    let mut group = c.benchmark_group("Encrypt + Decrypt Roundtrip");

    for size in [1024, 65536] {
        let plaintext = {
            let mut buf = vec![0u8; size];
            rand::thread_rng().fill_bytes(&mut buf);
            buf
        };
        let mut key = [0u8; 32];
        rand::thread_rng().fill_bytes(&mut key);
        let mut nonce = [0u8; 12];
        rand::thread_rng().fill_bytes(&mut nonce);

        group.throughput(Throughput::Bytes(size as u64));
        group.bench_function(BenchmarkId::from_parameter(format!("{}B", size)), |b| {
            b.iter(|| unsafe {
                let mut working = plaintext.clone();
                let tag = encrypt_via_ffi(&mut working, &key, &nonce);
                decrypt_via_ffi(&mut working, &key, &nonce, &tag);
                assert_eq!(&working, &plaintext);
            });
        });
    }
    group.finish();
}

criterion_group!(
    benches,
    bench_sha256,
    bench_bulk_hash,
    bench_verify_integrity,
    bench_chacha20_encrypt,
    bench_chacha20_decrypt,
    bench_pbkdf2,
    bench_roundtrip,
);
criterion_main!(benches);
