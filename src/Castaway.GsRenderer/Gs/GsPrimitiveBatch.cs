using System.Runtime.InteropServices;

namespace Castaway.GsRenderer.Gs;

public enum GsRendererMode : byte
{
    Software = 0,
    OpenGl   = 1
}

/// <summary>
/// Packed vertex formatted for OpenGL 4.6 DSA attribute buffers.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GsGlVertex
{
    public float X, Y, Z, W;     // Position: Screen space (X, Y) and Depth (Z in 0..1), W=1.0f
    public float R, G, B, A;     // Color: Normalized 0.0f .. 1.0f
    public float S, T, Q;        // Texture coordinates: STQ
    public float U, V;           // Raw UV texels
}

/// <summary>
/// Decoded GS drawing state for a primitive or draw call batch.
/// </summary>
public struct GsDrawContext
{
    public int Context;
    public bool Gouraud;
    public bool Tme;
    public bool Abe;
    public bool Fst;

    public int FbpBlock;
    public int FbwPixels;
    public GsPsm Fpsm;
    public uint Fbmsk;

    public int ZbpBlock;
    public GsPsm Zpsm;
    public bool Zmsk;

    public bool Ate;
    public int Atst;
    public byte Aref;
    public int Afail;
    public bool Date;
    public int Datm;
    public bool Zte;
    public int Ztst;

    public int Tbp0;
    public int Tbw;
    public GsPsm Psm;
    public int Tw;
    public int Th;
    public int Tcc;
    public int Tfx;
    public int Cbp;
    public GsPsm Cpsm;
    public int Csa;

    public int Wms;
    public int Wmt;
    public uint MinU;
    public uint MaxU;
    public uint MinV;
    public uint MaxV;

    public byte Ta0;
    public byte Ta1;
    public bool Aem;
    public bool LinearFilter;

    public int SpecA;
    public int SpecB;
    public int SpecC;
    public int SpecD;
    public byte Fix;
    public bool Colclamp;
    public bool Fba;

    public bool CanEarlyZ;
    public int StateVersion;
    public uint[]? ClutTable;
    public uint[]? DecodedTexture;
    public ulong DecodedTextureHash64;
    public int TexIndex;

    public readonly bool StateEquals(in GsDrawContext o)
    {
        return Context == o.Context
            && Gouraud == o.Gouraud
            && Tme == o.Tme
            && Abe == o.Abe
            && Fst == o.Fst
            && FbpBlock == o.FbpBlock
            && FbwPixels == o.FbwPixels
            && Fpsm == o.Fpsm
            && Fbmsk == o.Fbmsk
            && ZbpBlock == o.ZbpBlock
            && Zpsm == o.Zpsm
            && Zmsk == o.Zmsk
            && Ate == o.Ate
            && Atst == o.Atst
            && Aref == o.Aref
            && Afail == o.Afail
            && Date == o.Date
            && Datm == o.Datm
            && Zte == o.Zte
            && Ztst == o.Ztst
            && Tbp0 == o.Tbp0
            && Tbw == o.Tbw
            && Psm == o.Psm
            && Tw == o.Tw
            && Th == o.Th
            && Tcc == o.Tcc
            && Tfx == o.Tfx
            && Cbp == o.Cbp
            && Cpsm == o.Cpsm
            && Csa == o.Csa
            && Wms == o.Wms
            && Wmt == o.Wmt
            && MinU == o.MinU
            && MaxU == o.MaxU
            && MinV == o.MinV
            && MaxV == o.MaxV
            && Ta0 == o.Ta0
            && Ta1 == o.Ta1
            && Aem == o.Aem
            && LinearFilter == o.LinearFilter
            && SpecA == o.SpecA
            && SpecB == o.SpecB
            && SpecC == o.SpecC
            && SpecD == o.SpecD
            && Fix == o.Fix
            && Colclamp == o.Colclamp
            && Fba == o.Fba
            && TexIndex == o.TexIndex;
    }
}

public struct GsBatchTexture
{
    public int Width;
    public int Height;
    public ulong Hash64;
    public uint[] Pixels;
}

/// <summary>
/// State and vertex slice for a single OpenGL draw call.
/// </summary>
public struct GsGlDrawCall
{
    public int StartVertex;
    public int VertexCount;
    public int StartIndex;
    public int IndexCount;
    public GsPrimitiveType PrimType;
    public GsDrawContext DrawContext;
    public int Scx0, Scy0, Scx1, Scy1;
    public int Ofx, Ofy;
    public int ClutOffset;
}

