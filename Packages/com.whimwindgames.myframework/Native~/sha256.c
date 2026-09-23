// Minimal native SHA-256 for the hot-update verification benchmark.
// Same digest as the managed implementations, but executed in C at native speed.
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static const uint32_t K[64] = {
    0x428a2f98u,0x71374491u,0xb5c0fbcfu,0xe9b5dba5u,0x3956c25bu,0x59f111f1u,0x923f82a4u,0xab1c5ed5u,
    0xd807aa98u,0x12835b01u,0x243185beu,0x550c7dc3u,0x72be5d74u,0x80deb1feu,0x9bdc06a7u,0xc19bf174u,
    0xe49b69c1u,0xefbe4786u,0x0fc19dc6u,0x240ca1ccu,0x2de92c6fu,0x4a7484aau,0x5cb0a9dcu,0x76f988dau,
    0x983e5152u,0xa831c66du,0xb00327c8u,0xbf597fc7u,0xc6e00bf3u,0xd5a79147u,0x06ca6351u,0x14292967u,
    0x27b70a85u,0x2e1b2138u,0x4d2c6dfcu,0x53380d13u,0x650a7354u,0x766a0abbu,0x81c2c92eu,0x92722c85u,
    0xa2bfe8a1u,0xa81a664bu,0xc24b8b70u,0xc76c51a3u,0xd192e819u,0xd6990624u,0xf40e3585u,0x106aa070u,
    0x19a4c116u,0x1e376c08u,0x2748774cu,0x34b0bcb5u,0x391c0cb3u,0x4ed8aa4au,0x5b9cca4fu,0x682e6ff3u,
    0x748f82eeu,0x78a5636fu,0x84c87814u,0x8cc70208u,0x90befffau,0xa4506cebu,0xbef9a3f7u,0xc67178f2u
};

typedef struct { uint32_t h[8]; uint64_t len; uint8_t buf[64]; size_t n; } ctx;

static inline uint32_t rotr(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }

static void compress(ctx *c, const uint8_t *p)
{
    uint32_t w[64];
    int i;
    for (i = 0; i < 16; ++i)
        w[i] = ((uint32_t)p[i*4] << 24) | ((uint32_t)p[i*4+1] << 16) |
               ((uint32_t)p[i*4+2] << 8) | (uint32_t)p[i*4+3];
    for (i = 16; i < 64; ++i) {
        uint32_t x = w[i-15], y = w[i-2];
        uint32_t s0 = rotr(x,7) ^ rotr(x,18) ^ (x >> 3);
        uint32_t s1 = rotr(y,17) ^ rotr(y,19) ^ (y >> 10);
        w[i] = w[i-16] + s0 + w[i-7] + s1;
    }
    uint32_t a=c->h[0],b=c->h[1],cc=c->h[2],d=c->h[3],e=c->h[4],f=c->h[5],g=c->h[6],h=c->h[7];
    for (i = 0; i < 64; ++i) {
        uint32_t S1 = rotr(e,6) ^ rotr(e,11) ^ rotr(e,25);
        uint32_t ch = (e & f) ^ (~e & g);
        uint32_t t1 = h + S1 + ch + K[i] + w[i];
        uint32_t S0 = rotr(a,2) ^ rotr(a,13) ^ rotr(a,22);
        uint32_t maj = (a & b) ^ (a & cc) ^ (b & cc);
        uint32_t t2 = S0 + maj;
        h=g; g=f; f=e; e=d+t1; d=cc; cc=b; b=a; a=t1+t2;
    }
    c->h[0]+=a; c->h[1]+=b; c->h[2]+=cc; c->h[3]+=d;
    c->h[4]+=e; c->h[5]+=f; c->h[6]+=g; c->h[7]+=h;
}

static void update(ctx *c, const uint8_t *p, size_t len)
{
    c->len += (uint64_t)len * 8u;
    if (c->n) {
        size_t need = 64 - c->n;
        if (len < need) { memcpy(c->buf + c->n, p, len); c->n += len; return; }
        memcpy(c->buf + c->n, p, need);
        compress(c, c->buf);
        p += need; len -= need; c->n = 0;
    }
    while (len >= 64) { compress(c, p); p += 64; len -= 64; }
    if (len) { memcpy(c->buf, p, len); c->n = len; }
}

static void final(ctx *c, uint8_t out[32])
{
    uint64_t bits = c->len;
    size_t n = c->n;
    c->buf[n++] = 0x80;
    if (n > 56) { memset(c->buf + n, 0, 64 - n); compress(c, c->buf); n = 0; }
    memset(c->buf + n, 0, 56 - n);
    for (int i = 0; i < 8; ++i) c->buf[56 + i] = (uint8_t)(bits >> (56 - i * 8));
    compress(c, c->buf);
    for (int i = 0; i < 8; ++i) {
        out[i*4]   = (uint8_t)(c->h[i] >> 24);
        out[i*4+1] = (uint8_t)(c->h[i] >> 16);
        out[i*4+2] = (uint8_t)(c->h[i] >> 8);
        out[i*4+3] = (uint8_t)(c->h[i]);
    }
}

// Hashes a file straight from native code; returns 0 on success.
int arcade_sha256_file(const char *path, unsigned char out[32])
{
    if (!path || !out) return -3;
    FILE *fp = fopen(path, "rb");
    if (!fp) return -1;
    // Each call owns its input buffer. Verification runs on several worker threads.
    const size_t capacity = 256 * 1024;
    uint8_t *buffer = (uint8_t *)malloc(capacity);
    if (!buffer) { fclose(fp); return -3; }
    ctx c = {{0x6a09e667u,0xbb67ae85u,0x3c6ef372u,0xa54ff53au,
              0x510e527fu,0x9b05688cu,0x1f83d9abu,0x5be0cd19u}, 0, {0}, 0};
    size_t got;
    while ((got = fread(buffer, 1, capacity, fp)) > 0) update(&c, buffer, got);
    int bad = ferror(fp);
    fclose(fp);
    free(buffer);
    if (bad) return -2;
    final(&c, out);
    return 0;
}
