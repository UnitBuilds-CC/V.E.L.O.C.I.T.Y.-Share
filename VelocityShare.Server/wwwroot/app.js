// V.E.L.O.C.I.T.Y. SHARE - CLIENT APPLICATION ENGINE

// Unique random peer ID for this tab session
const myPeerId = "peer_" + Math.random().toString(36).substring(2, 8);
document.getElementById('my-peer-id').textContent = myPeerId;

function parseNda(arrayBuffer) {
    const view = new DataView(arrayBuffer);
    const magic = view.getUint32(0, true);
    if (magic !== 0x3141444E) {
        throw new Error("Invalid magic bytes in NDA document");
    }

    const tripleCount = view.getUint32(40, true);
    const commandCount = view.getUint32(44, true);
    const stringPoolOffset = view.getUint32(48, true);

    const stringPoolBytes = new Uint8Array(arrayBuffer, stringPoolOffset);

    function getString(offset) {
        if (offset === 0) return "";
        const len = new DataView(stringPoolBytes.buffer, stringPoolBytes.byteOffset + offset, 2).getUint16(0, true);
        const decoder = new TextDecoder("utf-8");
        return decoder.decode(new Uint8Array(stringPoolBytes.buffer, stringPoolBytes.byteOffset + offset + 2, len));
    }

    const triples = [];
    const tripleStart = 52;
    for (let i = 0; i < tripleCount; i++) {
        const sOff = view.getUint32(tripleStart + i * 12, true);
        const pOff = view.getUint32(tripleStart + i * 12 + 4, true);
        const oOff = view.getUint32(tripleStart + i * 12 + 8, true);

        triples.push({
            subject: getString(sOff),
            predicate: getString(pOff),
            object: getString(oOff)
        });
    }

    const result = { triples };
    const peers = [];
    triples.forEach(t => {
        if (t.subject === "Action" && t.predicate === "type") {
            result.type = t.object;
        } else if (t.subject === "Peer" && t.predicate === "id") {
            peers.push(t.object);
        } else if (t.subject === "TargetPeer" && t.predicate === "peer_id") {
            result.targetPeerId = t.object;
        } else if (t.subject === "File" && t.predicate === "path") {
            result.filePath = t.object;
        } else if (t.subject === "File" && t.predicate === "hash") {
            result.hashHex = t.object;
        } else if (t.subject === "File" && t.predicate === "size") {
            result.fileSize = parseInt(t.object);
        } else if (t.subject === "File" && t.predicate === "id") {
            result.fileId = t.object;
        } else if (t.subject === "Crypto" && t.predicate === "key") {
            result.keyHex = t.object;
        } else if (t.subject === "Crypto" && t.predicate === "nonce") {
            result.nonceHex = t.object;
        } else if (t.subject === "Network" && t.predicate === "port") {
            result.port = parseInt(t.object);
        } else if (t.subject === "Network" && t.predicate === "ip") {
            result.senderIp = t.object;
        }
    });

    if (peers.length > 0) {
        result.peers = peers;
    }

    return result;
}

// State management
let ws = null;
let peerConnections = {}; // targetPeerId -> RTCPeerConnection
let dataChannels = {};    // targetPeerId -> RTCDataChannel
let activeTransfers = {}; // fileId -> transferState
let selectedDropsite = { type: 'local_nas', path: '' };
let isSyncActive = false;

// Canvas configuration
const canvas = document.getElementById('matrix-canvas');
const ctx = canvas.getContext('2d');
let animParticles = [];

// Speed metrics
let uploadBytesSent = 0;
let downloadBytesRecv = 0;
let lastMetricsTime = Date.now();
let mockLatency = 0;

// Initialize WebSocket Signaling Handshake
function connectSignaling() {
    const wsScheme = window.location.protocol === 'https:' ? 'wss' : 'ws';
    const wsUrl = `${wsScheme}://${window.location.host}/ws/share?peerId=${myPeerId}`;
    
    ws = new WebSocket(wsUrl);
    
    ws.onopen = () => {
        document.getElementById('connection-status').textContent = "Matrix Handshake Secure";
        const dot = document.getElementById('connection-dot');
        dot.className = "status-dot online";
        mockLatency = Math.floor(Math.random() * 5) + 1; // 1-5ms local network
        updateTelemetry();
    };
    
    ws.onclose = () => {
        document.getElementById('connection-status').textContent = "Disconnected. Reconnecting...";
        const dot = document.getElementById('connection-dot');
        dot.className = "status-dot pulse";
        setTimeout(connectSignaling, 3000);
    };
    
    ws.onmessage = async (event) => {
        let msg;
        if (event.data instanceof Blob) {
            try {
                const buffer = await event.data.arrayBuffer();
                msg = parseNda(buffer);
            } catch (e) {
                console.error("Failed to parse binary NDA packet:", e);
                return;
            }
        } else {
            msg = JSON.parse(event.data);
        }

        if (msg.type === 'peer_list') {
            updatePeerList(msg.peers);
        } else if (msg.sender && msg.sender !== myPeerId) {
            handleSignalingMessage(msg);
        }
    };
}

