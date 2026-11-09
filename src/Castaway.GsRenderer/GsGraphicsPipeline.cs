using System.Runtime.InteropServices;
using Castaway.Core;
using Castaway.GsRenderer.Gs;
using Castaway.GsRenderer.OpenGl;
using Castaway.GsRenderer.Vif;
using Castaway.GsRenderer.Vu1;
using Castaway.Runtime;

namespace Castaway.GsRenderer;

/// <summary>
/// Unified Phase 4 Graphics Pipeline connecting DMA Channels (VIF0 Ch0, VIF1 Ch1, GIF Ch2),
/// VIF0/VIF1 Unpackers, the VU1 Microcode AOT/Hash Recompiler, the 4 MB swizzled GS Processor,
/// and the Silk.NET OpenGL 4.6 DSA + Compute Shader Renderer.
/// </summary>
public sealed unsafe class GsGraphicsPipeline : IDisposable
{
    private GCHandle _vu1DataPin;
    private GCHandle _vu1MicroPin;

    public VifUnit Vif0 { get; }
    public VifUnit Vif1 { get; }
    public Vu1State Vu1 { get; }
    public Vu1Recompiler Vu1Compiler { get; }
    public GsProcessor Gs { get; }
    public GsOpenGlRenderer OpenGl { get; }

    public GsPrimitiveBatch BackBatch { get; private set; } = new();
    public GsPrimitiveBatch FrontBatch { get; private set; } = new();
    public byte[] FrontVram { get; } = new byte[GsSwizzle.VramByteSize];
    private readonly object _batchLock = new();
    public object BatchLock => _batchLock;

    public GsRendererMode Mode
    {
        get => Gs.Mode;
        set => Gs.Mode = value;
    }

    public GsGraphicsPipeline()
    {
        Vu1 = new Vu1State();
        _vu1DataPin = GCHandle.Alloc(Vu1.DataMem, GCHandleType.Pinned);
        _vu1MicroPin = GCHandle.Alloc(Vu1.MicroMem, GCHandleType.Pinned);
        RuntimeDispatcher.Vu1Data = (byte*)_vu1DataPin.AddrOfPinnedObject();
        RuntimeDispatcher.Vu1Micro = (byte*)_vu1MicroPin.AddrOfPinnedObject();

        Vu1Compiler = new Vu1Recompiler();
        Gs = new GsProcessor();
        Gs.ActiveBatch = BackBatch;
        OpenGl = new GsOpenGlRenderer();

        Vif0 = new VifUnit(isVif1: false);
        Vif1 = new VifUnit(isVif1: true, microMem: Vu1.MicroMem, dataMem: Vu1.DataMem);

        // Wire VIF1 -> VU1 Recompiler & GS Path 2 (DIRECT)
        Vif1.OnMicroMemoryModified = Vu1Compiler.InvalidateMicroMemHash;
        Vif1.OnDirectGifData = data => Gs.SubmitGifPacket(data, "VIF1_DIRECT");
        Vif1.OnMicroProgramCall = (startByteAddr, isContinue) =>
        {
            Vu1.Top = Vif1.Top;
            Vu1.Itop = Vif1.Itop;
            ushort entryAddr = isContinue ? Vu1.Tpc : startByteAddr;
            Vu1Compiler.Execute(Vu1, entryAddr);
        };

        // Wire VU1 XGKICK -> GS Path 1
        Vu1.OnXgKick = data => Gs.SubmitGifPacket(data, "VU1_XGKICK");
    }

    /// <summary>
    /// Atomically swaps the front and back primitive batches at VBlank / frame presentation.
    /// </summary>
    public void SwapBatches()
    {
        lock (_batchLock)
        {
            var temp = FrontBatch;
            FrontBatch = BackBatch;
            BackBatch = temp;
            BackBatch.Reset();
            Gs.ActiveBatch = BackBatch;

            // Snapshot VRAM for thread-safe host presentation
            Buffer.BlockCopy(Gs.Vram, 0, FrontVram, 0, GsSwizzle.VramByteSize);
        }
    }

    /// <summary>
    /// Attaches the Phase 4 Graphics Pipeline to the Phase 3 Runtime DMA Controller.
    /// </summary>
    public void AttachToRuntime(CastawayRuntime runtime)
    {
        AttachToDma(runtime.Dma);
    }

    public void AttachToDma(DmaController dma)
    {
        dma.OnVif0Data = Vif0.ProcessData;
        dma.OnVif1Data = Vif1.ProcessData;
        dma.OnGifData = data => Gs.SubmitGifPacket(data, "DMA_GIF");
    }

    public void Dispose()
    {
        if (_vu1DataPin.IsAllocated)
        {
            _vu1DataPin.Free();
            RuntimeDispatcher.Vu1Data = null;
        }
        if (_vu1MicroPin.IsAllocated)
        {
            _vu1MicroPin.Free();
            RuntimeDispatcher.Vu1Micro = null;
        }
        OpenGl.Dispose();
    }
}
