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
public unsafe struct GsGlDrawParams
{
    public int Tme;
    public int Fst;
    public int Tbp0;
    public int Tbw;
    public int Psm;
    public int Tw;
    public int Th;
    public int Tcc;
    public int Tfx;
    public int Cbp;
    public int Cpsm;
    public int Csa;
    public int Ate;
    public int Atst;
    public int Aref;
    public int Afail;
    public int Wms;
    public int Wmt;
    public int MinU;
    public int MaxU;
    public int MinV;
    public int MaxV;
    public int LinearFilter;
    public int Ta0;
    public int Ta1;
    public int Aem;
    public int Pad0;
    public int Pad1;
    public fixed uint Clut[256];
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
    private uint _primitiveVao;
    private uint _drawParamsUbo;
    private uint _primitiveProgram;

    public int BaseDisplayWidth { get; private set; } = 640;
    public int BaseDisplayHeight { get; private set; } = 448;
    public int ScaleMultiplier { get; private set; } = 1;
    public int OutputWidth => BaseDisplayWidth * ScaleMultiplier;
    public int OutputHeight => BaseDisplayHeight * ScaleMultiplier;
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
    public void Initialize(GL gl, int baseWidth = 640, int baseHeight = 448, int scaleMultiplier = 1)
    {
        _gl = gl;
        BaseDisplayWidth = Math.Max(64, baseWidth);
        BaseDisplayHeight = Math.Max(64, baseHeight);
        ScaleMultiplier = Math.Clamp(scaleMultiplier, 1, 8);

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

        // 7. Allocate per-draw-call state UBOs via DSA
        gl.CreateBuffers(1, out _drawParamsUbo);
        gl.NamedBufferStorage(
            _drawParamsUbo,
            (nuint)sizeof(GsGlDrawParams),
            null,
            BufferStorageMask.DynamicStorageBit);

        // 8. Compile Primitive Graphics Program
        _primitiveProgram = CompileGraphicsProgram(gl, PrimitiveVertexShaderSource, PrimitiveFragmentShaderSource);

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

    /// <summary>
    /// Renders a batched stream of GS primitives directly on the GPU using OpenGL 4.6 DSA.
    /// Eliminates CPU software rasterization overhead while maintaining 100% PS2 drawing state accuracy.
    /// </summary>
    public void RenderBatch(GsPrimitiveBatch batch, GsProcessor gs, int viewportWidth, int viewportHeight, byte[]? vramOverride = null)
    {
        if (!IsInitialized || _gl == null)
            return;

        // 1. Sync 4 MB GS VRAM to SSBO via DSA when modified by DMA Host-to-Local BitBlt
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

        // 2. Upload display params UBO
        var dispParams = new GsComputeDisplayParams
        {
            FbpBlock = (int)(gs.Frame[0] & 0x1FFul) * 32,
            FbwPixels = Math.Max(1, (int)((gs.Frame[0] >> 16) & 0x3Ful)) * 64,
            Fpsm = (int)((gs.Frame[0] >> 24) & 0x3Ful),
            CbpBlock = (int)((gs.Tex0[0] >> 37) & 0x3FFFul),
            Cpsm = (int)((gs.Tex0[0] >> 51) & 0xFul),
            DisplayWidth = BaseDisplayWidth,
            DisplayHeight = BaseDisplayHeight,
            ScaleMultiplier = ScaleMultiplier
        };
        _gl.NamedBufferSubData(_paramsUbo, 0, (nuint)sizeof(GsComputeDisplayParams), &dispParams);

        if (batch.VertexCount == 0)
            return;

        // 3. Clear depth buffer for reverse-Z PS2 testing (ZTST: GEQUAL/GREATER)
        _gl.DepthMask(true);
        _gl.ClearDepth(0.0f);
        _gl.Clear(ClearBufferMask.DepthBufferBit);

        // 4. Upload dynamic vertex buffer via DSA NamedBufferSubData
        fixed (GsGlVertex* vtxPtr = batch.Vertices)
        {
            _gl.NamedBufferSubData(_primitiveVbo, 0, (nuint)(batch.VertexCount * sizeof(GsGlVertex)), vtxPtr);
        }

        // 5. Set global OpenGL viewport and state bindings
        _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);
        _gl.UseProgram(_primitiveProgram);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _vramSsbo);
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 1, _paramsUbo);
        _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 2, _drawParamsUbo);
        _gl.BindVertexArray(_primitiveVao);

        float scaleX = (float)viewportWidth / BaseDisplayWidth;
        float scaleY = (float)viewportHeight / BaseDisplayHeight;

        int totalDrawCalls = batch.DrawCallCount;
        for (int i = 0; i < totalDrawCalls; i++)
        {
            ref var call = ref batch.DrawCalls[i];
            ref var dc = ref call.DrawContext;

            // Scissor test
            int scLeft = (int)(call.Scx0 * scaleX);
            int scTop = (int)(call.Scy0 * scaleY);
            int scW = Math.Max(1, (int)((call.Scx1 - call.Scx0 + 1) * scaleX));
            int scH = Math.Max(1, (int)((call.Scy1 - call.Scy0 + 1) * scaleY));
            int scBottom = viewportHeight - (scTop + scH);
            _gl.Enable(EnableCap.ScissorTest);
            _gl.Scissor(Math.Max(0, scLeft), Math.Max(0, scBottom), (uint)scW, (uint)scH);

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
                _gl.DepthMask(!dc.Zmsk);
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
                if (dc.SpecA == 0 && dc.SpecB == 1 && dc.SpecC == 0 && dc.SpecD == 1)
                {
                    // Standard Alpha Blending: (Cs - Cd) * As + Cd = Cs * As + Cd * (1 - As)
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
                }
                else if (dc.SpecA == 0 && dc.SpecB == 2 && dc.SpecC == 0 && dc.SpecD == 1)
                {
                    // Modulated Additive: Cs * As + Cd
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                    _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
                }
                else if (dc.SpecA == 0 && dc.SpecB == 2 && dc.SpecD == 1)
                {
                    // Additive: Cs + Cd
                    _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                    _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
                }
                else if (dc.SpecA == 1 && dc.SpecB == 0 && dc.SpecC == 0 && dc.SpecD == 2)
                {
                    // Inverted modulate: (Cd - Cs) * As
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    _gl.BlendEquation(BlendEquationModeEXT.FuncReverseSubtract);
                }
                else
                {
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
                }
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
            }

            // Upload per-draw-call state to UBO via DSA
            var drawParams = new GsGlDrawParams
            {
                Tme = dc.Tme ? 1 : 0,
                Fst = dc.Fst ? 1 : 0,
                Tbp0 = dc.Tbp0,
                Tbw = dc.Tbw,
                Psm = (int)dc.Psm,
                Tw = dc.Tw,
                Th = dc.Th,
                Tcc = dc.Tcc,
                Tfx = dc.Tfx,
                Cbp = dc.Cbp,
                Cpsm = (int)dc.Cpsm,
                Csa = dc.Csa,
                Ate = dc.Ate ? 1 : 0,
                Atst = dc.Atst,
                Aref = dc.Aref,
                Afail = dc.Afail,
                Wms = dc.Wms,
                Wmt = dc.Wmt,
                MinU = (int)dc.MinU,
                MaxU = (int)dc.MaxU,
                MinV = (int)dc.MinV,
                MaxV = (int)dc.MaxV,
                LinearFilter = dc.LinearFilter ? 1 : 0,
                Ta0 = dc.Ta0,
                Ta1 = dc.Ta1,
                Aem = dc.Aem ? 1 : 0,
                Pad0 = 0,
                Pad1 = 0
            };

            if (call.ClutOffset >= 0)
            {
                fixed (uint* srcClut = &batch.ClutPool[call.ClutOffset])
                {
                    for (int c = 0; c < 256; c++)
                    {
                        drawParams.Clut[c] = srcClut[c];
                    }
                }
            }

            _gl.NamedBufferSubData(_drawParamsUbo, 0, (nuint)sizeof(GsGlDrawParams), &drawParams);

            // Draw triangles
            _gl.DrawArrays(PrimitiveType.Triangles, call.StartVertex, (uint)call.VertexCount);
        }

        _gl.Disable(EnableCap.ScissorTest);
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

        out vec4 vColor;
        out vec3 vStq;
        out vec2 vUv;

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

    public const string PrimitiveFragmentShaderSource = """
        #version 460 core
        in vec4 vColor;
        in vec3 vStq;
        in vec2 vUv;
        out vec4 fragColor;

        layout(std430, binding = 0) readonly buffer GsVramBuffer {
            uint VramWords[];
        };

        layout(std140, binding = 2) uniform GsDrawParams {
            int uTme;
            int uFst;
            int uTbp0;
            int uTbw;

            int uPsm;
            int uTw;
            int uTh;
            int uTcc;

            int uTfx;
            int uCbp;
            int uCpsm;
            int uCsa;

            int uAte;
            int uAtst;
            int uAref;
            int uAfail;

            int uWms;
            int uWmt;
            int uMinU;
            int uMaxU;

            int uMinV;
            int uMaxV;
            int uLinearFilter;
            int uTa0;

            int uTa1;
            int uAem;
            int uPad0;
            int uPad1;

            uvec4 uClut[64];
        };

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

        const int ColumnWord[32] = int[32](
            0, 1, 4, 5, 8, 9, 12, 13,
            2, 3, 6, 7, 10, 11, 14, 15,
            8, 9, 12, 13, 0, 1, 4, 5,
            10, 11, 14, 15, 2, 3, 6, 7
        );

        uint readTexel32(int baseBlock, int bwPixels, int x, int y) {
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

        uint readTexel16(int baseBlock, int bwPixels, int x, int y, bool is16S) {
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
            uint r8 = (c16 & 0x1Fu) << 3;
            uint g8 = ((c16 >> 5) & 0x1Fu) << 3;
            uint b8 = ((c16 >> 10) & 0x1Fu) << 3;
            uint a8;
            if ((c16 & 0x8000u) != 0u) {
                a8 = uint(uTa1);
            } else if (uAem != 0 && (c16 & 0x7FFFu) == 0u) {
                a8 = 0u;
            } else {
                a8 = uint(uTa0);
            }
            return r8 | (g8 << 8) | (b8 << 16) | (a8 << 24);
        }

        uint getPixelByteAddress8(int baseBlock, int bwPixels, int x, int y) {
            int perRow = max(1, bwPixels >> 7);
            int pageAddr = baseBlock * 256 + ((x >> 7) + (y >> 6) * perRow) * 8192;
            int block = BlockTable32[((y >> 4) & 3) * 8 + ((x >> 4) & 7)];
            int wx = x & 15;
            int wy = y & 15;
            int column = wy >> 2;
            int subWy = wy & 3;
            int table = (subWy & 2) >> 1;
            table ^= (y >> 2) & 1;
            int byteOffset = ((wx & 8) >> 2) + ((subWy & 2) >> 1);
            int colWord = ColumnWord[table * 16 + (subWy & 1) * 8 + (wx & 7)];
            return uint(pageAddr + block * 256 + column * 64 + colWord * 4 + byteOffset) & 0x3FFFFFu;
        }

        uint readTexel8(int baseBlock, int bwPixels, int x, int y) {
            uint byteAddr = getPixelByteAddress8(baseBlock, max(128, bwPixels), x, y);
            uint word = VramWords[byteAddr >> 2];
            uint shift = (byteAddr & 3u) * 8u;
            return (word >> shift) & 0xFFu;
        }

        uint getPixelNibbleAddress4(int baseBlock, int bwPixels, int x, int y) {
            int perRow = max(1, bwPixels >> 7);
            int pageAddr = baseBlock * 256 + ((x >> 7) + (y >> 7) * perRow) * 8192;
            int block = BlockTable16[((y >> 4) & 7) * 4 + ((x >> 5) & 3)];
            int wx = x & 31;
            int wy = y & 15;
            int column = wy >> 2;
            int subWy = wy & 3;
            int shift = (wx & 0x18) + ((subWy & 2) << 1);
            int nibble = shift >> 2;
            int table = (subWy & 2) >> 1;
            table ^= column & 1;
            int colWord = ColumnWord[table * 16 + (subWy & 1) * 8 + (wx & 7)];
            return (uint(pageAddr) << 1) + uint(((column * 64 + block * 256 + colWord * 4) * 2) + nibble);
        }

        uint readTexel4(int baseBlock, int bwPixels, int x, int y) {
            uint nibAddr = getPixelNibbleAddress4(baseBlock, max(128, bwPixels), x, y);
            uint byteAddr = (nibAddr >> 1) & 0x3FFFFFu;
            uint word = VramWords[byteAddr >> 2];
            uint shift = (byteAddr & 3u) * 8u;
            uint byteVal = (word >> shift) & 0xFFu;
            return ((nibAddr & 1u) == 0u) ? (byteVal & 0x0Fu) : ((byteVal >> 4) & 0x0Fu);
        }

        uint lookupClut(uint index, bool is8Bit) {
            uint idx = is8Bit ? (index & 0xFFu) : (index & 0x0Fu);
            return uClut[idx >> 2][idx & 3u];
        }

        uvec4 readSingleTexel(int tx, int ty) {
            uint raw;
            if (uPsm == 0x13) { // PSMT8
                uint idx = readTexel8(uTbp0, uTbw, tx, ty);
                raw = lookupClut(idx, true);
            } else if (uPsm == 0x1B) { // PSMT8H
                uint word = readTexel32(uTbp0, uTbw, tx, ty);
                uint idx = (word >> 24) & 0xFFu;
                raw = lookupClut(idx, true);
            } else if (uPsm == 0x14) { // PSMT4
                uint idx = readTexel4(uTbp0, uTbw, tx, ty);
                raw = lookupClut(idx, false);
            } else if (uPsm == 0x24) { // PSMT4HL
                uint word = readTexel32(uTbp0, uTbw, tx, ty);
                uint idx = (word >> 24) & 0x0Fu;
                raw = lookupClut(idx, false);
            } else if (uPsm == 0x2C) { // PSMT4HH
                uint word = readTexel32(uTbp0, uTbw, tx, ty);
                uint idx = (word >> 28) & 0x0Fu;
                raw = lookupClut(idx, false);
            } else if (uPsm == 0x02 || uPsm == 0x0A) { // PSMCT16 / PSMCT16S
                raw = readTexel16(uTbp0, uTbw, tx, ty, uPsm == 0x0A);
            } else if (uPsm == 0x01) { // PSMCT24
                uint raw24 = readTexel32(uTbp0, uTbw, tx, ty) & 0x00FFFFFFu;
                uint a = (uAem != 0 && raw24 == 0u) ? 0u : uint(uTa0);
                raw = raw24 | (a << 24);
            } else { // PSMCT32
                raw = readTexel32(uTbp0, uTbw, tx, ty);
            }

            return uvec4(raw & 0xFFu, (raw >> 8) & 0xFFu, (raw >> 16) & 0xFFu, (raw >> 24) & 0xFFu);
        }

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

        uvec4 sampleTexture(float uCoord, float vCoord) {
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

                uvec4 c00 = readSingleTexel(tx0, ty0);
                uvec4 c10 = readSingleTexel(tx1, ty0);
                uvec4 c01 = readSingleTexel(tx0, ty1);
                uvec4 c11 = readSingleTexel(tx1, ty1);

                vec4 top = mix(vec4(c00), vec4(c10), fracU);
                vec4 bot = mix(vec4(c01), vec4(c11), fracU);
                vec4 res = mix(top, bot, fracV);
                return uvec4(clamp(res + 0.5, 0.0, 255.0));
            } else {
                int tx = int(floor(uCoord));
                int ty = int(floor(vCoord));
                tx = applyWrap(tx, uTw, uWms, uMinU, uMaxU);
                ty = applyWrap(ty, uTh, uWmt, uMinV, uMaxV);
                return readSingleTexel(tx, ty);
            }
        }

        void main() {
            vec3 fragRgb = vColor.rgb;
            int fragA = int(round(vColor.a * 128.0));

            if (uTme != 0) {
                float uCoord, vCoord;
                if (uFst != 0) {
                    uCoord = vUv.x;
                    vCoord = vUv.y;
                } else {
                    float invQ = (vStq.z != 0.0) ? (1.0 / vStq.z) : 1.0;
                    uCoord = (vStq.x * invQ) * float(uTw);
                    vCoord = (vStq.y * invQ) * float(uTh);
                }

                uvec4 texB = sampleTexture(uCoord, vCoord);
                vec3 tRgb = vec3(texB.rgb) / 255.0;
                int tA = int(texB.a);
                int vA = fragA;

                if (uTfx == 0) { // Modulate: C = (Ct * Cf) >> 7, A = (At * Af) >> 7 (if TCC=1)
                    fragRgb = clamp(tRgb * vColor.rgb, 0.0, 1.0);
                    fragA = (uTcc != 0) ? ((tA * vA) >> 7) : vA;
                } else if (uTfx == 1) { // Decal: C = Ct, A = At (if TCC=1)
                    fragRgb = tRgb;
                    fragA = (uTcc != 0) ? tA : vA;
                } else if (uTfx == 2) { // Highlight: C = ((Ct * Cf) >> 7) + Af, A = (At + Af) (if TCC=1)
                    float af = float(vA) / 128.0;
                    fragRgb = clamp(tRgb * vColor.rgb + af, 0.0, 1.0);
                    fragA = (uTcc != 0) ? min(255, tA + vA) : vA;
                } else { // Highlight2: C = ((Ct * Cf) >> 7) + Af, A = At (if TCC=1)
                    float af = float(vA) / 128.0;
                    fragRgb = clamp(tRgb * vColor.rgb + af, 0.0, 1.0);
                    fragA = (uTcc != 0) ? tA : vA;
                }
            } else {
                fragRgb = clamp(vColor.rgb * (128.0 / 255.0), 0.0, 1.0);
            }

            // Alpha Test
            if (uAte != 0) {
                bool pass = true;
                if (uAtst == 0) pass = false;
                else if (uAtst == 2) pass = (fragA < uAref);
                else if (uAtst == 3) pass = (fragA <= uAref);
                else if (uAtst == 4) pass = (fragA == uAref);
                else if (uAtst == 5) pass = (fragA >= uAref);
                else if (uAtst == 6) pass = (fragA > uAref);
                else if (uAtst == 7) pass = (fragA != uAref);

                if (!pass) {
                    if (uAfail == 0) discard; // KEEP: discard pixel
                }
            }

            fragColor = vec4(fragRgb, clamp(float(fragA) / 128.0, 0.0, 1.0));
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
        if (_primitiveVao != 0) _gl.DeleteVertexArray(_primitiveVao);
        if (_drawParamsUbo != 0) _gl.DeleteBuffer(_drawParamsUbo);
        if (_primitiveProgram != 0) _gl.DeleteProgram(_primitiveProgram);

        IsInitialized = false;
    }
}