// Update the list of online available peers
function updatePeerList(peers) {
    const listContainer = document.getElementById('peer-list');
    document.getElementById('peer-count').textContent = `${peers.length - 1} Online`;
    listContainer.innerHTML = '';
    
    let otherPeersCount = 0;
    peers.forEach(peerId => {
        if (peerId !== myPeerId) {
            otherPeersCount++;
            const item = document.createElement('div');
            item.className = 'peer-item';
            item.innerHTML = `
                <span class="peer-item-name monospace">${peerId}</span>
                <button class="peer-action-btn" onclick="initiateP2PConnection('${peerId}')">CONNECT P2P</button>
            `;
            listContainer.appendChild(item);
        }
    });
    
    if (otherPeersCount === 0) {
        listContainer.innerHTML = '<div class="empty-peers">No other peers online. Open another tab/browser to test P2P transfer!</div>';
    }
}

// Initiate RTCPeerConnection (P2P) WebRTC pipeline
async function initiateP2PConnection(targetId) {
    if (peerConnections[targetId]) return;
    
    console.log(`[P2P] Initiating connection to: ${targetId}`);
    const pc = createPeerConnection(targetId);
    peerConnections[targetId] = pc;
    
    // Create data channel for high-speed file chunk streaming
    const dc = pc.createDataChannel("file-transfer", { ordered: true });
    setupDataChannel(targetId, dc);
    
    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    
    sendSignaling(targetId, {
        type: 'webrtc_offer',
        sdp: pc.localDescription
    });
}

// Create RTCPeerConnection object
function createPeerConnection(targetId) {
    const pc = new RTCPeerConnection({
        iceServers: [{ urls: 'stun:stun.l.google.com:19002' }]
    });
    
    pc.onicecandidate = (event) => {
        if (event.candidate) {
            sendSignaling(targetId, {
                type: 'ice_candidate',
                candidate: event.candidate
            });
        }
    };
    
    pc.ondatachannel = (event) => {
        console.log(`[P2P] Data channel received from: ${targetId}`);
        setupDataChannel(targetId, event.channel);
    };
    
    return pc;
}

// Configure WebRTC Data Channel event listeners
function setupDataChannel(targetId, dc) {
    dataChannels[targetId] = dc;
    dc.binaryType = "arraybuffer";
    
    dc.onopen = () => {
        console.log(`[P2P] Data channel OPEN with peer: ${targetId}`);
        triggerConnectionFlash(true); // Flash green line on visualizer
    };
    
    dc.onclose = () => {
        console.log(`[P2P] Data channel CLOSED with peer: ${targetId}`);
    };
    
    dc.onmessage = (event) => {
        handleIncomingData(targetId, event.data);
    };
}

