// Minimal SHA-256 (public-domain style) for test verification of recovered data.
#ifndef RX_TEST_SHA256_H
#define RX_TEST_SHA256_H
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
class Sha256 {
public:
    Sha256() { reset(); }
    void reset() {
        static const uint32_t i[8] = {0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19};
        std::memcpy(h_, i, sizeof h_); len_ = 0; n_ = 0;
    }
    void update(const void* d, size_t n) {
        const uint8_t* p = static_cast<const uint8_t*>(d); len_ += n;
        while (n--) { buf_[n_++] = *p++; if (n_ == 64) { block(); n_ = 0; } }
    }
    std::string hex() {
        uint64_t bits = len_ * 8; uint8_t pad = 0x80; update(&pad, 1); pad = 0;
        while (n_ != 56) update(&pad, 1);
        uint8_t l[8]; for (int i = 0; i < 8; ++i) l[i] = (uint8_t)(bits >> (56 - 8 * i));
        update(l, 8);
        char out[65]; for (int i = 0; i < 8; ++i) std::snprintf(out + 8 * i, 9, "%08x", h_[i]);
        return std::string(out);
    }
private:
    static uint32_t rr(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }
    void block() {
        static const uint32_t k[64] = {
          0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,
          0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
          0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,
          0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
          0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,
          0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2};
        uint32_t w[64];
        for (int i = 0; i < 16; ++i) w[i] = ((uint32_t)buf_[4*i] << 24) | ((uint32_t)buf_[4*i+1] << 16) | ((uint32_t)buf_[4*i+2] << 8) | buf_[4*i+3];
        for (int i = 16; i < 64; ++i) { uint32_t s0 = rr(w[i-15],7)^rr(w[i-15],18)^(w[i-15]>>3), s1 = rr(w[i-2],17)^rr(w[i-2],19)^(w[i-2]>>10); w[i] = w[i-16]+s0+w[i-7]+s1; }
        uint32_t a=h_[0],b=h_[1],c=h_[2],d=h_[3],e=h_[4],f=h_[5],g=h_[6],hh=h_[7];
        for (int i = 0; i < 64; ++i) {
            uint32_t S1 = rr(e,6)^rr(e,11)^rr(e,25), ch = (e&f)^(~e&g), t1 = hh+S1+ch+k[i]+w[i];
            uint32_t S0 = rr(a,2)^rr(a,13)^rr(a,22), mj = (a&b)^(a&c)^(b&c), t2 = S0+mj;
            hh=g; g=f; f=e; e=d+t1; d=c; c=b; b=a; a=t1+t2;
        }
        h_[0]+=a;h_[1]+=b;h_[2]+=c;h_[3]+=d;h_[4]+=e;h_[5]+=f;h_[6]+=g;h_[7]+=hh;
    }
    uint32_t h_[8]; uint8_t buf_[64]; size_t n_; uint64_t len_;
};
#endif
