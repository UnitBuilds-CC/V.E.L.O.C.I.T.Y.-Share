// V.E.L.O.C.I.T.Y. SHARE - CLIENT APPLICATION ENGINE

// Unique random peer ID for this tab session (crypto-secure)
const myPeerId = "peer_" + (() => {
    const arr = new Uint8Array(6);
    (window.crypto || window.msCrypto).getRandomValues(arr);
    return Array.from(arr, b => b.toString(36).padStart(2, '0')).join('').substring(0, 8);
})();
document.getElementById('my-peer-id').textContent = myPeerId;
document.getElementById('hero-peer-id').textContent = myPeerId;

// â"€â"€ Toast notification system â"€â"€
function showToast(message, type = 'info', duration = 3000) {
    const container = document.getElementById('toast-container');
    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.textContent = message;
    container.appendChild(toast);
    // Trigger animation
    requestAnimationFrame(() => toast.classList.add('toast-show'));
    setTimeout(() => {
        toast.classList.remove('toast-show');
        toast.classList.add('toast-hide');
        setTimeout(() => toast.remove(), 300);
    }, duration);
}

// â”€â”€ Copy peer ID to clipboard â”€â”€
const peerBadgeBtn = document.getElementById('peer-badge-btn');
function copyPeerId() {
    navigator.clipboard.writeText(myPeerId).then(() => {
        showToast('Peer ID copied to clipboard!', 'success', 2000);
    }).catch(() => {
        showToast('Failed to copy peer ID', 'error');
    });
}
peerBadgeBtn.addEventListener('click', copyPeerId);
peerBadgeBtn.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); copyPeerId(); }
});

// â"€â"€ Hero copy button â"€â"€
document.getElementById('hero-copy-btn').addEventListener('click', copyPeerId);

// â"€â"€ Tab switching â"€â"€
document.querySelectorAll('.sidebar-tab').forEach(tab => {
    tab.addEventListener('click', () => {
        document.querySelectorAll('.sidebar-tab').forEach(t => {
            t.classList.remove('active');
            t.setAttribute('aria-selected', 'false');
        });
        document.querySelectorAll('.tab-panel').forEach(p => p.classList.remove('active'));
        tab.classList.add('active');
        tab.setAttribute('aria-selected', 'true');
        const panelId = 'panel-' + tab.dataset.tab;
        const panel = document.getElementById(panelId);
        if (panel) panel.classList.add('active');
    });
});

// â"€â"€ Safe HTML escaping to prevent XSS â"€â"€
function escapeHtml(str) {
    const div = document.createElement('div');
    div.appendChild(document.createTextNode(str));
    return div.innerHTML;
}

function parseNda(arrayBuffer) {
    const view = new DataView(arrayBuffer);
    if (arrayBuffer.byteLength < 52) {
        throw new Error("NDA document too small for header");
    }
    const magic = view.getUint32(0, true);
    if (magic !== 0x3141444E) {
        throw new Error("Invalid magic bytes in NDA document");
    }

    const tripleCount = view.getUint32(40, true);
    const commandCount = view.getUint32(44, true);
    const stringPoolOffset = view.getUint32(48, true);

    // Bounds validation
    if (tripleCount > 10000) throw new Error("NDA tripleCount exceeds safety limit");
    if (stringPoolOffset >= arrayBuffer.byteLength) throw new Error("NDA stringPoolOffset exceeds buffer");
    if (52 + tripleCount * 12 > stringPoolOffset) throw new Error("NDA triples region overflows into string pool");

    const stringPoolBytes = new Uint8Array(arrayBuffer, stringPoolOffset);

    function getString(offset) {
        if (offset === 0) return "";
        if (offset + 2 > stringPoolBytes.byteLength) return "";
        const len = new DataView(stringPoolBytes.buffer, stringPoolBytes.byteOffset + offset, 2).getUint16(0, true);
        if (offset + 2 + len > stringPoolBytes.byteLength) return "";
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
let transferHistory = [];   // Array of completed/failed transfers
let selectedDropsite = { type: 'local_nas', path: '' };
let isSyncActive = false;
let isAuthenticated = false;
let apiKey = localStorage.getItem('velocity_api_key') || '';
let latencyPingIntervalId = null; // Track latency ping interval for cleanup
let reconnectTimeoutId = null;    // Track reconnect timer for cancellation
let peerConnectionStates = {};     // peerId -> 'disconnected' | 'connecting' | 'connected'
let telemetryTimerId = null;       // Track telemetry setTimeout chain for cleanup

// â”€â”€ Authentication flow â”€â”€
async function checkAuthStatus() {
    try {
        const res = await fetch('/api/share/auth/status');
        if (res.ok) {
            const data = await res.json();
            if (!data.authRequired) {
                // No auth needed (dev mode)
                isAuthenticated = true;
                onAuthSuccess();
                return;
            }
            // Auth required - check if we have a stored key
            if (apiKey) {
                await verifyApiKey(apiKey);
            } else {
                showAuthBar();
            }
        }
    } catch (e) {
        console.error('[Auth] Failed to check auth status:', e);
        showAuthBar();
    }
}

async function verifyApiKey(key) {
    try {
        const res = await fetch('/api/share/auth/verify', {
            method: 'POST',
            headers: { 'X-API-Key': key }
        });
        if (res.ok) {
            isAuthenticated = true;
            apiKey = key;
            localStorage.setItem('velocity_api_key', key);
            onAuthSuccess();
        } else {
            isAuthenticated = false;
            showAuthBar('Invalid API key. Please try again.');
        }
    } catch (e) {
        showAuthBar('Network error while verifying API key.');
    }
}

function onAuthSuccess() {
    hideAuthBar();
    document.getElementById('auth-badge').classList.remove('hidden');
    connectSignaling();
    fetchConfig();
}

function showAuthBar(errorMsg = '') {
    document.getElementById('auth-bar').classList.remove('hidden');
    const msgEl = document.getElementById('auth-status-msg');
    if (errorMsg) {
        msgEl.textContent = errorMsg;
        msgEl.className = 'auth-status-msg error';
    } else {
        msgEl.textContent = '';
        msgEl.className = 'auth-status-msg';
    }
    // Pre-fill if we have a stored key
    if (apiKey) {
        document.getElementById('api-key-input').value = apiKey;
    }
}

function hideAuthBar() {
    document.getElementById('auth-bar').classList.add('hidden');
}

// Auth bar event listeners
document.getElementById('btn-auth-submit').addEventListener('click', async () => {
    const key = document.getElementById('api-key-input').value.trim();
    if (!key) {
        showAuthBar('Please enter an API key.');
        return;
    }
    const btn = document.getElementById('btn-auth-submit');
    btn.textContent = 'VERIFYING...';
    btn.disabled = true;
    await verifyApiKey(key);
    btn.textContent = 'AUTHENTICATE';
    btn.disabled = false;
});

document.getElementById('api-key-input').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') document.getElementById('btn-auth-submit').click();
});

