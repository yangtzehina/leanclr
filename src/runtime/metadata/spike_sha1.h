// SPIKE: minimal SHA-1 for public key tokens (token = last 8 bytes of SHA-1(public key), reversed), as CoreCLR does.
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace leanclr
{
namespace metadata
{
inline void spike_sha1(const uint8_t* data, size_t len, uint8_t out[20])
{
    uint32_t h[5] = {0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u};
    auto rol = [](uint32_t v, int n) { return (v << n) | (v >> (32 - n)); };
    uint64_t bitlen = static_cast<uint64_t>(len) * 8;
    size_t total = ((len + 8) / 64 + 1) * 64;
    uint8_t block[64];
    for (size_t off = 0; off < total; off += 64)
    {
        for (size_t i = 0; i < 64; ++i)
        {
            size_t k = off + i;
            uint8_t b;
            if (k < len)
                b = data[k];
            else if (k == len)
                b = 0x80;
            else if (k >= total - 8)
                b = static_cast<uint8_t>(bitlen >> (8 * (total - 1 - k)));
            else
                b = 0;
            block[i] = b;
        }
        uint32_t w[80];
        for (int i = 0; i < 16; ++i)
            w[i] = (uint32_t(block[i * 4]) << 24) | (uint32_t(block[i * 4 + 1]) << 16) | (uint32_t(block[i * 4 + 2]) << 8) | block[i * 4 + 3];
        for (int i = 16; i < 80; ++i)
            w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int i = 0; i < 80; ++i)
        {
            uint32_t f, k;
            if (i < 20) { f = (b & c) | (~b & d); k = 0x5A827999u; }
            else if (i < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1u; }
            else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDCu; }
            else { f = b ^ c ^ d; k = 0xCA62C1D6u; }
            uint32_t t = rol(a, 5) + f + e + k + w[i];
            e = d; d = c; c = rol(b, 30); b = a; a = t;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e;
    }
    for (int i = 0; i < 5; ++i)
        for (int j = 0; j < 4; ++j)
            out[i * 4 + j] = static_cast<uint8_t>(h[i] >> (24 - 8 * j));
}

// blob points at the #Blob heap entry (compressed length prefix first)
inline bool spike_public_key_token_from_blob(const uint8_t* blob, uint8_t token[8])
{
    if (blob == nullptr)
        return false;
    size_t len, hdr;
    if ((blob[0] & 0x80) == 0) { len = blob[0]; hdr = 1; }
    else if ((blob[0] & 0xC0) == 0x80) { len = (size_t(blob[0] & 0x3F) << 8) | blob[1]; hdr = 2; }
    else { len = (size_t(blob[0] & 0x1F) << 24) | (size_t(blob[1]) << 16) | (size_t(blob[2]) << 8) | blob[3]; hdr = 4; }
    if (len == 0)
        return false;
    uint8_t digest[20];
    spike_sha1(blob + hdr, len, digest);
    for (int i = 0; i < 8; ++i)
        token[i] = digest[19 - i];
    return true;
}
} // namespace metadata
} // namespace leanclr
