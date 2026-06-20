using System;
using System.IO;
using System.Text;
using Velocity.NDA;

namespace VelocityShare.Protocol
{
    public static class NdaSignaling
    {
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

        public readonly ref struct ParsedMessage
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
            }
        }
    }
}