// â”€â”€ Auth-aware fetch helper â”€â”€
function authFetch(url, options = {}) {
    if (!options.headers) options.headers = {};
    if (isAuthenticated && apiKey) {
        options.headers['X-API-Key'] = apiKey;
    }
    return fetch(url, options).then(res => {
        // Handle 401 - API key may have been rotated
        if (res.status === 401 && isAuthenticated) {
            isAuthenticated = false;
            localStorage.removeItem('velocity_api_key');
            apiKey = '';
            document.getElementById('auth-badge').classList.add('hidden');
            showAuthBar('Session expired. Please re-authenticate.');
            showToast('Authentication expired. Please re-enter your API key.', 'error', 5000);
        }
        return res;
    });
}

// Canvas configuration
const canvas = document.getElementById('matrix-canvas');
const ctx = canvas.getContext('2d');
let animParticles = [];

// Speed metrics
let uploadBytesSent = 0;
let downloadBytesRecv = 0;
let lastMetricsTime = Date.now();
let measuredLatency = 0; // Real WebSocket RTT in ms
let latencyPingSent = 0; // Timestamp of last ping

// Initialize WebSocket Signaling Handshake
let reconnectAttempts = 0;
const maxReconnectDelay = 30000;

function connectSignaling() {
    const wsScheme = window.location.protocol === 'https:' ? 'wss' : 'ws';
    let wsUrl = `${wsScheme}://${window.location.host}/ws/share?peerId=${myPeerId}`;
    // Include WS token if authenticated
    if (isAuthenticated && apiKey) {
        wsUrl += `&apiKey=${encodeURIComponent(apiKey)}`;
    }
    
    ws = new WebSocket(wsUrl);
    
    ws.onopen = () => {
        reconnectAttempts = 0; // Reset on successful connection
        if (reconnectTimeoutId) { clearTimeout(reconnectTimeoutId); reconnectTimeoutId = null; }
        document.getElementById('connection-status').textContent = "Matrix Handshake Secure";
        const dot = document.getElementById('connection-dot');
        dot.className = "status-dot online";
        // Start real latency measurement
        startLatencyPing();
        updateTelemetry();
    };
    
    ws.onclose = () => {
        reconnectAttempts++;
        const dot = document.getElementById('connection-dot');
        dot.className = "status-dot pulse";
        // Clean up latency ping interval to prevent memory leak
        if (latencyPingIntervalId) { clearInterval(latencyPingIntervalId); latencyPingIntervalId = null; }
        
        // Exponential backoff with cap
        const delay = Math.min(3000 * Math.pow(1.5, reconnectAttempts - 1), maxReconnectDelay);
        document.getElementById('connection-status').textContent = `Reconnecting... (attempt ${reconnectAttempts})`;
        
        if (reconnectAttempts >= 3) {
            showToast('Connection lost. Attempting to reconnect...', 'error', 5000);
        }
        
        reconnectTimeoutId = setTimeout(connectSignaling, delay);
    };
    
    ws.onerror = () => {
        document.getElementById('connection-status').textContent = "Connection error";
        showToast('WebSocket connection error.', 'error');
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
            try {
                msg = JSON.parse(event.data);
            } catch (e) {
                console.error("[WebSocket] Failed to parse JSON message:", e);
                return;
            }
        }

        if (msg.type === 'peer_list') {
            updatePeerList(msg.peers || []);
        } else if (msg.type === 'pong' && msg.t) {
            // Measure real WebSocket round-trip time
            measuredLatency = Math.round(performance.now() - msg.t);
        } else if (msg.sender && msg.sender !== myPeerId) {
            handleSignalingMessage(msg);
        }
    };
}

