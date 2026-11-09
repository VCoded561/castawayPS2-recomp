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
    public uint[]? ClutTable;

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
            && SpecA == o.SpecA
            && SpecB == o.SpecB
            && SpecC == o.SpecC
            && SpecD == o.SpecD
            && Fix == o.Fix;
    }
}

/// <summary>
/// State and vertex slice for a single OpenGL draw call.
/// </summary>
public struct GsGlDrawCall
{
    public int StartVertex;
    public int VertexCount;
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
    public const int MaxDrawCalls = 16_384;

    public GsGlVertex[] Vertices { get; } = new GsGlVertex[MaxVertices];
    public GsGlDrawCall[] DrawCalls { get; } = new GsGlDrawCall[MaxDrawCalls];
    public uint[] ClutPool { get; } = new uint[1024 * 256];
    public int ClutPoolCount { get; set; }
    public int VertexCount { get; set; }
    public int DrawCallCount { get; set; }

    public void Reset()
    {
        VertexCount = 0;
        DrawCallCount = 0;
        ClutPoolCount = 0;
    }

    public void AddTriangle(
        in GsDrawContext dc,
        int scx0, int scy0, int scx1, int scy1,
        int ofx, int ofy,
        in GsGlVertex v0, in GsGlVertex v1, in GsGlVertex v2)
    {
        if (VertexCount + 3 > MaxVertices || DrawCallCount >= MaxDrawCalls)
            return;

        int startVtx = VertexCount;
        Vertices[VertexCount++] = v0;
        Vertices[VertexCount++] = v1;
        Vertices[VertexCount++] = v2;

        if (DrawCallCount > 0)
        {
            ref var lastCall = ref DrawCalls[DrawCallCount - 1];
            if (lastCall.PrimType == GsPrimitiveType.Triangle &&
                lastCall.Scx0 == scx0 && lastCall.Scy0 == scy0 &&
                lastCall.Scx1 == scx1 && lastCall.Scy1 == scy1 &&
                lastCall.Ofx == ofx && lastCall.Ofy == ofy &&
                lastCall.DrawContext.StateEquals(in dc))
            {
                lastCall.VertexCount += 3;
                return;
            }
        }

        int clutOffset = -1;
        if (dc.ClutTable != null && ClutPoolCount < 1024)
        {
            clutOffset = ClutPoolCount * 256;
            Array.Copy(dc.ClutTable, 0, ClutPool, clutOffset, 256);
            ClutPoolCount++;
        }

        DrawCalls[DrawCallCount++] = new GsGlDrawCall
        {
            StartVertex = startVtx,
            VertexCount = 3,
            PrimType = GsPrimitiveType.Triangle,
            DrawContext = dc,
            Scx0 = scx0,
            Scy0 = scy0,
            Scx1 = scx1,
            Scy1 = scy1,
            Ofx = ofx,
            Ofy = ofy,
            ClutOffset = clutOffset
        };
    }

    public void AddSprite(
        in GsDrawContext dc,
        int scx0, int scy0, int scx1, int scy1,
        int ofx, int ofy,
        in GsGlVertex v0, in GsGlVertex v1)
    {
        if (VertexCount + 6 > MaxVertices || DrawCallCount >= MaxDrawCalls)
            return;

        int startVtx = VertexCount;

        // Quad split into 2 triangles: (x0, y0), (x1, y0), (x0, y1) and (x1, y0), (x1, y1), (x0, y1)
        var q0 = new GsGlVertex { X = v0.X, Y = v0.Y, Z = v1.Z, W = 1f, R = v1.R, G = v1.G, B = v1.B, A = v1.A, S = v0.S, T = v0.T, Q = v1.Q, U = v0.U, V = v0.V };
        var q1 = new GsGlVertex { X = v1.X, Y = v0.Y, Z = v1.Z, W = 1f, R = v1.R, G = v1.G, B = v1.B, A = v1.A, S = v1.S, T = v0.T, Q = v1.Q, U = v1.U, V = v0.V };
        var q2 = new GsGlVertex { X = v0.X, Y = v1.Y, Z = v1.Z, W = 1f, R = v1.R, G = v1.G, B = v1.B, A = v1.A, S = v0.S, T = v1.T, Q = v1.Q, U = v0.U, V = v1.V };
        var q3 = new GsGlVertex { X = v1.X, Y = v1.Y, Z = v1.Z, W = 1f, R = v1.R, G = v1.G, B = v1.B, A = v1.A, S = v1.S, T = v1.T, Q = v1.Q, U = v1.U, V = v1.V };

        Vertices[VertexCount++] = q0;
        Vertices[VertexCount++] = q1;
        Vertices[VertexCount++] = q2;
        Vertices[VertexCount++] = q1;
        Vertices[VertexCount++] = q3;
        Vertices[VertexCount++] = q2;

        if (DrawCallCount > 0)
        {
            ref var lastCall = ref DrawCalls[DrawCallCount - 1];
            if (lastCall.PrimType == GsPrimitiveType.Triangle &&
                lastCall.Scx0 == scx0 && lastCall.Scy0 == scy0 &&
                lastCall.Scx1 == scx1 && lastCall.Scy1 == scy1 &&
                lastCall.Ofx == ofx && lastCall.Ofy == ofy &&
                lastCall.DrawContext.StateEquals(in dc))
            {
                lastCall.VertexCount += 6;
                return;
            }
        }

        int clutOffset = -1;
        if (dc.ClutTable != null && ClutPoolCount < 1024)
        {
            clutOffset = ClutPoolCount * 256;
            Array.Copy(dc.ClutTable, 0, ClutPool, clutOffset, 256);
            ClutPoolCount++;
        }

        DrawCalls[DrawCallCount++] = new GsGlDrawCall
        {
            StartVertex = startVtx,
            VertexCount = 6,
            PrimType = GsPrimitiveType.Triangle,
            DrawContext = dc,
            Scx0 = scx0,
            Scy0 = scy0,
            Scx1 = scx1,
            Scy1 = scy1,
            Ofx = ofx,
            Ofy = ofy,
            ClutOffset = clutOffset
        };
    }
}