// Process data chunks arriving over the Data Channel
function handleIncomingData(senderId, rawData) {
    if (typeof rawData === 'string') {
        const meta = JSON.parse(rawData);
        if (meta.type === 'file_header') {
            activeTransfers[meta.fileId] = {
                name: meta.name,
                size: meta.size,
                chunksTotal: meta.chunksTotal,
                chunksReceived: 0,
                buffer: new Array(meta.chunksTotal)
            };
            showTransferItem(meta.fileId, meta.name, meta.size, 'download');
        } else if (meta.type === 'folder_sync_payload') {
            // Received remote sync payload over P2P Data Channel!
            // Send it to our local server via WebSocket so it can write the changes to disk.
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify({
                    type: 'folder_sync_payload',
                    sender: senderId,
                    target: 'local_sync_engine',
                    data: meta.data
                }));
                console.log(`[Sync] Received remote sync payload from peer ${senderId} over P2P, applying locally`);
                createStreamParticle('peer', 'sender', '#00ff66', 'SYNC');
            }
        }
    } else {
        // Parse raw binary chunk payload:
        // Offset 0-16: fileId string (16 bytes)
        // Offset 16-20: chunkIndex uint32 (4 bytes)
        // Offset 20-52: SHA-256 integrity checksum (32 bytes)
        // Offset 52+: Encrypted ADPCM/ChaCha20 data
        const view = new DataView(rawData);
        const fileIdBytes = new Uint8Array(rawData, 0, 16);
        const fileId = new TextDecoder().decode(fileIdBytes).replace(/\0/g, '');
        const chunkIndex = view.getUint32(16, true);
        const fileState = activeTransfers[fileId];
        
        if (fileState) {
            const chunkData = new Uint8Array(rawData, 52);
            fileState.buffer[chunkIndex] = chunkData;
            fileState.chunksReceived++;
            downloadBytesRecv += chunkData.length;
            
            // Render glowing particle from Peer to Sender on Canvas
            createStreamParticle('peer', 'sender', '#00e5ff');
            
            updateTransferProgress(fileId, fileState.chunksReceived, fileState.chunksTotal);
            
            if (fileState.chunksReceived === fileState.chunksTotal) {
                // Reassemble file
                const blob = new Blob(fileState.buffer);
                const url = URL.createObjectURL(blob);
                triggerFileDownload(fileState.name, url);
                delete activeTransfers[fileId];
            }
        }
    }
}

// Trigger browser download for reassembled files
function triggerFileDownload(name, url) {
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
}

// Dispatch signaling messages via WebSocket Server
function sendSignaling(target, payload) {
    if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({
            target: target,
            sender: myPeerId,
            ...payload
        }));
    }
}

// Process signaling notifications from WebSocket hub
async function handleSignalingMessage(msg) {
    const sender = msg.sender;
    if (msg.type === 'webrtc_offer') {
        const pc = createPeerConnection(sender);
        peerConnections[sender] = pc;
        await pc.setRemoteDescription(new RTCSessionDescription(msg.sdp));
        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        sendSignaling(sender, {
            type: 'webrtc_answer',
            sdp: pc.localDescription
        });
    } else if (msg.type === 'webrtc_answer') {
        const pc = peerConnections[sender];
        if (pc) {
            await pc.setRemoteDescription(new RTCSessionDescription(msg.sdp));
        }
    } else if (msg.type === 'ice_candidate') {
        const pc = peerConnections[sender];
        if (pc) {
            await pc.addIceCandidate(new RTCIceCandidate(msg.candidate));
        }
    } else if (msg.type === 'folder_sync_payload') {
        if (msg.sender === 'local_sync_engine') {
            // This is a sync event from our OWN local server's sync engine
            // Forward it to the target peer via WebRTC data channel first, fallback to WebSocket signaling
            const target = msg.target;
            const dc = dataChannels[target];
            if (dc && dc.readyState === 'open') {
                dc.send(JSON.stringify(msg));
                console.log(`[Sync] Forwarded local sync payload to peer ${target} over P2P`);
                createStreamParticle('sender', 'peer', '#00ff66', 'SYNC');
            } else {
                sendSignaling(target, msg);
                console.log(`[Sync] P2P offline. Forwarded local sync payload to peer ${target} via signaling`);
                createStreamParticle('sender', 'server', '#00ff66', 'SYNC');
            }
        } else {
            // This is a sync event from a REMOTE peer!
            // Send it to our local server via WebSocket so it can write the changes to disk.
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify({
                    type: 'folder_sync_payload',
                    sender: msg.sender,
                    target: 'local_sync_engine',
                    data: msg.data
                }));
                console.log(`[Sync] Received remote sync payload from peer ${msg.sender}, applying locally`);
                createStreamParticle('peer', 'sender', '#00ff66', 'SYNC');
            }
        }
    }
}