// Update the list of online available peers
function updatePeerList(peers) {
    const otherPeers = peers.filter(p => p !== myPeerId);
    const count = otherPeers.length;
    
    // Update all peer count displays
    document.getElementById('peer-count').textContent = count;
    const inlineCount = document.getElementById('peer-count-inline');
    if (inlineCount) inlineCount.textContent = `${count} online`;
    const fullCount = document.getElementById('peer-count-full');
    if (fullCount) fullCount.textContent = `${count} online`;
    
    // Update both peer list views (home + peers tab)
    ['peer-list', 'peer-list-full'].forEach(listId => {
        const listContainer = document.getElementById(listId);
        if (!listContainer) return;
        listContainer.innerHTML = '';
        
        if (count === 0) {
            listContainer.innerHTML = '<div class="empty-state"><svg viewBox="0 0 24 24" width="40" height="40" stroke="currentColor" stroke-width="1" fill="none" opacity="0.25"><path d="M17 21v-2a4 4 0 00-4-4H5a4 4 0 00-4 4v2"/><circle cx="9" cy="7" r="4"/></svg><p>No peers online</p><span>Open another tab or browser to start sharing</span></div>';
            return;
        }
        
        otherPeers.forEach(peerId => {
            const item = document.createElement('div');
            item.className = 'peer-item';
            
            const avatar = document.createElement('div');
            avatar.className = 'peer-avatar';
            avatar.innerHTML = '<svg viewBox="0 0 24 24" width="18" height="18" stroke="currentColor" stroke-width="2" fill="none"><circle cx="12" cy="8" r="4"/><path d="M20 21a8 8 0 10-16 0"/></svg>';
            
            const nameSpan = document.createElement('span');
            nameSpan.className = 'peer-item-name';
            nameSpan.textContent = peerId;
            
            const connectBtn = document.createElement('button');
            connectBtn.className = 'peer-action-btn';
            connectBtn.textContent = 'CONNECT';
            connectBtn.addEventListener('click', () => initiateP2PConnection(peerId));
            
            // Connection state indicator
            const statusEl = document.createElement('span');
            const pState = peerConnectionStates[peerId] || 'disconnected';
            statusEl.className = `peer-status ${pState}`;
            statusEl.textContent = pState === 'connected' ? 'CONNECTED' : pState === 'connecting' ? 'CONNECTING' : 'CONNECT';
            
            item.appendChild(avatar);
            item.appendChild(nameSpan);
            item.appendChild(statusEl);
            item.appendChild(connectBtn);
            listContainer.appendChild(item);
        });
    });
}

// Initiate RTCPeerConnection (P2P) WebRTC pipeline
async function initiateP2PConnection(targetId) {
    if (peerConnections[targetId]) return;
    
    peerConnectionStates[targetId] = 'connecting';
    refreshPeerStatusUI(targetId);
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
        peerConnectionStates[targetId] = 'connected';
        refreshPeerStatusUI(targetId);
        triggerConnectionFlash(true); // Flash green line on visualizer
    };
    
    dc.onclose = () => {
        console.log(`[P2P] Data channel CLOSED with peer: ${targetId}`);
        peerConnectionStates[targetId] = 'disconnected';
        refreshPeerStatusUI(targetId);
    };
    
    dc.onmessage = (event) => {
        handleIncomingData(targetId, event.data);
    };
}

