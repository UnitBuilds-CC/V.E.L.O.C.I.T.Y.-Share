using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VelocityShare.Server;

namespace VelocityShare.E2ETest
{
    class Program
    {
        private const string ServerUrl = "ws://127.0.0.1:5213/ws/share";
        private const string PeerA = "PeerA";
        private const string PeerB = "PeerB";
        private const string TestFileName = "e2e_test_file.bin";
        private const int FileSize = 100 * 1024 * 1024; // 100MB file

        static async Task Main(string[] args)
        {
            Console.WriteLine("=====================================================================");
            Console.WriteLine("          V.E.L.O.C.I.T.Y. Share Client-to-Client E2E Test");
            Console.WriteLine("=====================================================================");

            // Generate test data and load it into source MMF
            byte[] srcBytes = new byte[FileSize];
            Random.Shared.NextBytes(srcBytes);

            byte[] expectedHashBytes = SHA256.HashData(srcBytes);
            string expectedHashHex = Convert.ToHexString(expectedHashBytes).ToLowerInvariant();

            using var srcMmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(null, FileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
            using (var accessor = srcMmf.CreateViewAccessor(0, FileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Write))
            {
                accessor.WriteArray(0, srcBytes, 0, srcBytes.Length);
            }

            Console.WriteLine($"[Init] Generated {FileSize / (1024.0 * 1024.0):F2}MB file in-memory MMF.");
            Console.WriteLine($"[Init] Expected Hash: {expectedHashHex}");

            // Run Phase 1: Cryptographic P2P Mode (No pacing, 100Gbps target)
            double speedCrypto = await RunTestPhase(srcMmf, expectedHashHex, bypassCrypto: false);

            // Cool-down delay to let socket resources clear on server
            await Task.Delay(1000);

            // Run Phase 2: Zero-Crypto P2P Mode (No pacing, 100Gbps target)
            double speedNoCrypto = await RunTestPhase(srcMmf, expectedHashHex, bypassCrypto: true);

            Console.WriteLine("---------------------------------------------------------------------");
            Console.WriteLine("                  VCTP E2E BENCHMARK COMPARISON                      ");
            Console.WriteLine("---------------------------------------------------------------------");
            Console.WriteLine($"Phase 1: Cryptographic P2P Mode : {speedCrypto:F2} MB/s ({speedCrypto * 8.0:F2} Mbps)");
            Console.WriteLine($"Phase 2: Zero-Crypto P2P Mode    : {speedNoCrypto:F2} MB/s ({speedNoCrypto * 8.0:F2} Mbps)");
            Console.WriteLine("=====================================================================");
        }

        private static async Task<double> RunTestPhase(System.IO.MemoryMappedFiles.MemoryMappedFile srcMmf, string expectedHashHex, bool bypassCrypto)
        {
            string label = bypassCrypto ? "Zero-Crypto P2P Mode" : "Cryptographic P2P Mode";
            Console.WriteLine($"\n--- Starting Run: {label} ---");

            // Create in-memory destination MMF
            using var destMmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(null, FileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);

            // Pre-touch / Warm both source and destination memory pages to allocate physical RAM and prevent page faults during the test!
            using (var srcAccessor = srcMmf.CreateViewAccessor(0, FileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
            using (var destAccessor = destMmf.CreateViewAccessor(0, FileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite))
            {
                unsafe
                {
                    byte* pSrc = null;
                    byte* pDest = null;
                    srcAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pSrc);
                    destAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pDest);
                    try
                    {
                        byte sum = 0;
                        for (long offset = 0; offset < FileSize; offset += 4096)
                        {
                            sum ^= pSrc[offset];
                            pDest[offset] = 0;
                        }
                        if (sum == 42) GC.KeepAlive(sum);
                    }
                    finally
                    {
                        srcAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                        destAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    }
                }
            }

            byte[] vctpKey = new byte[32];
            byte[] vctpNonce = new byte[12];
            System.Security.Cryptography.RandomNumberGenerator.Fill(vctpKey);
            System.Security.Cryptography.RandomNumberGenerator.Fill(vctpNonce);
            Guid fileId = Guid.NewGuid();

            using var wsA = new ClientWebSocket();
            wsA.Options.SetRequestHeader("Host", "share.unitbuilds.com");
            wsA.Options.RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

            using var wsB = new ClientWebSocket();
            wsB.Options.SetRequestHeader("Host", "share.unitbuilds.com");
            wsB.Options.RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

            var cts = new CancellationTokenSource();

            try
            {
                await wsA.ConnectAsync(new Uri($"{ServerUrl}?peerId={PeerA}"), cts.Token);
                await wsB.ConnectAsync(new Uri($"{ServerUrl}?peerId={PeerB}"), cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Network] ERROR: WebSocket connection failed: {ex.Message}");
                return 0;
            }

            var tcs = new TaskCompletionSource<bool>();
            string finalDestHash = "";
            VctpReceiver? activeReceiver = null;
            VctpSender? activeSender = null;
            var stopwatch = new Stopwatch();

            var peerBTask = Task.Run(async () =>
            {
                var buffer = new byte[1024 * 64];
                try
                {
                    while (wsB.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                    {
                        var result = await wsB.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string rawMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            var doc = JsonDocument.Parse(rawMsg);
                            string msgType = doc.RootElement.GetProperty("type").GetString() ?? "";

                            if (msgType == "folder_sync_payload")
                            {
                                string innerData = doc.RootElement.GetProperty("data").GetString() ?? "";
                                var innerDoc = JsonDocument.Parse(innerData);
                                string syncType = innerDoc.RootElement.GetProperty("type").GetString() ?? "";

                                if (syncType == "sync_vctp_offer")
                                {
                                    string file = innerDoc.RootElement.GetProperty("file").GetString() ?? "";
                                    Guid fid = innerDoc.RootElement.GetProperty("fileId").GetGuid();
                                    byte[] key = Convert.FromBase64String(innerDoc.RootElement.GetProperty("key").GetString() ?? "");
                                    byte[] nonce = Convert.FromBase64String(innerDoc.RootElement.GetProperty("nonce").GetString() ?? "");

                                    activeReceiver = new VctpReceiver(destMmf, FileSize, "", key, nonce, port: 0, bypassCrypto: bypassCrypto);
                                    activeReceiver.OnLog += (logMsg) => Console.WriteLine($"[Receiver Log] {logMsg}");
                                    activeReceiver.OnTransferComplete += (filePath, fileHash) =>
                                    {
                                        finalDestHash = fileHash;
                                        tcs.TrySetResult(true);
                                    };
                                    activeReceiver.Start();

                                    var acceptEnvelope = JsonSerializer.Serialize(new
                                    {
                                        type = "folder_sync_payload",
                                        sender = PeerB,
                                        target = PeerA,
                                        data = JsonSerializer.Serialize(new
                                        {
                                            type = "sync_vctp_accept",
                                            fileId = fid,
                                            port = activeReceiver.Port
                                        })
                                    });

                                    await wsB.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(acceptEnvelope)), WebSocketMessageType.Text, true, cts.Token);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Peer B Error] {ex.Message}");
                }
            });

            var peerATask = Task.Run(async () =>
            {
                var buffer = new byte[1024 * 64];
                try
                {
                    while (wsA.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                    {
                        var result = await wsA.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string rawMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            var doc = JsonDocument.Parse(rawMsg);
                            string msgType = doc.RootElement.GetProperty("type").GetString() ?? "";

                            if (msgType == "folder_sync_payload")
                            {
                                string innerData = doc.RootElement.GetProperty("data").GetString() ?? "";
                                var innerDoc = JsonDocument.Parse(innerData);
                                string syncType = innerDoc.RootElement.GetProperty("type").GetString() ?? "";
                                if (syncType == "sync_vctp_accept")
                                {
                                    Guid fid = innerDoc.RootElement.GetProperty("fileId").GetGuid();
                                    int port = innerDoc.RootElement.GetProperty("port").GetInt32();
                                    string senderIp = doc.RootElement.TryGetProperty("senderIp", out var ipProp) ? ipProp.GetString() ?? "127.0.0.1" : "127.0.0.1";
                                    var remoteEP = new IPEndPoint(IPAddress.Parse(senderIp), port);
                                    stopwatch.Start();

                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            activeSender = new VctpSender(srcMmf, FileSize, fid, expectedHashHex, remoteEP, vctpKey, vctpNonce, targetRateMbps: 100000.0, bypassCrypto: bypassCrypto);
                                            activeSender.OnLog += (logMsg) => Console.WriteLine($"[Sender Log] {logMsg}");
                                            await activeSender.StartAsync();
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[Peer A Sender Error] {ex.Message}");
                                        }
                                    });
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Peer A Error] {ex.Message}");
                }
            });

            var offerEnvelope = JsonSerializer.Serialize(new
            {
                type = "folder_sync_payload",
                sender = PeerA,
                target = PeerB,
                data = JsonSerializer.Serialize(new
                {
                    type = "sync_vctp_offer",
                    file = TestFileName,
                    hash = expectedHashHex,
                    size = FileSize,
                    fileId = fileId,
                    key = Convert.ToBase64String(vctpKey),
                    nonce = Convert.ToBase64String(vctpNonce)
                })
            });

            await wsA.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(offerEnvelope)), WebSocketMessageType.Text, true, cts.Token);

            // Wait for receiver to signal completion (max 60 seconds)
            await Task.WhenAny(tcs.Task, Task.Delay(60000));
            stopwatch.Stop();

            cts.Cancel();
            try { await Task.WhenAll(peerATask, peerBTask); } catch { }

            bool memCheckPassed = true;
            if (finalDestHash.Equals(expectedHashHex, StringComparison.OrdinalIgnoreCase))
            {
                using var srcAccessor = srcMmf.CreateViewAccessor(0, FileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                using var destAccessor = destMmf.CreateViewAccessor(0, FileSize, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                unsafe
                {
                    byte* pSrc = null;
                    byte* pDest = null;
                    srcAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pSrc);
                    destAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pDest);
                    try
                    {
                        long* pSrcLong = (long*)pSrc;
                        long* pDestLong = (long*)pDest;
                        long longCount = FileSize / 8;
                        for (long i = 0; i < longCount; i++)
                        {
                            if (pSrcLong[i] != pDestLong[i])
                            {
                                memCheckPassed = false;
                                break;
                            }
                        }
                    }
                    finally
                    {
                        srcAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                        destAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    }
                }
            }
            else
            {
                memCheckPassed = false;
            }

            bool verified = memCheckPassed;
            double speedMB = 0;
            if (verified)
            {
                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                speedMB = (FileSize / (1024.0 * 1024.0)) / elapsedSec;
                Console.WriteLine($"[Result] Verified = True. Speed: {speedMB:F2} MB/s in {elapsedSec:F3}s");
            }
            else
            {
                Console.WriteLine("[Result] ERROR: Verification failed or timeout!");
            }

            activeReceiver?.Dispose();
            activeSender?.Dispose();

            return speedMB;
        }
    }
}
