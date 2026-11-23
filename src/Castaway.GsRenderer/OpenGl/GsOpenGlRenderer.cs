using System.Runtime.InteropServices;
using Castaway.GsRenderer.Gs;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Castaway.GsRenderer.OpenGl;

[StructLayout(LayoutKind.Sequential)]
public struct GsComputeDisplayParams
{
    public int FbpBlock;
    public int FbwPixels;
    public int Fpsm;
    public int CbpBlock;
    public int Cpsm;
    public int DisplayWidth;
    public int DisplayHeight;
    public int ScaleMultiplier;
}

[StructLayout(LayoutKind.Sequential)]
public struct GsGlDrawParams
{
    public int Tme;
    public int Fst;
    public int Tw;
    public int Th;
    public int Tcc;
    public int Tfx;
    public int Ate;
    public int Atst;
    public int Aref;
    public int Afail;
    public int Zte;
    public int Wms;
    public int Wmt;
    public int MinU;
    public int MaxU;
    public int MinV;
    public int MaxV;
    public int LinearFilter;
    public int AfailMode;
    public int Fba;
    public int Date;
    public int Datm;
    private int _pad0;
    private int _pad1;
}

/// <summary>
/// Silk.NET OpenGL 4.6 Core Profile renderer for the PS2 Graphics Synthesizer (GS).
/// Strictly uses Direct State Access (DSA) and Compute Shaders to synchronize and unswizzle
/// the 4 MB virtual GS VRAM SSBO with hardware-accurate page/block/column swizzling and CSM1 CLUT lookups,
/// and hardware rasterization for high-throughput primitive batches.
/// </summary>
public sealed unsafe class GsOpenGlRenderer : IDisposable
{
    private GL? _gl;
    private uint _vramSsbo;
    private uint _paramsUbo;
    private uint _displayTexture;
    private uint _emptyVao;
    private uint _unswizzleComputeProgram;
    private uint _presentGraphicsProgram;

    // Hardware primitive rasterization resources (DSA)
    private uint _primitiveVbo;
    private uint _primitiveEbo;
    private uint _primitiveVao;
    private uint _drawParamsUbo;
    private uint _primitiveProgram;
    private uint _primitiveMsaaProgram;
    private uint _primitiveMsaaDateProgram;
    private uint _dummyWhiteTex;
    private uint _batchFbo;
    private uint _batchColorTex;
    private uint _batchDepthRbo;
    private int _batchFboWidth;
    private int _batchFboHeight;
    private int _batchFboSamples;
    private int _requestedMsaaSamples = 1;

    // FPS overlay resources (DSA)
    private uint _fpsFontTex;
    private uint _fpsVbo;
    private uint _fpsVao;
    private uint _fpsProgram;
    private const int FpsOverlayMaxVerts = 64 * 6; // up to 64 quads (background + characters)

    // GPU Texture2D cache keyed by decoded texture content hash + dimensions
    private readonly Dictionary<(ulong Hash64, int Width, int Height), uint> _gpuTexCache = new(256);
    private readonly Queue<(ulong Hash64, int Width, int Height)> _gpuTexOrder = new(512);
    private const int MaxGpuCachedTextures = 512;

    public int BaseDisplayWidth { get; private set; } = 640;
    public int BaseDisplayHeight { get; private set; } = 448;
    public int ScaleMultiplier { get; private set; } = 1;
    public int OutputWidth => BaseDisplayWidth * ScaleMultiplier;
    public int OutputHeight => BaseDisplayHeight * ScaleMultiplier;
    public int MaxSupportedMsaaSamples { get; private set; } = 1;
    public int ActiveMsaaSamples => _batchFboSamples > 1 ? _batchFboSamples : 1;
    public bool IsInitialized { get; private set; }

    public uint VramSsboHandle => _vramSsbo;
    public uint DisplayTextureHandle => _displayTexture;

    /// <summary>
    /// Creates Silk.NET WindowOptions configured strictly for OpenGL 4.6 Core Profile.
    /// </summary>
    public static WindowOptions CreateOpenGl46WindowOptions(
        string title = "The Sims 2: Castaway (PS2 Static Recompilation)",
        int width = 1280,
        int height = 896)
    {
        var opts = WindowOptions.Default;
        opts.Title = title;
        opts.Size = new Vector2D<int>(width, height);
        opts.API = new GraphicsAPI(
            ContextAPI.OpenGL,
            ContextProfile.Core,
            ContextFlags.ForwardCompatible,
            new APIVersion(4, 6));
        opts.PreferredDepthBufferBits = 24;
        return opts;
    }

    /// <summary>
    /// Initializes all OpenGL 4.6 resources exclusively via Direct State Access (DSA).
    /// </summary>
    public void Initialize(GL gl, int baseWidth = 640, int baseHeight = 448, int scaleMultiplier = 1, int msaaSamples = 1)
    {
        _gl = gl;
        BaseDisplayWidth = Math.Max(64, baseWidth);
        BaseDisplayHeight = Math.Max(64, baseHeight);
        ScaleMultiplier = Math.Clamp(scaleMultiplier, 1, 8);

        gl.GetInteger(GLEnum.MaxSamples, out int maxRboSamples);
        gl.GetInteger(GLEnum.MaxColorTextureSamples, out int maxColorTexSamples);
        gl.GetInteger(GLEnum.MaxDepthTextureSamples, out int maxDepthSamples);
        MaxSupportedMsaaSamples = Math.Max(1, Math.Min(maxRboSamples, Math.Min(maxColorTexSamples, maxDepthSamples)));
        Volatile.Write(ref _requestedMsaaSamples, msaaSamples);

        // 1. Allocate 4 MB GS VRAM Shader Storage Buffer Object (SSBO) using DSA
        gl.CreateBuffers(1, out _vramSsbo);
        gl.NamedBufferStorage(
            _vramSsbo,
            (nuint)GsSwizzle.VramByteSize,
            null,
            BufferStorageMask.DynamicStorageBit);

        // 2. Allocate Compute Uniform Buffer Object (UBO) using DSA
        gl.CreateBuffers(1, out _paramsUbo);
        gl.NamedBufferStorage(
            _paramsUbo,
            (nuint)sizeof(GsComputeDisplayParams),
            null,
            BufferStorageMask.DynamicStorageBit);

        // 3. Allocate Output RGBA8 Display Texture using DSA
        CreateDisplayTextureDsa();

        // 4. Create Empty Vertex Array Object (VAO) for fullscreen triangle pass using DSA
        gl.CreateVertexArrays(1, out _emptyVao);

        // 5. Compile OpenGL 4.6 Compute Shader and Fullscreen Presentation Program
        _unswizzleComputeProgram = CompileComputeProgram(gl, UnswizzleComputeShaderSource);
        _presentGraphicsProgram = CompileGraphicsProgram(gl, PresentVertexShaderSource, PresentFragmentShaderSource);

        // 6. Allocate Primitive Dynamic VBO and VAO via DSA
        gl.CreateBuffers(1, out _primitiveVbo);
        gl.NamedBufferStorage(
            _primitiveVbo,
            (nuint)(GsPrimitiveBatch.MaxVertices * sizeof(GsGlVertex)),
            null,
            BufferStorageMask.DynamicStorageBit);

        gl.CreateVertexArrays(1, out _primitiveVao);
        // Attribute 0: vec4 aPosition (location 0)
        gl.EnableVertexArrayAttrib(_primitiveVao, 0);
        gl.VertexArrayAttribFormat(_primitiveVao, 0, 4, VertexAttribType.Float, false, 0);
        gl.VertexArrayAttribBinding(_primitiveVao, 0, 0);

        // Attribute 1: vec4 aColor (location 1)
        gl.EnableVertexArrayAttrib(_primitiveVao, 1);
        gl.VertexArrayAttribFormat(_primitiveVao, 1, 4, VertexAttribType.Float, false, 16);
        gl.VertexArrayAttribBinding(_primitiveVao, 1, 0);

        // Attribute 2: vec3 aStq (location 2)
        gl.EnableVertexArrayAttrib(_primitiveVao, 2);
        gl.VertexArrayAttribFormat(_primitiveVao, 2, 3, VertexAttribType.Float, false, 32);
        gl.VertexArrayAttribBinding(_primitiveVao, 2, 0);

        // Attribute 3: vec2 aUv (location 3)
        gl.EnableVertexArrayAttrib(_primitiveVao, 3);
        gl.VertexArrayAttribFormat(_primitiveVao, 3, 2, VertexAttribType.Float, false, 44);
        gl.VertexArrayAttribBinding(_primitiveVao, 3, 0);

        gl.VertexArrayVertexBuffer(_primitiveVao, 0, _primitiveVbo, 0, (uint)sizeof(GsGlVertex));

        gl.CreateBuffers(1, out _primitiveEbo);
        gl.NamedBufferStorage(
            _primitiveEbo,
            (nuint)(GsPrimitiveBatch.MaxIndices * sizeof(uint)),
            null,
            BufferStorageMask.DynamicStorageBit);
        gl.VertexArrayElementBuffer(_primitiveVao, _primitiveEbo);

        // 7. Allocate per-draw-call state UBOs via DSA
        gl.CreateBuffers(1, out _drawParamsUbo);
        gl.NamedBufferStorage(
            _drawParamsUbo,
            (nuint)sizeof(GsGlDrawParams),
            null,
            BufferStorageMask.DynamicStorageBit);

        // 8. Compile Primitive Graphics Programs (single-sample, MSAA fast path, and MSAA DATE)
        _primitiveProgram = CompileGraphicsProgram(gl, PrimitiveVertexShaderSource, BuildPrimitiveFragmentShaderSource(msaa: false, msaaDate: false));
        _primitiveMsaaProgram = CompileGraphicsProgram(gl, PrimitiveVertexShaderSource, BuildPrimitiveFragmentShaderSource(msaa: true, msaaDate: false));
        _primitiveMsaaDateProgram = CompileGraphicsProgram(gl, PrimitiveVertexShaderSource, BuildPrimitiveFragmentShaderSource(msaa: true, msaaDate: true));

        // 9. Create 1x1 white dummy texture for untextured draws (DSA)
        gl.CreateTextures(TextureTarget.Texture2D, 1, out _dummyWhiteTex);
        gl.TextureStorage2D(_dummyWhiteTex, 1, SizedInternalFormat.Rgba8, 1, 1);
        uint whitePixel = 0xFFFFFFFFu;
        gl.TextureSubImage2D(_dummyWhiteTex, 0, 0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, &whitePixel);

        // 10. Initialize FPS overlay resources (DSA)
        InitializeFpsOverlayDsa(gl);

        IsInitialized = true;
    }