// Process data chunks arriving over the Data Channel
function handleIncomingData(senderId, rawData) {
    if (typeof rawData === 'string') {
        let meta;
        try {
            meta = JSON.parse(rawData);
        } catch (e) {
            console.error('[P2P] Failed to parse data channel message:', e);
            return;
        }
        if (meta.type === 'file_header') {
            activeTransfers[meta.fileId] = {
                name: meta.name,
                size: meta.size,
                chunksTotal: meta.chunksTotal,
                chunksReceived: 0,
                buffer: new Array(meta.chunksTotal),
                startTime: Date.now(),
                direction: 'download'
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
            
            updateTransferProgress(fileId, fileState.chunksReceived, fileState.chunksTotal, fileState.startTime, fileState.size);
            
            if (fileState.chunksReceived === fileState.chunksTotal) {
                // Reassemble file
                const blob = new Blob(fileState.buffer);
                const url = URL.createObjectURL(blob);
                triggerFileDownload(fileState.name, url);
                addToHistory(fileId, fileState.name, fileState.size, 'completed');
                sendNotification('Download complete', fileState.name);
                renderHistory();
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
    // Revoke blob URL after a short delay to free memory
    setTimeout(() => URL.revokeObjectURL(url), 10000);
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
    const fileId = "file_" + (() => {
        const arr = new Uint8Array(16);
        (window.crypto || window.msCrypto).getRandomValues(arr);
        return Array.from(arr, b => b.toString(36).padStart(2, '0')).join('').substring(0, 16).padEnd(16, '\0');
    })();
    const chunkSize = 1024 * 64; // 64KB blocks
    const chunksCount = Math.ceil(file.size / chunkSize);
    
    // â”€â”€ File size validation â”€â”€
    if (file.size > 50 * 1024 * 1024) {
        showToast(`File "${file.name}" exceeds 50 MB limit.`, 'error');
        return;
    }
    
    // Check if we have active WebRTC channels for P2P streaming
    const activePeerIds = Object.keys(dataChannels).filter(id => dataChannels[id].readyState === "open");
    
    // Track upload start time for ETA calculation
    const uploadStartTime = Date.now();
    
    showTransferItem(fileId, file.name, file.size, 'upload');
    
    try {
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
                
                updateTransferProgress(fileId, i + 1, chunksCount, uploadStartTime, file.size);
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
                
                const res = await authFetch('/api/share/upload', {
                    method: 'POST',
                    body: formData
                });
                
                if (res.ok) {
                    uploadBytesSent += (end - start);
                    createStreamParticle('sender', 'server', '#00ff66');
                    updateTransferProgress(fileId, i + 1, chunksCount, uploadStartTime, file.size);
                } else {
                    throw new Error(`Upload chunk ${i} failed: ${res.status}`);
                }
            }
        }
        
        showToast(`"${file.name}" sent successfully!`, 'success', 2000);
        addToHistory(fileId, file.name, file.size, 'completed');
        sendNotification('Upload complete', file.name);
        renderHistory();
    } catch (err) {
        console.error(`[Dispatch] Transfer failed for ${file.name}:`, err);
        showToast(`Transfer failed: ${file.name}`, 'error', 5000);
        addToHistory(fileId, file.name, file.size, 'failed');
        sendNotification('Transfer failed', file.name);
        renderHistory();
        // Mark transfer as failed in both views
        ['', 'full-'].forEach(prefix => {
            const transferItem = document.getElementById(`transfer-${prefix}${fileId}`);
            if (transferItem) {
                const details = transferItem.querySelector('.transfer-details');
                if (details) details.textContent = 'FAILED';
                const fill = transferItem.querySelector('.progress-bar-fill');
                if (fill) fill.style.background = 'linear-gradient(90deg, #ff3366, #ff0055)';
            }
        });
    }
}

// UI Helpers
function showTransferItem(fileId, name, size, direction) {
    document.getElementById('active-transfers-container').style.display = '';
    
    const sizeMB = (size / (1024 * 1024)).toFixed(2);
    
    // Create transfer item element
    function createTransferEl() {
        const item = document.createElement('div');
        item.className = 'transfer-item';
        item.id = `transfer-${fileId}`;
        
        const infoDiv = document.createElement('div');
        infoDiv.className = 'transfer-info';
        
        const nameSpan = document.createElement('span');
        nameSpan.className = 'transfer-name';
        nameSpan.textContent = name;
        
        const detailsSpan = document.createElement('span');
        detailsSpan.className = 'transfer-details';
        detailsSpan.textContent = `${direction === 'upload' ? 'Sending' : 'Receiving'} | ${sizeMB} MB`;
        
        infoDiv.appendChild(nameSpan);
        infoDiv.appendChild(detailsSpan);
        
        const progressContainer = document.createElement('div');
        progressContainer.className = 'progress-bar-container';
        
        const progressFill = document.createElement('div');
        progressFill.className = 'progress-bar-fill';
        progressFill.id = `progress-fill-${fileId}`;
        progressContainer.appendChild(progressFill);
        
        const cancelBtn = document.createElement('button');
        cancelBtn.className = 'transfer-cancel';
        cancelBtn.title = 'Cancel transfer';
        cancelBtn.innerHTML = '<svg viewBox="0 0 24 24" width="14" height="14" stroke="currentColor" stroke-width="2" fill="none"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>';
        cancelBtn.addEventListener('click', () => cancelTransfer(fileId));
        
        item.appendChild(infoDiv);
        item.appendChild(progressContainer);
        item.appendChild(cancelBtn);
        return item;
    }
    
    // Add to home preview list
    const list = document.getElementById('transfer-list');
    list.appendChild(createTransferEl());
    
    // Also add to full transfers tab
    const listFull = document.getElementById('transfer-list-full');
    if (listFull) {
        const fullItem = createTransferEl();
        fullItem.id = `transfer-full-${fileId}`;
        fullItem.querySelector('.progress-bar-fill').id = `progress-fill-full-${fileId}`;
        listFull.appendChild(fullItem);
        // Clear empty state if present
        const emptyState = listFull.querySelector('.empty-state');
        if (emptyState) emptyState.remove();
    }
}

function updateTransferProgress(fileId, current, total, startTime = 0, totalBytes = 0) {
    const pct = Math.floor((current / total) * 100);
    
    // Calculate ETA
    let etaText = '';
    if (startTime > 0 && current > 0 && current < total) {
        const elapsed = (Date.now() - startTime) / 1000;
        const speed = (totalBytes * (current / total)) / elapsed; // bytes/sec
        const remaining = totalBytes - (totalBytes * (current / total));
        const etaSec = speed > 0 ? Math.round(remaining / speed) : 0;
        if (etaSec < 60) etaText = `${etaSec}s remaining`;
        else if (etaSec < 3600) etaText = `${Math.floor(etaSec / 60)}m ${etaSec % 60}s remaining`;
        else etaText = `${Math.floor(etaSec / 3600)}h ${Math.floor((etaSec % 3600) / 60)}m remaining`;
        
        const speedMB = (speed / (1024 * 1024)).toFixed(1);
        etaText = `${speedMB} MB/s \u00B7 ${etaText}`;
    }
    
    // Update both home and full transfer views
    ['', 'full-'].forEach(prefix => {
        const fill = document.getElementById(`progress-fill-${prefix}${fileId}`);
        if (fill) fill.style.width = `${pct}%`;
        
        // Update ETA in transfer details
        const item = document.getElementById(`transfer-${prefix}${fileId}`);
        if (item) {
            let etaEl = item.querySelector('.transfer-eta');
            if (etaText) {
                if (!etaEl) {
                    etaEl = document.createElement('span');
                    etaEl.className = 'transfer-eta';
                    const details = item.querySelector('.transfer-details');
                    if (details) details.appendChild(etaEl);
                }
                etaEl.textContent = etaText;
            } else if (etaEl) {
                etaEl.remove();
            }
        }
    });
    
    if (pct >= 100) {
        setTimeout(() => {
            // Remove from both views
            const item = document.getElementById(`transfer-${fileId}`);
            if (item) item.remove();
            const fullItem = document.getElementById(`transfer-full-${fileId}`);
            if (fullItem) fullItem.remove();
            if (document.getElementById('transfer-list').children.length === 0) {
                document.getElementById('active-transfers-container').style.display = 'none';
            }
        }, 2000);
    }
}

// â”€â”€ Real WebSocket latency measurement â”€â”€
function startLatencyPing() {
    if (latencyPingIntervalId) { clearInterval(latencyPingIntervalId); }
    latencyPingIntervalId = setInterval(() => {
        if (ws && ws.readyState === WebSocket.OPEN) {
            latencyPingSent = performance.now();
            ws.send(JSON.stringify({ type: 'ping', t: latencyPingSent }));
        }
    }, 5000); // Ping every 5 seconds
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
    const valUploadDial = document.getElementById('val-upload-dial');
    if (valUploadDial) valUploadDial.textContent = uploadSpeed.toFixed(1);
    setDialPercentage('progress-upload', Math.min(uploadSpeed / 50, 1) * 100);
    
    // Download Dial
    document.getElementById('val-download').textContent = downloadSpeed.toFixed(1);
    const valDownloadDial = document.getElementById('val-download-dial');
    if (valDownloadDial) valDownloadDial.textContent = downloadSpeed.toFixed(1);
    setDialPercentage('progress-download', Math.min(downloadSpeed / 50, 1) * 100);
    
    // Latency (real measured RTT)
    document.getElementById('val-latency').textContent = measuredLatency;
    const valLatencyDial = document.getElementById('val-latency-dial');
    if (valLatencyDial) valLatencyDial.textContent = measuredLatency;
    setDialPercentage('progress-latency', Math.min(measuredLatency / 200, 1) * 100);
    
    // Saturation
    const totalSpeed = uploadSpeed + downloadSpeed;
    const saturation = Math.min(Math.floor((totalSpeed / 100) * 100), 100);
    document.getElementById('val-saturation').textContent = saturation;
    setDialPercentage('progress-saturation', saturation);
    
    telemetryTimerId = setTimeout(updateTelemetry, 1000);
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

let lastFrameTime = 0;
const idleFrameInterval = 2000; // Redraw every 2s when idle
const activeFrameInterval = 16;  // ~60fps when particles active

function renderMatrixVisualizer(timestamp = 0) {
    const hasParticles = animParticles.length > 0;
    const interval = hasParticles ? activeFrameInterval : idleFrameInterval;
    
    if (timestamp - lastFrameTime < interval) {
        requestAnimationFrame(renderMatrixVisualizer);
        return;
    }
    lastFrameTime = timestamp;
    
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
            ctx.font = "9px 'Inter', monospace";
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
dropzone.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fileInput.click(); }
});

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
    fileInput.value = ''; // Reset so same file can be re-selected
});