/// <summary>
/// Pre-allocated double-buffered primitive batch for lock-free transmission from the
/// guest EE/VU1 thread to the host OpenGL rendering thread.
/// </summary>
public sealed class GsPrimitiveBatch
{
    public const int MaxVertices = 300_000;
    public const int MaxIndices = 600_000;
    public const int MaxDrawCalls = 16_384;

    public GsGlVertex[] Vertices { get; } = new GsGlVertex[MaxVertices];
    public uint[] Indices { get; } = new uint[MaxIndices];
    public GsGlDrawCall[] DrawCalls { get; } = new GsGlDrawCall[MaxDrawCalls];
    public uint[] ClutPool { get; } = new uint[1024 * 256];
    public List<GsBatchTexture> Textures { get; } = new(256);
    private readonly Dictionary<(ulong Hash64, int Width, int Height), int> _frameTexDedup = new(256);
    public int ClutPoolCount { get; set; }
    public int VertexCount { get; set; }
    public int IndexCount { get; set; }
    public int DrawCallCount { get; set; }

    public void Reset()
    {
        VertexCount = 0;
        IndexCount = 0;
        DrawCallCount = 0;
        ClutPoolCount = 0;
        Textures.Clear();
        _frameTexDedup.Clear();
    }

    public int RegisterTexture(int width, int height, ulong hash64, uint[] srcPixels)
    {
        var dedupKey = (hash64, width, height);
        if (_frameTexDedup.TryGetValue(dedupKey, out int existingIdx))
            return existingIdx;

        int idx = Textures.Count;
        // Share immutable/versioned array reference from GsProcessor's cache (copy only if needed?
        // Actually GsProcessor allocates a fresh uint[] when a texture's VRAM revision changes,
        // so existing uint[] arrays are never mutated in-place!).
        Textures.Add(new GsBatchTexture
        {
            Width = width,
            Height = height,
            Hash64 = hash64,
            Pixels = srcPixels
        });
        _frameTexDedup[dedupKey] = idx;
        return idx;
    }

    public void AddTriangle(
        in GsDrawContext dc,
        int scx0, int scy0, int scx1, int scy1,
        int ofx, int ofy,
        in GsGlVertex v0, in GsGlVertex v1, in GsGlVertex v2)
    {
        if (VertexCount + 3 > MaxVertices || IndexCount + 3 > MaxIndices || DrawCallCount >= MaxDrawCalls)
            return;

        int startVtx = VertexCount;
        int startIdx = IndexCount;
        Vertices[VertexCount++] = v0;
        Vertices[VertexCount++] = v1;
        Vertices[VertexCount++] = v2;
        uint b = (uint)startVtx;
        Indices[IndexCount++] = b;
        Indices[IndexCount++] = b + 1u;
        Indices[IndexCount++] = b + 2u;

        if (DrawCallCount > 0)
        {
            ref var lastCall = ref DrawCalls[DrawCallCount - 1];
            if (lastCall.PrimType == GsPrimitiveType.Triangle &&
                lastCall.Scx0 == scx0 && lastCall.Scy0 == scy0 &&
                lastCall.Scx1 == scx1 && lastCall.Scy1 == scy1 &&
                lastCall.Ofx == ofx && lastCall.Ofy == ofy &&
                ((lastCall.DrawContext.StateVersion != 0 && lastCall.DrawContext.StateVersion == dc.StateVersion) ||
                 lastCall.DrawContext.StateEquals(in dc)))
            {
                lastCall.VertexCount += 3;
                lastCall.IndexCount += 3;
                return;
            }
        }

        DrawCalls[DrawCallCount++] = new GsGlDrawCall
        {
            StartVertex = startVtx,
            VertexCount = 3,
            StartIndex = startIdx,
            IndexCount = 3,
            PrimType = GsPrimitiveType.Triangle,
            DrawContext = dc,
            Scx0 = scx0,
            Scy0 = scy0,
            Scx1 = scx1,
            Scy1 = scy1,
            Ofx = ofx,
            Ofy = ofy,
            ClutOffset = -1
        };
    }

