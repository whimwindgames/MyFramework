using System;
using System.Buffers.Binary;

// SHA-256 with the same digest as System.Security.Cryptography.SHA256 but without the
// managed BCL implementation, which measures about 2 MB/s under IL2CPP on low-end
// Android (a 28 MB bundle then costs ~12 s to verify). Block compression here runs on
// plain uint arithmetic with a reusable schedule, so verification keeps the exact
// published sha256 semantics at a fraction of the cost.
internal sealed class UpdSha256
{
    static readonly uint[] K =
    {
        0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u,
        0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
        0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u,
        0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
        0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu,
        0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
        0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u,
        0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
        0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u,
        0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
        0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u,
        0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
        0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u,
        0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
        0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u,
        0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u,
    };

    readonly uint[] mState = new uint[8];
    readonly byte[] mBlock = new byte[64];
    readonly uint[] mSchedule = new uint[64];
    int mFilled;
    ulong mBits;

    public UpdSha256()
    {
        Reset();
    }

    public void Reset()
    {
        mState[0] = 0x6a09e667u; mState[1] = 0xbb67ae85u;
        mState[2] = 0x3c6ef372u; mState[3] = 0xa54ff53au;
        mState[4] = 0x510e527fu; mState[5] = 0x9b05688cu;
        mState[6] = 0x1f83d9abu; mState[7] = 0x5be0cd19u;
        mFilled = 0;
        mBits = 0;
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        mBits += (ulong)data.Length * 8u;
        if (mFilled > 0)
        {
            int need = 64 - mFilled;
            if (data.Length < need)
            {
                data.CopyTo(mBlock.AsSpan(mFilled));
                mFilled += data.Length;
                return;
            }
            data.Slice(0, need).CopyTo(mBlock.AsSpan(mFilled));
            Compress(mBlock, 0);
            mFilled = 0;
            data = data.Slice(need);
        }
        while (data.Length >= 64)
        {
            Compress(data, 0);
            data = data.Slice(64);
        }
        if (data.Length > 0)
        {
            data.CopyTo(mBlock);
            mFilled = data.Length;
        }
    }

    // Writes the 32 byte digest; the instance stays reusable afterwards.
    public void Finish(Span<byte> digest)
    {
        if (digest.Length < 32) throw new ArgumentException(nameof(digest));
        ulong bits = mBits;
        int filled = mFilled;
        mBlock[filled++] = 0x80;
        if (filled > 56)
        {
            while (filled < 64) mBlock[filled++] = 0;
            Compress(mBlock, 0);
            filled = 0;
        }
        while (filled < 56) mBlock[filled++] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(mBlock.AsSpan(56), bits);
        Compress(mBlock, 0);
        for (int i = 0; i < 8; ++i)
            BinaryPrimitives.WriteUInt32BigEndian(digest.Slice(i * 4), mState[i]);
        Reset();
    }

    void Compress(ReadOnlySpan<byte> block, int offset)
    {
        uint[] w = mSchedule;
        for (int i = 0; i < 16; ++i)
            w[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(offset + i * 4));
        for (int i = 16; i < 64; ++i)
        {
            uint x = w[i - 15];
            uint y = w[i - 2];
            uint s0 = Rotr(x, 7) ^ Rotr(x, 18) ^ (x >> 3);
            uint s1 = Rotr(y, 17) ^ Rotr(y, 19) ^ (y >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint a = mState[0], b = mState[1], c = mState[2], d = mState[3];
        uint e = mState[4], f = mState[5], g = mState[6], h = mState[7];
        for (int i = 0; i < 64; ++i)
        {
            uint s1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
            uint ch = (e & f) ^ (~e & g);
            uint t1 = h + s1 + ch + K[i] + w[i];
            uint s0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
            uint maj = (a & b) ^ (a & c) ^ (b & c);
            uint t2 = s0 + maj;
            h = g; g = f; f = e; e = d + t1;
            d = c; c = b; b = a; a = t1 + t2;
        }
        mState[0] += a; mState[1] += b; mState[2] += c; mState[3] += d;
        mState[4] += e; mState[5] += f; mState[6] += g; mState[7] += h;
    }

    static uint Rotr(uint value, int count) => (value >> count) | (value << (32 - count));
}