// Configure Dropsite Custom Endpoints
document.getElementById('btn-save-config').addEventListener('click', async () => {
    const btn = document.getElementById('btn-save-config');
    const originalText = btn.textContent;
    btn.textContent = 'SAVING...';
    btn.disabled = true;
    
    try {
        const type = document.getElementById('dropsite-type').value;
        const path = document.getElementById('dropsite-path').value;
        
        const res = await authFetch('/api/share/dumpsite', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ type, path })
        });
        
        if (res.ok) {
            const config = await res.json();
            selectedDropsite = config;
            document.getElementById('dropsite-badge').textContent = type.toUpperCase().replace('_', ' ');
            showToast('Dropsite settings saved successfully!', 'success');
        } else {
            const errorText = await res.text();
            showToast(`Failed to update dropsite: ${errorText}`, 'error');
        }
    } catch (e) {
        showToast('Network error while saving dropsite settings.', 'error');
    } finally {
        btn.textContent = originalText;
        btn.disabled = false;
    }
});

async function fetchConfig() {
    const res = await authFetch('/api/share/dumpsite');
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
            showToast('Please provide both local directory path and target peer ID.', 'error');
            return;
        }
        
        const res = await authFetch(`/api/share/sync/start?path=${encodeURIComponent(path)}&targetPeerId=${encodeURIComponent(targetPeerIdInput)}`, {
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
            showToast('Failed to start folder synchronization.', 'error');
        }
    } else {
        const res = await authFetch('/api/share/sync/stop', {
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
            showToast('Failed to stop folder synchronization.', 'error');
        }
    }
});