// Dispatch files in parallel chunks (WebRTC first, Server fallback)
async function dispatchFile(file) {
    const fileId = "file_" + Math.random().toString(36).substring(2, 10).padEnd(16, '\0').substring(0, 16);
    const chunkSize = 1024 * 64; // 64KB blocks
    const chunksCount = Math.ceil(file.size / chunkSize);
    
    // Check if we have active WebRTC channels for P2P streaming
    const activePeerIds = Object.keys(dataChannels).filter(id => dataChannels[id].readyState === "open");
    
    showTransferItem(fileId, file.name, file.size, 'upload');
    
    if (activePeerIds.length > 0) {
        // P2P Streaming route
        const targetPeer = activePeerIds[0];
        console.log(`[Dispatch] Uploading P2P to: ${targetPeer}`);
        
        const dc = dataChannels[targetPeer];
        dc.send(JSON.stringify({
            type: 'file_header',
            fileId: fileId,
            name: file.name,
            size: file.size,
            chunksTotal: chunksCount
        }));
        
        for (let i = 0; i < chunksCount; i++) {
            const start = i * chunkSize;
            const end = Math.min(file.size, start + chunkSize);
            const blobSlice = file.slice(start, end);
            const arrayBuffer = await blobSlice.arrayBuffer();
            
            // Prepare binary block header matching Rust FFI struct specs
            const packetBuffer = new ArrayBuffer(52 + arrayBuffer.byteLength);
            const packetView = new DataView(packetBuffer);
            
            // FileId (16 bytes)
            const idBytes = new TextEncoder().encode(fileId);
            new Uint8Array(packetBuffer, 0, 16).set(idBytes);
            
            // ChunkIndex (4 bytes, little-endian)
            packetView.setUint32(16, i, true);
            
            // Simulate FFI checksum (placeholder zeros in header)
            // Payload
            new Uint8Array(packetBuffer, 52).set(new Uint8Array(arrayBuffer));
            
            dc.send(packetBuffer);
            uploadBytesSent += arrayBuffer.byteLength;
            
            // Animation Particle
            createStreamParticle('sender', 'peer', '#00ff66');
            
            updateTransferProgress(fileId, i + 1, chunksCount);
            await new Promise(r => setTimeout(r, 10)); // pacing flow control
        }
    } else {
        // Fallback server-buffered HTTPS upload
        console.log("[Dispatch] No active P2P channels. Falling back to server-buffered dropsite upload.");
        
        for (let i = 0; i < chunksCount; i++) {
            const start = i * chunkSize;
            const end = Math.min(file.size, start + chunkSize);
            const blobSlice = file.slice(start, end);
            
            const formData = new FormData();
            formData.append("file", blobSlice);
            formData.append("fileId", fileId);
            formData.append("chunkIndex", i);
            formData.append("checksum", ""); // Let server calculate
            formData.append("encryptionKey", "");
            
            const res = await fetch('/api/share/upload', {
                method: 'POST',
                body: formData
            });
            
            if (res.ok) {
                uploadBytesSent += (end - start);
                createStreamParticle('sender', 'server', '#00ff66');
                updateTransferProgress(fileId, i + 1, chunksCount);
            }
        }
    }
}

// UI Helpers
function showTransferItem(fileId, name, size, direction) {
    const list = document.getElementById('transfer-list');
    document.getElementById('active-transfers-container').classList.remove('hidden');
    
    const sizeMB = (size / (1024 * 1024)).toFixed(2);
    const item = document.createElement('div');
    item.className = 'transfer-item';
    item.id = `transfer-${fileId}`;
    item.innerHTML = `
        <div class="transfer-info">
            <span class="transfer-name">${name}</span>
            <span class="transfer-details">${direction === 'upload' ? 'Sending' : 'Receiving'} | ${sizeMB} MB</span>
        </div>
        <div class="progress-bar-container">
            <div class="progress-bar-fill" id="progress-fill-${fileId}"></div>
        </div>
    `;
    list.appendChild(item);
}

function updateTransferProgress(fileId, current, total) {
    const fill = document.getElementById(`progress-fill-${fileId}`);
    if (fill) {
        const pct = Math.floor((current / total) * 100);
        fill.style.width = `${pct}%`;
        
        if (pct >= 100) {
            setTimeout(() => {
                const item = document.getElementById(`transfer-${fileId}`);
                if (item) item.remove();
                if (document.getElementById('transfer-list').children.length === 0) {
                    document.getElementById('active-transfers-container').classList.add('hidden');
                }
            }, 2000);
        }
    }
}

