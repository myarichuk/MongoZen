using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MongoDB.Bson.IO;

namespace MongoZen.Bson;

/// <summary>
/// Byte buffer backed by an array pool -> satisfy MongoDB driver's IByteBuffer contract
/// </summary>
/// <remarks>
/// this may cause managed "memory leak" if we allocate (rent) them faster than GC can reclaim
/// </remarks>
internal sealed unsafe class PooledByteBuffer : IByteBuffer
{
    private static readonly ConcurrentStack<PooledByteBuffer> Pool = new();

    private byte[]? _rentedArray;
    private int _length;
    private bool _isReadOnly;

    public static PooledByteBuffer Rent(byte* ptr, int length)
    {
        if (!Pool.TryPop(out var buffer))
        {
            buffer = new PooledByteBuffer();
        }
        
        buffer._rentedArray = ArrayPool<byte>.Shared.Rent(length);
        buffer._length = length;
        buffer._isReadOnly = true;

        fixed (byte* pDest = buffer._rentedArray)
        {
            Unsafe.CopyBlock(pDest, ptr, (uint)length);
        }

        GC.ReRegisterForFinalize(buffer);
        return buffer;
    }

    ~PooledByteBuffer() => DisposeInternal();

    public int Capacity => _rentedArray?.Length ?? 0;
    public bool IsReadOnly => _isReadOnly;
    public int Length 
    { 
        get => _length; 
        set => throw new NotSupportedException("ArenaByteBuffer is fixed length"); 
    }

    public ArraySegment<byte> AccessBackingBytes(int position) => 
        _rentedArray == null ? 
            default : new ArraySegment<byte>(_rentedArray, position, _length - position);

    public void Clear(int position, int count) => throw new NotSupportedException();

    public void EnsureCapacity(int minimumCapacity)
    {
        if (minimumCapacity > _length)
        {
            throw new NotSupportedException();
        }
    }

    public byte GetByte(int position) => _rentedArray![position];

    public void GetBytes(int position, byte[] destination, int offset, int count) => 
        Buffer.BlockCopy(_rentedArray!, position, destination, offset, count);

    public void GetBytes(int position, Span<byte> destination) => 
        _rentedArray.AsSpan(position, destination.Length).CopyTo(destination);

    public IByteBuffer GetSlice(int position, int length)
    {
        // don't support true slicing as it complicates pooling.
        // why not rent another one?
        fixed (byte* p = &_rentedArray![position])
        {
            return Rent(p, length);
        }
    }

    public void MakeReadOnly() => _isReadOnly = true;

    public void SetByte(int position, byte value) => throw new NotSupportedException();

    public void SetBytes(int position, byte[] source, int offset, int count) => throw new NotSupportedException();

    public void Dispose()
    {
        DisposeInternal();
        GC.SuppressFinalize(this);
        Pool.Push(this);
    }

    private void DisposeInternal()
    {
        if (_rentedArray != null)
        {
            ArrayPool<byte>.Shared.Return(_rentedArray);
            _rentedArray = null;
        }
        _length = 0;
    }
}
