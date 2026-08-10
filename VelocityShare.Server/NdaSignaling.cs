using System;
using System.IO;
using System.Text;
using Velocity.NDA;

namespace VelocityShare.Protocol
{
    public static class NdaSignaling
    {
        // ── Original sync messages ──────────────────────────────────────────

        public static byte[] CreateDelete(string targetPeerId, string filePath)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "delete");
            compiler.AddTriple("File", "path", filePath);
            return compiler.Compile();
        }

        public static byte[] CreateUpdate(string targetPeerId, string filePath, string hashHex, long fileSize, byte[] content)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "update");
            compiler.AddTriple("File", "path", filePath);
            compiler.AddTriple("File", "hash", hashHex);
            compiler.AddTriple("File", "size", fileSize.ToString());
            if (content != null && content.Length > 0)
            {
                compiler.AddTriple("File", "content", Convert.ToBase64String(content));
            }
            return compiler.Compile();
        }

        public static byte[] CreateOffer(string targetPeerId, string filePath, string hashHex, long fileSize, Guid fileId, byte[] key, byte[] nonce)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "offer");
            compiler.AddTriple("File", "path", filePath);
            compiler.AddTriple("File", "hash", hashHex);
            compiler.AddTriple("File", "size", fileSize.ToString());
            compiler.AddTriple("File", "id", fileId.ToString());
            compiler.AddTriple("Crypto", "key", Convert.ToHexString(key));
            compiler.AddTriple("Crypto", "nonce", Convert.ToHexString(nonce));
            return compiler.Compile();
        }

        public static byte[] CreateAccept(string targetPeerId, Guid fileId, int port, string? senderIp = null)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "accept");
            compiler.AddTriple("File", "id", fileId.ToString());
            compiler.AddTriple("Network", "port", port.ToString());
            if (!string.IsNullOrEmpty(senderIp))
            {
                compiler.AddTriple("Network", "ip", senderIp);
            }
            return compiler.Compile();
        }

        public static byte[] CreatePeerList(System.Collections.Generic.IEnumerable<string> peers)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("Action", "type", "peer_list");
            foreach (var peer in peers)
            {
                compiler.AddTriple("Peer", "id", peer);
            }
            return compiler.Compile();
        }

        // ── Delta sync messages ─────────────────────────────────────────────

        /// <summary>
        /// Offer a delta sync: send list of changed block indices and their hashes.
        /// Format: "block_list" = "idx:hash,idx:hash,..."
        /// </summary>
        public static byte[] CreateDeltaOffer(string targetPeerId, string filePath, string hashHex,
            long fileSize, int blockSize, string blockList, long lastModified)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "delta_offer");
            compiler.AddTriple("File", "path", filePath);
            compiler.AddTriple("File", "hash", hashHex);
            compiler.AddTriple("File", "size", fileSize.ToString());
            compiler.AddTriple("Sync", "block_size", blockSize.ToString());
            compiler.AddTriple("Sync", "block_list", blockList);
            compiler.AddTriple("Sync", "last_modified", lastModified.ToString());
            return compiler.Compile();
        }

        /// <summary>
        /// Request specific blocks from a peer (response to delta_offer).
        /// Format: "requested_blocks" = "idx,idx,idx,..."
        /// </summary>
        public static byte[] CreateBlockRequest(string targetPeerId, string filePath, string requestedBlocks)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "block_request");
            compiler.AddTriple("File", "path", filePath);
            compiler.AddTriple("Sync", "requested_blocks", requestedBlocks);
            return compiler.Compile();
        }

        /// <summary>
        /// Send a single block of data for a file.
        /// </summary>
        public static byte[] CreateBlockData(string targetPeerId, string filePath,
            int blockIndex, long offset, byte[] blockData, string blockHash)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "block_data");
            compiler.AddTriple("File", "path", filePath);
            compiler.AddTriple("Sync", "block_index", blockIndex.ToString());
            compiler.AddTriple("Sync", "block_offset", offset.ToString());
            compiler.AddTriple("Sync", "block_hash", blockHash);
            compiler.AddTriple("File", "content", Convert.ToBase64String(blockData));
            return compiler.Compile();
        }

        /// <summary>
        /// Signal that all blocks for a delta sync have been sent.
        /// </summary>
        public static byte[] CreateDeltaComplete(string targetPeerId, string filePath, string finalHash)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "delta_complete");
            compiler.AddTriple("File", "path", filePath);
            compiler.AddTriple("File", "hash", finalHash);
            return compiler.Compile();
        }

        // ── Full sync / initial reconciliation messages ─────────────────────

        /// <summary>
        /// Send a file manifest for initial full sync.
        /// Format: "manifest" = "path1|hash1|size1|mtime1,path2|hash2|size2|mtime2,..."
        /// </summary>
        public static byte[] CreateSyncManifest(string targetPeerId, string manifest)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "sync_manifest");
            compiler.AddTriple("Sync", "manifest", manifest);
            return compiler.Compile();
        }

        /// <summary>
        /// Signal that initial full sync is complete.
        /// </summary>
        public static byte[] CreateSyncManifestComplete(string targetPeerId)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "sync_manifest_complete");
            return compiler.Compile();
        }

        // ── Conflict resolution ─────────────────────────────────────────────

        /// <summary>
        /// Notify peer of a conflict (both sides modified same file).
        /// Sends our version's metadata so peer can decide LWW.
        /// </summary>
        public static byte[] CreateConflictResolution(string targetPeerId, string filePath,
            string ourHash, long ourSize, long ourLastModified, bool weWin)
        {
            var compiler = new NeuralDocument.Compiler();
            compiler.AddTriple("TargetPeer", "peer_id", targetPeerId);
            compiler.AddTriple("Action", "type", "conflict_resolve");
            compiler.AddTriple("File", "path", filePath);
            compiler.AddTriple("File", "hash", ourHash);
            compiler.AddTriple("File", "size", ourSize.ToString());
            compiler.AddTriple("Sync", "last_modified", ourLastModified.ToString());
            compiler.AddTriple("Sync", "winner", weWin ? "us" : "them");
            return compiler.Compile();
        }

        // ── Parsed message ──────────────────────────────────────────────────

        public readonly struct ParsedMessage
        {
            public string TargetPeerId { get; init; }
            public string Action { get; init; }
            public string FilePath { get; init; }
            public string HashHex { get; init; }
            public long FileSize { get; init; }
            public byte[] Content { get; init; }
            public Guid FileId { get; init; }
            public byte[] Key { get; init; }
            public byte[] Nonce { get; init; }
            public int Port { get; init; }
            public string SenderIp { get; init; }
            // Delta sync fields
            public int BlockSize { get; init; }
            public string BlockList { get; init; }
            public string RequestedBlocks { get; init; }
            public int BlockIndex { get; init; }
            public long BlockOffset { get; init; }
            public string BlockHash { get; init; }
            public string Manifest { get; init; }
            public long LastModified { get; init; }
            public string Winner { get; init; }

            public ParsedMessage(ReadOnlySpan<byte> ndaBuffer)
            {
                var reader = new NeuralDocument.Reader(ndaBuffer);
                string targetPeerId = "";
                string action = "";
                string filePath = "";
                string hashHex = "";
                long fileSize = 0;
                byte[] content = Array.Empty<byte>();
                Guid fileId = Guid.Empty;
                byte[] key = Array.Empty<byte>();
                byte[] nonce = Array.Empty<byte>();
                int port = 0;
                string senderIp = "127.0.0.1";
                int blockSize = 0;
                string blockList = "";
                string requestedBlocks = "";
                int blockIndex = 0;
                long blockOffset = 0;
                string blockHash = "";
                string manifest = "";
                long lastModified = 0;
                string winner = "";

                for (int i = 0; i < reader.TripleCount; i++)
                {
                    var triple = reader.GetTriple(i);
                    string s = reader.GetString(triple.SubjectOffset);
                    string p = reader.GetString(triple.PredicateOffset);
                    string o = reader.GetString(triple.ObjectOffset);

                    if (s == "TargetPeer" && p == "peer_id") targetPeerId = o;
                    else if (s == "Action" && p == "type") action = o;
                    else if (s == "File" && p == "path") filePath = o;
                    else if (s == "File" && p == "hash") hashHex = o;
                    else if (s == "File" && p == "size") long.TryParse(o, out fileSize);
                    else if (s == "File" && p == "content") content = Convert.FromBase64String(o);
                    else if (s == "File" && p == "id") Guid.TryParse(o, out fileId);
                    else if (s == "Crypto" && p == "key") key = Convert.FromHexString(o);
                    else if (s == "Crypto" && p == "nonce") nonce = Convert.FromHexString(o);
                    else if (s == "Network" && p == "port") int.TryParse(o, out port);
                    else if (s == "Network" && p == "ip") senderIp = o;
                    else if (s == "Sync" && p == "block_size") int.TryParse(o, out blockSize);
                    else if (s == "Sync" && p == "block_list") blockList = o;
                    else if (s == "Sync" && p == "requested_blocks") requestedBlocks = o;
                    else if (s == "Sync" && p == "block_index") int.TryParse(o, out blockIndex);
                    else if (s == "Sync" && p == "block_offset") long.TryParse(o, out blockOffset);
                    else if (s == "Sync" && p == "block_hash") blockHash = o;
                    else if (s == "Sync" && p == "manifest") manifest = o;
                    else if (s == "Sync" && p == "last_modified") long.TryParse(o, out lastModified);
                    else if (s == "Sync" && p == "winner") winner = o;
                }

                TargetPeerId = targetPeerId;
                Action = action;
                FilePath = filePath;
                HashHex = hashHex;
                FileSize = fileSize;
                Content = content;
                FileId = fileId;
                Key = key;
                Nonce = nonce;
                Port = port;
                SenderIp = senderIp;
                BlockSize = blockSize;
                BlockList = blockList;
                RequestedBlocks = requestedBlocks;
                BlockIndex = blockIndex;
                BlockOffset = blockOffset;
                BlockHash = blockHash;
                Manifest = manifest;
                LastModified = lastModified;
                Winner = winner;
            }
        }
    }
}