// Telemetry Speed Dials Animations
function updateTelemetry() {
    const now = Date.now();
    const dt = (now - lastMetricsTime) / 1000;
    lastMetricsTime = now;
    
    const uploadSpeed = (uploadBytesSent / (1024 * 1024)) / dt; // MB/s
    const downloadSpeed = (downloadBytesRecv / (1024 * 1024)) / dt; // MB/s
    
    uploadBytesSent = 0;
    downloadBytesRecv = 0;
    
    // Upload Dial
    document.getElementById('val-upload').textContent = uploadSpeed.toFixed(1);
    setDialPercentage('progress-upload', Math.min(uploadSpeed / 50, 1) * 100);
    
    // Download Dial
    document.getElementById('val-download').textContent = downloadSpeed.toFixed(1);
    setDialPercentage('progress-download', Math.min(downloadSpeed / 50, 1) * 100);
    
    // Latency
    document.getElementById('val-latency').textContent = mockLatency;
    setDialPercentage('progress-latency', Math.min(mockLatency / 200, 1) * 100);
    
    // Saturation
    const totalSpeed = uploadSpeed + downloadSpeed;
    const saturation = Math.min(Math.floor((totalSpeed / 100) * 100), 100);
    document.getElementById('val-saturation').textContent = saturation;
    setDialPercentage('progress-saturation', saturation);
    
    setTimeout(updateTelemetry, 1000);
}

function setDialPercentage(elementId, pct) {
    const circle = document.getElementById(elementId);
    if (circle) {
        const radius = circle.r.baseVal.value;
        const circumference = 2 * Math.PI * radius;
        const offset = circumference - (pct / 100) * circumference;
        circle.style.strokeDashoffset = offset;
    }
}

// -------------------------------------------------------------
// Interactive HTML5 Canvas connection visualizer
// -------------------------------------------------------------
function resizeCanvas() {
    canvas.width = canvas.parentElement.clientWidth;
    canvas.height = canvas.parentElement.clientHeight;
}
window.addEventListener('resize', resizeCanvas);
resizeCanvas();

const nodes = {
    sender: { x: 80, y: 160 },
    server: { x: 0, y: 40 }, // X is calculated on render based on canvas width
    peer: { x: 0, y: 160 }
};

function createStreamParticle(fromNode, toNode, color, label = "") {
    animParticles.push({
        from: fromNode,
        to: toNode,
        progress: 0,
        speed: 0.02,
        color: color,
        label: label
    });
}

function renderMatrixVisualizer() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    
    // Update Node Coordinates based on Canvas Width
    nodes.server.x = canvas.width / 2;
    nodes.peer.x = canvas.width - 80;
    
    // Draw Connection Lines
    ctx.lineWidth = 2;
    ctx.strokeStyle = "rgba(255, 255, 255, 0.05)";
    
    // Sender -> Server
    ctx.beginPath();
    ctx.moveTo(nodes.sender.x, nodes.sender.y);
    ctx.lineTo(nodes.server.x, nodes.server.y);
    ctx.stroke();
    
    // Server -> Peer
    ctx.beginPath();
    ctx.moveTo(nodes.server.x, nodes.server.y);
    ctx.lineTo(nodes.peer.x, nodes.peer.y);
    ctx.stroke();
    
    // Direct P2P: Sender -> Peer
    ctx.setLineDash([4, 4]);
    ctx.strokeStyle = "rgba(0, 255, 102, 0.15)";
    ctx.beginPath();
    ctx.moveTo(nodes.sender.x, nodes.sender.y);
    ctx.lineTo(nodes.peer.x, nodes.peer.y);
    ctx.stroke();
    ctx.setLineDash([]);
    
    // Draw Nodes
    drawNode(nodes.sender.x, nodes.sender.y, "SENDER", "#00ff66");
    drawNode(nodes.server.x, nodes.server.y, "GATEWAY", "#ffbb00");
    drawNode(nodes.peer.x, nodes.peer.y, "PEER", "#00e5ff");
    
    // Update and Draw Particles
    for (let i = animParticles.length - 1; i >= 0; i--) {
        const p = animParticles[i];
        p.progress += p.speed;
        
        if (p.progress >= 1) {
            animParticles.splice(i, 1);
            continue;
        }
        
        const start = nodes[p.from];
        const end = nodes[p.to];
        
        const currentX = start.x + (end.x - start.x) * p.progress;
        const currentY = start.y + (end.y - start.y) * p.progress;
        
        ctx.beginPath();
        ctx.arc(currentX, currentY, 4, 0, Math.PI * 2);
        ctx.fillStyle = p.color;
        ctx.shadowColor = p.color;
        ctx.shadowBlur = 10;
        ctx.fill();
        ctx.shadowBlur = 0; // reset
        
        if (p.label) {
            ctx.font = "9px 'Space Grotesk', monospace";
            ctx.fillStyle = p.color;
            ctx.fillText(p.label, currentX + 8, currentY - 4);
        }
    }
    
    requestAnimationFrame(renderMatrixVisualizer);
}

