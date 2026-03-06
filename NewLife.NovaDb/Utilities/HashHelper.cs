using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace NewLife.NovaDb.Utilities;

/// <summary>
/// 哈希计算辅助类，提供 MD5、SHA1、SHA256、SHA384、SHA512 等常用哈希算法的计算方法
/// </summary>
internal static class HashHelper
{
    private const String Hex = "0123456789abcdef";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Md5ToHex(String str) => ComputeHash(str, HashAlgorithmName.MD5, 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Sha1ToHex(String str) => ComputeHash(str, HashAlgorithmName.SHA1, 20);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Sha256ToHex(String str) => ComputeHash(str, HashAlgorithmName.SHA256, 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Sha384ToHex(String str) => ComputeHash(str, HashAlgorithmName.SHA384, 48);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Sha512ToHex(String str) => ComputeHash(str, HashAlgorithmName.SHA512, 64);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Md5ToBase64(String str) => ComputeHash(str, HashAlgorithmName.MD5, 16, false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Sha1ToBase64(String str) => ComputeHash(str, HashAlgorithmName.SHA1, 20, false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Sha256ToBase64(String str) => ComputeHash(str, HashAlgorithmName.SHA256, 32, false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Sha384ToBase64(String str) => ComputeHash(str, HashAlgorithmName.SHA384, 48, false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static String Sha512ToBase64(String str) => ComputeHash(str, HashAlgorithmName.SHA512, 64, false);

    private static String ComputeHash(String str, HashAlgorithmName alg, Int32 hashSize, Boolean hex = true)
    {
        using var bytes = str.ToPooledUtf8Bytes();
#if NET5_0_OR_GREATER
        Span<Byte> hash = stackalloc Byte[hashSize];

        if (!TryHashData(alg, bytes.AsSpan(), hash))
            throw new CryptographicException();

        return hex ? ToLowerHex(hash) : Convert.ToBase64String(hash);
#elif NETSTANDARD2_1_OR_GREATER
            using var algo = CreateAlgorithm(alg);

            Span<Byte> hash = stackalloc Byte[hashSize];

            if (!algo.TryComputeHash(bytes.AsSpan(), hash, out _))
                throw new CryptographicException();

            return hex ? ToLowerHex(hash) : Convert.ToBase64String(hash);
#else
            using var algo = CreateAlgorithm(alg);
            var hash = algo.ComputeHash(bytes.Buffer, 0, bytes.Length);
            return hex ? ToLowerHex(hash) : Convert.ToBase64String(hash);
#endif
    }

#if NET5_0_OR_GREATER
    private static Boolean TryHashData(HashAlgorithmName alg, ReadOnlySpan<Byte> data, Span<Byte> dest)
    {
        if (alg == HashAlgorithmName.MD5)
            return MD5.TryHashData(data, dest, out _);

        if (alg == HashAlgorithmName.SHA1)
            return SHA1.TryHashData(data, dest, out _);

        if (alg == HashAlgorithmName.SHA256)
            return SHA256.TryHashData(data, dest, out _);

        if (alg == HashAlgorithmName.SHA384)
            return SHA384.TryHashData(data, dest, out _);

        if (alg == HashAlgorithmName.SHA512)
            return SHA512.TryHashData(data, dest, out _);

        throw new NotSupportedException();
    }
#endif

    private static HashAlgorithm CreateAlgorithm(HashAlgorithmName alg)
    {
        if (alg == HashAlgorithmName.MD5) return MD5.Create();
        if (alg == HashAlgorithmName.SHA1) return SHA1.Create();
        if (alg == HashAlgorithmName.SHA256) return SHA256.Create();
        if (alg == HashAlgorithmName.SHA384) return SHA384.Create();
        if (alg == HashAlgorithmName.SHA512) return SHA512.Create();

        throw new NotSupportedException();
    }

    private static String ToLowerHex(ReadOnlySpan<Byte> bytes)
    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Char> chars = stackalloc Char[bytes.Length * 2];
#else
            var chars = new Char[bytes.Length * 2];
#endif
        var j = 0;

        foreach (var b in bytes)
        {
            chars[j++] = Hex[b >> 4];
            chars[j++] = Hex[b & 0xF];
        }

        return new String(chars);
    }

    private enum HashAlgorithmName
    {
        // ReSharper disable InconsistentNaming
        MD5,
        SHA1,
        SHA256,
        SHA384,
        SHA512
        // ReSharper restore InconsistentNaming
    }
}