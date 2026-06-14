# V-Codec: Design Specification for Zero-Algorithmic-Delay 1ms Audio

Traditional codecs like Opus, AAC, and MP3 are **frequency-domain codecs** that rely on Modified Discrete Cosine Transforms (MDCT) and overlapping windows. This introduces a hard mathematical limit: you cannot compress audio without a lookahead window (introducing algorithmic delay) and massive floating-point calculations.

To achieve a true **1.0ms packetization interval with zero algorithmic delay** and near-zero compute overhead, we must move to the **time-domain**. 

We propose **V-Codec** (Velocity ADPCM), a custom time-domain codec that encodes the differential amplitude changes between samples using an adaptive step-size quantizer.

---

## 📐 V-Codec Math & Bitstream Layout

1ms of audio at 48kHz Mono is exactly **48 samples**.
* **Input:** 48 samples of 16-bit linear PCM = **96 bytes**.
* **Output:** 48 samples compressed to 4-bit nibbles + predictor state = **26 bytes**.
* **Compression Ratio:** 3.7:1 (62% savings) with **0.0ms algorithmic delay**.

### 1. The Encoder State (4 Bytes)
Because time-domain prediction is history-dependent, the encoder and decoder maintain sync using a 4-byte predictor state prepended to every 1ms block:
* **Predictor Value (16-bit signed):** The decoded amplitude of the last sample (`short`).
* **Step Index (16-bit signed):** The current index into our step-size lookup table (clamped between 0 and 88).

### 2. The Step-Size Adaptation
We use an optimized step-size lookup table containing 89 step sizes ranging from 7 to 32767:
$$\text{StepSize} = \text{Table}[\text{StepIndex}]$$

For each sample, we calculate the difference between the actual PCM sample and our predicted value, quantize it into a 4-bit code ($0 \text{ to } 15$), and update the step index dynamically based on the magnitude of the change:
* If change is small, shrink the step size (closer to 0) to capture fine detail.
* If change is large, expand the step size (closer to 88) to prevent clipping on transients.

### 3. Bitstream Output Structure (26 Bytes total)
```
+-------------------+-----------------+-----------------------+
|  Predictor Value  |   Step Index    | 48 Nibbles (4-bit)    |
|     (2 bytes)     |    (2 bytes)    |      (24 bytes)       |
+-------------------+-----------------+-----------------------+
```

---

## 🏎️ The End-to-End Pipeline

```mermaid
graph LR
    A[WASAPI 1ms Capture] -->|96 bytes PCM| B[V-Codec Encoder]
    B -->|26 bytes V-Codec| C[UDP Packetizer]
    C -->|VUDP Relay| D[Server Relay]
    D -->|UDP Forward| E[UDP Receiver]
    E -->|26 bytes| F[V-Codec Decoder]
    F -->|96 bytes PCM| G[WASAPI 1ms Playout]
```

### 1. Capture (WASAPI Shared/Raw Mode)
* Buffer period configured to exactly **1ms** (or minimum supported by hardware, e.g. 3ms, with 1ms software segmenting).
* Collects 48 samples.

### 2. Compression (Sub-Microsecond Encode)
The V-Codec encoding algorithm does not require float operations, trigonometric functions, or memory allocations. It is pure integer addition and bit-shifting:
```csharp
// Loop 48 times per block
int diff = sample - predictedValue;
int code = 0;
int tempStep = stepSize;

if (diff < 0) {
    code = 8;
    diff = -diff;
}
if (diff >= tempStep) {
    code |= 4;
    diff -= tempStep;
}
tempStep >>= 1;
if (diff >= tempStep) {
    code |= 2;
    diff -= tempStep;
}
tempStep >>= 1;
if (diff >= tempStep) {
    code |= 1;
}
```
* **Encoding compute cost:** ~20 CPU instructions per sample = **<0.02ms** total CPU time per frame.

### 3. UDP Packetization
To keep headers minimal, we merge our `VudpAudioPacket` metadata directly with the V-Codec block:
```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VudpVcodecPacket
{
    public uint SequenceNumber;   // 4 bytes: Increments every 1ms
    public ushort Predictor;      // 2 bytes: Encoder state value
    public short StepIndex;       // 2 bytes: Encoder step index
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
    public byte[] CompressedData; // 24 bytes: 48 compressed samples
    // Total Packet Size = 32 bytes (Payload only!)
}
```
* Total size over the wire: **60 bytes** (32 bytes payload + 28 bytes UDP/IP headers).

---

## 📊 Comparison: Opus vs. V-Codec

| Metric | Opus (CELT Layer, 2.5ms) | V-Codec (V-ADPCM, 1ms) |
| --- | --- | --- |
| **Algorithmic Latency** | 2.5 ms (Lookahead & windowing) | **0.0 ms** (Instant sample-by-sample) |
| **Packetization Latency** | 2.5 ms | **1.0 ms** |
| **Encoding Time (CPU)** | ~0.15 ms (Float FFTs) | **<0.02 ms** (Int shifts/adds) |
| **Codec Complexity** | Extremely High (Opus FFI) | **Extremely Low** (Native C#) |
| **Audio Fidelity** | High (Excellent vocal tone) | Medium-High (Voice optimized, slight quantization hiss) |
| **Bandwidth (Payload)** | ~30-50 bytes / 2.5ms (150kbps) | **32 bytes / 1ms (256kbps)** |
