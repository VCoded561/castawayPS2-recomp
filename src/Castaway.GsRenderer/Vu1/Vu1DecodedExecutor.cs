using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Castaway.Core;

namespace Castaway.GsRenderer.Vu1;

internal enum Vu1UpperOp : byte
{
    Nop = 0,
    AddBc, SubBc, MaddBc, MsubBc, MaxBc, MinBc, MulBc,
    MulQ, MaxI, MulI, MinI,
    AddQ, MaddQ, AddI, MaddI, SubQ, MsubQ, SubI, MsubI,
    Add, Madd, Mul, Max, Sub, Msub, Opmsub, Min,
    AddABc, SubABc, MaddABc, MsubABc, MulABc,
    Itof0, Itof4, Itof12, Itof15,
    Ftoi0, Ftoi4, Ftoi12, Ftoi15,
    MulAq, Abs, MulAi, Clip,
    AddAq, MaddAq, AddAi, MaddAi, SubAq, MsubAq, SubAi, MsubAi,
    AddA, MaddA, MulA, SubA, MsubA, Opmula,
    Unknown
}

internal enum Vu1LowerOp : byte
{
    Nop = 0,
    Lq, Sq, Ilw, Isw, Iaddiu, Isubiu,
    Fceq, Fcset, Fcand, Fcor, Fsset, Fsand, Fsor, Fmeq, Fmand, Fmor, Fcget,
    Iadd, Isub, Iaddi, Iand, Ior,
    Move, Mr32, Lqi, Sqi, Lqd, Sqd,
    Div, Sqrt, Rsqrt, WaitQ,
    Mtir, Mfir, Ilwr, Iswr,
    Rnext, Rget, Rinit, Rxor,
    Mfp, Xtop, Xitop, Xgkick,
    Esadd, Ersadd, Eleng, Erleng, Eatanxy, Eatanxz, Esum, Ercpr, Esqrt, Ersqrt, WaitP, Esin, Eatan, Eexp
}

internal enum Vu1BranchOp : byte
{
    None = 0,
    B, Bal, Jr, Jalr, Ibeq, Ibne, Ibltz, Ibgtz, Iblez, Ibgez
}

internal struct Vu1DecodedSlot
{
    public Vu1UpperOp UpperOp;
    public Vu1LowerOp LowerOp;
    public Vu1BranchOp BranchOp;
    public byte Fd;
    public byte Fs;
    public byte Ft;
    public byte Dest;
    public byte Bc;
    public byte It;
    public byte Is;
    public byte Id;
    public byte LowerDest;
    public byte Fsf;
    public byte Ftf;
    public short Imm11;
    public ushort Uimm15;
    public ushort BranchTargetSlot;
    public uint Imm24;
    public float LoiFloat;
    public bool IsImm;
    public bool IsE;
    public bool NeedsWaitQ;
    public bool NeedsWaitP;
}

internal sealed class Vu1DecodedExecutor
{
    private readonly Vu1DecodedSlot[] _slots = new Vu1DecodedSlot[2048];
    private readonly byte[] _programUsesFlagsState = new byte[2048];
    private bool _dirty = true;
    private bool _anyFlagReader;

    public void Invalidate() => _dirty = true;

    private bool ProgramUsesFlags(int startSlot)
    {
        if (!_anyFlagReader)
            return false;

        byte cached = _programUsesFlagsState[startSlot];
        if (cached != 0)
            return cached == 2;

        Span<bool> visited = stackalloc bool[2048];
        Span<ushort> stack = stackalloc ushort[256];
        int sp = 0;
        visited[startSlot] = true;
        stack[sp++] = (ushort)startSlot;

        bool usesFlags = false;
        while (sp > 0)
        {
            int cur = stack[--sp];
            while (true)
            {
                ref readonly Vu1DecodedSlot s = ref _slots[cur];
                if (s.LowerOp is Vu1LowerOp.Fsand or Vu1LowerOp.Fsor or Vu1LowerOp.Fmeq or Vu1LowerOp.Fmand or Vu1LowerOp.Fmor)
                {
                    usesFlags = true;
                    break;
                }

                int next = (cur + 1) & 0x7FF;
                if (s.IsE)
                {
                    ref readonly Vu1DecodedSlot d = ref _slots[next];
                    if (d.LowerOp is Vu1LowerOp.Fsand or Vu1LowerOp.Fsor or Vu1LowerOp.Fmeq or Vu1LowerOp.Fmand or Vu1LowerOp.Fmor)
                        usesFlags = true;
                    break;
                }

                if (s.BranchOp != Vu1BranchOp.None)
                {
                    ref readonly Vu1DecodedSlot d = ref _slots[next];
                    if (d.LowerOp is Vu1LowerOp.Fsand or Vu1LowerOp.Fsor or Vu1LowerOp.Fmeq or Vu1LowerOp.Fmand or Vu1LowerOp.Fmor)
                    {
                        usesFlags = true;
                        break;
                    }

                    if (s.BranchOp is Vu1BranchOp.Jr or Vu1BranchOp.Jalr)
                    {
                        usesFlags = true;
                        break;
                    }

                    if (!d.IsE)
                    {
                        int t = s.BranchTargetSlot & 0x7FF;
                        if (!visited[t])
                        {
                            visited[t] = true;
                            if (sp < stack.Length) stack[sp++] = (ushort)t;
                            else { usesFlags = true; break; }
                        }

                        if (s.BranchOp is Vu1BranchOp.B or Vu1BranchOp.Bal)
                            break;

                        int fallthrough = (next + 1) & 0x7FF;
                        if (visited[fallthrough])
                            break;
                        visited[fallthrough] = true;
                        cur = fallthrough;
                        continue;
                    }
                    break;
                }

                if (visited[next])
                    break;
                visited[next] = true;
                cur = next;
            }

            if (usesFlags)
                break;
        }

        _programUsesFlagsState[startSlot] = usesFlags ? (byte)2 : (byte)1;
        return usesFlags;
    }

