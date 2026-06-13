use chacha20poly1305::aead::{AeadInPlace, KeyInit};
use chacha20poly1305::{ChaCha20Poly1305, Nonce, Tag};
use sha2::{Digest, Sha256};
use std::slice;

#[no_mangle]
pub extern "C" fn sha256_hash_chunk(
    data_ptr: *const u8,
    data_len: usize,
    hash_out_ptr: *mut u8,
) -> i32 {
    if data_ptr.is_null() || hash_out_ptr.is_null() || data_len == 0 {
        return -1;
    }

    unsafe {
        let data = slice::from_raw_parts(data_ptr, data_len);
        let mut hasher = Sha256::new();
        hasher.update(data);
        let result = hasher.finalize();

        let out_slice = slice::from_raw_parts_mut(hash_out_ptr, 32);
        out_slice.copy_from_slice(&result);
    }

    0
}

#[no_mangle]
pub extern "C" fn encrypt_block_chacha(
    key_ptr: *const u8,
    nonce_ptr: *const u8,
    data_ptr: *mut u8,
    data_len: usize,
    tag_out_ptr: *mut u8,
) -> i32 {
    if key_ptr.is_null() || nonce_ptr.is_null() || data_ptr.is_null() || tag_out_ptr.is_null() || data_len == 0 {
        return -1;
    }

    unsafe {
        let key_slice = slice::from_raw_parts(key_ptr, 32);
        let nonce_slice = slice::from_raw_parts(nonce_ptr, 12);
        let data_slice = slice::from_raw_parts_mut(data_ptr, data_len);
        let tag_out = slice::from_raw_parts_mut(tag_out_ptr, 16);

        let cipher = match ChaCha20Poly1305::new_from_slice(key_slice) {
            Ok(c) => c,
            Err(_) => return -2,
        };

        let nonce = Nonce::from_slice(nonce_slice);

        let tag = match cipher.encrypt_in_place_detached(nonce, &[], data_slice) {
            Ok(t) => t,
            Err(_) => return -3,
        };

        tag_out.copy_from_slice(&tag);
    }

    0
}

#[no_mangle]
pub extern "C" fn decrypt_block_chacha(
    key_ptr: *const u8,
    nonce_ptr: *const u8,
    data_ptr: *mut u8,
    data_len: usize,
    tag_ptr: *const u8,
) -> i32 {
    if key_ptr.is_null() || nonce_ptr.is_null() || data_ptr.is_null() || tag_ptr.is_null() || data_len == 0 {
        return -1;
    }

    unsafe {
        let key_slice = slice::from_raw_parts(key_ptr, 32);
        let nonce_slice = slice::from_raw_parts(nonce_ptr, 12);
        let data_slice = slice::from_raw_parts_mut(data_ptr, data_len);
        let tag_slice = slice::from_raw_parts(tag_ptr, 16);

        let cipher = match ChaCha20Poly1305::new_from_slice(key_slice) {
            Ok(c) => c,
            Err(_) => return -2,
        };

        let nonce = Nonce::from_slice(nonce_slice);
        let tag = Tag::from_slice(tag_slice);

        match cipher.decrypt_in_place_detached(nonce, &[], data_slice, tag) {
            Ok(_) => 0,
            Err(_) => -3,
        }
    }
}