    private void CreateDisplayTextureDsa()
    {
        if (_gl == null) return;
        if (_displayTexture != 0)
        {
            _gl.DeleteTexture(_displayTexture);
            _displayTexture = 0;
        }

        _gl.CreateTextures(TextureTarget.Texture2D, 1, out _displayTexture);
        _gl.TextureStorage2D(
            _displayTexture,
            1,
            SizedInternalFormat.Rgba8,
            (uint)OutputWidth,
            (uint)OutputHeight);

        int nearest = (int)TextureMinFilter.Nearest;
        int clampToEdge = (int)TextureWrapMode.ClampToEdge;
        _gl.TextureParameter(_displayTexture, TextureParameterName.TextureMinFilter, nearest);
        _gl.TextureParameter(_displayTexture, TextureParameterName.TextureMagFilter, nearest);
        _gl.TextureParameter(_displayTexture, TextureParameterName.TextureWrapS, clampToEdge);
        _gl.TextureParameter(_displayTexture, TextureParameterName.TextureWrapT, clampToEdge);
    }

    public void SetResolutionScale(int scaleMultiplier)
    {
        int clamped = Math.Clamp(scaleMultiplier, 1, 8);
        if (clamped == ScaleMultiplier)
            return;

        ScaleMultiplier = clamped;
        if (IsInitialized)
        {
            CreateDisplayTextureDsa();
        }
    }

    public void SetMsaaSamples(int samples)
    {
        Volatile.Write(ref _requestedMsaaSamples, samples);
    }

    private int ResolveEffectiveMsaaSamples(int requested)
    {
        if (requested <= 1 || MaxSupportedMsaaSamples <= 1)
            return 1;

        int clamped = Math.Clamp(requested, 2, MaxSupportedMsaaSamples);
        int pot = 1;
        while ((pot << 1) <= clamped)
            pot <<= 1;
        return Math.Max(2, pot);
    }

    private uint GetOrUploadBatchTexture(in GsBatchTexture tex)
    {
        var key = (tex.Hash64, tex.Width, tex.Height);
        if (_gpuTexCache.TryGetValue(key, out uint glTex))
            return glTex;

        if (_gpuTexOrder.Count >= MaxGpuCachedTextures)
        {
            var oldest = _gpuTexOrder.Dequeue();
            if (_gpuTexCache.Remove(oldest, out uint oldTex) && oldTex != 0)
            {
                _gl!.DeleteTexture(oldTex);
            }
        }

        _gl!.CreateTextures(TextureTarget.Texture2D, 1, out glTex);
        _gl.TextureStorage2D(glTex, 1, SizedInternalFormat.Rgba8, (uint)tex.Width, (uint)tex.Height);
        int nearest = (int)TextureMinFilter.Nearest;
        int clampToEdge = (int)TextureWrapMode.ClampToEdge;
        _gl.TextureParameter(glTex, TextureParameterName.TextureMinFilter, nearest);
        _gl.TextureParameter(glTex, TextureParameterName.TextureMagFilter, nearest);
        _gl.TextureParameter(glTex, TextureParameterName.TextureWrapS, clampToEdge);
        _gl.TextureParameter(glTex, TextureParameterName.TextureWrapT, clampToEdge);

        fixed (uint* pPixels = tex.Pixels)
        {
            _gl.TextureSubImage2D(glTex, 0, 0, 0, (uint)tex.Width, (uint)tex.Height, PixelFormat.Rgba, PixelType.UnsignedByte, pPixels);
        }

        _gpuTexCache[key] = glTex;
        _gpuTexOrder.Enqueue(key);
        return glTex;
    }

