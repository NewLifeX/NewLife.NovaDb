using System.Buffers;
using System.Runtime.CompilerServices;

namespace NewLife.NovaDb.Utilities
{
    /// <summary>
    /// 使用对象池管理字节数组，避免频繁分配和垃圾回收。
    /// </summary>
    internal struct PooledBytes : IDisposable
    {
#if NET45
        private static readonly Byte[] EmptyBytes = new Byte[0];
#else
        private static readonly Byte[] EmptyBytes = Array.Empty<Byte>();
#endif

        public static readonly PooledBytes Empty = new();

        /// <summary>
        /// 字节数组的有效数据长度。<br/>
        /// 字节数组的长度可能大于此值，因为它是从 <see cref="ArrayPool{Byte}.Shared"/> 对象池租用的。
        /// </summary>
        public Int32 Length { get; private set; }

        /// <summary>
        /// 获取字节数组。<br/>
        /// 注意：有效数据的长度由 <see cref="Length"/> 属性决定。<br/>
        /// 使用完毕后应调用 <see cref="Dispose"/> 方法归还数组到对象池。
        /// </summary>
        public Byte[] Buffer { get; private set; }

        public PooledBytes()
        {
            Length = 0;
            Buffer = EmptyBytes;
        }

        internal PooledBytes(Byte[] pooledBytes, Int32 length)
        {
            Length = length;
            Buffer = pooledBytes;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<Byte> AsSpan() => Length == 0 ? ReadOnlySpan<Byte>.Empty : Buffer.AsSpan(0, Length);

        public void Dispose()
        {
            if (Buffer == null || Buffer.Length == 0) return;
            ArrayPool<Byte>.Shared.Return(Buffer);
            Buffer = EmptyBytes;
            Length = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator ReadOnlySpan<Byte>(PooledBytes pooled) => pooled.AsSpan();
    }
}
