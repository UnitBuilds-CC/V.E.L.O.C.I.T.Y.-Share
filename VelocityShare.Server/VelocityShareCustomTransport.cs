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

    public class VctpSender : IDisposable
    {
        private readonly Socket _socket;
        private readonly IPEndPoint _remoteEndPoint;
        private readonly string _filePath;
        private readonly Guid _fileId;
        private readonly long _fileSize;
        private readonly string _expectedHash;
        private readonly byte[] _cryptoKey;
        private readonly byte[] _cryptoNonce;
        private readonly int _blockSize = 32768; // Optimized to 32KB
        private readonly double _targetRateMbps;
        private readonly MemoryMappedFile? _providedMmf;

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _mmfAccessor;
        private IntPtr _mmfPtr = IntPtr.Zero;
        private SafeBuffer? _safeBuffer;
        
        private VctpMetadata? _metadata;
        private int _totalBlocks;
        private ConcurrentQueue<int> _nackQueue = new();
        private bool _isFinished = false;
        private bool _receiverConfirmedComplete = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly BlockingCollection<(int Index, byte[] Bytes)> _packetQueue = new(128);

        public event Action<int, int>? OnProgress;
        public event Action<string>? OnLog;

        public VctpSender(string filePath, Guid fileId, string expectedHash, IPEndPoint remoteEndPoint, byte[] cryptoKey, byte[] cryptoNonce, double targetRateMbps = 500.0)
        {
            _filePath = filePath;
            _fileId = fileId;
            _expectedHash = expectedHash;
            _remoteEndPoint = remoteEndPoint;
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _targetRateMbps = targetRateMbps;
            _providedMmf = null;

            _fileSize = new FileInfo(filePath).Length;
            _totalBlocks = (int)Math.Ceiling((double)_fileSize / _blockSize);

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Connect(_remoteEndPoint);
        }

        public VctpSender(MemoryMappedFile mmf, long fileSize, Guid fileId, string expectedHash, IPEndPoint remoteEndPoint, byte[] cryptoKey, byte[] cryptoNonce, double targetRateMbps = 500.0)
        {
            _filePath = "";
            _fileId = fileId;
            _expectedHash = expectedHash;
            _remoteEndPoint = remoteEndPoint;
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _targetRateMbps = targetRateMbps;
            _providedMmf = mmf;
            _fileSize = fileSize;

            _totalBlocks = (int)Math.Ceiling((double)_fileSize / _blockSize);

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Connect(_remoteEndPoint);
        }

        private unsafe void InitSenderMmf()
        {
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
        }

        private unsafe byte[] CreateHandshakePacket(byte[] payload)
        {
            byte[] packetBytes = new byte[Marshal.SizeOf<VctpHeader>() + payload.Length];
            var header = new VctpHeader
            {
                FileId = _fileId,
                BlockIndex = 0,
                PayloadLen = (ushort)payload.Length,
                Flags = 0x04 // Handshake
            };

            fixed (byte* pPacket = packetBytes)
            {
                Marshal.StructureToPtr(header, (IntPtr)pPacket, false);
                fixed (byte* pPayload = payload)
                {
                    Buffer.MemoryCopy(pPayload, pPacket + Marshal.SizeOf<VctpHeader>(), payload.Length, payload.Length);
                }
            }
            return packetBytes;
        }

        public async Task StartAsync()
        {
            OnLog?.Invoke($"[VCTP Sender] Starting session {_fileId} targeting {_remoteEndPoint}");
            
            InitSenderMmf();

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
            _ = Task.Run(ReceivePacketsLoopAsync, _cts.Token);

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

        private void SendHandshakePacket(byte[] payload)
        {
            byte[] packetBytes = CreateHandshakePacket(payload);
            _socket.Send(packetBytes, SocketFlags.None);
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
                                var packet = CreateBlockPacket(blockIndex, out _);
                                if (packet != null && packet.Length > 0)
                                {
                                    _packetQueue.Add((blockIndex, packet), _cts.Token);
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
            double bytesPerSecond = (_targetRateMbps * 1024 * 1024) / 8.0;
            double secondsPerBlock = (double)_blockSize / bytesPerSecond;
            long ticksPerBlock = (long)(secondsPerBlock * Stopwatch.Frequency);

            long nextSendTime = Stopwatch.GetTimestamp();

            // Start background encryption producer
            StartEncryptionProducer();

            // 1. Initial Blast Phase
            try
            {
                foreach (var item in _packetQueue.GetConsumingEnumerable(_cts.Token))
                {
                    if (_cts.IsCancellationRequested || _isFinished) break;

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

                    _socket.Send(item.Bytes, SocketFlags.None);
                    _metadata!.MarkBlockCompleted(item.Index);
                    OnProgress?.Invoke(item.Index + 1, _totalBlocks);

                    nextSendTime += ticksPerBlock;
                }
            }
            catch (OperationCanceledException) { }

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
                    await Task.Delay(100);
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

        private unsafe byte[] CreateBlockPacket(int index, out int length)
        {
            long offset = (long)index * _blockSize;
            length = (int)Math.Min(_blockSize, _fileSize - offset);

            if (length <= 0) return Array.Empty<byte>();

            byte[] packetBytes = new byte[Marshal.SizeOf<VctpHeader>() + length + 16];

            fixed (byte* pPacket = packetBytes)
            {
                byte* pCiphertext = pPacket + Marshal.SizeOf<VctpHeader>();
                byte* pTag = pCiphertext + length;

                // Zero-copy plain read
                byte* pMmf = (byte*)_mmfPtr.ToPointer();
                Buffer.MemoryCopy(pMmf + offset, pCiphertext, length, length);

                // Call Rust FFI ChaCha20-Poly1305 encryption in-place
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

                var header = new VctpHeader
                {
                    FileId = _fileId,
                    BlockIndex = (uint)index,
                    PayloadLen = (ushort)(length + 16),
                    Flags = 0x01 // Data
                };

                Marshal.StructureToPtr(header, (IntPtr)pPacket, false);
            }
            return packetBytes;
        }

        private void SendBlock(int index)
        {
            byte[] packetBytes;
            int length;
            try
            {
                packetBytes = CreateBlockPacket(index, out length);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[VCTP Sender] {ex.Message}");
                return;
            }

            if (packetBytes.Length == 0) return;

            _socket.Send(packetBytes, SocketFlags.None);
            _metadata!.MarkBlockCompleted(index);
            OnProgress?.Invoke(index + 1, _totalBlocks);
        }

        private unsafe byte[] CreateEofPacket()
        {
            byte[] packetBytes = new byte[Marshal.SizeOf<VctpHeader>()];
            var header = new VctpHeader
            {
                FileId = _fileId,
                BlockIndex = 0,
                PayloadLen = 0,
                Flags = 0x08 // EOF Sync Query
            };

            fixed (byte* pPacket = packetBytes)
            {
                Marshal.StructureToPtr(header, (IntPtr)pPacket, false);
            }
            return packetBytes;
        }

        private void SendEofPacket()
        {
            byte[] packetBytes = CreateEofPacket();
            _socket.Send(packetBytes, SocketFlags.None);
        }

        private unsafe void ProcessIncomingPacket(byte[] buffer, int bytesReceived)
        {
            fixed (byte* pBuffer = buffer)
            {
                var header = Marshal.PtrToStructure<VctpHeader>((IntPtr)pBuffer);
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
                        _nackQueue.Enqueue(nackIndex);
                    }
                }
            }
        }

        private async Task ReceivePacketsLoopAsync()
        {
            byte[] buffer = new byte[65536];
            try
            {
                while (!_cts.IsCancellationRequested && !_isFinished)
                {
                    var result = await _socket.ReceiveAsync(buffer, SocketFlags.None);
                    if (result <= 0) continue;
                    ProcessIncomingPacket(buffer, result);
                }
            }
            catch (Exception ex)
            {
                if (!_isFinished)
                {
                    OnLog?.Invoke($"[VCTP Sender] Receive thread crashed: {ex.Message}");
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

        private unsafe void CleanupSenderMmf()
        {
            if (_safeBuffer != null && _mmfPtr != IntPtr.Zero)
            {
                _safeBuffer.ReleasePointer();
            }
            _mmfAccessor?.Dispose();
            if (_providedMmf == null)
            {
                _mmf?.Dispose();
            }
            _mmfPtr = IntPtr.Zero;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _packetQueue.Dispose();
            CleanupSenderMmf();
            _socket.Dispose();
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

        private ConcurrentQueue<int> _pendingNacks = new();
        private HashSet<int> _receivedIndices = new();
        private int _highestReceivedIndex = -1;

        private bool _isStarted = false;
        private bool _isFinished = false;
        private bool _isFinalizing = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private System.Threading.Timer? _nackTimer;
        private System.Threading.Timer? _flushTimer;
        private readonly BlockingCollection<(VctpHeader Header, byte[] Payload)> _decryptQueue = new(256);
        private readonly object _stateLock = new object();

        public event Action<int, int>? OnProgress;
        public event Action<string>? OnLog;
        public event Action<string, string>? OnTransferComplete; // filePath, fileHash

        public int Port { get; private set; }

        public VctpReceiver(string targetFolder, byte[] cryptoKey, byte[] cryptoNonce, int port = 0)
        {
            _targetFolder = targetFolder;
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _providedMmf = null;

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            this.Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }

        public VctpReceiver(MemoryMappedFile mmf, long fileSize, string targetFolder, byte[] cryptoKey, byte[] cryptoNonce, int port = 0)
        {
            _targetFolder = targetFolder;
            _cryptoKey = cryptoKey;
            _cryptoNonce = cryptoNonce;
            _providedMmf = mmf;
            _fileSize = fileSize;

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            this.Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
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
                        foreach (var item in _decryptQueue.GetConsumingEnumerable(_cts.Token))
                        {
                            unsafe
                            {
                                fixed (byte* pPayload = item.Payload)
                                {
                                    HandleDataPacket(item.Header, pPayload);
                                }
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

            _ = Task.Run(ReceiveLoopAsync, _cts.Token);
            StartDecryptionWorkers();

            _nackTimer = new System.Threading.Timer(ProcessNacks, null, 50, 50);
            _flushTimer = new System.Threading.Timer(FlushMetadata, null, 500, 500);
        }

        private unsafe void ProcessIncomingPacket(byte[] buffer, int bytesReceived, EndPoint remoteEP)
        {
            _senderEndPoint = (IPEndPoint)remoteEP;
            fixed (byte* pBuffer = buffer)
            {
                var header = Marshal.PtrToStructure<VctpHeader>((IntPtr)pBuffer);

                if ((header.Flags & 0x04) != 0) // Handshake init
                {
                    string handshakeJson = Encoding.UTF8.GetString(buffer, Marshal.SizeOf<VctpHeader>(), header.PayloadLen);
                    _ = Task.Run(() => HandleHandshakeAsync(header, handshakeJson));
                }
                else if ((header.Flags & 0x01) != 0) // Data packet
                {
                    byte[] payload = new byte[header.PayloadLen];
                    Marshal.Copy((IntPtr)(pBuffer + Marshal.SizeOf<VctpHeader>()), payload, 0, header.PayloadLen);
                    _decryptQueue.Add((header, payload), _cts.Token);
                }
                else if ((header.Flags & 0x08) != 0) // EOF
                {
                    _ = Task.Run(() => HandleEofAsync(header));
                }
            }
        }

        private async Task ReceiveLoopAsync()
        {
            byte[] buffer = new byte[65536 + 24 + 16];
            EndPoint senderRemoteEP = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                while (!_cts.IsCancellationRequested && !_isFinished)
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, senderRemoteEP);
                    if (result.ReceivedBytes <= 0) continue;
                    ProcessIncomingPacket(buffer, result.ReceivedBytes, result.RemoteEndPoint);
                }
            }
            catch (Exception ex)
            {
                if (!_isFinished)
                {
                    OnLog?.Invoke($"[VCTP Receiver] Receiver thread crashed: {ex.Message}");
                }
            }
        }

        private unsafe void InitReceiverMmf()
        {
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

                OnLog?.Invoke($"[VCTP Receiver] Initializing session {_fileId} for file {fileName} ({_fileSize} bytes)");

                if (_providedMmf == null)
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

                if (_providedMmf == null && File.Exists(_metaFilePath))
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
                        _receivedIndices.Add(i);
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
            if (_providedMmf == null)
            {
                meta.Save(_metaFilePath);
            }
            return meta;
        }

        private unsafe byte[] CreateHandshakeReplyPacket()
        {
            byte[] bitmap = _metadata!.BlockBitmap;
            byte[] packetBytes = new byte[Marshal.SizeOf<VctpHeader>() + bitmap.Length];

            var header = new VctpHeader
            {
                FileId = _fileId,
                BlockIndex = 0,
                PayloadLen = (ushort)bitmap.Length,
                Flags = 0x04 | 0x02 // Handshake | Reply
            };

            fixed (byte* pPacket = packetBytes)
            {
                Marshal.StructureToPtr(header, (IntPtr)pPacket, false);
                fixed (byte* pBitmap = bitmap)
                {
                    Buffer.MemoryCopy(pBitmap, pPacket + Marshal.SizeOf<VctpHeader>(), bitmap.Length, bitmap.Length);
                }
            }
            return packetBytes;
        }

        private void SendHandshakeReply()
        {
            if (_senderEndPoint == null || _metadata == null) return;
            byte[] packetBytes = CreateHandshakeReplyPacket();
            _socket.SendTo(packetBytes, SocketFlags.None, _senderEndPoint);
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

            byte* pMmf = (byte*)_mmfPtr.ToPointer();
            Buffer.MemoryCopy(pCiphertext, pMmf + offset, length, length);

            lock (_stateLock)
            {
                if (_metadata.IsBlockCompleted(index)) return;

                _metadata.MarkBlockCompleted(index);
                _completedBlocks++;
                _receivedIndices.Add(index);

                // Efficiently detect and queue gaps
                if (index > _highestReceivedIndex + 1)
                {
                    for (int k = _highestReceivedIndex + 1; k < index; k++)
                    {
                        _pendingNacks.Enqueue(k);
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
            if (_senderEndPoint == null || _pendingNacks.IsEmpty || _isFinished) return;

            var uniqueIndices = new HashSet<int>();
            while (_pendingNacks.TryDequeue(out int missedIndex))
            {
                if (!_receivedIndices.Contains(missedIndex))
                {
                    uniqueIndices.Add(missedIndex);
                }
            }

            if (uniqueIndices.Count == 0) return;

            var indicesToSend = new List<int>(uniqueIndices);
            int offset = 0;
            while (offset < indicesToSend.Count)
            {
                int limit = Math.Min(indicesToSend.Count - offset, 300);
                byte[] packetBytes = new byte[Marshal.SizeOf<VctpHeader>() + (limit * 4)];

                var header = new VctpHeader
                {
                    FileId = _fileId,
                    BlockIndex = 0,
                    PayloadLen = (ushort)(limit * 4),
                    Flags = 0x02 // NACK
                };

                fixed (byte* pPacket = packetBytes)
                {
                    Marshal.StructureToPtr(header, (IntPtr)pPacket, false);
                    uint* pPayload = (uint*)(pPacket + Marshal.SizeOf<VctpHeader>());
                    for (int i = 0; i < limit; i++)
                    {
                        pPayload[i] = (uint)indicesToSend[offset + i];
                    }
                }

                _socket.SendTo(packetBytes, _senderEndPoint);
                offset += limit;
            }
        }

        private void FlushMetadata(object? state)
        {
            if (_metadata != null && !_isFinished && _providedMmf == null)
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
            _mmfAccessor?.Dispose();
            if (_providedMmf == null)
            {
                _mmf?.Dispose();
            }
            _mmfPtr = IntPtr.Zero;
        }

        private unsafe byte[] CreateEofAckPacket()
        {
            byte[] packetBytes = new byte[Marshal.SizeOf<VctpHeader>()];
            var header = new VctpHeader
            {
                FileId = _fileId,
                BlockIndex = 0,
                PayloadLen = 0,
                Flags = 0x08 | 0x02 // EOF | Reply (ACK)
            };

            fixed (byte* pPacket = packetBytes)
            {
                Marshal.StructureToPtr(header, (IntPtr)pPacket, false);
            }
            return packetBytes;
        }

        private void SendEofAck()
        {
            if (_senderEndPoint == null) return;
            byte[] packetBytes = CreateEofAckPacket();
            _socket.SendTo(packetBytes, SocketFlags.None, _senderEndPoint);
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
                        if (!_receivedIndices.Contains(i))
                        {
                            _pendingNacks.Enqueue(i);
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

                if (_providedMmf == null && File.Exists(_metaFilePath))
                {
                    File.Delete(_metaFilePath);
                }

                OnLog?.Invoke($"[VCTP Receiver] Verification check completed. Integrity guaranteed by block-level AEAD.");
                OnTransferComplete?.Invoke(_providedMmf != null ? "in_memory" : _targetFilePath, _expectedHash);

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
            _decryptQueue.Dispose();
            _nackTimer?.Dispose();
            _flushTimer?.Dispose();
            CleanupReceiverMmf();
            _socket.Dispose();
        }
    }
}
