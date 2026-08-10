use ring::aead::{Aad, LessSafeKey, Nonce, UnboundKey, CHACHA20_POLY1305};
use sha2::{Digest, Sha256};
use std::slice;

/// Hash a data chunk using SHA-256.
///
/// # Safety
/// - `data_ptr` must point to a valid buffer of at least `data_len` bytes.
/// - `hash_out_ptr` must point to a writable buffer of at least 32 bytes.
/// - Both pointers must be non-null and properly aligned.
#[no_mangle]
pub unsafe extern "C" fn sha256_hash_chunk(
    data_ptr: *const u8,
    data_len: usize,
    hash_out_ptr: *mut u8,
) -> i32 {
    if data_ptr.is_null() || hash_out_ptr.is_null() || data_len == 0 {
        return -1;
    }

    let data = slice::from_raw_parts(data_ptr, data_len);
    let mut hasher = Sha256::new();
    hasher.update(data);
    let result = hasher.finalize();

    let out_slice = slice::from_raw_parts_mut(hash_out_ptr, 32);
    out_slice.copy_from_slice(&result);

    0
}

/// Hash multiple chunks in a single FFI call, avoiding per-call overhead.
///
/// Layout: `chunks_ptr` points to `chunk_count` length-prefixed chunks:
///   [4-byte LE length][data bytes] [4-byte LE length][data bytes] ...
/// `hashes_out_ptr` must have space for `chunk_count * 32` bytes.
///
/// # Safety
/// All pointers must be valid and properly aligned. Buffer must contain
/// exactly `chunk_count` length-prefixed chunks totaling `total_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn bulk_hash_chunks(
    chunks_ptr: *const u8,
    total_len: usize,
    chunk_count: u32,
    hashes_out_ptr: *mut u8,
) -> i32 {
    if chunks_ptr.is_null() || hashes_out_ptr.is_null() || chunk_count == 0 || total_len == 0 {
        return -1;
    }

    let input = slice::from_raw_parts(chunks_ptr, total_len);
    let out = slice::from_raw_parts_mut(hashes_out_ptr, (chunk_count as usize) * 32);

    let mut offset = 0;
    for i in 0..chunk_count as usize {
        if offset + 4 > total_len {
            return -2; // Malformed input
        }
        let len = u32::from_le_bytes([
            input[offset],
            input[offset + 1],
            input[offset + 2],
            input[offset + 3],
        ]) as usize;
        offset += 4;

        if offset + len > total_len {
            return -2;
        }

        let mut hasher = Sha256::new();
        hasher.update(&input[offset..offset + len]);
        let result = hasher.finalize();
        out[i * 32..(i + 1) * 32].copy_from_slice(&result);

        offset += len;
    }

    0
}

/// Verify chunk integrity: hash data and compare against expected hash.
/// Returns 0 if match, 1 if mismatch, negative on error.
/// Zero intermediate allocation for the hash comparison.
///
/// # Safety
/// All pointers must be valid and properly aligned.
#[no_mangle]
pub unsafe extern "C" fn verify_chunk_integrity(
    data_ptr: *const u8,
    data_len: usize,
    expected_hash_ptr: *const u8,
) -> i32 {
    if data_ptr.is_null() || expected_hash_ptr.is_null() || data_len == 0 {
        return -1;
    }

    let data = slice::from_raw_parts(data_ptr, data_len);
    let expected = slice::from_raw_parts(expected_hash_ptr, 32);

    let mut hasher = Sha256::new();
    hasher.update(data);
    let result = hasher.finalize();

    // Constant-time comparison to prevent timing attacks
    let actual = result.as_slice();
    let mut diff = 0u8;
    for i in 0..32 {
        diff |= actual[i] ^ expected[i];
    }

    if diff == 0 { 0 } else { 1 }
}

/// PBKDF2-HMAC-SHA256 key derivation for share link passwords.
/// Writes `out_len` bytes to `derived_key_ptr`.
///
/// # Safety
/// All pointers must be valid. `derived_key_ptr` must have `out_len` bytes writable.
#[no_mangle]
pub unsafe extern "C" fn pbkdf2_derive(
    password_ptr: *const u8,
    password_len: usize,
    salt_ptr: *const u8,
    salt_len: usize,
    iterations: u32,
    derived_key_ptr: *mut u8,
    out_len: usize,
) -> i32 {
    if password_ptr.is_null() || salt_ptr.is_null() || derived_key_ptr.is_null() {
        return -1;
    }
    if out_len == 0 || out_len > 256 {
        return -2;
    }

    let password = slice::from_raw_parts(password_ptr, password_len);
    let salt = slice::from_raw_parts(salt_ptr, salt_len);
    let out = slice::from_raw_parts_mut(derived_key_ptr, out_len);

    pbkdf2::pbkdf2_hmac::<sha2::Sha256>(password, salt, iterations, out);

    0
}