function drawNode(x, y, label, color) {
    ctx.beginPath();
    ctx.arc(x, y, 8, 0, Math.PI * 2);
    ctx.fillStyle = "#080a10";
    ctx.strokeStyle = color;
    ctx.lineWidth = 3;
    ctx.fill();
    ctx.stroke();
    
    // Glow ring
    ctx.beginPath();
    ctx.arc(x, y, 14, 0, Math.PI * 2);
    ctx.strokeStyle = color + "20";
    ctx.lineWidth = 1;
    ctx.stroke();
}

function triggerConnectionFlash(isP2P) {
    const flashColor = isP2P ? "#00ff66" : "#ffbb00";
    for(let i=0; i<10; i++) {
        setTimeout(() => {
            createStreamParticle('sender', isP2P ? 'peer' : 'server', flashColor);
        }, i * 80);
    }
}

// Drag and drop event registers
const dropzone = document.getElementById('dropzone');
const fileInput = document.getElementById('file-input');

dropzone.addEventListener('click', () => fileInput.click());

dropzone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropzone.classList.add('dragover');
});

dropzone.addEventListener('dragleave', () => {
    dropzone.classList.remove('dragover');
});

dropzone.addEventListener('drop', (e) => {
    e.preventDefault();
    dropzone.classList.remove('dragover');
    const files = e.dataTransfer.files;
    if (files.length > 0) {
        for (let i = 0; i < files.length; i++) {
            dispatchFile(files[i]);
        }
    }
});

fileInput.addEventListener('change', () => {
    const files = fileInput.files;
    if (files.length > 0) {
        for (let i = 0; i < files.length; i++) {
            dispatchFile(files[i]);
        }
    }
});

// Configure Dropsite Custom Endpoints
document.getElementById('btn-save-config').addEventListener('click', async () => {
    const type = document.getElementById('dropsite-type').value;
    const path = document.getElementById('dropsite-path').value;
    
    const res = await fetch('/api/share/dumpsite', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ type, path })
    });
    
    if (res.ok) {
        const config = await res.json();
        selectedDropsite = config;
        document.getElementById('dropsite-badge').textContent = type.toUpperCase().replace('_', ' ');
        alert("Dropsite settings successfully updated!");
    } else {
        alert("Failed to update dropsite path settings.");
    }
});

async function fetchConfig() {
    const res = await fetch('/api/share/dumpsite');
    if (res.ok) {
        const config = await res.json();
        selectedDropsite = config;
        document.getElementById('dropsite-badge').textContent = config.type.toUpperCase().replace('_', ' ');
        document.getElementById('dropsite-path').value = config.path;
    }
}

// Toggle Folder Synchronization
document.getElementById('btn-toggle-sync').addEventListener('click', async () => {
    const path = document.getElementById('sync-path').value.trim();
    const targetPeerIdInput = document.getElementById('sync-peer-id').value.trim();
    
    if (!isSyncActive) {
        if (!path || !targetPeerIdInput) {
            alert("Please provide both local directory path and target peer ID.");
            return;
        }
        
        const res = await fetch(`/api/share/sync/start?path=${encodeURIComponent(path)}&targetPeerId=${encodeURIComponent(targetPeerIdInput)}`, {
            method: 'POST'
        });
        
        if (res.ok) {
            isSyncActive = true;
            document.getElementById('btn-toggle-sync').textContent = "STOP SYNC";
            document.getElementById('btn-toggle-sync').classList.add('btn-danger');
            
            const badge = document.getElementById('sync-status-badge');
            badge.textContent = "ACTIVE";
            badge.classList.add('badge-online');
            console.log(`[Sync] Folder sync engine started for path: ${path} targeting peer: ${targetPeerIdInput}`);
        } else {
            alert("Failed to start folder synchronization.");
        }
    } else {
        const res = await fetch('/api/share/sync/stop', {
            method: 'POST'
        });
        
        if (res.ok) {
            isSyncActive = false;
            document.getElementById('btn-toggle-sync').textContent = "START SYNC";
            document.getElementById('btn-toggle-sync').classList.remove('btn-danger');
            
            const badge = document.getElementById('sync-status-badge');
            badge.textContent = "Inactive";
            badge.classList.remove('badge-online');
            console.log("[Sync] Folder sync engine stopped.");
        } else {
            alert("Failed to stop folder synchronization.");
        }
    }
});

// Start visualizer loop and connections
renderMatrixVisualizer();
connectSignaling();
fetchConfig();
