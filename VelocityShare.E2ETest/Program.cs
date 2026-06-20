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
        private const string ServerUrl = "wss://52.188.14.216/ws/share";
        private const string PeerA = "PeerA";
        private const string PeerB = "PeerB";
        private const string TestFileName = "e2e_test_file.bin";
        private const int FileSize = 10 * 1024 * 1024; // 10MB file for fast verification

        static async Task Main(string[] args)
        {
            Console.WriteLine("=====================================================================");
            Console.WriteLine("          V.E.L.O.C.I.T.Y. Share Client-to-Client E2E Test");
            Console.WriteLine("=====================================================================");

            // 1. Prepare temp directories
            string tempDir = Path.Combine(Path.GetTempPath(), "VelocityShare_E2E_" + Guid.NewGuid().ToString().Substring(0, 8));
            string dirA = Path.Combine(tempDir, "PeerA");
            string dirB = Path.Combine(tempDir, "PeerB");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            // Generate test file in dirA
            string srcFilePath = Path.Combine(dirA, TestFileName);
            byte[] srcBytes = new byte[FileSize];
            Random.Shared.NextBytes(srcBytes);
            File.WriteAllBytes(srcFilePath, srcBytes);

            byte[] expectedHashBytes = SHA256.HashData(srcBytes);
            string expectedHashHex = Convert.ToHexString(expectedHashBytes).ToLowerInvariant();

            Console.WriteLine($"[Init] Generated {FileSize / (1024.0 * 1024.0):F2}MB file in {dirA}");
            Console.WriteLine($"[Init] Expected Hash: {expectedHashHex}");

            // Generate crypto credentials for VCTP
            byte[] vctpKey = new byte[32];
            byte[] vctpNonce = new byte[12];
            Random.Shared.NextBytes(vctpKey);
            Random.Shared.NextBytes(vctpNonce);
            Guid fileId = Guid.NewGuid();

            // 2. Establish connections to Azure VM Signaling Hub
            Console.WriteLine("[Network] Connecting Peer A & Peer B to VM Signaling Server...");
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
                Console.WriteLine("[Network] Peer A & Peer B WebSockets connected successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Network] ERROR: WebSocket connection failed. Details: {ex.Message}");
                CleanupDirs(tempDir);
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            string finalDestHash = "";
            VctpReceiver? activeReceiver = null;
            VctpSender? activeSender = null;
            var stopwatch = new Stopwatch();

            // 3. Start Peer B receiver message loop
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
                                    string hash = innerDoc.RootElement.GetProperty("hash").GetString() ?? "";
                                    Guid fid = innerDoc.RootElement.GetProperty("fileId").GetGuid();
                                    byte[] key = Convert.FromBase64String(innerDoc.RootElement.GetProperty("key").GetString() ?? "");
                                    byte[] nonce = Convert.FromBase64String(innerDoc.RootElement.GetProperty("nonce").GetString() ?? "");

                                    Console.WriteLine($"[Peer B] Received VCTP Offer for file: {file}");

                                    activeReceiver = new VctpReceiver(dirB, key, nonce, port: 0);
                                    activeReceiver.OnTransferComplete += (filePath, fileHash) =>
                                    {
                                        finalDestHash = fileHash;
                                        tcs.TrySetResult(true);
                                    };
                                    activeReceiver.Start();
                                    Console.WriteLine($"[Peer B] Started VctpReceiver on port {activeReceiver.Port}");

                                    // Send sync_vctp_accept back to Peer A
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
                                    Console.WriteLine($"[Peer B] Dispatched sync_vctp_accept back to Peer A on port {activeReceiver.Port}");
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

            // 4. Start Peer A sender message loop
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

                                    Console.WriteLine($"[Peer A] Received VCTP Accept. Remote Port: {port}. Initiating transfer...");

                                    var remoteEP = new IPEndPoint(IPAddress.Loopback, port);
                                    stopwatch.Start();

                                    _ = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            activeSender = new VctpSender(srcFilePath, fid, expectedHashHex, remoteEP, vctpKey, vctpNonce, targetRateMbps: 2000.0);
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

            // 5. Send the offer from Peer A to Peer B via the VM
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

            Console.WriteLine("[Peer A] Dispatching sync_vctp_offer to VM...");
            await wsA.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(offerEnvelope)), WebSocketMessageType.Text, true, cts.Token);

            // Wait for receiver to signal completion (max 45 seconds)
            await Task.WhenAny(tcs.Task, Task.Delay(45000));
            stopwatch.Stop();

            cts.Cancel();
            try { await Task.WhenAll(peerATask, peerBTask); } catch { }

            // 6. Print E2E Summary
            bool verified = finalDestHash.Equals(expectedHashHex, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine("---------------------------------------------------------------------");
            Console.WriteLine("                      E2E TEST RESULT SUMMARY                        ");
            Console.WriteLine("---------------------------------------------------------------------");
            Console.WriteLine($"Hash Match Verified:  {verified}");
            Console.WriteLine($"Expected Hash:        {expectedHashHex}");
            Console.WriteLine($"Received Hash:        {finalDestHash}");
            
            if (verified)
            {
                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                double speedMB = (FileSize / (1024.0 * 1024.0)) / elapsedSec;
                Console.WriteLine($"Time Taken:           {elapsedSec:F3} seconds");
                Console.WriteLine($"P2P Throughput:       {speedMB:F2} MB/s ({speedMB * 8.0:F2} Mbps)");
            }
            else
            {
                Console.WriteLine("ERROR: Verification failed! Hashes do not match or timeout occurred.");
            }
            Console.WriteLine("=====================================================================");

            // Cleanup
            activeReceiver?.Dispose();
            activeSender?.Dispose();
            CleanupDirs(tempDir);
        }

        private static void CleanupDirs(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch { }
        }
    }
}
