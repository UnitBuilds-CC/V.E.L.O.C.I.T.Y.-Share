use ring::aead::{Aad, LessSafeKey, Nonce, UnboundKey, CHACHA20_POLY1305};
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
        let data_ptr_mut = data_ptr;

        let unbound_key = match UnboundKey::new(&CHACHA20_POLY1305, key_slice) {
            Ok(k) => k,
            Err(_) => return -2,
        };
        let less_safe_key = LessSafeKey::new(unbound_key);

        let nonce = match Nonce::try_assume_unique_for_key(nonce_slice) {
            Ok(n) => n,
            Err(_) => return -3,
        };

        if tag_ptr == data_ptr_mut.add(data_len) {
            let in_out = slice::from_raw_parts_mut(data_ptr_mut, data_len + 16);
            match less_safe_key.open_in_place(nonce, Aad::empty(), in_out) {
                Ok(_) => 0,
                Err(_) => -4,
            }
        } else {
            let tag_slice = slice::from_raw_parts(tag_ptr, 16);
            let data_slice = slice::from_raw_parts_mut(data_ptr_mut, data_len);
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
}