/// Encrypt a block in-place using ChaCha20-Poly1305, writing the tag separately.
///
/// # Safety
/// - `key_ptr` must point to a valid 32-byte key.
/// - `nonce_ptr` must point to a valid 12-byte nonce (unique per message).
/// - `data_ptr` must point to a writable buffer of at least `data_len` bytes.
/// - `tag_out_ptr` must point to a writable buffer of at least 16 bytes.
/// - All pointers must be non-null and properly aligned.
#[no_mangle]
pub unsafe extern "C" fn encrypt_block_chacha(
    key_ptr: *const u8,
    nonce_ptr: *const u8,
    data_ptr: *mut u8,
    data_len: usize,
    tag_out_ptr: *mut u8,
) -> i32 {
    if key_ptr.is_null() || nonce_ptr.is_null() || data_ptr.is_null() || tag_out_ptr.is_null() || data_len == 0 {
        return -1;
    }

    let key_slice = slice::from_raw_parts(key_ptr, 32);
    let nonce_slice = slice::from_raw_parts(nonce_ptr, 12);
    let data_slice = slice::from_raw_parts_mut(data_ptr, data_len);
    let tag_out = slice::from_raw_parts_mut(tag_out_ptr, 16);

    let unbound_key = match UnboundKey::new(&CHACHA20_POLY1305, key_slice) {
        Ok(k) => k,
        Err(_) => return -2,
    };
    let less_safe_key = LessSafeKey::new(unbound_key);

    let nonce = match Nonce::try_assume_unique_for_key(nonce_slice) {
        Ok(n) => n,
        Err(_) => return -3,
    };

    let tag = match less_safe_key.seal_in_place_separate_tag(nonce, Aad::empty(), data_slice) {
        Ok(t) => t,
        Err(_) => return -4,
    };

    tag_out.copy_from_slice(tag.as_ref());

    0
}

/// Decrypt a block in-place using ChaCha20-Poly1305.
///
/// # Safety
/// - `key_ptr` must point to a valid 32-byte key.
/// - `nonce_ptr` must point to a valid 12-byte nonce.
/// - `data_ptr` must point to a writable buffer of at least `data_len` bytes.
///   The decrypted plaintext is written back to the same buffer.
/// - `tag_ptr` must point to a readable buffer of at least 16 bytes.
/// - All pointers must be non-null and properly aligned.
#[no_mangle]
pub unsafe extern "C" fn decrypt_block_chacha(
    key_ptr: *const u8,
    nonce_ptr: *const u8,
    data_ptr: *mut u8,
    data_len: usize,
    tag_ptr: *const u8,
) -> i32 {
    if key_ptr.is_null() || nonce_ptr.is_null() || data_ptr.is_null() || tag_ptr.is_null() || data_len == 0 {
        return -1;
    }

    let key_slice = slice::from_raw_parts(key_ptr, 32);
    let nonce_slice = slice::from_raw_parts(nonce_ptr, 12);

    let unbound_key = match UnboundKey::new(&CHACHA20_POLY1305, key_slice) {
        Ok(k) => k,
        Err(_) => return -2,
    };
    let less_safe_key = LessSafeKey::new(unbound_key);

    let nonce = match Nonce::try_assume_unique_for_key(nonce_slice) {
        Ok(n) => n,
        Err(_) => return -3,
    };

    // If tag is immediately after data, decrypt in-place on the combined buffer
    if tag_ptr == data_ptr.add(data_len) {
        let in_out = slice::from_raw_parts_mut(data_ptr, data_len + 16);
        match less_safe_key.open_in_place(nonce, Aad::empty(), in_out) {
            Ok(_) => 0,
            Err(_) => -4,
        }
    } else {
        // Tag is separate: copy data+tag into a temp buffer for decryption
        let tag_slice = slice::from_raw_parts(tag_ptr, 16);
        let data_slice = slice::from_raw_parts_mut(data_ptr, data_len);
        let mut temp = vec![0u8; data_len + 16];
        temp[..data_len].copy_from_slice(data_slice);
        temp[data_len..].copy_from_slice(tag_slice);
        
        match less_safe_key.open_in_place(nonce, Aad::empty(), &mut temp) {
            Ok(plaintext) => {
                data_slice.copy_from_slice(&plaintext[..data_len]);
                0
            }
            Err(_) => -4,
        }
    }
}