    private void EnsureBatchFramebuffer(int width, int height, int samples)
    {
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        int s = samples > 1 ? samples : 1;
        if (_batchFbo != 0 && _batchFboWidth == w && _batchFboHeight == h && _batchFboSamples == s)
            return;

        if (_batchFbo != 0) _gl!.DeleteFramebuffer(_batchFbo);
        if (_batchColorTex != 0) _gl!.DeleteTexture(_batchColorTex);
        if (_batchDepthRbo != 0) _gl!.DeleteRenderbuffer(_batchDepthRbo);
        _batchFbo = 0;
        _batchColorTex = 0;
        _batchDepthRbo = 0;

        if (s > 1)
        {
            _gl!.CreateTextures(TextureTarget.Texture2DMultisample, 1, out _batchColorTex);
            _gl.TextureStorage2DMultisample(_batchColorTex, (uint)s, SizedInternalFormat.Rgba8, (uint)w, (uint)h, true);

            _gl.CreateRenderbuffers(1, out _batchDepthRbo);
            _gl.NamedRenderbufferStorageMultisample(_batchDepthRbo, (uint)s, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        }
        else
        {
            _gl!.CreateTextures(TextureTarget.Texture2D, 1, out _batchColorTex);
            _gl.TextureStorage2D(_batchColorTex, 1, SizedInternalFormat.Rgba8, (uint)w, (uint)h);
            int nearest = (int)TextureMinFilter.Nearest;
            int clampToEdge = (int)TextureWrapMode.ClampToEdge;
            _gl.TextureParameter(_batchColorTex, TextureParameterName.TextureMinFilter, nearest);
            _gl.TextureParameter(_batchColorTex, TextureParameterName.TextureMagFilter, nearest);
            _gl.TextureParameter(_batchColorTex, TextureParameterName.TextureWrapS, clampToEdge);
            _gl.TextureParameter(_batchColorTex, TextureParameterName.TextureWrapT, clampToEdge);

            _gl.CreateRenderbuffers(1, out _batchDepthRbo);
            _gl.NamedRenderbufferStorage(_batchDepthRbo, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        }

        _gl.CreateFramebuffers(1, out _batchFbo);
        _gl.NamedFramebufferTexture(_batchFbo, FramebufferAttachment.ColorAttachment0, _batchColorTex, 0);
        _gl.NamedFramebufferRenderbuffer(_batchFbo, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _batchDepthRbo);

        if (s > 1 && _gl.CheckNamedFramebufferStatus(_batchFbo, FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
        {
            // Fall back cleanly to single-sampled FBO if driver rejects requested multisample config
            _batchFboWidth = 0;
            _batchFboHeight = 0;
            _batchFboSamples = 0;
            EnsureBatchFramebuffer(w, h, 1);
            return;
        }

        _batchFboWidth = w;
        _batchFboHeight = h;
        _batchFboSamples = s;

        // Initialize newly allocated FBO memory once; existing FBO color persists across frames like GS VRAM.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _batchFbo);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(true);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.ClearDepth(0.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    /// <summary>
    /// Renders a batched stream of GS primitives directly on the GPU using OpenGL 4.6 DSA.
    /// Uses cached Texture2D snapshots for mid-frame VRAM overwrite accuracy and hardware texture L1/L2 caching.
    /// </summary>
    public void RenderBatch(GsPrimitiveBatch batch, GsProcessor gs, int viewportWidth, int viewportHeight, byte[]? vramOverride = null, bool useDisplayTextureBackdrop = false)
    {
        if (!IsInitialized || _gl == null || viewportWidth <= 0 || viewportHeight <= 0)
            return;

        if (batch.VertexCount == 0)
            return;

        // 1. Bind persistent offscreen FBO (Rgba8 + Depth24, optionally MSAA) so DATE reads exact destination alpha
        int requestedSamples = ResolveEffectiveMsaaSamples(Volatile.Read(ref _requestedMsaaSamples));
        EnsureBatchFramebuffer(viewportWidth, viewportHeight, requestedSamples);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _batchFbo);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(true);
        _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);

        if (useDisplayTextureBackdrop && _displayTexture != 0)
        {
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.UseProgram(_presentGraphicsProgram);
            _gl.BindTextureUnit(0, _displayTexture);
            _gl.BindVertexArray(_emptyVao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            _gl.DepthMask(true);
            _gl.ClearDepth(0.0f);
            _gl.Clear(ClearBufferMask.DepthBufferBit);
        }
        else
        {
            // Preserve persistent GS framebuffer color across frames; clear only reverse-Z depth buffer (ZTST: GEQUAL/GREATER)
            _gl.ClearDepth(0.0f);
            _gl.Clear(ClearBufferMask.DepthBufferBit);
        }

        if (_batchFboSamples > 1)
            _gl.Enable(EnableCap.Multisample);
        else
            _gl.Disable(EnableCap.Multisample);

        // 2. Upload dynamic vertex & index buffers via DSA NamedBufferSubData
        fixed (GsGlVertex* vtxPtr = batch.Vertices)
        {
            _gl.NamedBufferSubData(_primitiveVbo, 0, (nuint)(batch.VertexCount * sizeof(GsGlVertex)), vtxPtr);
        }
        if (batch.IndexCount > 0)
        {
            fixed (uint* idxPtr = batch.Indices)
            {
                _gl.NamedBufferSubData(_primitiveEbo, 0, (nuint)(batch.IndexCount * sizeof(uint)), idxPtr);
            }
        }

        // 3. Set global OpenGL state bindings
        uint activeProgram = _batchFboSamples > 1 ? _primitiveMsaaProgram : _primitiveProgram;
        _gl.UseProgram(activeProgram);
        var displayParams = new GsComputeDisplayParams
        {
            DisplayWidth = Math.Max(1, BaseDisplayWidth),
            DisplayHeight = Math.Max(1, BaseDisplayHeight),
            ScaleMultiplier = Math.Max(1, ScaleMultiplier)
        };
        _gl.NamedBufferSubData(_paramsUbo, 0, (nuint)sizeof(GsComputeDisplayParams), &displayParams);
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 1, _paramsUbo);
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 2, _drawParamsUbo);
        _gl.BindVertexArray(_primitiveVao);

        float scaleX = (float)viewportWidth / BaseDisplayWidth;
        float scaleY = (float)viewportHeight / BaseDisplayHeight;
        uint lastBoundTex = 0;

        int totalDrawCalls = batch.DrawCallCount;
        for (int i = 0; i < totalDrawCalls; i++)
        {
            ref var call = ref batch.DrawCalls[i];
            ref var dc = ref call.DrawContext;

            // Scissor test
            int scLeft = Math.Max(0, (int)MathF.Floor(call.Scx0 * scaleX));
            int scTop = Math.Max(0, (int)MathF.Floor(call.Scy0 * scaleY));
            int scRight = Math.Min(viewportWidth, (int)MathF.Ceiling((call.Scx1 + 1) * scaleX));
            int scBottomTop = Math.Min(viewportHeight, (int)MathF.Ceiling((call.Scy1 + 1) * scaleY));
            int scW = Math.Max(1, scRight - scLeft);
            int scH = Math.Max(1, scBottomTop - scTop);
            int scBottom = Math.Max(0, viewportHeight - (scTop + scH));
            _gl.Enable(EnableCap.ScissorTest);
            _gl.Scissor(scLeft, scBottom, (uint)scW, (uint)scH);

            // Framebuffer channel mask (FBMSK: 1 = protected, 0 = writable)
            // Protect bit 7 of alpha when bit 31 is masked (standard GS DATE stencil preservation).
            bool maskR = (dc.Fbmsk & 0x000000FFu) != 0x000000FFu;
            bool maskG = (dc.Fbmsk & 0x0000FF00u) != 0x0000FF00u;
            bool maskB = (dc.Fbmsk & 0x00FF0000u) != 0x00FF0000u;
            bool maskA = (dc.Fbmsk & 0xFF000000u) != 0xFF000000u && (dc.Fbmsk & 0x80000000u) == 0;
            bool writeDepth = dc.Zte && !dc.Zmsk;

            if (dc.Ate && dc.Atst == 0)
            {
                if (dc.Afail == 0 || (dc.Afail == 2 && !writeDepth))
                    continue;
            }

            _gl.ColorMask(maskR, maskG, maskB, maskA);

            // Depth test (PS2 ZTST: 2=GEQUAL, 3=GREATER, 1=ALWAYS)
            if (dc.Zte)
            {
                _gl.Enable(EnableCap.DepthTest);
                DepthFunction depthFn = dc.Ztst switch
                {
                    2 => DepthFunction.Gequal,
                    3 => DepthFunction.Greater,
                    1 => DepthFunction.Always,
                    _ => DepthFunction.Never
                };
                _gl.DepthFunc(depthFn);
                _gl.DepthMask(writeDepth);
            }
            else
            {
                _gl.Disable(EnableCap.DepthTest);
                _gl.DepthMask(false);
            }

            // Alpha blending (PS2 ALPHA: (A - B) * C >> 7 + D)
            if (dc.Abe)
            {
                _gl.Enable(EnableCap.Blend);
                if (dc.SpecC == 2)
                {
                    float fix = Math.Clamp(dc.Fix / 128.0f, 0.0f, 1.0f);
                    _gl.BlendColor(0.0f, 0.0f, 0.0f, fix);
                }

                BlendingFactor cFactor = dc.SpecC switch
                {
                    0 => BlendingFactor.Src1Alpha,
                    1 => BlendingFactor.DstAlpha,
                    2 => BlendingFactor.ConstantAlpha,
                    _ => BlendingFactor.Src1Alpha
                };
                BlendingFactor invCFactor = dc.SpecC switch
                {
                    0 => BlendingFactor.OneMinusSrc1Alpha,
                    1 => BlendingFactor.OneMinusDstAlpha,
                    2 => BlendingFactor.OneMinusConstantAlpha,
                    _ => BlendingFactor.OneMinusSrc1Alpha
                };

                var (srcRgb, dstRgb, eqRgb) = (dc.SpecA, dc.SpecB, dc.SpecD) switch
                {
                    (0, 0, 0) or (1, 1, 0) or (2, 2, 0) => (BlendingFactor.One, BlendingFactor.Zero, BlendEquationModeEXT.FuncAdd),
                    (0, 0, 1) or (1, 1, 1) or (2, 2, 1) => (BlendingFactor.Zero, BlendingFactor.One, BlendEquationModeEXT.FuncAdd),
                    (0, 0, 2) or (1, 1, 2) or (2, 2, 2) => (BlendingFactor.Zero, BlendingFactor.Zero, BlendEquationModeEXT.FuncAdd),

                    (0, 1, 1) => (cFactor, invCFactor, BlendEquationModeEXT.FuncAdd),
                    (0, 1, 2) => (cFactor, cFactor, BlendEquationModeEXT.FuncSubtract),
                    (0, 1, 0) => (BlendingFactor.One, cFactor, BlendEquationModeEXT.FuncSubtract),

                    (1, 0, 0) => (invCFactor, cFactor, BlendEquationModeEXT.FuncAdd),
                    (1, 0, 2) => (cFactor, cFactor, BlendEquationModeEXT.FuncReverseSubtract),
                    (1, 0, 1) => (cFactor, BlendingFactor.One, BlendEquationModeEXT.FuncReverseSubtract),

                    (0, 2, 1) => (cFactor, BlendingFactor.One, BlendEquationModeEXT.FuncAdd),
                    (0, 2, 2) => (cFactor, BlendingFactor.Zero, BlendEquationModeEXT.FuncAdd),
                    (0, 2, 0) => (BlendingFactor.One, BlendingFactor.Zero, BlendEquationModeEXT.FuncAdd),

                    (1, 2, 0) => (BlendingFactor.One, cFactor, BlendEquationModeEXT.FuncAdd),
                    (1, 2, 2) => (BlendingFactor.Zero, cFactor, BlendEquationModeEXT.FuncAdd),
                    (1, 2, 1) => (BlendingFactor.Zero, BlendingFactor.One, BlendEquationModeEXT.FuncAdd),

                    (2, 0, 1) => (cFactor, BlendingFactor.One, BlendEquationModeEXT.FuncReverseSubtract),
                    (2, 0, 0) => (invCFactor, BlendingFactor.Zero, BlendEquationModeEXT.FuncAdd),
                    (2, 0, 2) => (BlendingFactor.Zero, BlendingFactor.Zero, BlendEquationModeEXT.FuncAdd),

                    (2, 1, 0) => (BlendingFactor.One, cFactor, BlendEquationModeEXT.FuncSubtract),
                    (2, 1, 1) => (BlendingFactor.Zero, invCFactor, BlendEquationModeEXT.FuncAdd),
                    (2, 1, 2) => (BlendingFactor.Zero, BlendingFactor.Zero, BlendEquationModeEXT.FuncAdd),

                    _ => (cFactor, invCFactor, BlendEquationModeEXT.FuncAdd)
                };

                _gl.BlendFuncSeparate(srcRgb, dstRgb, BlendingFactor.One, BlendingFactor.Zero);
                _gl.BlendEquationSeparate(eqRgb, BlendEquationModeEXT.FuncAdd);
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
            }

            // Bind decoded Texture2D via DSA
            uint glTex = _dummyWhiteTex;
            if (dc.Tme && dc.TexIndex >= 0 && dc.TexIndex < batch.Textures.Count)
            {
                glTex = GetOrUploadBatchTexture(batch.Textures[dc.TexIndex]);
            }
            if (glTex != lastBoundTex)
            {
                _gl.BindTextureUnit(0, glTex);
                lastBoundTex = glTex;
            }

            bool twoPassAfail = false;
            int afailMode = 0;
            if (dc.Ate && dc.Atst == 0)
            {
                afailMode = 2; // ATST=NEVER always fails
                if (dc.Afail == 2)
                    _gl.ColorMask(false, false, false, false);
                else if (dc.Afail == 1)
                    _gl.DepthMask(false);
                else if (dc.Afail == 3)
                {
                    _gl.ColorMask(maskR, maskG, maskB, false);
                    _gl.DepthMask(false);
                }
            }
            else if (dc.Ate && dc.Atst >= 2 && dc.Afail != 0)
            {
                if (dc.Afail == 1 && !writeDepth)
                    afailMode = 2; // FB_ONLY with no depth write
                else if (dc.Afail == 2 && !writeDepth)
                    afailMode = 0; // ZB_ONLY with no depth write == KEEP
                else
                    twoPassAfail = true;
            }

            uint desiredProgram = _batchFboSamples > 1
                ? (dc.Date ? _primitiveMsaaDateProgram : _primitiveMsaaProgram)
                : _primitiveProgram;
            if (desiredProgram != activeProgram)
            {
                _gl.UseProgram(desiredProgram);
                activeProgram = desiredProgram;
            }

            if (dc.Date)
            {
                _gl.TextureBarrier();
                _gl.BindTextureUnit(1, _batchColorTex);
            }

            // Upload compact per-draw-call state to UBO via DSA (96 bytes)
            var drawParams = new GsGlDrawParams
            {
                Tme = dc.Tme ? 1 : 0,
                Fst = dc.Fst ? 1 : 0,
                Tw = dc.Tw,
                Th = dc.Th,
                Tcc = dc.Tcc,
                Tfx = dc.Tfx,
                Ate = dc.Ate ? 1 : 0,
                Atst = dc.Atst,
                Aref = dc.Aref,
                Afail = dc.Afail,
                Zte = dc.Zte ? 1 : 0,
                Wms = dc.Wms,
                Wmt = dc.Wmt,
                MinU = (int)dc.MinU,
                MaxU = (int)dc.MaxU,
                MinV = (int)dc.MinV,
                MaxV = (int)dc.MaxV,
                LinearFilter = dc.LinearFilter ? 1 : 0,
                AfailMode = afailMode,
                Fba = dc.Fba ? 1 : 0,
                Date = dc.Date ? 1 : 0,
                Datm = dc.Datm
            };

            _gl.NamedBufferSubData(_drawParamsUbo, 0, (nuint)sizeof(GsGlDrawParams), &drawParams);

            // Draw indexed triangles
            if (call.IndexCount > 0)
                _gl.DrawElements(PrimitiveType.Triangles, (uint)call.IndexCount, DrawElementsType.UnsignedInt, (void*)((nuint)call.StartIndex * sizeof(uint)));
            else
                _gl.DrawArrays(PrimitiveType.Triangles, call.StartVertex, (uint)call.VertexCount);

            if (twoPassAfail)
            {
                drawParams.AfailMode = 1; // Pass 2: keep only alpha-failing fragments
                _gl.NamedBufferSubData(_drawParamsUbo, 0, (nuint)sizeof(GsGlDrawParams), &drawParams);
                if (dc.Afail == 2)
                {
                    _gl.ColorMask(false, false, false, false);
                }
                else if (dc.Afail == 1)
                {
                    _gl.DepthMask(false);
                }
                else // Afail == 3 (RGB_ONLY)
                {
                    _gl.ColorMask(maskR, maskG, maskB, false);
                    _gl.DepthMask(false);
                }
                if (call.IndexCount > 0)
                    _gl.DrawElements(PrimitiveType.Triangles, (uint)call.IndexCount, DrawElementsType.UnsignedInt, (void*)((nuint)call.StartIndex * sizeof(uint)));
                else
                    _gl.DrawArrays(PrimitiveType.Triangles, call.StartVertex, (uint)call.VertexCount);
            }
        }

        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.Multisample);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BlitNamedFramebuffer(
            _batchFbo, 0,
            0, 0, viewportWidth, viewportHeight,
            0, 0, viewportWidth, viewportHeight,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
    }

    /// <summary>
    /// Synchronizes the 4 MB virtual GS VRAM buffer to the GPU SSBO via DSA (`NamedBufferSubData`),
    /// dispatches the OpenGL 4.6 unswizzle/CLUT compute shader, and renders the output frame (Software Mode presentation).
    /// </summary>
    public void RenderFrame(GsProcessor gs, int viewportWidth, int viewportHeight, ulong dispfb1 = 0, byte[]? vramOverride = null)
    {
        if (!IsInitialized || _gl == null)
            return;

        // 1. Upload 4 MB GS VRAM to SSBO via DSA NamedBufferSubData when dirty
        byte[] vram = vramOverride ?? gs.Vram;
        bool dirty = vramOverride != null || gs.VramDirty;
        if (dirty)
        {
            fixed (byte* vramPtr = vram)
            {
                _gl.NamedBufferSubData(_vramSsbo, 0, (nuint)GsSwizzle.VramByteSize, vramPtr);
            }
            if (vramOverride == null)
                gs.VramDirty = false;
        }

        // 2. Upload Display / Framebuffer parameters via DSA NamedBufferSubData
        int fbpBlock, fbwPixels, fpsm;
        if (dispfb1 != 0)
        {
            fbpBlock = (int)(dispfb1 & 0x1FFul) * 32;
            fbwPixels = Math.Max(1, (int)((dispfb1 >> 9) & 0x3Ful)) * 64;
            fpsm = (int)((dispfb1 >> 15) & 0x1Ful);
        }
        else
        {
            ulong frameReg = gs.Frame[0];
            fbpBlock = (int)(frameReg & 0x1FFul) * 32;
            fbwPixels = Math.Max(1, (int)((frameReg >> 16) & 0x3Ful)) * 64;
            fpsm = (int)((frameReg >> 24) & 0x3Ful);
        }

        ulong tex0Reg = gs.Tex0[0];
        var dispParams = new GsComputeDisplayParams
        {
            FbpBlock = fbpBlock,
            FbwPixels = fbwPixels,
            Fpsm = fpsm,
            CbpBlock = (int)((tex0Reg >> 37) & 0x3FFFul),
            Cpsm = (int)((tex0Reg >> 51) & 0xFul),
            DisplayWidth = BaseDisplayWidth,
            DisplayHeight = BaseDisplayHeight,
            ScaleMultiplier = ScaleMultiplier
        };
        _gl.NamedBufferSubData(_paramsUbo, 0, (nuint)sizeof(GsComputeDisplayParams), &dispParams);

        // 3. Dispatch OpenGL 4.6 Compute Shader to unswizzle GS VRAM into _displayTexture
        _gl.UseProgram(_unswizzleComputeProgram);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _vramSsbo);
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 1, _paramsUbo);
        _gl.BindImageTexture(0, _displayTexture, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.Rgba8);

        uint groupsX = (uint)((OutputWidth + 15) / 16);
        uint groupsY = (uint)((OutputHeight + 15) / 16);
        _gl.DispatchCompute(groupsX, groupsY, 1);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);

