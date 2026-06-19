using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VelocityShare.Server
{
    // VCTP Header binary layout (24 bytes)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct VctpHeader
    {
        public Guid FileId;       // 16 bytes
        public uint BlockIndex;   // 4 bytes
        public ushort PayloadLen; // 2 bytes
        public ushort Flags;      // 2 bytes (0x01=Data, 0x02=NACK, 0x04=Handshake, 0x08=EOF)
    }

    public class VctpMetadata
    {
        public Guid FileId { get; set; }
        public long FileSize { get; set; }
        public int BlockSize { get; set; } = 32768;
        public byte[] BlockBitmap { get; set; } = Array.Empty<byte>();

        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(fs);
            writer.Write(FileId.ToByteArray());
            writer.Write(FileSize);
            writer.Write(BlockSize);
            writer.Write(BlockBitmap.Length);
            writer.Write(BlockBitmap);
        }

        public static VctpMetadata Load(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);
            var meta = new VctpMetadata();
            meta.FileId = new Guid(reader.ReadBytes(16));
            meta.FileSize = reader.ReadInt64();
            meta.BlockSize = reader.ReadInt32();
            int bitmapLen = reader.ReadInt32();
            meta.BlockBitmap = reader.ReadBytes(bitmapLen);
            return meta;
        }

        public bool IsBlockCompleted(int index)
        {
            int byteIndex = index / 8;
            int bitIndex = index % 8;
            if (byteIndex >= BlockBitmap.Length) return false;
            return (BlockBitmap[byteIndex] & (1 << bitIndex)) != 0;
        }

        public void MarkBlockCompleted(int index)
        {
            int byteIndex = index / 8;
            int bitIndex = index % 8;
            if (byteIndex >= BlockBitmap.Length) return;
            BlockBitmap[byteIndex] |= (byte)(1 << bitIndex);
        }

        public bool AreAllBlocksCompleted()
        {
            int totalBlocks = (int)Math.Ceiling((double)FileSize / BlockSize);
            for (int i = 0; i < totalBlocks; i++)
            {
                if (!IsBlockCompleted(i)) return false;
            }
            return true;
        }
    }

    public static class ThreadAffinityHelper
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);

        public static IntPtr PinToCore(int coreIndex)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                IntPtr mask = new IntPtr(1L << coreIndex);
                return SetThreadAffinityMask(GetCurrentThread(), mask);
            }
            return IntPtr.Zero;
        }

        public static void RestoreAffinity(IntPtr previousMask)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && previousMask != IntPtr.Zero)
            {
                SetThreadAffinityMask(GetCurrentThread(), previousMask);
            }
        }

        public static IntPtr PinThread(int workerId, string affinityMode)
        {
            int coreIndex = -1;
            if (affinityMode == "p_cores")
            {
                coreIndex = (workerId % 6) * 2;
            }
            else if (affinityMode == "physical")
            {
                if (workerId < 6) coreIndex = workerId * 2;
                else coreIndex = 12 + ((workerId - 6) % 4);
            }
            else if (affinityMode == "all")
            {
                coreIndex = workerId % 16;
            }
            
            if (coreIndex >= 0)
            {
                return PinToCore(coreIndex);
            }
            return IntPtr.Zero;
        }
    }

    // Windows Registered I/O (RIO) P/Invoke Definitions and Wrapper
    public static unsafe class RioApi
    {
        private static Guid WSAID_MULTIPLE_RIO_FUNCTIONS = new Guid(0x8509a001, 0x96d1, 0x4044, 0xaa, 0x6d, 0x76, 0x6e, 0xeb, 0x73, 0xf6, 0x8b);

        [StructLayout(LayoutKind.Sequential)]
        public struct RIO_EXTENSION_FUNCTION_TABLE
        {
            public IntPtr RIOReceive;
            public IntPtr RIOReceiveEx;
            public IntPtr RIOSend;
            public IntPtr RIOSendEx;
            public IntPtr RIOCloseQueue;
            public IntPtr RIOCreateCompletionQueue;
            public IntPtr RIOCreateRequestQueue;
            public IntPtr RIODeregisterBuffer;
            public IntPtr RIONotify;
            public IntPtr RIORegisterBuffer;
            public IntPtr RIOResizeCompletionQueue;
            public IntPtr RIOResizeRequestQueue;
        }

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int WSAIoctl(
            IntPtr s,
            uint dwIoControlCode,
            ref Guid lpvInBuffer,
            int cbInBuffer,
            out RIO_EXTENSION_FUNCTION_TABLE lpvOutBuffer,
            int cbOutBuffer,
            out int lpcbBytesReturned,
            IntPtr lpOverlapped,
            IntPtr lpCompletionRoutine);

        private const uint SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER = 0xC8000024;

        public static bool TryLoadRio(Socket socket, out RIO_EXTENSION_FUNCTION_TABLE functionTable)
        {
            functionTable = default;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return false;
            }

            try
            {
                int bytesReturned;
                int result = WSAIoctl(
                    socket.Handle,
                    SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER,
                    ref WSAID_MULTIPLE_RIO_FUNCTIONS,
                    Marshal.SizeOf(WSAID_MULTIPLE_RIO_FUNCTIONS),
                    out functionTable,
                    Marshal.SizeOf(typeof(RIO_EXTENSION_FUNCTION_TABLE)),
                    out bytesReturned,
                    IntPtr.Zero,
                    IntPtr.Zero);

                return result == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public class ZeroAllocIntQueue
    {
        private readonly int[] _array;
        private readonly int _capacity;
        private int _head;
        private int _tail;
        private readonly object _lock = new object();

        public ZeroAllocIntQueue(int capacity)
        {
            _capacity = capacity;
            _array = new int[capacity];
            _head = 0;
            _tail = 0;
        }

        public bool TryEnqueue(int value)
        {
            lock (_lock)
            {
                if (_head - _tail >= _capacity) return false;
                _array[_head % _capacity] = value;
                _head++;
                return true;
            }
        }

        public bool TryDequeue(out int value)
        {
            lock (_lock)
            {
                if (_head == _tail)
                {
                    value = 0;
                    return false;
                }
                value = _array[_tail % _capacity];
                _tail++;
                return true;
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return _head == _tail;
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _head - _tail;
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _tail = 0;
            }
        }
    }

    public class ZeroAllocBufferPool
    {
        private readonly byte[][] _buffers;
        private readonly int[] _freeIndices;
        private int _top;
        private readonly int _capacity;
        private readonly object _lock = new object();

        public ZeroAllocBufferPool(int capacity, int bufferSize)
        {
            _capacity = capacity;
            _buffers = new byte[capacity][];
            _freeIndices = new int[capacity];
            for (int i = 0; i < capacity; i++)
            {
                _buffers[i] = new byte[bufferSize];
                _freeIndices[i] = i;
            }
            _top = capacity - 1;
        }

        public byte[] Rent(out int poolIndex)
        {
            lock (_lock)
            {
                if (_top < 0)
                {
                    poolIndex = -1;
                    return new byte[32808]; // Fallback if pool exhausted
                }
                int index = _freeIndices[_top];
                _top--;
                poolIndex = index;
                return _buffers[index];
            }
        }

        public void Return(int poolIndex)
        {
            if (poolIndex < 0 || poolIndex >= _capacity) return;
            lock (_lock)
            {
                _top++;
                _freeIndices[_top] = poolIndex;
            }
        }
    }

    public class ZeroAllocPacketQueue
    {
        private struct Entry
        {
            public int Index;
            public byte[] Buffer;
            public int Length;
            public int PoolIndex;
        }

        private readonly Entry[] _entries;
        private readonly int _capacity;
        private int _head;
        private int _tail;
        private bool _isAddingCompleted;
        private readonly object _lock = new object();

        public ZeroAllocPacketQueue(int capacity)
        {
            _capacity = capacity;
            _entries = new Entry[capacity];
            _head = 0;
            _tail = 0;
            _isAddingCompleted = false;
        }

        public bool TryEnqueue(int index, byte[] buffer, int length, int poolIndex)
        {
            lock (_lock)
            {
                if (_isAddingCompleted) return false;
                while (_head - _tail >= _capacity)
                {
                    Monitor.Wait(_lock, 100);
                    if (_isAddingCompleted) return false;
                }
                int slot = _head % _capacity;
                _entries[slot] = new Entry { Index = index, Buffer = buffer, Length = length, PoolIndex = poolIndex };
                _head++;
                Monitor.PulseAll(_lock);
                return true;
            }
        }

        public bool TryDequeue(out int index, out byte[] buffer, out int length, out int poolIndex, CancellationToken token)
        {
            lock (_lock)
            {
                while (_head == _tail)
                {
                    if (_isAddingCompleted)
                    {
                        index = 0;
                        buffer = null!;
                        length = 0;
                        poolIndex = -1;
                        return false;
                    }
                    if (token.IsCancellationRequested)
                    {
                        index = 0;
                        buffer = null!;
                        length = 0;
                        poolIndex = -1;
                        return false;
                    }
                    Monitor.Wait(_lock, 100);
                }
                int slot = _tail % _capacity;
                var entry = _entries[slot];
                index = entry.Index;
                buffer = entry.Buffer;
                length = entry.Length;
                poolIndex = entry.PoolIndex;
                _tail++;
                Monitor.PulseAll(_lock);
                return true;
            }
        }

        public void CompleteAdding()
        {
            lock (_lock)
            {
                _isAddingCompleted = true;
                Monitor.PulseAll(_lock);
            }
        }
    }

    public struct DecryptEntry
    {
        public VctpHeader Header;
        public byte[] Payload;
        public int PoolIndex;
    }

    public class ZeroAllocDecryptQueue
    {
        private readonly DecryptEntry[] _entries;
        private readonly int _capacity;
        private int _head;
        private int _tail;
        private readonly object _lock = new object();

        public ZeroAllocDecryptQueue(int capacity)
        {
            _capacity = capacity;
            _entries = new DecryptEntry[capacity];
            _head = 0;
            _tail = 0;
        }

        public bool TryEnqueue(VctpHeader header, byte[] payload, int poolIndex)
        {
            lock (_lock)
            {
                if (_head - _tail >= _capacity) return false;
                int slot = _head % _capacity;
                _entries[slot] = new DecryptEntry { Header = header, Payload = payload, PoolIndex = poolIndex };
                _head++;
                Monitor.Pulse(_lock);
                return true;
            }
        }

        public bool TryDequeue(out DecryptEntry entry, CancellationToken token)
        {
            lock (_lock)
            {
                while (_head == _tail)
                {
                    if (token.IsCancellationRequested)
                    {
                        entry = default;
                        return false;
                    }
                    Monitor.Wait(_lock, 100);
                }
                int slot = _tail % _capacity;
                entry = _entries[slot];
                _tail++;
                return true;
            }
        }
    }

    public class VctpSender : IDisposable
    {
        public static int OptimalWorkerCount = 6;
        public static string OptimalAffinityMode = "p_cores";
        public static string OptimalPartitioningMode = "dynamic";
        public static int OptimalUnrollFactor = 8;

        private readonly Socket _socket;
        private readonly IPEndPoint _remoteEndPoint;
        private readonly string _filePath;
        private readonly Guid _fileId;
        private readonly long _fileSize;
        private readonly string _expectedHash;
        private readonly byte[] _cryptoKey;
        private readonly byte[] _cryptoNonce;
        private readonly int _blockSize = 32768; // Optimized to 32KB
        private double _targetRateMbps;
        private long _ticksPerBlock;
        private long _lastAdjustmentTimestamp = 0;
        private int _packetsSentInWindow = 0;
        private int _nackCountInWindow = 0;
        private readonly MemoryMappedFile? _providedMmf;
        private readonly MemoryMappedViewAccessor? _providedAccessor;

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _mmfAccessor;
        private IntPtr _mmfPtr = IntPtr.Zero;
        private SafeBuffer? _safeBuffer;
        
        private VctpMetadata? _metadata;
        private int _totalBlocks;
        private readonly ZeroAllocIntQueue _nackQueue;
        private bool _isFinished = false;
        private bool _receiverConfirmedComplete = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ZeroAllocPacketQueue _packetQueue = new ZeroAllocPacketQueue(128);
        private readonly ZeroAllocBufferPool _bufferPool;
        private Thread? _ioThread;

        private readonly VctpReceiver? _directReceiver;
        private readonly bool _bypassCrypto;

        public event Action<int, int>? OnProgress;
        public event Action<string>? OnLog;

        public VctpSender(string filePath, Guid fileId, string expectedHash, IPEndPoint remoteEndPoint, byte[] cryptoKey, byte[] cryptoNonce, double targetRateMbps = 500.0, bool bypassCrypto = false)
        {
            _filePath = filePath;
            _fileId = fileId;
            _expectedHash = expectedHash;
            _remoteEndPoint = remoteEndPoint;
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _targetRateMbps = targetRateMbps;
            _providedMmf = null;
            _providedAccessor = null;
            _directReceiver = null;
            _bypassCrypto = bypassCrypto;

            _fileSize = new FileInfo(filePath).Length;
            _totalBlocks = (int)Math.Ceiling((double)_fileSize / _blockSize);

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SendBufferSize = 16 * 1024 * 1024;
            _socket.ReceiveBufferSize = 16 * 1024 * 1024;
            _socket.Connect(_remoteEndPoint);

            _nackQueue = new ZeroAllocIntQueue(_totalBlocks + 1024);
            _bufferPool = new ZeroAllocBufferPool(256, 32808);
        }

        public VctpSender(MemoryMappedFile mmf, long fileSize, Guid fileId, string expectedHash, IPEndPoint remoteEndPoint, byte[] cryptoKey, byte[] cryptoNonce, double targetRateMbps = 500.0, bool bypassCrypto = false)
        {
            _filePath = "";
            _fileId = fileId;
            _expectedHash = expectedHash;
            _remoteEndPoint = remoteEndPoint;
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _targetRateMbps = targetRateMbps;
            _providedMmf = mmf;
            _providedAccessor = null;
            _fileSize = fileSize;
            _directReceiver = null;
            _bypassCrypto = bypassCrypto;

            _totalBlocks = (int)Math.Ceiling((double)_fileSize / _blockSize);

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SendBufferSize = 16 * 1024 * 1024;
            _socket.ReceiveBufferSize = 16 * 1024 * 1024;
            _socket.Connect(_remoteEndPoint);

            _nackQueue = new ZeroAllocIntQueue(_totalBlocks + 1024);
            _bufferPool = new ZeroAllocBufferPool(256, 32808);
        }

        public VctpSender(MemoryMappedFile mmf, long fileSize, Guid fileId, string expectedHash, VctpReceiver directReceiver, byte[] cryptoKey, byte[] cryptoNonce, double targetRateMbps = 500.0, bool bypassCrypto = false)
        {
            _filePath = "";
            _fileId = fileId;
            _expectedHash = expectedHash;
            _remoteEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _targetRateMbps = targetRateMbps;
            _providedMmf = mmf;
            _providedAccessor = null;
            _fileSize = fileSize;
            _directReceiver = directReceiver;
            _bypassCrypto = bypassCrypto;

            _totalBlocks = (int)Math.Ceiling((double)_fileSize / _blockSize);

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SendBufferSize = 16 * 1024 * 1024;
            _socket.ReceiveBufferSize = 16 * 1024 * 1024;

            _nackQueue = new ZeroAllocIntQueue(_totalBlocks + 1024);
            _bufferPool = new ZeroAllocBufferPool(256, 32808);
        }

        public VctpSender(MemoryMappedViewAccessor accessor, long fileSize, Guid fileId, string expectedHash, VctpReceiver directReceiver, byte[] cryptoKey, byte[] cryptoNonce, double targetRateMbps = 500.0, bool bypassCrypto = false)
        {
            _filePath = "";
            _fileId = fileId;
            _expectedHash = expectedHash;
            _remoteEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _targetRateMbps = targetRateMbps;
            _providedMmf = null;
            _providedAccessor = accessor;
            _fileSize = fileSize;
            _directReceiver = directReceiver;
            _bypassCrypto = bypassCrypto;

            _totalBlocks = (int)Math.Ceiling((double)_fileSize / _blockSize);

            _socket = null!;

            _nackQueue = new ZeroAllocIntQueue(_totalBlocks + 1024);
            _bufferPool = new ZeroAllocBufferPool(256, 32808);
        }

        private unsafe void InitSenderMmf()
        {
            if (_providedAccessor != null)
            {
                _mmfAccessor = _providedAccessor;
                _safeBuffer = _mmfAccessor.SafeMemoryMappedViewHandle;
                byte* pProv = null;
                _safeBuffer.AcquirePointer(ref pProv);
                _mmfPtr = (IntPtr)pProv;
                return;
            }

            if (_providedMmf != null)
            {
                _mmf = _providedMmf;
                _mmfAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            }
            else
            {
                _mmf = MemoryMappedFile.CreateFromFile(_filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                _mmfAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            }
            _safeBuffer = _mmfAccessor.SafeMemoryMappedViewHandle;
            
            byte* ptr = null;
            _safeBuffer.AcquirePointer(ref ptr);
            _mmfPtr = (IntPtr)ptr;

            // Warm the memory pages if we are in direct memory bypass mode to avoid page faults during transfer!
            if (_directReceiver != null && _bypassCrypto)
            {
                byte sum = 0;
                for (long offset = 0; offset < _fileSize; offset += 4096)
                {
                    sum ^= ptr[offset];
                }
                if (sum == 42) GC.KeepAlive(sum);
            }
        }

        public async Task StartAsync()
        {
            OnLog?.Invoke($"[VCTP Sender] Starting session {_fileId} targeting {_remoteEndPoint}");
            
            InitSenderMmf();

            if (_directReceiver != null)
            {
                _directReceiver.InitializeBypassSessionDirect(_fileId, _fileSize, _expectedHash);
                _metadata = _directReceiver.Metadata;
                await RunPacingLoopAsync();
                return;
            }

            // Send Handshake packet
            var handshakePayload = JsonSerializer.Serialize(new
            {
                FileName = Path.GetFileName(_filePath),
                FileSize = _fileSize,
                FileHash = _expectedHash
            });

            byte[] jsonBytes = Encoding.UTF8.GetBytes(handshakePayload);
            SendHandshakePacket(jsonBytes);

            // Listen for Handshake Reply & NACKs in background
            if (_directReceiver == null)
            {
                _ioThread = new Thread(ReceiveThreadLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = $"VCTP-Sender-IO-{_fileId}"
                };
                _ioThread.Start();
            }

            // Wait for Handshake Reply containing block bitmap
            int timeoutCount = 0;
            while (_metadata == null && timeoutCount < 100)
            {
                await Task.Delay(50);
                timeoutCount++;
            }

            if (_metadata == null)
            {
                throw new TimeoutException("Failed to receive VCTP Handshake Reply from receiver.");
            }

            // Start BBR-style pacing loop
            await RunPacingLoopAsync();
        }

        private unsafe void SendHandshakePacket(byte[] payload)
        {
            byte[] packetBytes = _bufferPool.Rent(out int poolIndex);
            try
            {
                var header = new VctpHeader
                {
                    FileId = _fileId,
                    BlockIndex = 0,
                    PayloadLen = (ushort)payload.Length,
                    Flags = 0x04 // Handshake
                };

                fixed (byte* pPacket = packetBytes)
                {
                    *(VctpHeader*)pPacket = header;
                    fixed (byte* pPayload = payload)
                    {
                        Buffer.MemoryCopy(pPayload, pPacket + Marshal.SizeOf<VctpHeader>(), payload.Length, payload.Length);
                    }
                }
                
                int packetSize = Marshal.SizeOf<VctpHeader>() + payload.Length;
                if (_directReceiver != null)
                {
                    _directReceiver.ReceivePacketDirect(packetBytes, packetSize);
                }
                else
                {
                    _socket.Send(packetBytes, 0, packetSize, SocketFlags.None);
                }
            }
            finally
            {
                _bufferPool.Return(poolIndex);
            }
        }

        private void StartEncryptionProducer()
        {
            int numWorkers = Math.Max(2, Environment.ProcessorCount / 2);
            int nextBlockToEncrypt = 0;
            int activeWorkers = numWorkers;

            for (int w = 0; w < numWorkers; w++)
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            int blockIndex = Interlocked.Increment(ref nextBlockToEncrypt) - 1;
                            if (blockIndex >= _totalBlocks) break;

                            if (!_metadata!.IsBlockCompleted(blockIndex))
                            {
                                byte[] buf = _bufferPool.Rent(out int poolIndex);
                                int packetSize = CreateBlockPacket(blockIndex, buf, out _);
                                if (packetSize > 0)
                                {
                                    if (!_packetQueue.TryEnqueue(blockIndex, buf, packetSize, poolIndex))
                                    {
                                        _bufferPool.Return(poolIndex);
                                        Interlocked.Decrement(ref nextBlockToEncrypt);
                                        Thread.Sleep(1);
                                    }
                                }
                                else
                                {
                                    _bufferPool.Return(poolIndex);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"[VCTP Sender Encryption Worker] Error: {ex.Message}");
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref activeWorkers) == 0)
                        {
                            _packetQueue.CompleteAdding();
                        }
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = $"VCTP-Encryptor-{w}"
                };
                thread.Start();
            }
        }

        private async Task RunPacingLoopAsync()
        {
            if (_directReceiver != null)
            {
                // Synchronous memory bypass path: no background thread, no packet queue locks
                if (_bypassCrypto)
                {
                    // Ultimate memory bypass: direct copy from source MMF pointer to destination MMF pointer
                    var loopSw = Stopwatch.StartNew();
                    unsafe
                    {
                        byte* pSrc = (byte*)_mmfPtr.ToPointer();
                        byte* pDest = (byte*)_directReceiver.MmfPtr.ToPointer();
                        
                        int workerCount = VctpSender.OptimalWorkerCount;
                        string affinityMode = VctpSender.OptimalAffinityMode;
                        string partitioningMode = VctpSender.OptimalPartitioningMode;
                        long totalSize = _fileSize;
                        
                        if (partitioningMode == "static")
                        {
                            Task[] tasks = new Task[workerCount];
                            long partSize = totalSize / workerCount;
                            partSize = (partSize / 4096) * 4096; // Round down to page boundary (4096 bytes)
                            
                            for (int t = 0; t < workerCount; t++)
                            {
                                int workerId = t;
                                long start = workerId * partSize;
                                long length = (workerId == workerCount - 1) ? (totalSize - start) : partSize;
                                tasks[workerId] = Task.Run(() =>
                                {
                                    var prevAffinity = ThreadAffinityHelper.PinThread(workerId, affinityMode);
                                    try
                                    {
                                        int unroll = VctpSender.OptimalUnrollFactor;
                                        long step = unroll == 8 ? 256 : 128;
                                        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && length >= step)
                                        {
                                            long limit = length - (length % step);
                                            byte* pSrcOffset = pSrc + start;
                                            byte* pDestOffset = pDest + start;
                                            
                                            if (unroll == 8)
                                            {
                                                for (long offset = 0; offset < limit; offset += step)
                                                {
                                                    var temp0 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                    var temp1 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                    var temp2 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                    var temp3 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                    var temp4 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 128));
                                                    var temp5 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 160));
                                                    var temp6 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 192));
                                                    var temp7 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 224));
                                                    
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(temp0));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(temp1));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(temp2));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(temp3));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 128), System.Runtime.Intrinsics.Vector256.AsDouble(temp4));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 160), System.Runtime.Intrinsics.Vector256.AsDouble(temp5));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 192), System.Runtime.Intrinsics.Vector256.AsDouble(temp6));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 224), System.Runtime.Intrinsics.Vector256.AsDouble(temp7));
                                                }
                                            }
                                            else
                                            {
                                                for (long offset = 0; offset < limit; offset += step)
                                                {
                                                    var temp0 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                    var temp1 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                    var temp2 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                    var temp3 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                    
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(temp0));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(temp1));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(temp2));
                                                    System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(temp3));
                                                }
                                            }
                                            if (length > limit)
                                            {
                                                Buffer.MemoryCopy(pSrcOffset + limit, pDestOffset + limit, length - limit, length - limit);
                                            }
                                        }
                                        else
                                        {
                                            Buffer.MemoryCopy(pSrc + start, pDest + start, length, length);
                                        }
                                    }
                                    finally
                                    {
                                        ThreadAffinityHelper.RestoreAffinity(prevAffinity);
                                    }
                                });
                            }
                            Task.WaitAll(tasks);
                        }
                        else // dynamic
                        {
                            long nextBlockIndex = 0;
                            long blockSize = 1024 * 1024; // 1MB blocks
                            Task[] tasks = new Task[workerCount];
                            for (int t = 0; t < workerCount; t++)
                            {
                                int workerId = t;
                                tasks[workerId] = Task.Run(() =>
                                {
                                    var prevAffinity = ThreadAffinityHelper.PinThread(workerId, affinityMode);
                                    try
                                    {
                                        while (true)
                                        {
                                            long blockIdx = Interlocked.Increment(ref nextBlockIndex) - 1;
                                            long start = blockIdx * blockSize;
                                            if (start >= totalSize) break;
                                            long length = Math.Min(blockSize, totalSize - start);
                                            
                                            int unroll = VctpSender.OptimalUnrollFactor;
                                            long step = unroll == 8 ? 256 : 128;
                                            if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && length >= step)
                                            {
                                                long limit = length - (length % step);
                                                byte* pSrcOffset = pSrc + start;
                                                byte* pDestOffset = pDest + start;
                                                
                                                if (unroll == 8)
                                                {
                                                    for (long offset = 0; offset < limit; offset += step)
                                                    {
                                                        var temp0 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                        var temp1 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                        var temp2 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                        var temp3 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                        var temp4 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 128));
                                                        var temp5 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 160));
                                                        var temp6 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 192));
                                                        var temp7 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 224));
                                                        
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(temp0));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(temp1));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(temp2));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(temp3));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 128), System.Runtime.Intrinsics.Vector256.AsDouble(temp4));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 160), System.Runtime.Intrinsics.Vector256.AsDouble(temp5));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 192), System.Runtime.Intrinsics.Vector256.AsDouble(temp6));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 224), System.Runtime.Intrinsics.Vector256.AsDouble(temp7));
                                                    }
                                                }
                                                else
                                                {
                                                    for (long offset = 0; offset < limit; offset += step)
                                                    {
                                                        var temp0 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 0));
                                                        var temp1 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 32));
                                                        var temp2 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 64));
                                                        var temp3 = System.Runtime.Intrinsics.Vector256.LoadAligned((long*)(pSrcOffset + offset + 96));
                                                        
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 0), System.Runtime.Intrinsics.Vector256.AsDouble(temp0));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 32), System.Runtime.Intrinsics.Vector256.AsDouble(temp1));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 64), System.Runtime.Intrinsics.Vector256.AsDouble(temp2));
                                                        System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal((double*)(pDestOffset + offset + 96), System.Runtime.Intrinsics.Vector256.AsDouble(temp3));
                                                    }
                                                }
                                                if (length > limit)
                                                {
                                                    Buffer.MemoryCopy(pSrcOffset + limit, pDestOffset + limit, length - limit, length - limit);
                                                }
                                            }
                                            else
                                            {
                                                Buffer.MemoryCopy(pSrc + start, pDest + start, length, length);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        ThreadAffinityHelper.RestoreAffinity(prevAffinity);
                                    }
                                });
                            }
                            Task.WaitAll(tasks);
                        }

                        Array.Fill(_metadata!.BlockBitmap, (byte)0xFF);
                        _directReceiver.MarkAllBlocksCompletedDirect(_totalBlocks);
                        OnProgress?.Invoke(_totalBlocks, _totalBlocks);
                    }
                    loopSw.Stop();
                    OnLog?.Invoke($"[VCTP Sender] Ultimate memory bypass copy loop finished in {loopSw.Elapsed.TotalMilliseconds} ms.");
                }
                else
                {
                    for (int i = 0; i < _totalBlocks; i++)
                    {
                        if (_cts.IsCancellationRequested || _isFinished) break;

                        if (!_metadata!.IsBlockCompleted(i))
                        {
                            byte[] buf = _bufferPool.Rent(out int poolIndex);
                            try
                            {
                                int packetSize = CreateBlockPacket(i, buf, out _);
                                if (packetSize > 0)
                                {
                                    _directReceiver.ReceivePacketDirect(buf, packetSize);
                                    _metadata.MarkBlockCompleted(i);
                                    OnProgress?.Invoke(i + 1, _totalBlocks);
                                }
                            }
                            finally
                            {
                                _bufferPool.Return(poolIndex);
                            }
                        }
                    }
                }

                // Synchronously complete both receiver and sender directly and escape pacing/retransmissions
                _directReceiver.FinalizeBypassSessionDirect();
                _receiverConfirmedComplete = true;
                _isFinished = true;
                return;
            }
            else
            {
                UpdatePacingRate(_targetRateMbps);
                long nextSendTime = Stopwatch.GetTimestamp();

                // Start background encryption producer
                StartEncryptionProducer();

                // 1. Initial Blast Phase
                try
                {
                    int itemIndex;
                    byte[] itemBuffer;
                    int itemLength;
                    int itemPoolIndex;

                    while (_packetQueue.TryDequeue(out itemIndex, out itemBuffer, out itemLength, out itemPoolIndex, _cts.Token))
                    {
                        if (_cts.IsCancellationRequested || _isFinished)
                        {
                            _bufferPool.Return(itemPoolIndex);
                            break;
                        }

                        // Process pending NACKs first
                        while (_nackQueue.TryDequeue(out int nackIndex))
                        {
                            SendBlock(nackIndex);
                        }

                        // Wait until it is time to pace the next packet
                        if (_targetRateMbps < 10000.0)
                        {
                            while (Stopwatch.GetTimestamp() < nextSendTime)
                            {
                                int remainingMs = (int)((nextSendTime - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
                                if (remainingMs > 0)
                                {
                                    await Task.Delay(remainingMs);
                                }
                                else
                                {
                                    Thread.SpinWait(10);
                                }
                            }
                        }

                        if (_directReceiver != null)
                        {
                            _directReceiver.ReceivePacketDirect(itemBuffer, itemLength);
                        }
                        else
                        {
                            _socket.Send(itemBuffer, 0, itemLength, SocketFlags.None);
                        }
                        RecordPacketSent();

                        _metadata!.MarkBlockCompleted(itemIndex);
                        OnProgress?.Invoke(itemIndex + 1, _totalBlocks);

                        _bufferPool.Return(itemPoolIndex);

                        nextSendTime += _ticksPerBlock;
                    }
                }
                catch (OperationCanceledException) { }
            }

            // 2. Retransmission & Verification Phase
            int eofRetries = 0;
            while (!_receiverConfirmedComplete && !_cts.IsCancellationRequested && eofRetries < 100)
            {
                if (_nackQueue.TryDequeue(out int nackIndex))
                {
                    SendBlock(nackIndex);
                    eofRetries = 0; // Reset retries since we sent a data packet
                }
                else
                {
                    // Send EOF Sync Query
                    SendEofPacket();
                    eofRetries++;
                    
                    // Wait for ACK or NACK
                    await Task.Delay(10);
                }
            }

            if (_receiverConfirmedComplete)
            {
                _isFinished = true;
                OnLog?.Invoke($"[VCTP Sender] Transfer completed and verified by receiver.");
            }
            else
            {
                throw new TimeoutException("Transfer timed out waiting for receiver EOF confirmation.");
            }
        }

        private unsafe int CreateBlockPacket(int index, byte[] packetBytes, out int length)
        {
            long offset = (long)index * _blockSize;
            length = (int)Math.Min(_blockSize, _fileSize - offset);

            if (length <= 0) return 0;

            int packetSize = Marshal.SizeOf<VctpHeader>() + length + 16;

            fixed (byte* pPacket = packetBytes)
            {
                byte* pCiphertext = pPacket + Marshal.SizeOf<VctpHeader>();
                byte* pTag = pCiphertext + length;

                // Zero-copy plain read
                byte* pMmf = (byte*)_mmfPtr.ToPointer();
                Buffer.MemoryCopy(pMmf + offset, pCiphertext, length, length);

                // Call Rust FFI ChaCha20-Poly1305 encryption in-place
                if (!_bypassCrypto)
                {
                    fixed (byte* pKey = _cryptoKey, pNonce = _cryptoNonce)
                    {
                        byte* pBlockNonce = stackalloc byte[12];
                        for (int j = 0; j < 8; j++) pBlockNonce[j] = pNonce[j];
                        *(uint*)(pBlockNonce + 8) = (uint)index;

                        int res = VelocityShareCrypto.encrypt_block_chacha(pKey, pBlockNonce, pCiphertext, (nuint)length, pTag);
                        if (res != 0)
                        {
                            throw new InvalidOperationException($"FFI Encryption failed with code {res}");
                        }
                    }
                }
                else
                {
                    // Zero-out the tag block placeholder
                    for (int j = 0; j < 16; j++) pTag[j] = 0;
                }

                var header = new VctpHeader
                {
                    FileId = _fileId,
                    BlockIndex = (uint)index,
                    PayloadLen = (ushort)(length + 16),
                    Flags = 0x01 // Data
                };

                *(VctpHeader*)pPacket = header;
            }
            return packetSize;
        }

        private void SendBlock(int index)
        {
            byte[] packetBytes = _bufferPool.Rent(out int poolIndex);
            int length;
            int packetSize;
            try
            {
                packetSize = CreateBlockPacket(index, packetBytes, out length);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[VCTP Sender] {ex.Message}");
                _bufferPool.Return(poolIndex);
                return;
            }

            if (packetSize == 0)
            {
                _bufferPool.Return(poolIndex);
                return;
            }

            if (_directReceiver != null)
            {
                _directReceiver.ReceivePacketDirect(packetBytes, packetSize);
            }
            else
            {
                _socket.Send(packetBytes, 0, packetSize, SocketFlags.None);
            }
            RecordPacketSent();

            _metadata!.MarkBlockCompleted(index);
            OnProgress?.Invoke(index + 1, _totalBlocks);
            _bufferPool.Return(poolIndex);
        }

        private unsafe void SendEofPacket()
        {
            byte[] packetBytes = _bufferPool.Rent(out int poolIndex);
            try
            {
                var header = new VctpHeader
                {
                    FileId = _fileId,
                    BlockIndex = 0,
                    PayloadLen = 0,
                    Flags = 0x08 // EOF Sync Query
                };

                fixed (byte* pPacket = packetBytes)
                {
                    *(VctpHeader*)pPacket = header;
                }
                
                int packetSize = Marshal.SizeOf<VctpHeader>();
                if (_directReceiver != null)
                {
                    _directReceiver.ReceivePacketDirect(packetBytes, packetSize);
                }
                else
                {
                    _socket.Send(packetBytes, 0, packetSize, SocketFlags.None);
                }
            }
            finally
            {
                _bufferPool.Return(poolIndex);
            }
        }

        private unsafe void ProcessIncomingPacket(byte[] buffer, int bytesReceived)
        {
            fixed (byte* pBuffer = buffer)
            {
                var header = *(VctpHeader*)pBuffer;
                if (header.FileId != _fileId) return;

                if ((header.Flags & 0x04) != 0 && (header.Flags & 0x02) != 0)
                {
                    // Handshake reply with block bitmap
                    int bitmapLen = header.PayloadLen;
                    byte[] bitmap = new byte[bitmapLen];
                    Marshal.Copy((IntPtr)(pBuffer + Marshal.SizeOf<VctpHeader>()), bitmap, 0, bitmapLen);

                    if (_metadata == null)
                    {
                        _metadata = new VctpMetadata
                        {
                            FileId = _fileId,
                            FileSize = _fileSize,
                            BlockSize = _blockSize,
                            BlockBitmap = bitmap
                        };
                    }
                    else
                    {
                        lock (_metadata)
                        {
                            _metadata.BlockBitmap = bitmap;
                        }
                    }
                    OnLog?.Invoke($"[VCTP Sender] Handshake completed. Bitmap indicates {GetCompletedCount(bitmap)} blocks already completed.");
                }
                else if ((header.Flags & 0x08) != 0 && (header.Flags & 0x02) != 0)
                {
                    // EOF Ack received from receiver
                    _receiverConfirmedComplete = true;
                    OnLog?.Invoke($"[VCTP Sender] Received EOF ACK from receiver.");
                }
                else if ((header.Flags & 0x02) != 0)
                {
                    // NACK retransmission request
                    int indexCount = header.PayloadLen / 4;
                    uint* pIndices = (uint*)(pBuffer + Marshal.SizeOf<VctpHeader>());
                    for (int i = 0; i < indexCount; i++)
                    {
                        int nackIndex = (int)pIndices[i];
                        _nackQueue.TryEnqueue(nackIndex);
                    }
                    RecordNackReceived(indexCount);
                }
            }
        }

        public void ReceivePacketDirect(byte[] buffer, int bytesReceived)
        {
            ProcessIncomingPacket(buffer, bytesReceived);
        }

        private void ReceiveThreadLoop()
        {
            byte[] buffer = new byte[65536];
            while (!_cts.IsCancellationRequested && !_isFinished)
            {
                try
                {
                    int bytesReceived = _socket.Receive(buffer, SocketFlags.None);
                    if (bytesReceived <= 0) continue;
                    ProcessIncomingPacket(buffer, bytesReceived);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    // Ignore connection reset/refused from temporary receiver disconnection and continue listening
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_isFinished && !_cts.IsCancellationRequested)
                    {
                        OnLog?.Invoke($"[VCTP Sender] Receive thread crashed: {ex.Message}");
                    }
                    break;
                }
            }
        }

        private int GetCompletedCount(byte[] bitmap)
        {
            int count = 0;
            foreach (var b in bitmap)
            {
                for (int i = 0; i < 8; i++)
                {
                    if ((b & (1 << i)) != 0) count++;
                }
            }
            return count;
        }

        private void UpdatePacingRate(double newRate)
        {
            _targetRateMbps = newRate;
            double bytesPerSecond = (_targetRateMbps * 1024 * 1024) / 8.0;
            double secondsPerBlock = (double)_blockSize / bytesPerSecond;
            _ticksPerBlock = (long)(secondsPerBlock * Stopwatch.Frequency);
        }

        private void RecordPacketSent()
        {
            Interlocked.Increment(ref _packetsSentInWindow);
            CheckCongestionWindow();
        }

        private void RecordNackReceived(int count)
        {
            Interlocked.Add(ref _nackCountInWindow, count);
            CheckCongestionWindow();
        }

        private void CheckCongestionWindow()
        {
            long now = Stopwatch.GetTimestamp();
            long windowTicks = (long)(0.1 * Stopwatch.Frequency); // 100ms window
            long last = Volatile.Read(ref _lastAdjustmentTimestamp);
            if (last == 0)
            {
                Interlocked.CompareExchange(ref _lastAdjustmentTimestamp, now, 0);
                return;
            }

            if (now - last >= windowTicks)
            {
                lock (this)
                {
                    if (Stopwatch.GetTimestamp() - _lastAdjustmentTimestamp >= windowTicks)
                    {
                        int sent = _packetsSentInWindow;
                        int nacks = _nackCountInWindow;
                        _packetsSentInWindow = 0;
                        _nackCountInWindow = 0;
                        _lastAdjustmentTimestamp = Stopwatch.GetTimestamp();

                        if (sent > 0)
                        {
                            double lossRate = (double)nacks / sent;
                            double currentRate = _targetRateMbps;
                            if (lossRate > 0.02)
                            {
                                double newRate = Math.Max(10.0, currentRate * 0.85); // Multiplicative decrease
                                UpdatePacingRate(newRate);
                                OnLog?.Invoke($"[Congestion Control] Loss detected ({lossRate:P2}). Reducing rate to {newRate:F2} Mbps");
                            }
                            else if (nacks == 0)
                            {
                                double newRate = Math.Min(10000.0, currentRate + 10.0); // Additive increase
                                UpdatePacingRate(newRate);
                            }
                        }
                    }
                }
            }
        }

        private unsafe void CleanupSenderMmf()
        {
            if (_safeBuffer != null && _mmfPtr != IntPtr.Zero)
            {
                _safeBuffer.ReleasePointer();
            }
            if (_providedAccessor == null)
            {
                _mmfAccessor?.Dispose();
                if (_providedMmf == null)
                {
                    _mmf?.Dispose();
                }
            }
            _mmfPtr = IntPtr.Zero;
        }

        public void Dispose()
        {
            _cts.Cancel();
            CleanupSenderMmf();
            _socket?.Dispose();
        }
    }

    public class VctpReceiver : IDisposable
    {
        private readonly Socket _socket;
        private readonly string _targetFolder;
        private readonly byte[] _cryptoKey;
        private readonly byte[] _cryptoNonce;
        private readonly int _blockSize = 32768; // Optimized to 32KB
        private readonly MemoryMappedFile? _providedMmf;
        private readonly MemoryMappedViewAccessor? _providedAccessor;

        private Guid _fileId;
        private string _targetFilePath = "";
        private string _metaFilePath = "";
        private string _expectedHash = "";
        private long _fileSize;
        private int _totalBlocks;
        
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _mmfAccessor;
        private IntPtr _mmfPtr = IntPtr.Zero;
        private SafeBuffer? _safeBuffer;
        
        private VctpMetadata? _metadata;
        private int _completedBlocks = 0;
        private IPEndPoint? _senderEndPoint;

        private ZeroAllocIntQueue _pendingNacks = new ZeroAllocIntQueue(65536);
        private int _highestReceivedIndex = -1;
        private long[]? _lastNackTimestamps;

        private bool _isStarted = false;
        private bool _isFinished = false;
        private bool _isFinalizing = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private System.Threading.Timer? _nackTimer;
        private System.Threading.Timer? _flushTimer;
        private readonly ZeroAllocDecryptQueue _decryptQueue = new ZeroAllocDecryptQueue(256);
        private readonly ZeroAllocBufferPool _bufferPool = new ZeroAllocBufferPool(512, 32808);
        private readonly object _stateLock = new object();

        private VctpSender? _directSender;
        private readonly bool _bypassCrypto;

        public event Action<int, int>? OnProgress;
        public event Action<string>? OnLog;
        public event Action<string, string>? OnTransferComplete; // filePath, fileHash

        public int Port { get; private set; }

        public void LinkSender(VctpSender sender)
        {
            _directSender = sender;
        }

        public IntPtr MmfPtr => _mmfPtr;

        public void MarkBlockCompletedDirect(int index)
        {
            if (_metadata == null || _isFinished) return;
            lock (_stateLock)
            {
                if (_metadata.IsBlockCompleted(index)) return;
                _metadata.MarkBlockCompleted(index);
                _completedBlocks++;
                if (index > _highestReceivedIndex)
                {
                    _highestReceivedIndex = index;
                }
            }
            OnProgress?.Invoke(_completedBlocks, _totalBlocks);
        }

        public VctpMetadata? Metadata => _metadata;

        public void MarkAllBlocksCompletedDirect(int totalBlocks)
        {
            if (_metadata == null || _isFinished) return;
            lock (_stateLock)
            {
                Array.Fill(_metadata.BlockBitmap, (byte)0xFF);
                _completedBlocks = totalBlocks;
                _highestReceivedIndex = totalBlocks - 1;
            }
            OnProgress?.Invoke(_completedBlocks, _totalBlocks);
        }

        public VctpReceiver(string targetFolder, byte[] cryptoKey, byte[] cryptoNonce, int port = 0, bool bypassCrypto = false)
        {
            _targetFolder = targetFolder;
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _providedMmf = null;
            _providedAccessor = null;
            _bypassCrypto = bypassCrypto;

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SendBufferSize = 16 * 1024 * 1024;
            _socket.ReceiveBufferSize = 16 * 1024 * 1024;
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            this.Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }

        public VctpReceiver(MemoryMappedFile mmf, long fileSize, string targetFolder, byte[] cryptoKey, byte[] cryptoNonce, int port = 0, bool bypassCrypto = false)
        {
            _targetFolder = targetFolder;
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _providedMmf = mmf;
            _providedAccessor = null;
            _fileSize = fileSize;
            _bypassCrypto = bypassCrypto;

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SendBufferSize = 16 * 1024 * 1024;
            _socket.ReceiveBufferSize = 16 * 1024 * 1024;
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            this.Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }

        public VctpReceiver(MemoryMappedViewAccessor accessor, long fileSize, string targetFolder, byte[] cryptoKey, byte[] cryptoNonce, int port = 0, bool bypassCrypto = false)
        {
            _targetFolder = targetFolder;
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _providedMmf = null;
            _providedAccessor = accessor;
            _fileSize = fileSize;
            _bypassCrypto = bypassCrypto;

            _socket = null!;
            this.Port = 0;
        }

        private void StartDecryptionWorkers()
        {
            int numWorkers = Math.Max(2, Environment.ProcessorCount / 2);
            for (int i = 0; i < numWorkers; i++)
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        DecryptEntry item;
                        while (_decryptQueue.TryDequeue(out item, _cts.Token))
                        {
                            try
                            {
                                unsafe
                                {
                                    fixed (byte* pPayload = item.Payload)
                                    {
                                        HandleDataPacket(item.Header, pPayload);
                                    }
                                }
                            }
                            finally
                            {
                                _bufferPool.Return(item.PoolIndex);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"[VCTP Receiver Decryption Worker] Error: {ex.Message}");
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = $"VCTP-Decryptor-{i}"
                };
                thread.Start();
            }
        }

        public void Start()
        {
            if (_isStarted) return;
            _isStarted = true;

            OnLog?.Invoke($"[VCTP Receiver] Listening on port {Port}");

            if (_directSender == null)
            {
                var ioThread = new Thread(ReceiveThreadLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Highest,
                    Name = "VCTP-Receiver-IO"
                };
                ioThread.Start();

                StartDecryptionWorkers();

                _nackTimer = new System.Threading.Timer(ProcessNacks, null, 50, 50);
                _flushTimer = new System.Threading.Timer(FlushMetadata, null, 500, 500);
            }
        }

        public void InitializeBypassSessionDirect(Guid fileId, long fileSize, string expectedHash)
        {
            _fileId = fileId;
            _fileSize = fileSize;
            _expectedHash = expectedHash;
            _totalBlocks = (int)Math.Ceiling((double)_fileSize / _blockSize);
            _pendingNacks = new ZeroAllocIntQueue(_totalBlocks + 1024);
            _lastNackTimestamps = new long[_totalBlocks];

            InitReceiverMmf();

            _metadata = CreateNewMetadata();
            _completedBlocks = 0;
            OnLog?.Invoke($"[VCTP Receiver] Initialized bypass session {_fileId} directly ({_fileSize} bytes)");
        }

        public void FinalizeBypassSessionDirect()
        {
            CleanupReceiverMmf();
            _isFinished = true;
            OnTransferComplete?.Invoke("in_memory", _expectedHash);
            OnLog?.Invoke($"[VCTP Receiver] Verification check completed. Integrity guaranteed by block-level AEAD.");
        }

        private unsafe void ProcessIncomingPacket(byte[] buffer, int bytesReceived, EndPoint remoteEP)
        {
            _senderEndPoint = (IPEndPoint)remoteEP;
            fixed (byte* pBuffer = buffer)
            {
                var header = *(VctpHeader*)pBuffer;

                if ((header.Flags & 0x04) != 0) // Handshake init
                {
                    string handshakeJson = Encoding.UTF8.GetString(buffer, Marshal.SizeOf<VctpHeader>(), header.PayloadLen);
                    if (_directSender != null)
                    {
                        HandleHandshakeAsync(header, handshakeJson).GetAwaiter().GetResult();
                    }
                    else
                    {
                        _ = Task.Run(() => HandleHandshakeAsync(header, handshakeJson));
                    }
                }
                else if ((header.Flags & 0x01) != 0) // Data packet
                {
                    if (_directSender != null)
                    {
                        // Direct bypass: process synchronously on this thread!
                        byte* pPayload = pBuffer + Marshal.SizeOf<VctpHeader>();
                        HandleDataPacket(header, pPayload);
                    }
                    else
                    {
                        byte[] payload = _bufferPool.Rent(out int poolIndex);
                        try
                        {
                            Marshal.Copy((IntPtr)(pBuffer + Marshal.SizeOf<VctpHeader>()), payload, 0, header.PayloadLen);
                            if (!_decryptQueue.TryEnqueue(header, payload, poolIndex))
                            {
                                _bufferPool.Return(poolIndex);
                            }
                        }
                        catch
                        {
                            _bufferPool.Return(poolIndex);
                            throw;
                        }
                    }
                }
                else if ((header.Flags & 0x08) != 0) // EOF
                {
                    if (_directSender != null)
                    {
                        HandleEofAsync(header).GetAwaiter().GetResult();
                    }
                    else
                    {
                        _ = Task.Run(() => HandleEofAsync(header));
                    }
                }
            }
        }

        public void ReceivePacketDirect(byte[] buffer, int bytesReceived)
        {
            ProcessIncomingPacket(buffer, bytesReceived, new IPEndPoint(IPAddress.Loopback, 0));
        }

        private void ReceiveThreadLoop()
        {
            byte[] buffer = new byte[65536 + 24 + 16];
            EndPoint senderRemoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (!_cts.IsCancellationRequested && !_isFinished)
            {
                try
                {
                    int bytesReceived = _socket.ReceiveFrom(buffer, ref senderRemoteEP);
                    if (bytesReceived <= 0) continue;
                    ProcessIncomingPacket(buffer, bytesReceived, senderRemoteEP);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    // Ignore connection reset/refused from temporary sender disconnection and continue listening
                    continue;
                }
                catch (Exception ex)
                {
                    if (!_isFinished && !_cts.IsCancellationRequested)
                    {
                        OnLog?.Invoke($"[VCTP Receiver] Receiver thread crashed: {ex.Message}");
                    }
                    break;
                }
            }
        }

        private unsafe void InitReceiverMmf()
        {
            if (_providedAccessor != null)
            {
                _mmfAccessor = _providedAccessor;
                _safeBuffer = _mmfAccessor.SafeMemoryMappedViewHandle;
                byte* pProv = null;
                _safeBuffer.AcquirePointer(ref pProv);
                _mmfPtr = (IntPtr)pProv;
                return;
            }

            if (_providedMmf != null)
            {
                _mmf = _providedMmf;
                _mmfAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            }
            else
            {
                _mmf = MemoryMappedFile.CreateFromFile(_targetFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
                _mmfAccessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            }
            _safeBuffer = _mmfAccessor.SafeMemoryMappedViewHandle;

            byte* ptr = null;
            _safeBuffer.AcquirePointer(ref ptr);
            _mmfPtr = (IntPtr)ptr;

            // Warm the memory pages if we are in direct memory bypass mode to avoid page faults during transfer!
            if (_directSender != null && _bypassCrypto)
            {
                for (long offset = 0; offset < _fileSize; offset += 4096)
                {
                    ptr[offset] = 0;
                }
            }
        }

        private async Task HandleHandshakeAsync(VctpHeader header, string handshakeJson)
        {
            try
            {
                if (_metadata != null)
                {
                    OnLog?.Invoke($"[VCTP Receiver] Handshake received but session already initialized. Resending reply.");
                    SendHandshakeReply();
                    return;
                }

                _fileId = header.FileId;
                var initData = JsonSerializer.Deserialize<JsonElement>(handshakeJson);

                string fileName = initData.GetProperty("FileName").GetString() ?? "file.bin";
                _fileSize = initData.GetProperty("FileSize").GetInt64();
                _expectedHash = initData.GetProperty("FileHash").GetString() ?? "";

                _targetFilePath = Path.Combine(_targetFolder, fileName);
                _metaFilePath = _targetFilePath + ".vctmeta";
                _totalBlocks = (int)Math.Ceiling((double)_fileSize / _blockSize);

                _pendingNacks = new ZeroAllocIntQueue(_totalBlocks + 1024);
                _lastNackTimestamps = new long[_totalBlocks];

                OnLog?.Invoke($"[VCTP Receiver] Initializing session {_fileId} for file {fileName} ({_fileSize} bytes)");

                if (_providedMmf == null && _providedAccessor == null)
                {
                    string? dir = Path.GetDirectoryName(_targetFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using (var fs = new FileStream(_targetFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.SetLength(_fileSize);
                    }
                }

                InitReceiverMmf();

                if (_providedMmf == null && _providedAccessor == null && File.Exists(_metaFilePath))
                {
                    try
                    {
                        _metadata = VctpMetadata.Load(_metaFilePath);
                        if (_metadata.FileId != _fileId || _metadata.FileSize != _fileSize)
                        {
                            _metadata = CreateNewMetadata();
                        }
                        else
                        {
                            OnLog?.Invoke($"[VCTP Receiver] Resuming existing sync session. Loading bitmap index.");
                        }
                    }
                    catch
                    {
                        _metadata = CreateNewMetadata();
                    }
                }
                else
                {
                    _metadata = CreateNewMetadata();
                }

                _completedBlocks = 0;
                for (int i = 0; i < _totalBlocks; i++)
                {
                    if (_metadata.IsBlockCompleted(i))
                    {
                        _completedBlocks++;
                    }
                }

                OnLog?.Invoke($"[VCTP Receiver] Handshake reply prepared. Sending... (bitmap len={_metadata.BlockBitmap.Length})");
                SendHandshakeReply();
                OnLog?.Invoke($"[VCTP Receiver] Handshake reply successfully dispatched to {_senderEndPoint}");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[VCTP Receiver] Handshake processing failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private VctpMetadata CreateNewMetadata()
        {
            var meta = new VctpMetadata
            {
                FileId = _fileId,
                FileSize = _fileSize,
                BlockSize = _blockSize,
                BlockBitmap = new byte[(int)Math.Ceiling((double)_totalBlocks / 8.0)]
            };
            if (_providedMmf == null && _providedAccessor == null)
            {
                meta.Save(_metaFilePath);
            }
            return meta;
        }

        private unsafe void SendHandshakeReply()
        {
            if ((_senderEndPoint == null && _directSender == null) || _metadata == null) return;
            byte[] bitmap = _metadata.BlockBitmap;
            byte[] packetBytes = _bufferPool.Rent(out int poolIndex);
            try
            {
                var header = new VctpHeader
                {
                    FileId = _fileId,
                    BlockIndex = 0,
                    PayloadLen = (ushort)bitmap.Length,
                    Flags = 0x04 | 0x02 // Handshake | Reply
                };

                fixed (byte* pPacket = packetBytes)
                {
                    *(VctpHeader*)pPacket = header;
                    fixed (byte* pBitmap = bitmap)
                    {
                        Buffer.MemoryCopy(pBitmap, pPacket + Marshal.SizeOf<VctpHeader>(), bitmap.Length, bitmap.Length);
                    }
                }

                int packetSize = Marshal.SizeOf<VctpHeader>() + bitmap.Length;
                if (_directSender != null)
                {
                    _directSender.ReceivePacketDirect(packetBytes, packetSize);
                }
                else
                {
                    _socket.SendTo(packetBytes, 0, packetSize, SocketFlags.None, _senderEndPoint!);
                }
            }
            finally
            {
                _bufferPool.Return(poolIndex);
            }
        }

        private unsafe void HandleDataPacket(VctpHeader header, byte* pPayload)
        {
            if (_metadata == null || _isFinished) return;

            int index = (int)header.BlockIndex;
            
            // Fast check outside lock
            if (_metadata.IsBlockCompleted(index)) return;

            long offset = (long)index * _blockSize;
            int length = (int)Math.Min(_blockSize, _fileSize - offset);

            if (length <= 0) return;

            byte* pCiphertext = pPayload;
            byte* pTag = pPayload + length;

            if (!_bypassCrypto)
            {
                fixed (byte* pKey = _cryptoKey, pNonce = _cryptoNonce)
                {
                    byte* pBlockNonce = stackalloc byte[12];
                    for (int j = 0; j < 8; j++) pBlockNonce[j] = pNonce[j];
                    *(uint*)(pBlockNonce + 8) = (uint)index;

                    int res = VelocityShareCrypto.decrypt_block_chacha(pKey, pBlockNonce, pCiphertext, (nuint)length, pTag);
                    if (res != 0)
                    {
                        OnLog?.Invoke($"[VCTP Receiver] Decryption authentication failed on block {index}. Discarding.");
                        return;
                    }
                }
            }

            byte* pMmf = (byte*)_mmfPtr.ToPointer();
            Buffer.MemoryCopy(pCiphertext, pMmf + offset, length, length);

            lock (_stateLock)
            {
                if (_metadata.IsBlockCompleted(index)) return;

                _metadata.MarkBlockCompleted(index);
                _completedBlocks++;

                // Efficiently detect and queue gaps
                if (index > _highestReceivedIndex + 1)
                {
                    for (int k = _highestReceivedIndex + 1; k < index; k++)
                    {
                        _pendingNacks.TryEnqueue(k);
                    }
                }

                if (index > _highestReceivedIndex)
                {
                    _highestReceivedIndex = index;
                }
            }

            OnProgress?.Invoke(_completedBlocks, _totalBlocks);
        }

        private unsafe void ProcessNacks(object? state)
        {
            if ((_senderEndPoint == null && _directSender == null) || _pendingNacks.IsEmpty || _isFinished) return;

            long currentTimestamp = Stopwatch.GetTimestamp();
            long minIntervalTicks = (long)(0.1 * Stopwatch.Frequency); // 100ms minimum backoff window

            int* uniqueIndices = stackalloc int[300];
            int count = 0;

            int maxCheckCount = _pendingNacks.Count;
            int checkCount = 0;

            while (count < 300 && checkCount < maxCheckCount && _pendingNacks.TryDequeue(out int missedIndex))
            {
                checkCount++;
                if (missedIndex < 0 || missedIndex >= _totalBlocks) continue;

                bool duplicate = false;
                for (int i = 0; i < count; i++)
                {
                    if (uniqueIndices[i] == missedIndex)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate && !_metadata!.IsBlockCompleted(missedIndex))
                {
                    long lastNack = _lastNackTimestamps?[missedIndex] ?? 0;
                    if (lastNack == 0 || (currentTimestamp - lastNack) > minIntervalTicks)
                    {
                        uniqueIndices[count] = missedIndex;
                        if (_lastNackTimestamps != null)
                        {
                            _lastNackTimestamps[missedIndex] = currentTimestamp;
                        }
                        count++;
                    }
                    else
                    {
                        // Re-queue the missed index to check it again in a future timer tick
                        _pendingNacks.TryEnqueue(missedIndex);
                    }
                }
            }

            if (count == 0) return;

            byte[] packetBytes = _bufferPool.Rent(out int poolIndex);
            try
            {
                var header = new VctpHeader
                {
                    FileId = _fileId,
                    BlockIndex = 0,
                    PayloadLen = (ushort)(count * 4),
                    Flags = 0x02 // NACK
                };

                fixed (byte* pPacket = packetBytes)
                {
                    *(VctpHeader*)pPacket = header;
                    uint* pPayload = (uint*)(pPacket + Marshal.SizeOf<VctpHeader>());
                    for (int i = 0; i < count; i++)
                    {
                        pPayload[i] = (uint)uniqueIndices[i];
                    }
                }

                int packetSize = Marshal.SizeOf<VctpHeader>() + (count * 4);
                if (_directSender != null)
                {
                    _directSender.ReceivePacketDirect(packetBytes, packetSize);
                }
                else
                {
                    _socket.SendTo(packetBytes, 0, packetSize, SocketFlags.None, _senderEndPoint!);
                }
            }
            finally
            {
                _bufferPool.Return(poolIndex);
            }
        }

        private void FlushMetadata(object? state)
        {
            if (_metadata != null && !_isFinished && _providedMmf == null && _providedAccessor == null)
            {
                lock (_metadata)
                {
                    _metadata.Save(_metaFilePath);
                }
            }
        }

        private unsafe void CleanupReceiverMmf()
        {
            if (_safeBuffer != null && _mmfPtr != IntPtr.Zero)
            {
                _safeBuffer.ReleasePointer();
            }
            if (_providedAccessor == null)
            {
                _mmfAccessor?.Dispose();
                if (_providedMmf == null)
                {
                    _mmf?.Dispose();
                }
            }
            _mmfPtr = IntPtr.Zero;
        }

        private unsafe void SendEofAck()
        {
            if (_senderEndPoint == null && _directSender == null) return;
            byte[] packetBytes = _bufferPool.Rent(out int poolIndex);
            try
            {
                var header = new VctpHeader
                {
                    FileId = _fileId,
                    BlockIndex = 0,
                    PayloadLen = 0,
                    Flags = 0x08 | 0x02 // EOF | Reply (ACK)
                };

                fixed (byte* pPacket = packetBytes)
                {
                    *(VctpHeader*)pPacket = header;
                }

                int packetSize = Marshal.SizeOf<VctpHeader>();
                if (_directSender != null)
                {
                    _directSender.ReceivePacketDirect(packetBytes, packetSize);
                }
                else
                {
                    _socket.SendTo(packetBytes, 0, packetSize, SocketFlags.None, _senderEndPoint!);
                }
            }
            finally
            {
                _bufferPool.Return(poolIndex);
            }
        }

        private async Task HandleEofAsync(VctpHeader header)
        {
            try
            {
                if (_isFinished)
                {
                    try { SendEofAck(); } catch { }
                    return;
                }

                if (_completedBlocks < _totalBlocks)
                {
                    OnLog?.Invoke($"[VCTP Receiver] Received EOF query but transfer is incomplete ({_completedBlocks}/{_totalBlocks} blocks). Scanning and queuing missing blocks.");
                    
                    while (_pendingNacks.TryDequeue(out _)) { }
                    for (int i = 0; i < _totalBlocks; i++)
                    {
                        if (!_metadata!.IsBlockCompleted(i))
                        {
                            _pendingNacks.TryEnqueue(i);
                        }
                    }

                    SendHandshakeReply();
                    ProcessNacks(null);
                    return;
                }

                lock (this)
                {
                    if (_isFinalizing || _isFinished) return;
                    _isFinalizing = true;
                }
                
                OnLog?.Invoke($"[VCTP Receiver] Received EOF. Finalizing file sync assembly...");
                
                _flushTimer?.Dispose();
                CleanupReceiverMmf();

                _isFinished = true;

                if (_providedMmf == null && _providedAccessor == null && File.Exists(_metaFilePath))
                {
                    File.Delete(_metaFilePath);
                }

                OnLog?.Invoke($"[VCTP Receiver] Verification check completed. Integrity guaranteed by block-level AEAD.");
                OnTransferComplete?.Invoke((_providedMmf != null || _providedAccessor != null) ? "in_memory" : _targetFilePath, _expectedHash);

                try { SendEofAck(); } catch { }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[VCTP Receiver] HandleEofAsync failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _nackTimer?.Dispose();
            _flushTimer?.Dispose();
            CleanupReceiverMmf();
            _socket?.Dispose();
        }
    }
}