    public void Execute(Vu1State vu, ushort startByteAddr)
    {
        if (_dirty)
        {
            DecodeAll(vu.MicroMem);
            _dirty = false;
        }

        int curSlot = (startByteAddr >> 3) & 0x7FF;
        bool updateFlags = ProgramUsesFlags(curSlot);
        int maxSteps = 65536;
        int steps = 0;

        while (maxSteps-- > 0)
        {
            ref readonly Vu1DecodedSlot slot = ref _slots[curSlot];

            bool branchTaken = false;
            int branchTargetSlot = slot.BranchTargetSlot;
            int linkReg = 0;
            ushort linkVal = 0;

            if (slot.BranchOp != Vu1BranchOp.None)
            {
                switch (slot.BranchOp)
                {
                    case Vu1BranchOp.B:
                        branchTaken = true;
                        break;
                    case Vu1BranchOp.Bal:
                        branchTaken = true;
                        linkReg = slot.It;
                        linkVal = (ushort)((curSlot + 2) & 0x7FF);
                        break;
                    case Vu1BranchOp.Jr:
                        branchTaken = true;
                        branchTargetSlot = vu.VI[slot.Is & 15] & 0x7FF;
                        break;
                    case Vu1BranchOp.Jalr:
                        branchTaken = true;
                        branchTargetSlot = vu.VI[slot.Is & 15] & 0x7FF;
                        linkReg = slot.It;
                        linkVal = (ushort)((curSlot + 2) & 0x7FF);
                        break;
                    case Vu1BranchOp.Ibeq:
                        branchTaken = vu.VI[slot.It & 15] == vu.VI[slot.Is & 15];
                        break;
                    case Vu1BranchOp.Ibne:
                        branchTaken = vu.VI[slot.It & 15] != vu.VI[slot.Is & 15];
                        break;
                    case Vu1BranchOp.Ibltz:
                        branchTaken = (short)vu.VI[slot.Is & 15] < 0;
                        break;
                    case Vu1BranchOp.Ibgtz:
                        branchTaken = (short)vu.VI[slot.Is & 15] > 0;
                        break;
                    case Vu1BranchOp.Iblez:
                        branchTaken = (short)vu.VI[slot.Is & 15] <= 0;
                        break;
                    case Vu1BranchOp.Ibgez:
                        branchTaken = (short)vu.VI[slot.Is & 15] >= 0;
                        break;
                }
            }

            ExecBundle(vu, in slot, updateFlags);
            steps++;

            int delaySlot = (curSlot + 1) & 0x7FF;

            if (slot.IsE)
            {
                ref readonly Vu1DecodedSlot dSlot = ref _slots[delaySlot];
                vu.LastWrittenAnyViReg = 0;
                ExecBundle(vu, in dSlot, updateFlags);
                steps++;
                if (linkReg != 0 && vu.LastWrittenAnyViReg != (linkReg & 15))
                    vu.VI[linkReg & 15] = linkVal;
                vu.Tpc = (ushort)(branchTaken ? (branchTargetSlot << 3) : (((delaySlot + 1) & 0x7FF) << 3));
                break;
            }

            if (slot.BranchOp != Vu1BranchOp.None)
            {
                ref readonly Vu1DecodedSlot dSlot = ref _slots[delaySlot];
                vu.LastWrittenAnyViReg = 0;
                ExecBundle(vu, in dSlot, updateFlags);
                steps++;

                if (linkReg != 0 && vu.LastWrittenAnyViReg != (linkReg & 15))
                    vu.VI[linkReg & 15] = linkVal;

                if (dSlot.IsE)
                {
                    vu.Tpc = (ushort)(branchTaken ? (branchTargetSlot << 3) : (((delaySlot + 1) & 0x7FF) << 3));
                    break;
                }

                curSlot = branchTaken ? branchTargetSlot : ((delaySlot + 1) & 0x7FF);
                continue;
            }

            curSlot = delaySlot;
        }

        if (vu.HasPendingP)
        {
            vu.P = vu.PendingP;
            vu.HasPendingP = false;
            vu.PendingPReadyCycle = 0;
        }

        vu.TotalInstructionsExecuted += steps;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExecBundle(Vu1State vu, in Vu1DecodedSlot slot, bool updateFlags)
    {
        if (vu.HasPendingP && --vu.PendingPReadyCycle <= 0)
        {
            vu.P = vu.PendingP;
            vu.HasPendingP = false;
        }

        int pendingReg = 0;
        Vector128<float> pendingVal = default;
        uint pendingMask = 0;

        if (slot.UpperOp != Vu1UpperOp.Nop)
        {
            ExecUpper(vu, in slot, updateFlags, out pendingReg, out pendingVal, out pendingMask);
        }

        if (slot.LowerOp != Vu1LowerOp.Nop)
        {
            ExecLower(vu, in slot);
        }

        if (pendingMask != 0)
        {
            vu.WriteVfNoFlags(pendingReg, pendingVal, pendingMask);
        }

        if (slot.IsImm)
        {
            vu.I = slot.LoiFloat;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExecUpper(
        Vu1State vu,
        in Vu1DecodedSlot slot,
        bool updateFlags,
        out int pendingReg,
        out Vector128<float> pendingVal,
        out uint pendingMask)
    {
        pendingReg = 0;
        pendingVal = default;
        pendingMask = 0;

        Vector128<float> vfs = vu.VF[slot.Fs];
        uint dest = slot.Dest;

        Vector128<float> accVal;
        switch (slot.UpperOp)
        {
            case Vu1UpperOp.AddABc:
                accVal = Ps2Float.VecAdd(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc));
                goto WriteAccOut;
            case Vu1UpperOp.SubABc:
                accVal = Ps2Float.VecSub(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc));
                goto WriteAccOut;
            case Vu1UpperOp.MaddABc:
                accVal = Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc)));
                goto WriteAccOut;
            case Vu1UpperOp.MsubABc:
                accVal = Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc)));
                goto WriteAccOut;
            case Vu1UpperOp.MulABc:
                accVal = Ps2Float.VecMul(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc));
                goto WriteAccOut;
            case Vu1UpperOp.Itof0:
                pendingReg = slot.Ft; pendingVal = Vu1State.ExecItof(vfs, 0); pendingMask = dest; return;
            case Vu1UpperOp.Itof4:
                pendingReg = slot.Ft; pendingVal = Vu1State.ExecItof(vfs, 4); pendingMask = dest; return;
            case Vu1UpperOp.Itof12:
                pendingReg = slot.Ft; pendingVal = Vu1State.ExecItof(vfs, 12); pendingMask = dest; return;
            case Vu1UpperOp.Itof15:
                pendingReg = slot.Ft; pendingVal = Vu1State.ExecItof(vfs, 15); pendingMask = dest; return;
            case Vu1UpperOp.Ftoi0:
                pendingReg = slot.Ft; pendingVal = Vu1State.ExecFtoi(vfs, 0); pendingMask = dest; return;
            case Vu1UpperOp.Ftoi4:
                pendingReg = slot.Ft; pendingVal = Vu1State.ExecFtoi(vfs, 4); pendingMask = dest; return;
            case Vu1UpperOp.Ftoi12:
                pendingReg = slot.Ft; pendingVal = Vu1State.ExecFtoi(vfs, 12); pendingMask = dest; return;
            case Vu1UpperOp.Ftoi15:
                pendingReg = slot.Ft; pendingVal = Vu1State.ExecFtoi(vfs, 15); pendingMask = dest; return;
            case Vu1UpperOp.MulAq:
                accVal = Ps2Float.VecMul(vfs, Vector128.Create(vu.Q));
                goto WriteAccOut;
            case Vu1UpperOp.Abs:
                pendingReg = slot.Ft; pendingVal = Vu1State.ExecAbs(vfs); pendingMask = dest; return;
            case Vu1UpperOp.MulAi:
                accVal = Ps2Float.VecMul(vfs, Vector128.Create(vu.I));
                goto WriteAccOut;
            case Vu1UpperOp.Clip:
                vu.ExecClip(vfs, vu.VF[slot.Ft]); return;
            case Vu1UpperOp.AddAq:
                accVal = Ps2Float.VecAdd(vfs, Vector128.Create(vu.Q));
                goto WriteAccOut;
            case Vu1UpperOp.MaddAq:
                accVal = Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, Vector128.Create(vu.Q)));
                goto WriteAccOut;
            case Vu1UpperOp.AddAi:
                accVal = Ps2Float.VecAdd(vfs, Vector128.Create(vu.I));
                goto WriteAccOut;
            case Vu1UpperOp.MaddAi:
                accVal = Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, Vector128.Create(vu.I)));
                goto WriteAccOut;
            case Vu1UpperOp.SubAq:
                accVal = Ps2Float.VecSub(vfs, Vector128.Create(vu.Q));
                goto WriteAccOut;
            case Vu1UpperOp.MsubAq:
                accVal = Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, Vector128.Create(vu.Q)));
                goto WriteAccOut;
            case Vu1UpperOp.SubAi:
                accVal = Ps2Float.VecSub(vfs, Vector128.Create(vu.I));
                goto WriteAccOut;
            case Vu1UpperOp.MsubAi:
                accVal = Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, Vector128.Create(vu.I)));
                goto WriteAccOut;
            case Vu1UpperOp.AddA:
                accVal = Ps2Float.VecAdd(vfs, vu.VF[slot.Ft]);
                goto WriteAccOut;
            case Vu1UpperOp.MaddA:
                accVal = Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, vu.VF[slot.Ft]));
                goto WriteAccOut;
            case Vu1UpperOp.MulA:
                accVal = Ps2Float.VecMul(vfs, vu.VF[slot.Ft]);
                goto WriteAccOut;
            case Vu1UpperOp.SubA:
                accVal = Ps2Float.VecSub(vfs, vu.VF[slot.Ft]);
                goto WriteAccOut;
            case Vu1UpperOp.MsubA:
                accVal = Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, vu.VF[slot.Ft]));
                goto WriteAccOut;
            case Vu1UpperOp.Opmula:
                vu.ExecOpmula(vfs, vu.VF[slot.Ft], updateFlags); return;
        }

        uint writeMask = dest;
        Vector128<float> result = slot.UpperOp switch
        {
            Vu1UpperOp.AddBc => Ps2Float.VecAdd(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc)),
            Vu1UpperOp.SubBc => Ps2Float.VecSub(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc)),
            Vu1UpperOp.MaddBc => Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc))),
            Vu1UpperOp.MsubBc => Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc))),
            Vu1UpperOp.MaxBc => Ps2Vu0.VecMax(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc)),
            Vu1UpperOp.MinBc => Ps2Vu0.VecMin(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc)),
            Vu1UpperOp.MulBc => Ps2Float.VecMul(vfs, Ps2Vu0.BroadcastBc(vu.VF[slot.Ft], slot.Bc)),
            Vu1UpperOp.MulQ => Ps2Float.VecMul(vfs, Vector128.Create(vu.Q)),
            Vu1UpperOp.MaxI => Ps2Vu0.VecMax(vfs, Vector128.Create(vu.I)),
            Vu1UpperOp.MulI => Ps2Float.VecMul(vfs, Vector128.Create(vu.I)),
            Vu1UpperOp.MinI => Ps2Vu0.VecMin(vfs, Vector128.Create(vu.I)),
            Vu1UpperOp.AddQ => Ps2Float.VecAdd(vfs, Vector128.Create(vu.Q)),
            Vu1UpperOp.MaddQ => Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, Vector128.Create(vu.Q))),
            Vu1UpperOp.AddI => Ps2Float.VecAdd(vfs, Vector128.Create(vu.I)),
            Vu1UpperOp.MaddI => Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, Vector128.Create(vu.I))),
            Vu1UpperOp.SubQ => Ps2Float.VecSub(vfs, Vector128.Create(vu.Q)),
            Vu1UpperOp.MsubQ => Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, Vector128.Create(vu.Q))),
            Vu1UpperOp.SubI => Ps2Float.VecSub(vfs, Vector128.Create(vu.I)),
            Vu1UpperOp.MsubI => Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, Vector128.Create(vu.I))),
            Vu1UpperOp.Add => Ps2Float.VecAdd(vfs, vu.VF[slot.Ft]),
            Vu1UpperOp.Madd => Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, vu.VF[slot.Ft])),
            Vu1UpperOp.Mul => Ps2Float.VecMul(vfs, vu.VF[slot.Ft]),
            Vu1UpperOp.Max => Ps2Vu0.VecMax(vfs, vu.VF[slot.Ft]),
            Vu1UpperOp.Sub => Ps2Float.VecSub(vfs, vu.VF[slot.Ft]),
            Vu1UpperOp.Msub => Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, vu.VF[slot.Ft])),
            Vu1UpperOp.Opmsub => vu.ExecOpmsub(vfs, vu.VF[slot.Ft]),
            Vu1UpperOp.Min => Ps2Vu0.VecMin(vfs, vu.VF[slot.Ft]),
            _ => vfs
        };

        if (slot.UpperOp == Vu1UpperOp.Opmsub)
            writeMask = 0xEu;

        if (updateFlags && slot.UpperOp is not (
            Vu1UpperOp.Max or Vu1UpperOp.MaxBc or Vu1UpperOp.MaxI or
            Vu1UpperOp.Min or Vu1UpperOp.MinBc or Vu1UpperOp.MinI))
        {
            vu.UpdateMacAndStatusFlags(result, writeMask);
        }
        if (slot.Fd != 0)
        {
            pendingReg = slot.Fd;
            pendingVal = result;
            pendingMask = writeMask;
        }
        return;

    WriteAccOut:
        if (updateFlags)
            vu.WriteAcc(accVal, dest);
        else
            vu.WriteAccNoFlags(accVal, dest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExecLower(Vu1State vu, in Vu1DecodedSlot slot)
    {
        switch (slot.LowerOp)
        {
            case Vu1LowerOp.Lq:
                vu.WriteVfNoFlags(slot.It, vu.LoadQuadword(((short)vu.VI[slot.Is & 15] + slot.Imm11) & 0x3FF), slot.LowerDest);
                break;
            case Vu1LowerOp.Sq:
                vu.StoreQuadword(((short)vu.VI[slot.It & 15] + slot.Imm11) & 0x3FF, vu.VF[slot.Is], slot.LowerDest);
                break;
            case Vu1LowerOp.Ilw:
                vu.WriteVi(slot.It, vu.LoadInt16(((short)vu.VI[slot.Is & 15] + slot.Imm11) & 0x3FF, slot.LowerDest));
                break;
            case Vu1LowerOp.Isw:
                vu.StoreInt16(((short)vu.VI[slot.Is & 15] + slot.Imm11) & 0x3FF, vu.VI[slot.It & 15], slot.LowerDest);
                break;
            case Vu1LowerOp.Iaddiu:
                vu.WriteVi(slot.It, (ushort)(vu.VI[slot.Is & 15] + slot.Uimm15));
                break;
            case Vu1LowerOp.Isubiu:
                vu.WriteVi(slot.It, (ushort)(vu.VI[slot.Is & 15] - slot.Uimm15));
                break;
            case Vu1LowerOp.Fceq:
                vu.WriteVi(1, (ushort)((vu.ClipFlags & 0xFFFFFFu) == slot.Imm24 ? 1 : 0));
                break;
            case Vu1LowerOp.Fcset:
                vu.ClipFlags = slot.Imm24;
                break;
            case Vu1LowerOp.Fcand:
                vu.WriteVi(1, (ushort)(((vu.ClipFlags & slot.Imm24) != 0) ? 1 : 0));
                break;
            case Vu1LowerOp.Fcor:
                vu.WriteVi(1, (ushort)((((vu.ClipFlags | slot.Imm24) & 0xFFFFFFu) == 0xFFFFFFu) ? 1 : 0));
                break;
            case Vu1LowerOp.Fsset:
                vu.StatusFlags = (ushort)((vu.StatusFlags & 0x03Fu) | (slot.Imm24 & 0xFC0u));
                break;
            case Vu1LowerOp.Fsand:
                vu.WriteVi(slot.It, (ushort)(vu.StatusFlags & (slot.Imm24 & 0xFFFu)));
                break;
            case Vu1LowerOp.Fsor:
                vu.WriteVi(slot.It, (ushort)((vu.StatusFlags | (slot.Imm24 & 0xFFFu)) & 0xFFFu));
                break;
            case Vu1LowerOp.Fmeq:
                vu.WriteVi(slot.It, (ushort)(vu.MacFlags == vu.VI[slot.Is & 15] ? 1 : 0));
                break;
            case Vu1LowerOp.Fmand:
                vu.WriteVi(slot.It, (ushort)(vu.MacFlags & vu.VI[slot.Is & 15]));
                break;
            case Vu1LowerOp.Fmor:
                vu.WriteVi(slot.It, (ushort)(vu.MacFlags | vu.VI[slot.Is & 15]));
                break;
            case Vu1LowerOp.Fcget:
                vu.WriteVi(slot.It, (ushort)(vu.ClipFlags & 0xFFFu));
                break;
            case Vu1LowerOp.Iadd:
                vu.WriteVi(slot.Id, (ushort)(vu.VI[slot.Is & 15] + vu.VI[slot.It & 15]));
                break;
            case Vu1LowerOp.Isub:
                vu.WriteVi(slot.Id, (ushort)(vu.VI[slot.Is & 15] - vu.VI[slot.It & 15]));
                break;
            case Vu1LowerOp.Iaddi:
                vu.WriteVi(slot.It, (ushort)(vu.VI[slot.Is & 15] + slot.Imm11));
                break;
            case Vu1LowerOp.Iand:
                vu.WriteVi(slot.Id, (ushort)(vu.VI[slot.Is & 15] & vu.VI[slot.It & 15]));
                break;
            case Vu1LowerOp.Ior:
                vu.WriteVi(slot.Id, (ushort)(vu.VI[slot.Is & 15] | vu.VI[slot.It & 15]));
                break;
            case Vu1LowerOp.Move:
                vu.WriteVfNoFlags(slot.It, vu.VF[slot.Is], slot.LowerDest);
                break;
            case Vu1LowerOp.Mr32:
                vu.WriteVfNoFlags(slot.It, Vu1State.ExecMr32(vu.VF[slot.Is]), slot.LowerDest);
                break;
            case Vu1LowerOp.Lqi:
            {
                ushort idx = vu.VI[slot.Is & 15];
                vu.WriteVfNoFlags(slot.It, vu.LoadQuadword(idx & 0x3FF), slot.LowerDest);
                vu.WriteVi(slot.Is, (ushort)((idx + 1) & 0x3FF));
                break;
            }
            case Vu1LowerOp.Sqi:
            {
                ushort idx = vu.VI[slot.It & 15];
                vu.StoreQuadword(idx & 0x3FF, vu.VF[slot.Is], slot.LowerDest);
                vu.WriteVi(slot.It, (ushort)((idx + 1) & 0x3FF));
                break;
            }
            case Vu1LowerOp.Lqd:
            {
                ushort idx = (ushort)((vu.VI[slot.Is & 15] - 1) & 0x3FF);
                vu.WriteVi(slot.Is, idx);
                vu.WriteVfNoFlags(slot.It, vu.LoadQuadword(idx), slot.LowerDest);
                break;
            }
            case Vu1LowerOp.Sqd:
            {
                ushort idx = (ushort)((vu.VI[slot.It & 15] - 1) & 0x3FF);
                vu.WriteVi(slot.It, idx);
                vu.StoreQuadword(idx, vu.VF[slot.Is], slot.LowerDest);
                break;
            }
            case Vu1LowerOp.Div:
                vu.Q = Ps2Float.Div(
                    Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.Is]), slot.Fsf),
                    Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.It]), slot.Ftf));
                break;
            case Vu1LowerOp.Sqrt:
                vu.Q = Ps2Float.Sqrt(
                    Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.It]), slot.Ftf));
                break;
            case Vu1LowerOp.Rsqrt:
                vu.Q = Ps2Float.Rsqrt(
                    Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.Is]), slot.Fsf),
                    Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.It]), slot.Ftf));
                break;
            case Vu1LowerOp.WaitQ:
                break;
            case Vu1LowerOp.Mtir:
                vu.WriteVi(slot.It, (ushort)Unsafe.Add(ref Unsafe.As<Vector128<float>, uint>(ref vu.VF[slot.Is]), slot.Fsf));
                break;
            case Vu1LowerOp.Mfir:
                vu.WriteVfNoFlags(slot.It, Vector128.Create((int)(short)vu.VI[slot.Is & 15]).AsSingle(), slot.LowerDest);
                break;
            case Vu1LowerOp.Ilwr:
                vu.WriteVi(slot.It, vu.LoadInt16(vu.VI[slot.Is & 15] & 0x3FF, slot.LowerDest));
                break;
            case Vu1LowerOp.Iswr:
                vu.StoreInt16(vu.VI[slot.Is & 15] & 0x3FF, vu.VI[slot.It & 15], slot.LowerDest);
                break;
            case Vu1LowerOp.Rnext:
                vu.WriteVfNoFlags(slot.It, vu.ExecRnext(), slot.LowerDest);
                break;
            case Vu1LowerOp.Rget:
                vu.WriteVfNoFlags(slot.It, vu.ExecRget(), slot.LowerDest);
                break;
            case Vu1LowerOp.Rinit:
                vu.ExecRinit(vu.VF[slot.Is], slot.Fsf);
                break;
            case Vu1LowerOp.Rxor:
                vu.ExecRxor(vu.VF[slot.Is], slot.Fsf);
                break;
            case Vu1LowerOp.Mfp:
                vu.WriteVfNoFlags(slot.It, Vector128.Create(vu.P), slot.LowerDest);
                break;
            case Vu1LowerOp.Xtop:
                vu.WriteVi(slot.It, vu.Top);
                break;
            case Vu1LowerOp.Xitop:
                vu.WriteVi(slot.It, vu.Itop);
                break;
            case Vu1LowerOp.Xgkick:
                vu.ExecXgKick(slot.Is);
                break;
            case Vu1LowerOp.Esadd:
            {
                var v = vu.VF[slot.Is];
                StartEfu(vu, v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2));
                break;
            }
            case Vu1LowerOp.Ersadd:
            {
                var v = vu.VF[slot.Is];
                float sumSq = v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2);
                StartEfu(vu, Ps2Float.Div(1.0f, sumSq));
                break;
            }
            case Vu1LowerOp.Eleng:
            {
                var v = vu.VF[slot.Is];
                float sumSq = v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2);
                StartEfu(vu, Ps2Float.Sqrt(sumSq));
                break;
            }
            case Vu1LowerOp.Erleng:
            {
                var v = vu.VF[slot.Is];
                float sumSq = v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2);
                StartEfu(vu, Ps2Float.Rsqrt(1.0f, sumSq));
                break;
            }
            case Vu1LowerOp.Eatanxy:
            {
                var v = vu.VF[slot.Is];
                StartEfu(vu, MathF.Atan2(v.GetElement(1), v.GetElement(0)));
                break;
            }
            case Vu1LowerOp.Eatanxz:
            {
                var v = vu.VF[slot.Is];
                StartEfu(vu, MathF.Atan2(v.GetElement(2), v.GetElement(0)));
                break;
            }
            case Vu1LowerOp.Esum:
            {
                var v = vu.VF[slot.Is];
                StartEfu(vu, v.GetElement(0) + v.GetElement(1) + v.GetElement(2) + v.GetElement(3));
                break;
            }
            case Vu1LowerOp.Ercpr:
                StartEfu(vu, Ps2Float.Div(1.0f, Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.Is]), slot.Fsf)));
                break;
            case Vu1LowerOp.Esqrt:
                StartEfu(vu, Ps2Float.Sqrt(Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.Is]), slot.Fsf)));
                break;
            case Vu1LowerOp.Ersqrt:
                StartEfu(vu, Ps2Float.Rsqrt(1.0f, Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.Is]), slot.Fsf)));
                break;
            case Vu1LowerOp.WaitP:
                if (vu.HasPendingP)
                {
                    vu.P = vu.PendingP;
                    vu.HasPendingP = false;
                    vu.PendingPReadyCycle = 0;
                }
                break;
            case Vu1LowerOp.Esin:
                StartEfu(vu, MathF.Sin(Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.Is]), slot.Fsf)));
                break;
            case Vu1LowerOp.Eatan:
                StartEfu(vu, MathF.Atan(Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.Is]), slot.Fsf)));
                break;
            case Vu1LowerOp.Eexp:
                StartEfu(vu, MathF.Exp(-Unsafe.Add(ref Unsafe.As<Vector128<float>, float>(ref vu.VF[slot.Is]), slot.Fsf)));
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StartEfu(Vu1State vu, float val)
    {
        if (vu.HasPendingP)
        {
            vu.P = vu.PendingP;
            vu.PendingP = val;
            vu.PendingPReadyCycle = 12;
        }
        else
        {
            vu.P = val;
            vu.PendingP = val;
            vu.HasPendingP = true;
            vu.PendingPReadyCycle = 12;
        }
    }

    private void DecodeAll(byte[] microMem)
    {
        _anyFlagReader = false;
        Array.Clear(_programUsesFlagsState);
        for (int i = 0; i < 2048; i++)
        {
            ulong raw = Unsafe.ReadUnaligned<ulong>(ref microMem[i << 3]);
            uint up = (uint)(raw >> 32);
            uint lo = (uint)raw;

            Vu1DecodedSlot s = default;
            s.IsImm = (up & 0x80000000u) != 0;
            s.IsE = (up & 0x40000000u) != 0;
            if (s.IsImm)
                s.LoiFloat = BitConverter.UInt32BitsToSingle(lo);

            // Upper decode
            if ((up & 0x07FFFFFFu) != 0x000002FFu)
            {
                s.Dest = (byte)((up >> 21) & 0xFu);
                s.Ft = (byte)((up >> 16) & 0x1Fu);
                s.Fs = (byte)((up >> 11) & 0x1Fu);
                s.Fd = (byte)((up >> 6) & 0x1Fu);
                uint op = up & 0x3Fu;
                if (op >= 0x3C)
                {
                    uint subOp = ((up >> 4) & 0x7Cu) | (up & 0x3u);
                    s.Bc = (byte)(up & 3u);
                    s.UpperOp = subOp switch
                    {
                        0x00 or 0x01 or 0x02 or 0x03 => Vu1UpperOp.AddABc,
                        0x04 or 0x05 or 0x06 or 0x07 => Vu1UpperOp.SubABc,
                        0x08 or 0x09 or 0x0A or 0x0B => Vu1UpperOp.MaddABc,
                        0x0C or 0x0D or 0x0E or 0x0F => Vu1UpperOp.MsubABc,
                        0x10 => Vu1UpperOp.Itof0,
                        0x11 => Vu1UpperOp.Itof4,
                        0x12 => Vu1UpperOp.Itof12,
                        0x13 => Vu1UpperOp.Itof15,
                        0x14 => Vu1UpperOp.Ftoi0,
                        0x15 => Vu1UpperOp.Ftoi4,
                        0x16 => Vu1UpperOp.Ftoi12,
                        0x17 => Vu1UpperOp.Ftoi15,
                        0x18 or 0x19 or 0x1A or 0x1B => Vu1UpperOp.MulABc,
                        0x1C => Vu1UpperOp.MulAq,
                        0x1D => Vu1UpperOp.Abs,
                        0x1E => Vu1UpperOp.MulAi,
                        0x1F => Vu1UpperOp.Clip,
                        0x20 => Vu1UpperOp.AddAq,
                        0x21 => Vu1UpperOp.MaddAq,
                        0x22 => Vu1UpperOp.AddAi,
                        0x23 => Vu1UpperOp.MaddAi,
                        0x24 => Vu1UpperOp.SubAq,
                        0x25 => Vu1UpperOp.MsubAq,
                        0x26 => Vu1UpperOp.SubAi,
                        0x27 => Vu1UpperOp.MsubAi,
                        0x28 => Vu1UpperOp.AddA,
                        0x29 => Vu1UpperOp.MaddA,
                        0x2A => Vu1UpperOp.MulA,
                        0x2C => Vu1UpperOp.SubA,
                        0x2D => Vu1UpperOp.MsubA,
                        0x2E => Vu1UpperOp.Opmula,
                        _ => Vu1UpperOp.Unknown
                    };
                }
                else
                {
                    s.Bc = (byte)(op & 3u);
                    s.UpperOp = op switch
                    {
                        0x00 or 0x01 or 0x02 or 0x03 => Vu1UpperOp.AddBc,
                        0x04 or 0x05 or 0x06 or 0x07 => Vu1UpperOp.SubBc,
                        0x08 or 0x09 or 0x0A or 0x0B => Vu1UpperOp.MaddBc,
                        0x0C or 0x0D or 0x0E or 0x0F => Vu1UpperOp.MsubBc,
                        0x10 or 0x11 or 0x12 or 0x13 => Vu1UpperOp.MaxBc,
                        0x14 or 0x15 or 0x16 or 0x17 => Vu1UpperOp.MinBc,
                        0x18 or 0x19 or 0x1A or 0x1B => Vu1UpperOp.MulBc,
                        0x1C => Vu1UpperOp.MulQ,
                        0x1D => Vu1UpperOp.MaxI,
                        0x1E => Vu1UpperOp.MulI,
                        0x1F => Vu1UpperOp.MinI,
                        0x20 => Vu1UpperOp.AddQ,
                        0x21 => Vu1UpperOp.MaddQ,
                        0x22 => Vu1UpperOp.AddI,
                        0x23 => Vu1UpperOp.MaddI,
                        0x24 => Vu1UpperOp.SubQ,
                        0x25 => Vu1UpperOp.MsubQ,
                        0x26 => Vu1UpperOp.SubI,
                        0x27 => Vu1UpperOp.MsubI,
                        0x28 => Vu1UpperOp.Add,
                        0x29 => Vu1UpperOp.Madd,
                        0x2A => Vu1UpperOp.Mul,
                        0x2B => Vu1UpperOp.Max,
                        0x2C => Vu1UpperOp.Sub,
                        0x2D => Vu1UpperOp.Msub,
                        0x2E => Vu1UpperOp.Opmsub,
                        0x2F => Vu1UpperOp.Min,
                        _ => Vu1UpperOp.Unknown
                    };
                }
            }

            // Lower decode
            if (!s.IsImm && lo != 0x8000033Cu && lo != 0u)
            {
                uint top = (lo >> 25) & 0x7Fu;
                s.LowerDest = (byte)((lo >> 21) & 0xFu);
                s.It = (byte)((lo >> 16) & 0x1Fu);
                s.Is = (byte)((lo >> 11) & 0x1Fu);
                s.Id = (byte)((lo >> 6) & 0x1Fu);
                int imm11 = (int)(lo & 0x7FFu);
                if ((imm11 & 0x400) != 0) imm11 |= ~0x7FF;
                s.Imm11 = (short)imm11;
                s.Uimm15 = (ushort)((((lo >> 21) & 0xFu) << 11) | (lo & 0x7FFu));
                s.Imm24 = lo & 0xFFFFFFu;

                if (top >= 0x20 && top <= 0x2F)
                {
                    s.BranchTargetSlot = (ushort)((i + 1 + imm11) & 0x7FF);
                    s.BranchOp = top switch
                    {
                        0x20 => Vu1BranchOp.B,
                        0x21 => Vu1BranchOp.Bal,
                        0x24 => Vu1BranchOp.Jr,
                        0x25 => Vu1BranchOp.Jalr,
                        0x28 => Vu1BranchOp.Ibeq,
                        0x29 => Vu1BranchOp.Ibne,
                        0x2C => Vu1BranchOp.Ibltz,
                        0x2D => Vu1BranchOp.Ibgtz,
                        0x2E => Vu1BranchOp.Iblez,
                        0x2F => Vu1BranchOp.Ibgez,
                        _ => Vu1BranchOp.None
                    };
                }
                else if (top == 0x40)
                {
                    uint op = lo & 0x3Fu;
                    if (op < 0x3C)
                    {
                        int imm5 = s.Id;
                        if ((imm5 & 0x10) != 0) imm5 |= ~0x1F;
                        s.Imm11 = (short)imm5;
                        s.LowerOp = op switch
                        {
                            0x30 => Vu1LowerOp.Iadd,
                            0x31 => Vu1LowerOp.Isub,
                            0x32 => Vu1LowerOp.Iaddi,
                            0x34 => Vu1LowerOp.Iand,
                            0x35 => Vu1LowerOp.Ior,
                            _ => Vu1LowerOp.Nop
                        };
                    }
                    else
                    {
                        uint subOp = ((lo >> 4) & 0x7Cu) | (lo & 0x3u);
                        s.Fsf = (byte)((lo >> 21) & 0x3u);
                        s.Ftf = (byte)((lo >> 23) & 0x3u);
                        if (subOp is >= 0x38u and <= 0x3Bu) s.NeedsWaitQ = true;
                        else if (subOp >= 0x70u && subOp <= 0x7Eu) s.NeedsWaitP = true;
                        s.LowerOp = subOp switch
                        {
                            0x30 => Vu1LowerOp.Move,
                            0x31 => Vu1LowerOp.Mr32,
                            0x34 => Vu1LowerOp.Lqi,
                            0x35 => Vu1LowerOp.Sqi,
                            0x36 => Vu1LowerOp.Lqd,
                            0x37 => Vu1LowerOp.Sqd,
                            0x38 => Vu1LowerOp.Div,
                            0x39 => Vu1LowerOp.Sqrt,
                            0x3A => Vu1LowerOp.Rsqrt,
                            0x3B => Vu1LowerOp.WaitQ,
                            0x3C => Vu1LowerOp.Mtir,
                            0x3D => Vu1LowerOp.Mfir,
                            0x3E => Vu1LowerOp.Ilwr,
                            0x3F => Vu1LowerOp.Iswr,
                            0x40 => Vu1LowerOp.Rnext,
                            0x41 => Vu1LowerOp.Rget,
                            0x42 => Vu1LowerOp.Rinit,
                            0x43 => Vu1LowerOp.Rxor,
                            0x64 => Vu1LowerOp.Mfp,
                            0x68 => Vu1LowerOp.Xtop,
                            0x69 => Vu1LowerOp.Xitop,
                            0x6C => Vu1LowerOp.Xgkick,
                            0x70 => Vu1LowerOp.Esadd,
                            0x71 => Vu1LowerOp.Ersadd,
                            0x72 => Vu1LowerOp.Eleng,
                            0x73 => Vu1LowerOp.Erleng,
                            0x74 => Vu1LowerOp.Eatanxy,
                            0x75 => Vu1LowerOp.Eatanxz,
                            0x76 => Vu1LowerOp.Esum,
                            0x77 => Vu1LowerOp.Ercpr,
                            0x78 => Vu1LowerOp.Esqrt,
                            0x79 => Vu1LowerOp.Ersqrt,
                            0x7B => Vu1LowerOp.WaitP,
                            0x7C => Vu1LowerOp.Esin,
                            0x7D => Vu1LowerOp.Eatan,
                            0x7E => Vu1LowerOp.Eexp,
                            _ => Vu1LowerOp.Nop
                        };
                    }
                }
                else
                {
                    s.LowerOp = top switch
                    {
                        0x00 => Vu1LowerOp.Lq,
                        0x01 => Vu1LowerOp.Sq,
                        0x04 => Vu1LowerOp.Ilw,
                        0x05 => Vu1LowerOp.Isw,
                        0x08 => Vu1LowerOp.Iaddiu,
                        0x09 => Vu1LowerOp.Isubiu,
                        0x10 => Vu1LowerOp.Fceq,
                        0x11 => Vu1LowerOp.Fcset,
                        0x12 => Vu1LowerOp.Fcand,
                        0x13 => Vu1LowerOp.Fcor,
                        0x14 => Vu1LowerOp.Fsset,
                        0x16 => Vu1LowerOp.Fsand,
                        0x17 => Vu1LowerOp.Fsor,
                        0x18 => Vu1LowerOp.Fmeq,
                        0x1A => Vu1LowerOp.Fmand,
                        0x1B => Vu1LowerOp.Fmor,
                        0x1C => Vu1LowerOp.Fcget,
                        _ => Vu1LowerOp.Nop
                    };

                    if (s.LowerOp is Vu1LowerOp.Fsand or Vu1LowerOp.Fsor or Vu1LowerOp.Fmeq or Vu1LowerOp.Fmand or Vu1LowerOp.Fmor)
                        _anyFlagReader = true;
                }
            }

            _slots[i] = s;
        }
    }
}
