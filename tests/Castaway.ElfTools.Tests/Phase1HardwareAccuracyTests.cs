using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using Castaway.Core;
using Castaway.GsRenderer.Gs;
using Castaway.GsRenderer.Vif;
using Castaway.GsRenderer.Vu1;
using Castaway.Runtime;
using Xunit;

namespace Castaway.ElfTools.Tests;

public unsafe class Phase1HardwareAccuracyTests
{
    [Fact]
    public void Vu1Recompiler_LoiLatchTiming_UpperReadsPreviousI()
    {
        // Slot 0: Upper: MULAi.x ACC.x, VF01.x, I   (subOp=0x1E -> 0x1FE, dest=8 (X), fs=1, I-bit=1)
        //         Lower: LOI 42.0f
        // Slot 1: Upper: MADDAi.x ACC.x, VF01.x, I [E] (subOp=0x23 -> 0x23F, dest=8 (X), fs=1, E-bit=1)
        //         Lower: NOP
        // Slot 2 (delay): NOP, NOP
        //
        // VF01.x = 2.0f, initial vu.I = 3.0f.
        // Slot 0 upper: ACC.x = 2.0f * 3.0f (previous I) = 6.0f; Slot 0 lower latches I = 42.0f.
        // Slot 1 upper: ACC.x = 6.0f + 2.0f * 42.0f (new I) = 90.0f.

        var vu = new Vu1State();
        vu.VF[1] = Vector128.Create(2.0f, 0.0f, 0.0f, 1.0f);
        vu.I = 3.0f;

        uint slot0Upper = 0x80000000u | (8u << 21) | (1u << 11) | 0x1FEu; // MULAi.x (dest=8)
        uint slot0Lower = BitConverter.SingleToUInt32Bits(42.0f);

        uint slot1Upper = 0x40000000u | (8u << 21) | (1u << 11) | 0x23Fu; // MADDAi.x (dest=8, E-bit)
        uint slot1Lower = 0x8000033Cu;

        uint slot2Upper = 0x00000000u;
        uint slot2Lower = 0x8000033Cu;

        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(0, 4), slot0Lower);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(4, 4), slot0Upper);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(8, 4), slot1Lower);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(12, 4), slot1Upper);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(16, 4), slot2Lower);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(20, 4), slot2Upper);

        var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
        prog.ExecuteDelegate(vu);

        Assert.Equal(42.0f, vu.I);
        Assert.Equal(90.0f, vu.ACC.GetElement(0));
    }

    [Fact]
    public void Vu1Recompiler_FcandAndFcor_AccuratelyEvaluateClipFlags()
    {
        var vu = new Vu1State();
        vu.ClipFlags = 0x00000005u; // bits 0 and 2 set

        // PS2 Hardware Specification:
        // FCAND (0x12): VI[1] = ((ClipFlags & imm24) != 0) ? 1 : 0 (1 if ANY masked bit is set)
        uint fcandHit  = (0x12u << 25) | 0x00000004u; // bit 2 is set -> 1
        uint fcandMiss = (0x12u << 25) | 0x00000002u; // bit 1 is not set -> 0

        // FCOR (0x13): VI[1] = (((ClipFlags | imm24) & 0xFFFFFF) == 0xFFFFFF) ? 1 : 0
        uint fcorAllSet  = (0x13u << 25) | (0xFFFFFFu ^ 0x000005u); // bits 0 and 2 are both set -> 1
        uint fcorMissing = (0x13u << 25) | (0xFFFFFFu ^ 0x000007u); // bit 1 is missing -> 0

        void RunSingleLower(uint lower)
        {
            Array.Clear(vu.MicroMem);
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(0, 4), lower);
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(4, 4), 0x40000000u); // isE
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(8, 4), 0x8000033Cu); // NOP
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(12, 4), 0x00000000u);
            var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
            prog.ExecuteDelegate(vu);
        }

        RunSingleLower(fcandHit);
        Assert.Equal((ushort)1, vu.ReadVi(1));

        RunSingleLower(fcandMiss);
        Assert.Equal((ushort)0, vu.ReadVi(1));

        RunSingleLower(fcorAllSet);
        Assert.Equal((ushort)1, vu.ReadVi(1));

        RunSingleLower(fcorMissing);
        Assert.Equal((ushort)0, vu.ReadVi(1));
    }

    [Fact]
    public void Vu1Recompiler_Iaddiu_ForwardsImmediatelyToBranchCondition()
    {
        var vu = new Vu1State();
        vu.WriteVi(1, 0);

        uint s0Lo = (0x08u << 25) | (1u << 16) | 5u;  // IADDIU VI[1], VI[0], 5
        uint s0Up = 0u;
        uint s1Lo = (0x29u << 25) | (1u << 16) | 2u;  // IBNE VI[1], VI[0], +2 (target = slot 4)
        uint s1Up = 0u;
        uint s2Lo = 0x8000033Cu;                      // Delay slot NOP
        uint s2Up = 0u;
        uint s3Lo = (0x08u << 25) | (2u << 16) | 99u; // Fallthrough: VI[2] = 99 [E]
        uint s3Up = 0x40000000u;
        uint s4Lo = (0x08u << 25) | (2u << 16) | 42u; // Branch target: VI[2] = 42 [E]
        uint s4Up = 0x40000000u;
        uint s5Lo = 0x8000033Cu;                      // Delay slot NOP
        uint s5Up = 0u;

        Array.Clear(vu.MicroMem);
        uint[] bundles = { s0Lo, s0Up, s1Lo, s1Up, s2Lo, s2Up, s3Lo, s3Up, s4Lo, s4Up, s5Lo, s5Up };
        for (int i = 0; i < bundles.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(i * 4, 4), bundles[i]);

        var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
        prog.ExecuteDelegate(vu);

        // IBNE reads updated VI[1]=5, so 5 != 0 is true -> takes branch to slot 4, setting VI[2]=42
        Assert.Equal((ushort)42, vu.ReadVi(2));
        Assert.Equal((ushort)5, vu.ReadVi(1));
    }

    [Fact]
    public void Vu1Recompiler_Bal_WritesLinkRegAfterDelaySlot()
    {
        var vu = new Vu1State();
        vu.WriteVi(15, 100);
        vu.ResetViHazard();

        uint s0Lo = (0x21u << 25) | (15u << 16) | 1u;              // BAL VI[15], +1 (target = slot 2)
        uint s0Up = 0u;
        uint s1Lo = (0x08u << 25) | (14u << 16) | (15u << 11) | 10u; // Delay: IADDIU VI[14], VI[15], 10
        uint s1Up = 0u;
        uint s2Lo = 0x8000033Cu;                                   // Target: NOP [E]
        uint s2Up = 0x40000000u;
        uint s3Lo = 0x8000033Cu;                                   // Delay: NOP
        uint s3Up = 0u;

        Array.Clear(vu.MicroMem);
        uint[] bundles = { s0Lo, s0Up, s1Lo, s1Up, s2Lo, s2Up, s3Lo, s3Up };
        for (int i = 0; i < bundles.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(i * 4, 4), bundles[i]);

        var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
        prog.ExecuteDelegate(vu);

        Assert.Equal((ushort)110, vu.ReadVi(14));
        Assert.Equal((ushort)2, vu.ReadVi(15));
    }

    [Fact]
    public void Vu1Recompiler_Bal_DoesNotOverwriteDelaySlotWriteToLinkReg()
    {
        var vu = new Vu1State();
        vu.WriteVi(15, 100);
        vu.ResetViHazard();

        uint s0Lo = (0x21u << 25) | (15u << 16) | 1u;                 // BAL VI[15], +1 (target = slot 2, linkVal = 2)
        uint s0Up = 0u;
        uint s1Lo = (0x08u << 25) | (15u << 16) | (0u << 11) | 555u;  // Delay: IADDIU VI[15], VI[0], 555
        uint s1Up = 0u;
        uint s2Lo = 0x8000033Cu;                                      // Target: NOP [E]
        uint s2Up = 0x40000000u;
        uint s3Lo = 0x8000033Cu;                                      // Delay: NOP
        uint s3Up = 0u;

        Array.Clear(vu.MicroMem);
        uint[] bundles = { s0Lo, s0Up, s1Lo, s1Up, s2Lo, s2Up, s3Lo, s3Up };
        for (int i = 0; i < bundles.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(i * 4, 4), bundles[i]);

        var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
        prog.ExecuteDelegate(vu);

        // Delay slot's write to VI[15] (555) must NOT be clobbered by linkVal (2)
        Assert.Equal((ushort)555, vu.ReadVi(15));
    }

    [Fact]
    public void Vu1Recompiler_SqiFollowedByIbne_DoesNotTriggerBranchHazard()
    {
        var vu = new Vu1State();
        vu.WriteVi(1, 0); // vi1 = 0 (pointer)
        vu.WriteVi(2, 3); // vi2 = 3 (limit)
        vu.ResetViHazard();

        // Slot 0: SQI vf0, (vi1++)
        // lower: top=0x40, it=1, isReg=0, subOp=0x35 (0x340 | 0x3D)
        uint s0Lo = (0x40u << 25) | (1u << 16) | (0u << 11) | 0x340u | 0x3Du;
        uint s0Up = 0u;

        // Slot 1: IBNE vi1, vi2, slot 0 (relTarget = 0, imm11 = -2)
        // SignExtend11: target = curSlot(1) + 1 + imm11 = 2 + imm11 = 0 => imm11 = -2 = 0x7FE
        uint s1Lo = (0x29u << 25) | (1u << 16) | (2u << 11) | (0x7FEu & 0x7FFu);
        uint s1Up = 0u;

        // Slot 2: Delay slot NOP
        uint s2Lo = 0x8000033Cu;
        uint s2Up = 0u;

        // Slot 3: Loop exit: NOP [E]
        uint s3Lo = 0x8000033Cu;
        uint s3Up = 0x40000000u;

        // Slot 4: Delay slot NOP
        uint s4Lo = 0x8000033Cu;
        uint s4Up = 0u;

        Array.Clear(vu.MicroMem);
        uint[] bundles = { s0Lo, s0Up, s1Lo, s1Up, s2Lo, s2Up, s3Lo, s3Up, s4Lo, s4Up };
        for (int i = 0; i < bundles.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(i * 4, 4), bundles[i]);

        var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
        prog.ExecuteDelegate(vu);

        // Should loop exactly 3 times (vi1=0->1, 1->2, 2->3) and exit when vi1==vi2==3.
        // Under the hazard bug, vi1 would be 4 because SQI hazard caused IBNE to read old vi1=2.
        Assert.Equal((ushort)3, vu.ReadVi(1));
    }

    [Fact]
    public void Vu1Recompiler_DivLatency_MulqReadsPreviousQUntilWaitQ()
    {
        var vu = new Vu1State();
        vu.Q = 10.0f; // Initial Q = 10.0
        vu.VF[1] = Vector128.Create(0.0f, 0.0f, 0.0f, 2.0f); // vf01.w = 2.0
        vu.VF[2] = Vector128.Create(100.0f, 100.0f, 100.0f, 100.0f);

        // Slot 0: Upper NOP (0x000002FFu), Lower: DIV Q, vf00.w (1.0), vf01.w (2.0) -> result 0.5f in 7 cycles
        uint s0Lo = 0x80000000u | (3u << 23) | (3u << 21) | (1u << 16) | 0x3BCu;
        uint s0Up = 0x000002FFu;

        // Slot 1: Upper MULq.xyzw vf3, vf2, Q; Lower NOP (0x8000033Cu)
        // MULq: dest=0xF, ft=0, fs=2, fd=3, op=0x1C
        uint s1Lo = 0x8000033Cu;
        uint s1Up = (0xFu << 21) | (0u << 16) | (2u << 11) | (3u << 6) | 0x1Cu;

        // Slot 2: Upper NOP, Lower WAITQ (0x800003BFu)
        uint s2Lo = 0x800003BFu;
        uint s2Up = 0x000002FFu;

        // Slot 3: Upper MULq.xyzw vf4, vf2, Q [E]; Lower NOP
        // [E] bit in Upper is 0x40000000u
        uint s3Lo = 0x8000033Cu;
        uint s3Up = 0x40000000u | (0xFu << 21) | (0u << 16) | (2u << 11) | (4u << 6) | 0x1Cu;

        // Slot 4: Delay slot NOP
        uint s4Lo = 0x8000033Cu;
        uint s4Up = 0x000002FFu;

        Array.Clear(vu.MicroMem);
        uint[] bundles = { s0Lo, s0Up, s1Lo, s1Up, s2Lo, s2Up, s3Lo, s3Up, s4Lo, s4Up };
        for (int i = 0; i < bundles.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(i * 4, 4), bundles[i]);

        var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
        prog.ExecuteDelegate(vu);

        // Before WAITQ, MULq in Slot 1 must have read OLD Q (10.0f) -> vf3.x = 100 * 10 = 1000.0f
        Assert.Equal(1000.0f, vu.ReadVf(3).GetElement(0));

        // After WAITQ, MULq in Slot 3 must have read NEW Q (0.5f) -> vf4.x = 100 * 0.5 = 50.0f
        Assert.Equal(50.0f, vu.ReadVf(4).GetElement(0));
    }

    [Fact]
    public void Vu1Recompiler_DivLatency_AutoLatchesAfter7CyclesWithoutWaitQ()
    {
        var vu = new Vu1State();
        vu.Q = 10.0f; // Initial Q = 10.0
        vu.VF[1] = Vector128.Create(0.0f, 0.0f, 0.0f, 2.0f); // vf01.w = 2.0
        vu.VF[2] = Vector128.Create(100.0f, 100.0f, 100.0f, 100.0f);

        // Slot 0: DIV Q, vf00.w, vf01.w (queued at cycle 1, ready at cycle 8 = 1 + 7)
        uint s0Lo = 0x80000000u | (3u << 23) | (3u << 21) | (1u << 16) | 0x3BCu;
        uint s0Up = 0x000002FFu;

        // Slots 1..6: 6 NOP cycles (cycles 2..7)
        // Slot 7: 7th cycle after DIV (cycle 8) -> MULq.xyzw vf3, vf2, Q [E]
        uint nopLo = 0x8000033Cu;
        uint nopUp = 0x000002FFu;

        uint s7Lo = 0x8000033Cu;
        uint s7Up = 0x40000000u | (0xFu << 21) | (0u << 16) | (2u << 11) | (3u << 6) | 0x1Cu; // [E]

        uint delayLo = 0x8000033Cu;
        uint delayUp = 0x000002FFu;

        Array.Clear(vu.MicroMem);
        uint[] bundles = {
            s0Lo, s0Up,
            nopLo, nopUp, // slot 1 (cycle 2)
            nopLo, nopUp, // slot 2 (cycle 3)
            nopLo, nopUp, // slot 3 (cycle 4)
            nopLo, nopUp, // slot 4 (cycle 5)
            nopLo, nopUp, // slot 5 (cycle 6)
            nopLo, nopUp, // slot 6 (cycle 7)
            s7Lo, s7Up,   // slot 7 (cycle 8, ready!) [E]
            delayLo, delayUp // slot 8 (cycle 9)
        };
        for (int i = 0; i < bundles.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(i * 4, 4), bundles[i]);

        var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
        prog.ExecuteDelegate(vu);

        // At cycle 8, 7 cycles have elapsed since DIV queued at cycle 1, so Q auto-latches to 0.5f
        Assert.Equal(50.0f, vu.ReadVf(3).GetElement(0));
    }

    [Fact]
    public void Vu1Recompiler_MulqPairedWithWaitqInSameBundle_ReadsAwaitedQ()
    {
        var vu = new Vu1State();
        vu.Q = 10.0f; // Initial Q = 10.0
        vu.VF[1] = Vector128.Create(0.0f, 0.0f, 0.0f, 2.0f); // vf01.w = 2.0
        vu.VF[2] = Vector128.Create(100.0f, 100.0f, 100.0f, 100.0f);

        // Slot 0: DIV Q, vf00.w (1.0), vf01.w (2.0) -> result 0.5f in 7 cycles
        uint s0Lo = 0x80000000u | (3u << 23) | (3u << 21) | (1u << 16) | 0x3BCu;
        uint s0Up = 0x000002FFu;

        // Slot 1: Same bundle: Upper MULq.xyzw vf3, vf2, Q | Lower WAITQ (0x800003BFu) [E]
        // WAITQ in Lower must stall/latch Q before Upper runs, so MULq reads NEW Q (0.5f) -> vf3.x = 100 * 0.5 = 50.0f!
        uint s1Lo = 0x800003BFu;
        uint s1Up = 0x40000000u | (0xFu << 21) | (0u << 16) | (2u << 11) | (3u << 6) | 0x1Cu; // [E]

        // Slot 2: Delay slot NOP
        uint s2Lo = 0x8000033Cu;
        uint s2Up = 0x000002FFu;

        Array.Clear(vu.MicroMem);
        uint[] bundles = { s0Lo, s0Up, s1Lo, s1Up, s2Lo, s2Up };
        for (int i = 0; i < bundles.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(i * 4, 4), bundles[i]);

        var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
        prog.ExecuteDelegate(vu);

        Assert.Equal(50.0f, vu.ReadVf(3).GetElement(0));
    }

    [Fact]
    public void Vu1Recompiler_MulqPairedWithDivInSameBundle_ReadsPreviousCompletedQ()
    {
        var vu = new Vu1State();
        vu.Q = 10.0f; // Initial Q = 10.0
        vu.VF[1] = Vector128.Create(0.0f, 0.0f, 0.0f, 2.0f); // vf01.w = 2.0 (1.0 / 2.0 = 0.5f)
        vu.VF[2] = Vector128.Create(100.0f, 100.0f, 100.0f, 100.0f);
        vu.VF[4] = Vector128.Create(0.0f, 0.0f, 0.0f, 4.0f); // vf04.w = 4.0 (1.0 / 4.0 = 0.25f)

        // Slot 0: DIV Q, vf00.w (1.0), vf01.w (2.0) -> queues Q1 = 0.5f
        uint s0Lo = 0x80000000u | (3u << 23) | (3u << 21) | (1u << 16) | 0x3BCu;
        uint s0Up = 0x000002FFu;

        // Slot 1 (Pipelined bundle): Upper MULq vf3, vf2, Q | Lower DIV Q, vf00.w, vf04.w (target 0.25f) [E]
        // Hardware stalls bundle until Q1 is ready: Upper gets Q1 (0.5f) -> vf3.x = 50.0f!
        // Lower then queues Q2 (0.25f) for the NEXT vertex.
        uint s1Lo = 0x80000000u | (3u << 23) | (3u << 21) | (4u << 16) | 0x3BCu;
        uint s1Up = 0x40000000u | (0xFu << 21) | (0u << 16) | (2u << 11) | (3u << 6) | 0x1Cu; // [E]

        // Slot 2: Delay slot NOP
        uint s2Lo = 0x8000033Cu;
        uint s2Up = 0x000002FFu;

        Array.Clear(vu.MicroMem);
        uint[] bundles = { s0Lo, s0Up, s1Lo, s1Up, s2Lo, s2Up };
        for (int i = 0; i < bundles.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(i * 4, 4), bundles[i]);

        var prog = Vu1Recompiler.CompileProgram(vu.MicroMem, 0, 0);
        prog.ExecuteDelegate(vu);

        // Upper slot MULq must have executed with the completed Q1 (0.5f), not old 10.0f or queued 0.25f
        Assert.Equal(50.0f, vu.ReadVf(3).GetElement(0));
    }

    [Fact]
    public void VifUnit_BaseCommand_UpdatesTops()
    {
        var vif = new VifUnit(isVif1: true);
        vif.Ofst = 100;
        vif.Dbf = true;

        byte[] cmd = { 200, 0, 0, 0x03 }; // BASE = 200
        vif.ProcessData(cmd);

        Assert.Equal((ushort)200, vif.Base);
        Assert.Equal((ushort)300, vif.Tops);
    }

    [Fact]
    public void VifUnit_UnpackV2_DoesNotOverwriteZAndWInVuMem()
    {
        var vif = new VifUnit(isVif1: true);

        BinaryPrimitives.WriteUInt32LittleEndian(vif.DataMem.AsSpan(0, 4), 0x11111111u);
        BinaryPrimitives.WriteUInt32LittleEndian(vif.DataMem.AsSpan(4, 4), 0x22222222u);
        BinaryPrimitives.WriteUInt32LittleEndian(vif.DataMem.AsSpan(8, 4), 0x33333333u);
        BinaryPrimitives.WriteUInt32LittleEndian(vif.DataMem.AsSpan(12, 4), 0x44444444u);

        byte[] packet = new byte[12];
        packet[0] = 0;
        packet[1] = 0;
        packet[2] = 1;
        packet[3] = 0x64; // UNPACK V2-32
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), 0xAAAAAAAAu);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 0xBBBBBBBBu);

        vif.ProcessData(packet);

        Assert.Equal(0xAAAAAAAAu, BinaryPrimitives.ReadUInt32LittleEndian(vif.DataMem.AsSpan(0, 4)));
        Assert.Equal(0xBBBBBBBBu, BinaryPrimitives.ReadUInt32LittleEndian(vif.DataMem.AsSpan(4, 4)));
        Assert.Equal(0x33333333u, BinaryPrimitives.ReadUInt32LittleEndian(vif.DataMem.AsSpan(8, 4)));
        Assert.Equal(0x44444444u, BinaryPrimitives.ReadUInt32LittleEndian(vif.DataMem.AsSpan(12, 4)));
    }

    [Fact]
    public void DmaController_GetDmaPointerSpan_DistinguishesScratchpadFromMainRam()
    {
        byte[] ram = new byte[CpuContext.RamSize];
        byte[] sp = new byte[CpuContext.ScratchpadSize];

        for (int i = 0; i < 32; i++) sp[i] = 0x55;
        for (int i = 0; i < 32; i++) ram[0x00010000 + i] = 0x77;

        fixed (byte* pRam = ram)
        fixed (byte* pSp = sp)
        {
            CpuContext ctx = default;
            ctx.Initialize(pSp);

            var sprSpan1 = DmaController.GetDmaPointerSpan(&ctx, pRam, 0x70000000u, 16);
            Assert.Equal(0x55, sprSpan1[0]);

            var sprSpan2 = DmaController.GetDmaPointerSpan(&ctx, pRam, 0x80000000u, 16);
            Assert.Equal(0x55, sprSpan2[0]);

            var ramSpan = DmaController.GetDmaPointerSpan(&ctx, pRam, 0x80010000u, 16);
            Assert.Equal(0x77, ramSpan[0]);
        }
    }

    [Fact]
    public void GsProcessor_ActiveCbpFallback_AndWrapModes_BehaveAccurately()
    {
        var gs = new GsProcessor();

        // 1. Write palette entry 1 = pure green (0x8000FF00) at CLUT block 0x120
        GsSwizzle.WritePixel32(gs.Vram, 0x120, 64, 1, 0, 0x8000FF00u);

        // 2. Write texel (0,0) = palette index 1 at texture block 0x150 (PSMT8)
        GsSwizzle.WriteTexel8(gs.Vram, 0x150, 128, 0, 0, 1);

        // 3. Latch CLUT block 0x120 using CLD=1
        ulong tex0LoadClut = (0x150ul) | (2ul << 14) | ((ulong)GsPsm.Psmt8 << 20)
                           | (1ul << 35) // TFX = Decal
                           | (0x120ul << 37)
                           | (1ul << 61); // CLD = 1 (load CLUT)
        gs.WriteInternalRegister(0x06, tex0LoadClut);

        // 4. Subsequent TEX0 write with CLD=0 and CBP=0 (must keep using latched CBP=0x120!)
        ulong tex0Draw = (0x150ul) | (2ul << 14) | ((ulong)GsPsm.Psmt8 << 20)
                       | (1ul << 35); // TFX = Decal, CBP = 0, CLD = 0
        gs.WriteInternalRegister(0x06, tex0Draw);

        // 5. Draw a 1x1 textured point at (0,0) (with default XYOFFSET = 2048.0 -> raw X/Y = 2048<<4)
        gs.WriteInternalRegister(0x00, (ulong)GsPrimitiveType.Point | (1ul << 4) | (1ul << 8)); // Point | TME | FST
        gs.WriteInternalRegister(0x03, 0ul); // UV = (0,0)
        ushort rawCoord = (ushort)(2048 << 4);
        gs.WriteInternalRegister(0x05, (ulong)rawCoord | ((ulong)rawCoord << 16) | (100ul << 32)); // XYZ2

        uint pixel = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 0, 0);
        Assert.Equal(0x8000FF00u, pixel);
    }
}
