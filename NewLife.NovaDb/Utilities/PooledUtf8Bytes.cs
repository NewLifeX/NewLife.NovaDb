using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace NewLife.NovaDb.Utilities
{
    /// <summary>
    /// 使用对象池管理 UTF-8 编码的字节数组，避免频繁分配和垃圾回收。
    /// </summary>
    internal struct PooledUtf8Bytes : IDisposable
    {
        private static readonly Encoding Encoding = Encoding.UTF8;
#if NET45
        private static readonly byte[] EmptyBytes = new byte[0];
#else
        private static readonly byte[] EmptyBytes = Array.Empty<byte>();
#endif

        /// <summary>
        /// UTF-8 编码的字节数组长度，表示有效数据的长度。<br/>
        /// 数组长度可能大于此值，因为它是从 <see cref="ArrayPool{Byte}.Shared"/> 对象池租用的。
        /// </summary>
        public int Length { get; private set; }

        /// <summary>
        /// 获取 UTF-8 编码的字节数组，有效数据的长度由 <see cref="Length"/> 属性决定。<br/>
        /// 使用完毕后应调用 <see cref="Dispose"/> 方法归还数组到对象池。
        /// </summary>
        public byte[] Buffer { get; private set; }

        public PooledUtf8Bytes()
        {
            Length = 0;
            Buffer = EmptyBytes;
        }

        internal PooledUtf8Bytes(byte[] pooledBytes, int length)
        {
            Length = length;
            Buffer = pooledBytes;
        }

#if NETSTANDARD2_1_OR_GREATER
        public PooledUtf8Bytes(ReadOnlySpan<char> value)
        {
            if (value.IsEmpty)
            {
                Length = 0;
                Buffer = EmptyBytes;
            }
            else
            {
                 Length = Encoding.GetByteCount(value); // GetByteCount函数在.NET Standard 2.0版本中不支持 ReadOnlySpan<char> 参数
                 Buffer = ArrayPool<byte>.Shared.Rent(Length);
                 Encoding.GetBytes(value, Buffer);
            }
        }
#endif

        public PooledUtf8Bytes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                Length = 0;
                Buffer = EmptyBytes;
            }
            else
            {
                Length = Encoding.GetByteCount(value);
                Buffer = ArrayPool<byte>.Shared.Rent(Length);
                Encoding.GetBytes(value, 0, value.Length, Buffer, 0);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> AsSpan() => Length == 0 ? ReadOnlySpan<byte>.Empty : Buffer.AsSpan(0, Length);

        public void Dispose()
        {
            if (Buffer == null || Buffer.Length == 0) return;
            ArrayPool<byte>.Shared.Return(Buffer);
            Buffer = EmptyBytes;
            Length = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator ReadOnlySpan<byte>(PooledUtf8Bytes pooledBytes) => pooledBytes.AsSpan();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PooledUtf8Bytes(string value) => new PooledUtf8Bytes(value);
    }

    /// <summary>
    /// 提供字符串与 UTF-8 编码字节数组之间的转换扩展方法，使用对象池管理字节数组以提高性能。
    /// </summary>
    internal static class EncodingExtensions
    {
        /// <summary>
        /// 将字符串转换为使用对象池管理的 UTF-8 编码字节数组。
        /// </summary>
        /// <param name="value">要转换的字符串。</param>
        /// <returns>返回一个 <see cref="PooledUtf8Bytes"/> 实例，包含 UTF-8 编码的字节数组。</returns>
        public static PooledUtf8Bytes ToPooledUtf8Bytes(this string value) => new PooledUtf8Bytes(value);

        /// <summary>
        /// 将字符串转换为使用对象池管理的指定编码的字节数组。
        /// </summary>
        /// <param name="encoding">要使用的编码。</param>
        /// <param name="value">要转换的字符串。</param>
        /// <returns>返回一个 <see cref="PooledUtf8Bytes"/> 实例，包含指定编码的字节数组。</returns>
        public static PooledUtf8Bytes GetPooledEncodedBytes(this Encoding encoding, string value)
        {
            var length = encoding.GetByteCount(value);
            var pooledBytes = ArrayPool<byte>.Shared.Rent(length);
            encoding.GetBytes(value, 0, value.Length, pooledBytes, 0);
            return new PooledUtf8Bytes(pooledBytes, length);
        }
    }
}