    public int PrepareTriangleDrawCall(
        in GsDrawContext dc,
        int scx0, int scy0, int scx1, int scy1,
        int ofx, int ofy,
        out bool createdNew)
    {
        if (DrawCallCount > 0)
        {
            ref var lastCall = ref DrawCalls[DrawCallCount - 1];
            if (lastCall.PrimType == GsPrimitiveType.Triangle &&
                lastCall.Scx0 == scx0 && lastCall.Scy0 == scy0 &&
                lastCall.Scx1 == scx1 && lastCall.Scy1 == scy1 &&
                lastCall.Ofx == ofx && lastCall.Ofy == ofy &&
                ((lastCall.DrawContext.StateVersion != 0 && lastCall.DrawContext.StateVersion == dc.StateVersion) ||
                 lastCall.DrawContext.StateEquals(in dc)))
            {
                createdNew = false;
                return DrawCallCount - 1;
            }
        }

        if (DrawCallCount >= MaxDrawCalls)
        {
            createdNew = false;
            return -1;
        }

        int idx = DrawCallCount++;
        DrawCalls[idx] = new GsGlDrawCall
        {
            StartVertex = VertexCount,
            VertexCount = 0,
            StartIndex = IndexCount,
            IndexCount = 0,
            PrimType = GsPrimitiveType.Triangle,
            DrawContext = dc,
            Scx0 = scx0,
            Scy0 = scy0,
            Scx1 = scx1,
            Scy1 = scy1,
            Ofx = ofx,
            Ofy = ofy,
            ClutOffset = -1
        };
        createdNew = true;
        return idx;
    }

    public void RollbackEmptyDrawCall(int idx, bool createdNew)
    {
        if (createdNew && idx == DrawCallCount - 1 && DrawCalls[idx].IndexCount == 0)
        {
            DrawCallCount--;
        }
    }

    public void AddSprite(
        in GsDrawContext dc,
        int scx0, int scy0, int scx1, int scy1,
        int ofx, int ofy,
        in GsGlVertex v0, in GsGlVertex v1)
    {
        if (VertexCount + 4 > MaxVertices || IndexCount + 6 > MaxIndices || DrawCallCount >= MaxDrawCalls)
            return;

        int startVtx = VertexCount;
        int startIdx = IndexCount;

        // Quad split into 2 indexed triangles (4 vertices, 6 indices)
        var q0 = new GsGlVertex { X = v0.X, Y = v0.Y, Z = v1.Z, W = 1f, R = v1.R, G = v1.G, B = v1.B, A = v1.A, S = v0.S, T = v0.T, Q = v1.Q, U = v0.U, V = v0.V };
        var q1 = new GsGlVertex { X = v1.X, Y = v0.Y, Z = v1.Z, W = 1f, R = v1.R, G = v1.G, B = v1.B, A = v1.A, S = v1.S, T = v0.T, Q = v1.Q, U = v1.U, V = v0.V };
        var q2 = new GsGlVertex { X = v0.X, Y = v1.Y, Z = v1.Z, W = 1f, R = v1.R, G = v1.G, B = v1.B, A = v1.A, S = v0.S, T = v1.T, Q = v1.Q, U = v0.U, V = v1.V };
        var q3 = new GsGlVertex { X = v1.X, Y = v1.Y, Z = v1.Z, W = 1f, R = v1.R, G = v1.G, B = v1.B, A = v1.A, S = v1.S, T = v1.T, Q = v1.Q, U = v1.U, V = v1.V };

        Vertices[VertexCount++] = q0;
        Vertices[VertexCount++] = q1;
        Vertices[VertexCount++] = q2;
        Vertices[VertexCount++] = q3;
        uint b = (uint)startVtx;
        Indices[IndexCount++] = b;
        Indices[IndexCount++] = b + 1u;
        Indices[IndexCount++] = b + 2u;
        Indices[IndexCount++] = b + 1u;
        Indices[IndexCount++] = b + 3u;
        Indices[IndexCount++] = b + 2u;

        if (DrawCallCount > 0)
        {
            ref var lastCall = ref DrawCalls[DrawCallCount - 1];
            if (lastCall.PrimType == GsPrimitiveType.Triangle &&
                lastCall.Scx0 == scx0 && lastCall.Scy0 == scy0 &&
                lastCall.Scx1 == scx1 && lastCall.Scy1 == scy1 &&
                lastCall.Ofx == ofx && lastCall.Ofy == ofy &&
                ((lastCall.DrawContext.StateVersion != 0 && lastCall.DrawContext.StateVersion == dc.StateVersion) ||
                 lastCall.DrawContext.StateEquals(in dc)))
            {
                lastCall.VertexCount += 6;
                lastCall.IndexCount += 6;
                return;
            }
        }

        DrawCalls[DrawCallCount++] = new GsGlDrawCall
        {
            StartVertex = startVtx,
            VertexCount = 6,
            StartIndex = startIdx,
            IndexCount = 6,
            PrimType = GsPrimitiveType.Triangle,
            DrawContext = dc,
            Scx0 = scx0,
            Scy0 = scy0,
            Scx1 = scx1,
            Scy1 = scy1,
            Ofx = ofx,
            Ofy = ofy,
            ClutOffset = -1
        };
    }
}