// Start visualizer loop and auth-gated connections
renderMatrixVisualizer();
checkAuthStatus();

// â”€â”€ Cancel active transfer â”€â”€
function cancelTransfer(fileId) {
    delete activeTransfers[fileId];
    ['', 'full-'].forEach(prefix => {
        const el = document.getElementById(`transfer-${prefix}${fileId}`);
        if (el) el.remove();
    });
    if (document.getElementById('transfer-list').children.length === 0) {
        document.getElementById('active-transfers-container').style.display = 'none';
    }
    showToast('Transfer cancelled', 'info', 2000);
}

// â”€â”€ Refresh peer status indicator for a specific peer â”€â”€
function refreshPeerStatusUI(targetId) {
    const state = peerConnectionStates[targetId] || 'disconnected';
    ['', '-full'].forEach(suffix => {
        const listEl = document.getElementById(`peer-list${suffix}`);
        if (!listEl) return;
        const items = listEl.querySelectorAll('.peer-item');
        items.forEach(item => {
            const nameEl = item.querySelector('.peer-item-name');
            if (nameEl && nameEl.textContent === targetId) {
                let statusEl = item.querySelector('.peer-status');
                if (!statusEl) {
                    statusEl = document.createElement('span');
                    statusEl.className = 'peer-status';
                    const btn = item.querySelector('.peer-action-btn');
                    if (btn) item.insertBefore(statusEl, btn);
                    else item.appendChild(statusEl);
                }
                statusEl.className = `peer-status ${state}`;
                statusEl.textContent = state === 'connected' ? 'CONNECTED' : state === 'connecting' ? 'CONNECTING' : 'CONNECT';
            }
        });
    });
}

// â”€â”€ Document-level drag & drop overlay â”€â”€
let dragCounter = 0;
const dragOverlay = document.getElementById('drag-overlay');

document.addEventListener('dragenter', (e) => {
    e.preventDefault();
    if (e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files')) {
        dragCounter++;
        dragOverlay.classList.add('active');
    }
});

document.addEventListener('dragleave', (e) => {
    e.preventDefault();
    dragCounter--;
    if (dragCounter <= 0) {
        dragCounter = 0;
        dragOverlay.classList.remove('active');
    }
});

document.addEventListener('dragover', (e) => {
    e.preventDefault();
});

document.addEventListener('drop', (e) => {
    e.preventDefault();
    dragCounter = 0;
    dragOverlay.classList.remove('active');
    // If dropped outside the dropzone, dispatch files
    if (!dropzone.contains(e.target) && e.dataTransfer.files.length > 0) {
        for (let i = 0; i < e.dataTransfer.files.length; i++) {
            dispatchFile(e.dataTransfer.files[i]);
        }
        // Switch to home tab so user sees the transfer
        const homeTab = document.querySelector('.sidebar-tab[data-tab="home"]');
        if (homeTab && !homeTab.classList.contains('active')) homeTab.click();
    }
});

