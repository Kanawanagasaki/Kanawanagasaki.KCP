namespace Kanawanagasaki.KCP;

using System.Runtime.InteropServices;

public static unsafe class KcpMemoryPool
{
    private const int NumBuckets = 8;
    private const nuint BaseSize = 128;

    private static readonly nuint HeaderSize = 2 * (nuint)sizeof(nuint);

    public static int PooledBlockCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < NumBuckets; i++)
            {
                lock (_locks[i])
                    total += _freeListCounts[i];
            }
            return total;
        }
    }

    private static long _totalPooledBytes;
    public static long PooledBytes
        => Interlocked.Read(ref _totalPooledBytes);

    private static long _inUseCount;
    public static long InUseCount
        => Interlocked.Read(ref _inUseCount);

    private static long _totalPoolHits;
    public static long PoolHits
        => Interlocked.Read(ref _totalPoolHits);

    private static readonly void** _freeLists;
    private static readonly int[] _freeListCounts;
    private static readonly int[] _maxPerBucket;
    private static readonly Lock[] _locks;

    static KcpMemoryPool()
    {
        _freeLists = (void**)NativeMemory.AllocZeroed((nuint)(NumBuckets * sizeof(void*)));
        _freeListCounts = new int[NumBuckets];
        _maxPerBucket = new int[NumBuckets];
        _locks = new Lock[NumBuckets];

        for (int i = 0; i < NumBuckets; i++)
        {
            _maxPerBucket[i] = 1024;
            _locks[i] = new Lock();
        }
    }

    private static nuint BucketSize(int index)
    {
        return BaseSize << index;
    }

    private static int GetBucketIndex(nuint size)
    {
        if (size == 0 || BucketSize(NumBuckets - 1) < size)
            return -1;

        for (int i = 0; i < NumBuckets; i++)
        {
            if (size <= BucketSize(i))
                return i;
        }
        return -1;
    }

    private static void* PooledMalloc(nuint size)
    {
        nuint totalSize = size + HeaderSize;
        int bucket = GetBucketIndex(totalSize);

        if (0 <= bucket)
        {
            lock (_locks[bucket])
            {
                if (_freeLists[bucket] != null)
                {
                    void* block = _freeLists[bucket];
                    _freeLists[bucket] = *(void**)block;
                    _freeListCounts[bucket]--;
                    Interlocked.Increment(ref _totalPoolHits);
                    Interlocked.Increment(ref _inUseCount);

                    var header = (nuint*)block;
                    header[0] = BucketSize(bucket);
                    header[1] = (nuint)bucket;

                    return (byte*)block + HeaderSize;
                }
            }
        }

        nuint allocSize = 0 <= bucket ? BucketSize(bucket) : totalSize;
        void* mem = NativeMemory.Alloc(allocSize);
        if (mem == null)
            return null;

        Interlocked.Increment(ref _inUseCount);

        var hdr = (nuint*)mem;
        hdr[0] = allocSize;
        hdr[1] = (nuint)(bucket >= 0 ? bucket : 0xFFFF);

        return (byte*)mem + HeaderSize;
    }

    private static void PooledFree(void* ptr)
    {
        if (ptr == null)
            return;

        void* block = (byte*)ptr - HeaderSize;
        var header = (nuint*)block;
        nuint blockSize = header[0];
        nuint bucketIndex = header[1];

        if (bucketIndex != 0xFFFF && bucketIndex < NumBuckets)
        {
            int bi = (int)bucketIndex;
            lock (_locks[bi])
            {
                if (_freeListCounts[bi] < _maxPerBucket[bi])
                {
                    *(void**)block = _freeLists[bi];
                    _freeLists[bi] = block;
                    _freeListCounts[bi]++;
                    Interlocked.Add(ref _totalPooledBytes, (long)blockSize);
                    Interlocked.Decrement(ref _inUseCount);
                    return;
                }
            }
        }

        NativeMemory.Free(block);
        Interlocked.Decrement(ref _inUseCount);
    }

    public static void Install()
    {
        KCP.ikcp_allocator(&PooledMalloc, &PooledFree);
    }

    public static void Uninstall()
    {
        KCP.ikcp_allocator(null, null);
        Drain();
    }

    public static void Drain()
    {
        for (int i = 0; i < NumBuckets; i++)
        {
            lock (_locks[i])
            {
                void* current = _freeLists[i];
                while (current != null)
                {
                    void* next = *(void**)current;
                    NativeMemory.Free(current);
                    current = next;
                }
                _freeLists[i] = null;
                _freeListCounts[i] = 0;
            }
        }
        _totalPooledBytes = 0;
    }
}