        // 4. Present _displayTexture to the window backbuffer using DSA BindTextureUnit & BindVertexArray
        _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.UseProgram(_presentGraphicsProgram);
        _gl.BindTextureUnit(0, _displayTexture);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private static uint CompileComputeProgram(GL gl, string source)
    {
        uint shader = gl.CreateShader(ShaderType.ComputeShader);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"OpenGL 4.6 Compute Shader compilation failed: {log}");
        }

        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, shader);
        gl.LinkProgram(prog);
        gl.DeleteShader(shader);

        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            string log = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            throw new InvalidOperationException($"OpenGL 4.6 Compute Program link failed: {log}");
        }

        return prog;
    }

    private static uint CompileGraphicsProgram(GL gl, string vsSource, string fsSource)
    {
        uint vs = gl.CreateShader(ShaderType.VertexShader);
        gl.ShaderSource(vs, vsSource);
        gl.CompileShader(vs);
        gl.GetShader(vs, ShaderParameterName.CompileStatus, out int vsStatus);
        if (vsStatus == 0)
        {
            string log = gl.GetShaderInfoLog(vs);
            gl.DeleteShader(vs);
            throw new InvalidOperationException($"OpenGL 4.6 Vertex Shader compilation failed: {log}");
        }

        uint fs = gl.CreateShader(ShaderType.FragmentShader);
        gl.ShaderSource(fs, fsSource);
        gl.CompileShader(fs);
        gl.GetShader(fs, ShaderParameterName.CompileStatus, out int fsStatus);
        if (fsStatus == 0)
        {
            string log = gl.GetShaderInfoLog(fs);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);
            throw new InvalidOperationException($"OpenGL 4.6 Fragment Shader compilation failed: {log}");
        }

        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            string log = gl.GetProgramInfoLog(prog);
            gl.DeleteProgram(prog);
            throw new InvalidOperationException($"OpenGL 4.6 Graphics Program link failed: {log}");
        }

        return prog;
    }

    public const string PrimitiveVertexShaderSource = """
        #version 460 core
        layout(location = 0) in vec4 aPosition;
        layout(location = 1) in vec4 aColor;
        layout(location = 2) in vec3 aStq;
        layout(location = 3) in vec2 aUv;

        layout(std140, binding = 1) uniform GsDisplayParams {
            int uDispFbpBlock;
            int uDispFbwPixels;
            int uDispFpsm;
            int uDispCbpBlock;
            int uDispCpsm;
            int uDisplayWidth;
            int uDisplayHeight;
            int uDispScaleMultiplier;
        } uDisplay;

        centroid out vec4 vColor;
        centroid out vec3 vStq;
        centroid out vec2 vUv;

        void main() {
            vColor = aColor;
            vStq = aStq;
            vUv = aUv;

            float clipX = (aPosition.x / (float(uDisplay.uDisplayWidth) * 0.5)) - 1.0;
            float clipY = 1.0 - (aPosition.y / (float(uDisplay.uDisplayHeight) * 0.5));
            float clipZ = aPosition.z * 2.0 - 1.0;

            gl_Position = vec4(clipX, clipY, clipZ, 1.0);
        }
        """;

    private static string BuildPrimitiveFragmentShaderSource(bool msaa, bool msaaDate)
    {
        string defines = msaaDate
            ? "#version 460 core\n#define MSAA 1\n#define MSAA_DATE 1"
            : (msaa ? "#version 460 core\n#define MSAA 1" : "#version 460 core");
        return PrimitiveFragmentShaderSource.Replace("#version 460 core", defines, StringComparison.Ordinal);
    }

    public const string PrimitiveFragmentShaderSource = """
        #version 460 core
        centroid in vec4 vColor;
        centroid in vec3 vStq;
        centroid in vec2 vUv;
        layout(location = 0, index = 0) out vec4 fragColor;
        layout(location = 0, index = 1) out vec4 fragBlend;

        layout(binding = 0) uniform sampler2D uTex;
        #if defined(MSAA_DATE)
        layout(binding = 1) uniform sampler2DMS uDstTexMs;
        #elif !defined(MSAA)
        layout(binding = 1) uniform sampler2D uDstTex;
        #endif

        layout(std140, binding = 2) uniform GsDrawParams {
            int uTme;
            int uFst;
            int uTw;
            int uTh;

            int uTcc;
            int uTfx;
            int uAte;
            int uAtst;

            int uAref;
            int uAfail;
            int uZte;
            int uWms;

            int uWmt;
            int uMinU;
            int uMaxU;
            int uMinV;

            int uMaxV;
            int uLinearFilter;
            int uAfailMode;
            int uFba;

            int uDate;
            int uDatm;
            int _uPad0;
            int _uPad1;
        };

        int applyWrap(int coord, int size, int mode, int minVal, int maxVal) {
            if (mode == 0) { // REPEAT
                return coord & (max(1, size) - 1);
            } else if (mode == 1) { // CLAMP
                return clamp(coord, 0, size - 1);
            } else if (mode == 2) { // REGION_CLAMP
                return clamp(coord, minVal, maxVal);
            } else { // REGION_REPEAT
                return (coord & minVal) | maxVal;
            }
        }

        vec4 readTexel(int tx, int ty) {
            return texelFetch(uTex, ivec2(tx, ty), 0);
        }

        vec4 sampleTexture(float uCoord, float vCoord) {
            if (uLinearFilter != 0) {
                float fU = uCoord - 0.5;
                float fV = vCoord - 0.5;
                int tx0 = int(floor(fU));
                int ty0 = int(floor(fV));
                int tx1 = tx0 + 1;
                int ty1 = ty0 + 1;

                float fracU = fU - float(tx0);
                float fracV = fV - float(ty0);

                tx0 = applyWrap(tx0, uTw, uWms, uMinU, uMaxU);
                ty0 = applyWrap(ty0, uTh, uWmt, uMinV, uMaxV);
                tx1 = applyWrap(tx1, uTw, uWms, uMinU, uMaxU);
                ty1 = applyWrap(ty1, uTh, uWmt, uMinV, uMaxV);

                vec4 c00 = readTexel(tx0, ty0);
                vec4 c10 = readTexel(tx1, ty0);
                vec4 c01 = readTexel(tx0, ty1);
                vec4 c11 = readTexel(tx1, ty1);

                vec4 top = mix(c00, c10, fracU);
                vec4 bot = mix(c01, c11, fracU);
                return mix(top, bot, fracV);
            } else {
                int tx = int(floor(uCoord));
                int ty = int(floor(vCoord));
                tx = applyWrap(tx, uTw, uWms, uMinU, uMaxU);
                ty = applyWrap(ty, uTh, uWmt, uMinV, uMaxV);
                return readTexel(tx, ty);
            }
        }

        void main() {
        #if defined(MSAA_DATE)
            if (uDate != 0) {
                int dstA = int(round(texelFetch(uDstTexMs, ivec2(gl_FragCoord.xy), gl_SampleID).a * 255.0));
                if (((dstA >> 7) & 1) != uDatm) {
                    discard;
                }
            }
        #elif !defined(MSAA)
            if (uDate != 0) {
                int dstA = int(round(texelFetch(uDstTex, ivec2(gl_FragCoord.xy), 0).a * 255.0));
                if (((dstA >> 7) & 1) != uDatm) {
                    discard;
                }
            }
        #endif

            vec3 fragRgb = vColor.rgb;
            int fragA = int(round(vColor.a * 128.0));

            if (uTme != 0) {
                float uCoord, vCoord;
                if (uFst != 0) {
                    uCoord = vUv.x;
                    vCoord = vUv.y;
                } else {
                    float invQ = (abs(vStq.z) > 1e-6) ? (1.0 / vStq.z) : 1.0;
                    uCoord = (vStq.x * invQ) * float(uTw);
                    vCoord = (vStq.y * invQ) * float(uTh);
                }

                vec4 tex = sampleTexture(uCoord, vCoord);
                int tA = int(round(tex.a * 255.0));
                int vA = fragA;

                if (uTfx == 0) { // Modulate
                    fragRgb = clamp(tex.rgb * vColor.rgb, 0.0, 1.0);
                    fragA = (uTcc != 0) ? ((tA * vA) >> 7) : vA;
                } else if (uTfx == 1) { // Decal
                    fragRgb = tex.rgb;
                    fragA = (uTcc != 0) ? tA : vA;
                } else if (uTfx == 2) { // Highlight
                    float af = float(vA) / 255.0;
                    fragRgb = clamp(tex.rgb * vColor.rgb + af, 0.0, 1.0);
                    fragA = (uTcc != 0) ? min(255, tA + vA) : vA;
                } else { // Highlight2
                    float af = float(vA) / 255.0;
                    fragRgb = clamp(tex.rgb * vColor.rgb + af, 0.0, 1.0);
                    fragA = (uTcc != 0) ? tA : vA;
                }
            } else {
                fragRgb = clamp(vColor.rgb * (128.0 / 255.0), 0.0, 1.0);
            }

            // Alpha Test
            if (uAte != 0 && uAfailMode != 2) {
                bool pass = true;
                if (uAtst == 0) pass = false;
                else if (uAtst == 2) pass = (fragA < uAref);
                else if (uAtst == 3) pass = (fragA <= uAref);
                else if (uAtst == 4) pass = (fragA == uAref);
                else if (uAtst == 5) pass = (fragA >= uAref);
                else if (uAtst == 6) pass = (fragA > uAref);
                else if (uAtst == 7) pass = (fragA != uAref);

                if (uAfailMode == 0) {
                    if (!pass) discard;
                } else {
                    if (pass) discard;
                }
            }

            if (uFba != 0) {
                fragA = fragA | 128;
            }

            fragColor = vec4(fragRgb, clamp(float(fragA) / 255.0, 0.0, 1.0));
            fragBlend = vec4(0.0, 0.0, 0.0, clamp(float(fragA) / 128.0, 0.0, 1.0));
        }
        """;

    public const string UnswizzleComputeShaderSource = """
        #version 460 core
        layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

        layout(std430, binding = 0) readonly buffer GsVramBuffer {
            uint VramWords[];
        };

        layout(std140, binding = 1) uniform GsDisplayParams {
            int uFbpBlock;
            int uFbwPixels;
            int uFpsm;
            int uCbpBlock;
            int uCpsm;
            int uDisplayWidth;
            int uDisplayHeight;
            int uScaleMultiplier;
        };

        layout(rgba8, binding = 0) writeonly uniform image2D uOutImage;

        const int BlockTable32[32] = int[32](
             0,  1,  4,  5, 16, 17, 20, 21,
             2,  3,  6,  7, 18, 19, 22, 23,
             8,  9, 12, 13, 24, 25, 28, 29,
            10, 11, 14, 15, 26, 27, 30, 31
        );

        const int BlockTable16[32] = int[32](
             0,  2,  8, 10,
             1,  3,  9, 11,
             4,  6, 12, 14,
             5,  7, 13, 15,
            16, 18, 24, 26,
            17, 19, 25, 27,
            20, 22, 28, 30,
            21, 23, 29, 31
        );

        const int BlockTable16S[32] = int[32](
             0,  2, 16, 18,
             1,  3, 17, 19,
             8, 10, 24, 26,
             9, 11, 25, 27,
             4,  6, 20, 22,
             5,  7, 21, 23,
            12, 14, 28, 30,
            13, 15, 29, 31
        );

        const int ColumnWordTable32[64] = int[64](
             0,  1,  4,  5,  8,  9, 12, 13,
             2,  3,  6,  7, 10, 11, 14, 15,
            16, 17, 20, 21, 24, 25, 28, 29,
            18, 19, 22, 23, 26, 27, 30, 31,
            32, 33, 36, 37, 40, 41, 44, 45,
            34, 35, 38, 39, 42, 43, 46, 47,
            48, 49, 52, 53, 56, 57, 60, 61,
            50, 51, 54, 55, 58, 59, 62, 63
        );

        const int ColumnHalfwordTable16[128] = int[128](
              0,   1,   4,   5,   8,   9,  12,  13,  16,  17,  20,  21,  24,  25,  28,  29,
              2,   3,   6,   7,  10,  11,  14,  15,  18,  19,  22,  23,  26,  27,  30,  31,
             32,  33,  36,  37,  40,  41,  44,  45,  48,  49,  52,  53,  56,  57,  60,  61,
             34,  35,  38,  39,  42,  43,  46,  47,  50,  51,  54,  55,  58,  59,  62,  63,
             64,  65,  68,  69,  72,  73,  76,  77,  80,  81,  84,  85,  88,  89,  92,  93,
             66,  67,  70,  71,  74,  75,  78,  79,  82,  83,  86,  87,  90,  91,  94,  95,
             96,  97, 100, 101, 104, 105, 108, 109, 112, 113, 116, 117, 120, 121, 124, 125,
             98,  99, 102, 103, 106, 107, 110, 111, 114, 115, 118, 119, 122, 123, 126, 127
        );

        uint readPixel32(int baseBlock, int bwPixels, int x, int y) {
            int pagesPerRow = max(1, bwPixels >> 6);
            int pageX = x >> 6;
            int pageY = y >> 5;
            int pageBlockBase = baseBlock + (pageY * pagesPerRow + pageX) * 32;
            int bx = (x & 63) >> 3;
            int by = (y & 31) >> 3;
            int blockInPage = BlockTable32[(by << 3) + bx];
            int blockIdx = (pageBlockBase + blockInPage) & 0x3FFF;
            int wordInBlock = ColumnWordTable32[((y & 7) << 3) + (x & 7)];
            int wordIndex = ((blockIdx << 6) + wordInBlock) & 0xFFFFF;
            return VramWords[wordIndex];
        }

        uint readPixel16(int baseBlock, int bwPixels, int x, int y, bool is16S) {
            int pagesPerRow = max(1, bwPixels >> 6);
            int pageX = x >> 6;
            int pageY = y >> 6;
            int pageBlockBase = baseBlock + (pageY * pagesPerRow + pageX) * 32;

            int bx = (x & 63) >> 4;
            int by = (y & 63) >> 3;
            int blockInPage = is16S ? BlockTable16S[(by << 2) + bx] : BlockTable16[(by << 2) + bx];
            int blockIdx = (pageBlockBase + blockInPage) & 0x3FFF;

            int px = x & 15;
            int py = y & 7;
            int halfwordInBlock = ColumnHalfwordTable16[(py << 4) + px];
            int halfwordIndex = ((blockIdx << 7) + halfwordInBlock) & 0x1FFFFF;

            uint word = VramWords[halfwordIndex >> 1];
            uint c16 = ((halfwordIndex & 1) != 0) ? (word >> 16) : (word & 0xFFFFu);

            uint r5 = c16 & 0x1Fu;
            uint g5 = (c16 >> 5) & 0x1Fu;
            uint b5 = (c16 >> 10) & 0x1Fu;
            uint a1 = (c16 >> 15) & 1u;

            uint r8 = (r5 << 3) | (r5 >> 2);
            uint g8 = (g5 << 3) | (g5 >> 2);
            uint b8 = (b5 << 3) | (b5 >> 2);
            uint a8 = (a1 != 0u) ? 255u : 0u;

            return r8 | (g8 << 8) | (b8 << 16) | (a8 << 24);
        }

        void main() {
            ivec2 outCoord = ivec2(gl_GlobalInvocationID.xy);
            int outW = uDisplayWidth * uScaleMultiplier;
            int outH = uDisplayHeight * uScaleMultiplier;
            if (outCoord.x >= outW || outCoord.y >= outH)
                return;

            int srcX = outCoord.x / max(1, uScaleMultiplier);
            int srcY = outCoord.y / max(1, uScaleMultiplier);

            uint raw;
            if (uFpsm == 0x02) {
                raw = readPixel16(uFbpBlock, uFbwPixels, srcX, srcY, false);
            } else if (uFpsm == 0x0A) {
                raw = readPixel16(uFbpBlock, uFbwPixels, srcX, srcY, true);
            } else {
                raw = readPixel32(uFbpBlock, uFbwPixels, srcX, srcY);
            }

            float r = float(raw & 0xFFu) / 255.0;
            float g = float((raw >> 8) & 0xFFu) / 255.0;
            float b = float((raw >> 16) & 0xFFu) / 255.0;

            imageStore(uOutImage, outCoord, vec4(r, g, b, 1.0));
        }
        """;

    public const string PresentVertexShaderSource = """
        #version 460 core
        out vec2 vUv;
        void main() {
            vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            vUv = vec2(pos.x, 1.0 - pos.y);
            gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

        public const string PresentFragmentShaderSource = """
        #version 460 core
        in vec2 vUv;
        out vec4 fragColor;
        layout(binding = 0) uniform sampler2D uDisplayTex;
        void main() {
            fragColor = vec4(texture(uDisplayTex, vUv).rgb, 1.0);
        }
        """;

    private const string FpsVertexShaderSource = """
        #version 460 core
        layout(location = 0) in vec2 aPosNdc;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec4 aColor;
        out vec2 vUv;
        out vec4 vColor;
        void main() {
            vUv = aUv;
            vColor = aColor;
            gl_Position = vec4(aPosNdc, 0.0, 1.0);
        }
        """;

    private const string FpsFragmentShaderSource = """
        #version 460 core
        in vec2 vUv;
        in vec4 vColor;
        out vec4 fragColor;
        layout(binding = 0) uniform sampler2D uFontTex;
        void main() {
            vec4 tex = texture(uFontTex, vUv);
            fragColor = tex * vColor;
            if (fragColor.a < 0.01) discard;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct FpsOverlayVertex
    {
        public float X, Y, U, V;
        public float R, G, B, A;
    }

    // 5x7 bitmap glyphs: 0-9, F, P, S, :, . (15 glyphs, 6px wide x 8px tall each -> 96x8 texture)
    private static readonly byte[][] FpsGlyphs =
    {
        new byte[] { 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E }, // 0
        new byte[] { 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E }, // 1
        new byte[] { 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F }, // 2
        new byte[] { 0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E }, // 3
        new byte[] { 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02 }, // 4
        new byte[] { 0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E }, // 5
        new byte[] { 0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E }, // 6
        new byte[] { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08 }, // 7
        new byte[] { 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E }, // 8
        new byte[] { 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C }, // 9
        new byte[] { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10 }, // F (10)
        new byte[] { 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10 }, // P (11)
        new byte[] { 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E }, // S (12)
        new byte[] { 0x00, 0x04, 0x04, 0x00, 0x04, 0x04, 0x00 }, // : (13)
        new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C }, // . (14)
        new byte[] { 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F, 0x1F }, // solid block (15)
    };

    private void InitializeFpsOverlayDsa(GL gl)
    {
        const int atlasW = 96;
        const int atlasH = 8;
        uint[] pixels = new uint[atlasW * atlasH];
        for (int g = 0; g < FpsGlyphs.Length; g++)
        {
            byte[] rows = FpsGlyphs[g];
            int gx = g * 6;
            for (int y = 0; y < 7; y++)
            {
                byte bits = rows[y];
                for (int x = 0; x < 5; x++)
                {
                    bool on = ((bits >> (4 - x)) & 1) != 0;
                    pixels[y * atlasW + (gx + x)] = on ? 0xFFFFFFFFu : 0x00000000u;
                }
            }
        }

        gl.CreateTextures(TextureTarget.Texture2D, 1, out _fpsFontTex);
        gl.TextureStorage2D(_fpsFontTex, 1, SizedInternalFormat.Rgba8, atlasW, atlasH);
        int nearest = (int)TextureMinFilter.Nearest;
        int clamp = (int)TextureWrapMode.ClampToEdge;
        gl.TextureParameter(_fpsFontTex, TextureParameterName.TextureMinFilter, nearest);
        gl.TextureParameter(_fpsFontTex, TextureParameterName.TextureMagFilter, nearest);
        gl.TextureParameter(_fpsFontTex, TextureParameterName.TextureWrapS, clamp);
        gl.TextureParameter(_fpsFontTex, TextureParameterName.TextureWrapT, clamp);
        fixed (uint* p = pixels)
        {
            gl.TextureSubImage2D(_fpsFontTex, 0, 0, 0, atlasW, atlasH, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        gl.CreateBuffers(1, out _fpsVbo);
        gl.NamedBufferStorage(_fpsVbo, (nuint)(FpsOverlayMaxVerts * sizeof(FpsOverlayVertex)), null, BufferStorageMask.DynamicStorageBit);

        gl.CreateVertexArrays(1, out _fpsVao);
        gl.EnableVertexArrayAttrib(_fpsVao, 0);
        gl.VertexArrayAttribFormat(_fpsVao, 0, 2, VertexAttribType.Float, false, 0);
        gl.VertexArrayAttribBinding(_fpsVao, 0, 0);
        gl.EnableVertexArrayAttrib(_fpsVao, 1);
        gl.VertexArrayAttribFormat(_fpsVao, 1, 2, VertexAttribType.Float, false, 8);
        gl.VertexArrayAttribBinding(_fpsVao, 1, 0);
        gl.EnableVertexArrayAttrib(_fpsVao, 2);
        gl.VertexArrayAttribFormat(_fpsVao, 2, 4, VertexAttribType.Float, false, 16);
        gl.VertexArrayAttribBinding(_fpsVao, 2, 0);
        gl.VertexArrayVertexBuffer(_fpsVao, 0, _fpsVbo, 0, (uint)sizeof(FpsOverlayVertex));

        _fpsProgram = CompileGraphicsProgram(gl, FpsVertexShaderSource, FpsFragmentShaderSource);
    }

    public void RenderFpsOverlay(int viewportWidth, int viewportHeight, double fps)
    {
        if (!IsInitialized || _gl == null || viewportWidth <= 0 || viewportHeight <= 0 || _fpsProgram == 0)
            return;

        string text = $"FPS:{fps:F0}";
        int scale = Math.Max(2, viewportHeight / 300);
        int padX = 6;
        int padY = 4;
        int charW = 6 * scale;
        int charH = 8 * scale;
        int boxX = 8;
        int boxY = 8;
        int boxW = text.Length * charW + padX * 2;
        int boxH = charH + padY * 2;

        Span<FpsOverlayVertex> verts = stackalloc FpsOverlayVertex[FpsOverlayMaxVerts];
        int vCount = 0;

        static void AddQuad(Span<FpsOverlayVertex> v, ref int count, int vpW, int vpH, float px, float py, float pw, float ph, float u0, float v0, float u1, float v1, float r, float g, float b, float a)
        {
            if (count + 6 > FpsOverlayMaxVerts) return;
            float x0 = (px / vpW) * 2.0f - 1.0f;
            float x1 = ((px + pw) / vpW) * 2.0f - 1.0f;
            float y0 = 1.0f - (py / vpH) * 2.0f;
            float y1 = 1.0f - ((py + ph) / vpH) * 2.0f;

            v[count++] = new FpsOverlayVertex { X = x0, Y = y0, U = u0, V = v0, R = r, G = g, B = b, A = a };
            v[count++] = new FpsOverlayVertex { X = x1, Y = y0, U = u1, V = v0, R = r, G = g, B = b, A = a };
            v[count++] = new FpsOverlayVertex { X = x0, Y = y1, U = u0, V = v1, R = r, G = g, B = b, A = a };
            v[count++] = new FpsOverlayVertex { X = x1, Y = y0, U = u1, V = v0, R = r, G = g, B = b, A = a };
            v[count++] = new FpsOverlayVertex { X = x1, Y = y1, U = u1, V = v1, R = r, G = g, B = b, A = a };
            v[count++] = new FpsOverlayVertex { X = x0, Y = y1, U = u0, V = v1, R = r, G = g, B = b, A = a };
        }

        // Solid dark background box using glyph 15
        const float invAtlasW = 1.0f / 96.0f;
        const float invAtlasH = 1.0f / 8.0f;
        AddQuad(verts, ref vCount, viewportWidth, viewportHeight, boxX, boxY, boxW, boxH, (15 * 6 + 1) * invAtlasW, 1 * invAtlasH, (15 * 6 + 4) * invAtlasW, 6 * invAtlasH, 0.0f, 0.0f, 0.0f, 0.72f);

        float cr = fps >= 50 ? 0.25f : (fps >= 25 ? 1.0f : 1.0f);
        float cg = fps >= 50 ? 1.0f : (fps >= 25 ? 0.9f : 0.3f);
        float cb = fps >= 50 ? 0.35f : (fps >= 25 ? 0.2f : 0.3f);

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            int glyphIdx = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                'F' => 10,
                'P' => 11,
                'S' => 12,
                ':' => 13,
                '.' => 14,
                _ => -1
            };
            if (glyphIdx < 0) continue;

            float u0 = (glyphIdx * 6) * invAtlasW;
            float u1 = (glyphIdx * 6 + 5) * invAtlasW;
            AddQuad(verts, ref vCount, viewportWidth, viewportHeight, boxX + padX + i * charW, boxY + padY, 5 * scale, 7 * scale, u0, 0.0f, u1, 7.0f * invAtlasH, cr, cg, cb, 1.0f);
        }

        fixed (FpsOverlayVertex* pVerts = verts)
        {
            _gl.NamedBufferSubData(_fpsVbo, 0, (nuint)(vCount * sizeof(FpsOverlayVertex)), pVerts);
        }

        _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.ColorMask(true, true, true, true);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);

        _gl.UseProgram(_fpsProgram);
        _gl.BindTextureUnit(0, _fpsFontTex);
        _gl.BindVertexArray(_fpsVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)vCount);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>
    /// Reads RGBA8 pixel data from the active default backbuffer (bottom-to-top OpenGL row order).
    /// Must be called on the OpenGL context thread after rendering and before SwapBuffers.
    /// </summary>
    public byte[]? CaptureBackbufferRgba(int width, int height)
    {
        if (!IsInitialized || _gl == null || width <= 0 || height <= 0)
            return null;

        byte[] rgba = new byte[width * height * 4];
        _gl.Finish();
        _gl.ReadBuffer(ReadBufferMode.Back);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = rgba)
        {
            _gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        return rgba;
    }

    public void Dispose()
    {
        if (!IsInitialized || _gl == null)
            return;

        if (_vramSsbo != 0) _gl.DeleteBuffer(_vramSsbo);
        if (_paramsUbo != 0) _gl.DeleteBuffer(_paramsUbo);
        if (_displayTexture != 0) _gl.DeleteTexture(_displayTexture);
        if (_emptyVao != 0) _gl.DeleteVertexArray(_emptyVao);
        if (_unswizzleComputeProgram != 0) _gl.DeleteProgram(_unswizzleComputeProgram);
        if (_presentGraphicsProgram != 0) _gl.DeleteProgram(_presentGraphicsProgram);

        if (_primitiveVbo != 0) _gl.DeleteBuffer(_primitiveVbo);
        if (_primitiveEbo != 0) _gl.DeleteBuffer(_primitiveEbo);
        if (_primitiveVao != 0) _gl.DeleteVertexArray(_primitiveVao);
        if (_drawParamsUbo != 0) _gl.DeleteBuffer(_drawParamsUbo);
        if (_primitiveProgram != 0) _gl.DeleteProgram(_primitiveProgram);
        if (_primitiveMsaaProgram != 0) _gl.DeleteProgram(_primitiveMsaaProgram);
        if (_primitiveMsaaDateProgram != 0) _gl.DeleteProgram(_primitiveMsaaDateProgram);
        if (_dummyWhiteTex != 0) _gl.DeleteTexture(_dummyWhiteTex);
        if (_batchFbo != 0) _gl.DeleteFramebuffer(_batchFbo);
        if (_batchColorTex != 0) _gl.DeleteTexture(_batchColorTex);
        if (_batchDepthRbo != 0) _gl.DeleteRenderbuffer(_batchDepthRbo);
        if (_fpsFontTex != 0) _gl.DeleteTexture(_fpsFontTex);
        if (_fpsVbo != 0) _gl.DeleteBuffer(_fpsVbo);
        if (_fpsVao != 0) _gl.DeleteVertexArray(_fpsVao);
        if (_fpsProgram != 0) _gl.DeleteProgram(_fpsProgram);

        foreach (uint tex in _gpuTexCache.Values)
        {
            if (tex != 0) _gl.DeleteTexture(tex);
        }
        _gpuTexCache.Clear();
        _gpuTexOrder.Clear();

        IsInitialized = false;
    }
}