// â”€â”€ Transfer History â”€â”€
function addToHistory(fileId, fileName, fileSize, status) {
    transferHistory.unshift({
        fileId, fileName, fileSize, status,
        timestamp: new Date().toISOString()
    });
    if (transferHistory.length > 50) transferHistory.pop();
}

// â”€â”€ Desktop Notifications â”€â”€
function requestNotificationPermission() {
    if ('Notification' in window && Notification.permission === 'default') {
        Notification.requestPermission();
    }
}
function sendNotification(title, body) {
    if ('Notification' in window && Notification.permission === 'granted') {
        new Notification(title, { body: body });
    }
}
requestNotificationPermission();

// ── Peer connect/disconnect notifications ──
const origDcOnopen = null;
const origDcOnclose = null;
// Wrap data channel setup to add notifications
const origSetupDataChannel = setupDataChannel;
setupDataChannel = function(targetId, dc) {
    origSetupDataChannel(targetId, dc);
    const prevOnopen = dc.onopen;
    const prevOnclose = dc.onclose;
    dc.onopen = function() {
        if (prevOnopen) prevOnopen();
        sendNotification('Peer connected', targetId + ' is now online');
    };
    dc.onclose = function() {
        if (prevOnclose) prevOnclose();
        sendNotification('Peer disconnected', targetId + ' went offline');
    };
};

// â”€â”€ Keyboard Shortcuts â”€â”€
document.addEventListener('keydown', (e) => {
    if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT') return;
    if (e.key === '?' || e.key === '/') {
        e.preventDefault();
        showToast('Shortcuts: Ctrl+O=Open, 1-4=Tabs, Esc=Cancel, ?=Help', 'info', 5000);
    }
    if ((e.ctrlKey || e.metaKey) && e.key === 'o') {
        e.preventDefault();
        document.getElementById('file-input').click();
    }
    if (e.key === 'Escape') {
        const ids = Object.keys(activeTransfers);
        if (ids.length > 0) { cancelTransfer(ids[0]); showToast('Transfer cancelled', 'info', 2000); }
    }
    const tabKeys = { '1': 'home', '2': 'transfers', '3': 'peers', '4': 'settings' };
    if (tabKeys[e.key]) {
        e.preventDefault();
        const tab = document.querySelector('.sidebar-tab[data-tab="' + tabKeys[e.key] + '"]');
        if (tab) tab.click();
    }
});

// â”€â”€ QR Code Generator (visual fingerprint) â”€â”€
function generateQRCode(text, size) {
    const canvas = document.createElement('canvas');
    canvas.width = size; canvas.height = size;
    const ctx = canvas.getContext('2d');
    const modules = 21, cellSize = size / modules;
    let hash = 0;
    for (let i = 0; i < text.length; i++) hash = ((hash << 5) - hash + text.charCodeAt(i)) | 0;
    ctx.fillStyle = '#0d1018'; ctx.fillRect(0, 0, size, size); ctx.fillStyle = '#00ff66';
    function drawFinder(x, y) {
        for (let r = 0; r < 7; r++) for (let c = 0; c < 7; c++) {
            if (r===0||r===6||c===0||c===6||(r>=2&&r<=4&&c>=2&&c<=4))
                ctx.fillRect((x+c)*cellSize,(y+r)*cellSize,cellSize,cellSize);
        }
    }
    drawFinder(0,0); drawFinder(modules-7,0); drawFinder(0,modules-7);
    const seed = Math.abs(hash);
    for (let r=8;r<modules-8;r++) for (let c=8;c<modules;c++) {
        if (((seed>>((r*modules+c)%30))&1)||((r+c)%3===0))
            ctx.fillRect(c*cellSize,r*cellSize,cellSize-0.5,cellSize-0.5);
    }
    return canvas;
}
(function addQRToHero() {
    const heroCard = document.querySelector('.hero-id-card');
    if (!heroCard) return;
    const qrWrap = document.createElement('div');
    qrWrap.style.cssText = 'margin-top:12px;display:flex;justify-content:center';
    const qr = generateQRCode(myPeerId, 80);
    qr.style.borderRadius = '8px'; qr.title = 'Peer ID: ' + myPeerId;
    qrWrap.appendChild(qr); heroCard.appendChild(qrWrap);
})();

// ── Transfer History Rendering ──
function formatFileSize(bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}

