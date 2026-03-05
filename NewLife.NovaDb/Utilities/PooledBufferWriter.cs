using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace NewLife.NovaDb.Utilities
{
    /// <summary>
    /// 池化的字节数组写入器，提供高效的写入方法。<br/>
    /// 使用对象池管理字节数组，避免频繁分配和垃圾回收。<br/>
    /// 使用完毕后应调用 <see cref="Dispose"/> 方法归还数组到 <see cref="ArrayPool{Byte}.Shared"/> 对象池。
    /// </summary>
    internal struct PooledBufferWriter : IDisposable
    {
#if NET45
        private static readonly Byte[] EmptyBytes = new Byte[0];
#else
        private static readonly Byte[] EmptyBytes = Array.Empty<Byte>();
#endif

        private Byte[] _buffer;
        private Int32 _pos;

        public PooledBufferWriter(Int32 initialCapacity)
        {
            _buffer = ArrayPool<Byte>.Shared.Rent(initialCapacity);
            _pos = 0;
        }

        public readonly Int32 WrittenCount => _pos;
        public readonly Byte[] Buffer => _buffer;

        public void Dispose()
        {
            var buf = _buffer;
            _buffer = EmptyBytes;
            _pos = 0;
            if (buf.Length != 0)
                ArrayPool<Byte>.Shared.Return(buf);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Ensure(Int32 sizeHint)
        {
            if ((UInt32)(_pos + sizeHint) <= (UInt32)_buffer.Length) return;

            var newSize = _buffer.Length * 2;
            var needed = _pos + sizeHint;
            if (newSize < needed) newSize = needed;

            var newBuf = ArrayPool<Byte>.Shared.Rent(newSize);
            System.Buffer.BlockCopy(_buffer, 0, newBuf, 0, _pos);
            ArrayPool<Byte>.Shared.Return(_buffer);
            _buffer = newBuf;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(Byte value)
        {
            Ensure(1);
            _buffer[_pos++] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt32(Int32 value)
        {
            Ensure(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_pos, 4), value);
            _pos += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt32(UInt32 value)
        {
            Ensure(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_pos, 4), value);
            _pos += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt64(Int64 value)
        {
            Ensure(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_pos, 8), value);
            _pos += 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt64(UInt64 value)
        {
            Ensure(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_pos, 8), value);
            _pos += 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteDouble(Double value)
        {
            // 兼容低于 .NET 6：用 bits + WriteInt64
#if NET6_0_OR_GREATER
            Ensure(8);
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_pos, 8), value);
            _pos += 8;
#else
            WriteInt64(BitConverter.DoubleToInt64Bits(value));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBool(Boolean value) => WriteByte(value ? (Byte)1 : (Byte)0);

        public void WriteBytes(ReadOnlySpan<Byte> src)
        {
            Ensure(src.Length);
            src.CopyTo(_buffer.AsSpan(_pos, src.Length));
            _pos += src.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBytes(Byte[] src) => WriteBytes(src.AsSpan());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBytes(Byte[] src, Int32 offset, Int32 count) => WriteBytes(src.AsSpan(offset, count));

        /// <summary>
        /// 获取一个可写的字节数组切片，长度由参数指定。
        /// </summary>
        /// <param name="length">要获取的字节数组切片的长度</param>
        /// <returns>返回一个可写的字节数组切片</returns>
        public Span<Byte> GetWritableSpan(Int32 length)
        {
            Ensure(length);
            var span = _buffer.AsSpan(_pos, length);
            _pos += length;
            return span;
        }

        /// <summary>
        /// 获取一个可写的字节数组切片，长度由参数指定，但返回值类型为 <see cref="ArraySegment{Byte}"/>，以兼容某些需要 ArraySegment 的 API。
        /// </summary>
        /// <param name="length">要获取的字节数组切片的长度</param>
        /// <returns>返回一个可写的字节数组切片</returns>
        public ArraySegment<Byte> GetWritableSegment(Int32 length)
        {
            Ensure(length);
            var segment = new ArraySegment<Byte>(_buffer, _pos, length);
            _pos += length;
            return segment;
        }
    }
}