function renderHistory() {
    const section = document.getElementById('history-section');
    const list = document.getElementById('history-list');
    if (!section || !list) return;
    
    if (transferHistory.length === 0) {
        section.style.display = 'none';
        list.innerHTML = '';
        return;
    }
    
    section.style.display = '';
    list.innerHTML = '';
    
    transferHistory.forEach((item, idx) => {
        const el = document.createElement('div');
        el.className = 'history-item';
        
        const iconClass = item.status === 'completed' ? 'completed' : item.status === 'failed' ? 'failed' : 'cancelled';
        const iconSymbol = item.status === 'completed' ? '\u2713' : item.status === 'failed' ? '\u2717' : '\u2014';
        
        const time = new Date(item.timestamp);
        const timeStr = time.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        
        el.innerHTML = `
            <div class="history-icon ${iconClass}">${iconSymbol}</div>
            <div class="history-info">
                <div class="history-name">${escapeHtml(item.fileName)}</div>
                <div class="history-meta">${formatFileSize(item.fileSize)} \u00B7 ${timeStr} \u00B7 ${item.status}</div>
            </div>
            ${item.status === 'completed' ? '<div class="history-actions"><button class="history-share-btn" data-idx="' + idx + '">SHARE LINK</button></div>' : ''}
        `;
        list.appendChild(el);
    });
    
    // Attach share button handlers
    list.querySelectorAll('.history-share-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const idx = parseInt(btn.dataset.idx);
            const item = transferHistory[idx];
            if (item) openShareModal(item);
        });
    });
}

// Clear history button
document.getElementById('btn-clear-history').addEventListener('click', () => {
    transferHistory = [];
    renderHistory();
    showToast('History cleared', 'info', 2000);
});

// ── Share Link Modal ──
let shareModalFileId = null;

function openShareModal(historyItem) {
    shareModalFileId = historyItem.fileId;
    document.getElementById('share-modal-filename').textContent = historyItem.fileName + ' (' + formatFileSize(historyItem.fileSize) + ')';
    document.getElementById('share-password').value = '';
    document.getElementById('share-expiry').value = '24';
    document.getElementById('share-max-downloads').value = '100';
    document.getElementById('share-result').classList.add('hidden');
    document.getElementById('share-url-output').value = '';
    document.getElementById('share-create-btn').disabled = false;
    document.getElementById('share-create-btn').textContent = 'Create Link';
    
    const modal = document.getElementById('share-modal');
    modal.classList.remove('hidden');
    modal.setAttribute('aria-hidden', 'false');
}

function closeShareModal() {
    const modal = document.getElementById('share-modal');
    modal.classList.add('hidden');
    modal.setAttribute('aria-hidden', 'true');
    shareModalFileId = null;
}

document.getElementById('share-modal-close').addEventListener('click', closeShareModal);
document.getElementById('share-cancel-btn').addEventListener('click', closeShareModal);
document.getElementById('share-modal').addEventListener('click', (e) => {
    if (e.target === e.currentTarget) closeShareModal();
});

document.getElementById('share-create-btn').addEventListener('click', async () => {
    if (!shareModalFileId) return;
    
    const btn = document.getElementById('share-create-btn');
    btn.disabled = true;
    btn.textContent = 'Creating...';
    
    const expiryHours = parseInt(document.getElementById('share-expiry').value);
    const password = document.getElementById('share-password').value.trim() || undefined;
    const maxDownloads = parseInt(document.getElementById('share-max-downloads').value);
    
    try {
        const res = await authFetch('/api/share/link', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                fileId: shareModalFileId,
                fileName: document.getElementById('share-modal-filename').textContent.split(' (')[0],
                expiryHours,
                password,
                maxDownloads
            })
        });
        
        if (res.ok) {
            const data = await res.json();
            document.getElementById('share-url-output').value = data.shareUrl;
            document.getElementById('share-result').classList.remove('hidden');
            showToast('Share link created!', 'success', 2000);
        } else {
            const err = await res.json().catch(() => ({ error: 'Unknown error' }));
            showToast(err.error || 'Failed to create share link', 'error');
        }
    } catch (e) {
        showToast('Network error creating share link', 'error');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Create Link';
    }
});

document.getElementById('share-copy-btn').addEventListener('click', () => {
    const urlInput = document.getElementById('share-url-output');
    urlInput.select();
    navigator.clipboard.writeText(urlInput.value).then(() => {
        showToast('Link copied to clipboard!', 'success', 2000);
    }).catch(() => {
        showToast('Failed to copy', 'error');
    });
});

// ── Clean up connections on page unload ──
window.addEventListener('beforeunload', () => {
    // Close WebSocket
    if (ws) {
        try { ws.close(1000, 'Page unload'); } catch {}
    }
    // Close all WebRTC peer connections
    Object.values(peerConnections).forEach(pc => {
        try { pc.close(); } catch {}
    });
    // Clear timers
    if (latencyPingIntervalId) clearInterval(latencyPingIntervalId);
    if (reconnectTimeoutId) clearTimeout(reconnectTimeoutId);
    if (telemetryTimerId) clearTimeout(telemetryTimerId);
});
