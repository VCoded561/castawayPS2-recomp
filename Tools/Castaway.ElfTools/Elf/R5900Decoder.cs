namespace Castaway.ElfTools.Elf;

public enum R5900Op : ushort
{
    Invalid = 0,
    // Special
    Sll, Srl, Sra, Sllv, Srlv, Srav, Jr, Jalr, Movz, Movn, Syscall, Break, Sync,
    Mfhi, Mthi, Mflo, Mtlo, Dsllv, Dsrlv, Dsrav, Mult, Multu, Div, Divu,
    Add, Addu, Sub, Subu, And, Or, Xor, Nor, Mfsa, Mtsa, Slt, Sltu,
    Dadd, Daddu, Dsub, Dsubu, Tge, Tgeu, Tlt, Tltu, Teq, Tne,
    Dsll, Dsrl, Dsra, Dsll32, Dsrl32, Dsra32,

    // Regimm
    Bltz, Bgez, Bltzl, Bgezl, Tgei, Tgeiu, Tlti, Tltiu, Teqi, Tnei,
    Bltzal, Bgezal, Bltzall, Bgezall, Mtsab, Mtsah,

    // Primary
    J, Jal, Beq, Bne, Blez, Bgtz, Addi, Addiu, Slti, Sltiu, Andi, Ori, Xori, Lui,
    Beql, Bnel, Blezl, Bgtzl, Daddi, Daddiu, Ldl, Ldr, Lq, Sq,
    Lb, Lh, Lwl, Lw, Lbu, Lhu, Lwr, Lwu, Sb, Sh, Swl, Sw, Sdl, Sdr, Swr,
    Cache, Ll, Lwc1, Pref, Lqc2, Ld, Sc, Swc1, Sqc2, Sd,

    // MMI
    Madd, Maddu, Msub, Msubu, Plzcw, Mfhi1, Mthi1, Mflo1, Mtlo1, Mult1, Multu1, Div1, Divu1,
    Madd1, Maddu1, Pmfhl, Pmthl, Psllh, Psrlh, Psrah, Psllw, Psrlw, Psraw,
    Paddw, Psubw, Pcgtw, Pmaxw, Paddh, Psubh, Pcgth, Pmaxh, Paddb, Psubb, Pcgtb,
    Paddsw, Psubsw, Pextlw, Ppacw, Paddsh, Psubsh, Pextlh, Ppach,
    Paddsb, Psubsb, Pextlb, Ppacb, Pext5, Ppac5,
    Pabsw, Pceqw, Pminw, Padsbh, Pabsh, Pceqh, Pminh, Pceqb,
    Padduw, Psubuw, Pextuw, Padduh, Psubuh, Pextuh, Paddub, Psubub, Pextub, Qfsrv,
    Pmaddw, Psllvw, Psrlvw, Pmsubw, Pmfhi, Pmflo, Pinth, Pmultw, Pdivw, Pcpyld,
    Pmaddh, Phmadh, Pand, Pxor, Pmsubh, Phmsbh, Pexeh, Prevh, Pmulth, Pdivbw, Pexew, Prot3w,
    Pmadduw, Psravw, Pmthi, Pmtlo, Pinteh, Pmultuw, Pdivuw, Pcpyud, Por, Pnor,
    Pexch, Pcpyh, Pexcw,

    // COP0
    Mfc0, Mtc0, Bc0f, Bc0t, Bc0fl, Bc0tl, Tlbr, Tlbwi, Tlbwr, Tlbp, Eret, Ei, Di,

    // COP1
    Mfc1, Cfc1, Mtc1, Ctc1, Bc1f, Bc1t, Bc1fl, Bc1tl,
    AddS, SubS, MulS, DivS, SqrtS, AbsS, MovS, NegS,
    RoundWS, TruncWS, CeilWS, FloorWS,
    RsqrtS, AddaS, SubaS, MulaS,
    MaddS, MsubS, MaddaS, MsubaS, CvtWS, MaxS, MinS,
    CF, CUn, CEq, CUeq, COlt, CUlt, COle, CUle, CSf, CNgle, CSeq, CNgl, CLt, CNge, CLe, CNgt,
    CvtSW,

    // COP2 (VU0 Macro mode)
    Qmfc2, Cfc2, Qmtc2, Ctc2, Bc2f, Bc2t, Bc2fl, Bc2tl,
    Vaddbc, Vsubbc, Vmaddbc, Vmsubbc, Vmaxbc, Vminibc, Vmulbc, Vmulq, Vmaxi, Vmuli, Vminii,
    Vaddq, Vmaddq, Vaddi, Vmaddi, Vsubq, Vmsubq, Vsubi, Vmsubi,
    Vadd, Vmadd, Vmul, Vmax, Vsub, Vmsub, Vopmsub, Vmini,
    Viadd, Visub, Viaddi, Viand, Vior, Vcallms, Vcallmsr,
    Vaddabc, Vsubabc, Vmaddabc, Vmsubabc, Vitof0, Vitof4, Vitof12, Vitof15,
    Vftoi0, Vftoi4, Vftoi12, Vftoi15, Vmulabc, Vmulaq, Vabs, Vmulai, Vclip,
    Vaddaq, Vmaddaq, Vaddai, Vmaddai, Vsubaq, Vmsubaq, Vsubai, Vmsubai,
    Vadda, Vmadda, Vmula, Vsuba, Vmsuba, Vopmula, Vnop, Vmove, Vmr32,
    Vlqi, Vsqi, Vlqd, Vsqd, Vdiv, Vsqrt, Vrsqrt, Vwaitq, Vmtir, Vmfir, Vilwr, Viswr,
    Vrnext, Vrget, Vrinit, Vrxor
}

public enum FlowType : byte
{
    None,
    Branch,
    BranchLikely,
    Jump,
    Call,
    JumpRegister,
    CallRegister,
    Stop
}

public readonly struct R5900Insn
{
    public readonly uint Pc;
    public readonly uint Word;
    public readonly R5900Op Op;

    public R5900Insn(uint pc, uint word, R5900Op op)
    {
        Pc = pc;
        Word = word;
        Op = op;
    }

    public uint Rs => (Word >> 21) & 31u;
    public uint Rt => (Word >> 16) & 31u;
    public uint Rd => (Word >> 11) & 31u;
    public uint Sa => (Word >> 6) & 31u;
    public uint Fs => Rd;
    public uint Ft => Rt;
    public uint Fd => Sa;
    public short Simm => (short)(Word & 0xFFFFu);
    public ushort Uimm => (ushort)(Word & 0xFFFFu);
    public uint JumpTarget => ((Pc + 4u) & 0xF0000000u) | ((Word & 0x03FFFFFFu) << 2);
    public uint BranchTarget => (uint)(Pc + 4u + ((int)Simm << 2));
    public uint Dest => (Word >> 21) & 15u;
    public uint Fsf => (Word >> 21) & 3u;
    public uint Ftf => (Word >> 23) & 3u;
    public uint Bc => Word & 3u;

    public bool HasDelaySlot => R5900Decoder.HasDelaySlot(Op);
    public FlowType Flow => R5900Decoder.GetFlow(Op);
    public bool IsBranch => Flow == FlowType.Branch || Flow == FlowType.BranchLikely;
    public bool IsBranchLikely => Flow == FlowType.BranchLikely;
    public bool IsCall => Flow == FlowType.Call || Flow == FlowType.CallRegister;
    public bool IsJump => Flow == FlowType.Jump || Flow == FlowType.JumpRegister;
    public bool IsReturn => Op == R5900Op.Jr && Rs == 31; // jr $ra

    public bool IsLoad => Op switch
    {
        R5900Op.Lb or R5900Op.Lh or R5900Op.Lwl or R5900Op.Lw or R5900Op.Lbu or
        R5900Op.Lhu or R5900Op.Lwr or R5900Op.Lwu or R5900Op.Ld or R5900Op.Ldl or
        R5900Op.Ldr or R5900Op.Lq or R5900Op.Lwc1 or R5900Op.Lqc2 => true,
        _ => false
    };

    public bool IsStore => Op switch
    {
        R5900Op.Sb or R5900Op.Sh or R5900Op.Swl or R5900Op.Sw or R5900Op.Sdl or
        R5900Op.Sdr or R5900Op.Swr or R5900Op.Sd or R5900Op.Sq or R5900Op.Swc1 or
        R5900Op.Sqc2 => true,
        _ => false
    };

    /// <summary>
    /// Returns the GPR index (1..31) modified by this instruction, or 0 if no GPR is written.
    /// Avoids treating bits 15..11 of I-type immediates as an R-type Rd destination.
    /// </summary>
    public uint WrittenGpr
    {
        get
        {
            uint pri = (Word >> 26) & 0x3Fu;
            if (pri == 0 || pri == 28) // SPECIAL or MMI (R-type)
            {
                return Op is R5900Op.Jr or R5900Op.Syscall or R5900Op.Break or R5900Op.Sync or
                             R5900Op.Mthi or R5900Op.Mtlo or R5900Op.Mtsa or R5900Op.Div or R5900Op.Divu or
                             R5900Op.Tge or R5900Op.Tgeu or R5900Op.Tlt or R5900Op.Tltu or R5900Op.Teq or R5900Op.Tne or
                             R5900Op.Mthi1 or R5900Op.Mtlo1 or R5900Op.Div1 or R5900Op.Divu1 or
                             R5900Op.Pmthl or R5900Op.Pmthi or R5900Op.Pmtlo
                    ? 0u
                    : Rd;
            }
            return Op switch
            {
                R5900Op.Addi or R5900Op.Addiu or R5900Op.Slti or R5900Op.Sltiu or
                R5900Op.Andi or R5900Op.Ori or R5900Op.Xori or R5900Op.Lui or
                R5900Op.Daddi or R5900Op.Daddiu or R5900Op.Ldl or R5900Op.Ldr or R5900Op.Lq or
                R5900Op.Lb or R5900Op.Lh or R5900Op.Lwl or R5900Op.Lw or
                R5900Op.Lbu or R5900Op.Lhu or R5900Op.Lwr or R5900Op.Lwu or
                R5900Op.Ll or R5900Op.Ld or R5900Op.Sc or
                R5900Op.Mfc0 or R5900Op.Mfc1 or R5900Op.Cfc1 or R5900Op.Qmfc2 or R5900Op.Cfc2 => Rt,
                R5900Op.Jal or R5900Op.Bltzal or R5900Op.Bgezal or R5900Op.Bltzall or R5900Op.Bgezall => 31u,
                _ => 0u
            };
        }
    }

    public override string ToString() => R5900Decoder.Disassemble(this);
}

public static class R5900Decoder
{
    private static readonly string[] GprNames =
    {
        "$zero", "$at", "$v0", "$v1", "$a0", "$a1", "$a2", "$a3",
        "$t0", "$t1", "$t2", "$t3", "$t4", "$t5", "$t6", "$t7",
        "$s0", "$s1", "$s2", "$s3", "$s4", "$s5", "$s6", "$s7",
        "$t8", "$t9", "$k0", "$k1", "$gp", "$sp", "$fp", "$ra"
    };

    private static readonly R5900Op[] PrimaryTable = new R5900Op[64]
    {
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.J, R5900Op.Jal, R5900Op.Beq, R5900Op.Bne, R5900Op.Blez, R5900Op.Bgtz,
        R5900Op.Addi, R5900Op.Addiu, R5900Op.Slti, R5900Op.Sltiu, R5900Op.Andi, R5900Op.Ori, R5900Op.Xori, R5900Op.Lui,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Beql, R5900Op.Bnel, R5900Op.Blezl, R5900Op.Bgtzl,
        R5900Op.Daddi, R5900Op.Daddiu, R5900Op.Ldl, R5900Op.Ldr, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Lq, R5900Op.Sq,
        R5900Op.Lb, R5900Op.Lh, R5900Op.Lwl, R5900Op.Lw, R5900Op.Lbu, R5900Op.Lhu, R5900Op.Lwr, R5900Op.Lwu,
        R5900Op.Sb, R5900Op.Sh, R5900Op.Swl, R5900Op.Sw, R5900Op.Sdl, R5900Op.Sdr, R5900Op.Swr, R5900Op.Cache,
        R5900Op.Ll, R5900Op.Lwc1, R5900Op.Invalid, R5900Op.Pref, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Lqc2, R5900Op.Ld,
        R5900Op.Sc, R5900Op.Swc1, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Sqc2, R5900Op.Sd
    };

    private static readonly R5900Op[] SpecialTable = new R5900Op[64]
    {
        R5900Op.Sll, R5900Op.Invalid, R5900Op.Srl, R5900Op.Sra, R5900Op.Sllv, R5900Op.Invalid, R5900Op.Srlv, R5900Op.Srav,
        R5900Op.Jr, R5900Op.Jalr, R5900Op.Movz, R5900Op.Movn, R5900Op.Syscall, R5900Op.Break, R5900Op.Invalid, R5900Op.Sync,
        R5900Op.Mfhi, R5900Op.Mthi, R5900Op.Mflo, R5900Op.Mtlo, R5900Op.Dsllv, R5900Op.Invalid, R5900Op.Dsrlv, R5900Op.Dsrav,
        R5900Op.Mult, R5900Op.Multu, R5900Op.Div, R5900Op.Divu, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Add, R5900Op.Addu, R5900Op.Sub, R5900Op.Subu, R5900Op.And, R5900Op.Or, R5900Op.Xor, R5900Op.Nor,
        R5900Op.Mfsa, R5900Op.Mtsa, R5900Op.Slt, R5900Op.Sltu, R5900Op.Dadd, R5900Op.Daddu, R5900Op.Dsub, R5900Op.Dsubu,
        R5900Op.Tge, R5900Op.Tgeu, R5900Op.Tlt, R5900Op.Tltu, R5900Op.Teq, R5900Op.Invalid, R5900Op.Tne, R5900Op.Invalid,
        R5900Op.Dsll, R5900Op.Invalid, R5900Op.Dsrl, R5900Op.Dsra, R5900Op.Dsll32, R5900Op.Invalid, R5900Op.Dsrl32, R5900Op.Dsra32
    };

    private static readonly R5900Op[] RegimmTable = new R5900Op[32]
    {
        R5900Op.Bltz, R5900Op.Bgez, R5900Op.Bltzl, R5900Op.Bgezl, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Tgei, R5900Op.Tgeiu, R5900Op.Tlti, R5900Op.Tltiu, R5900Op.Teqi, R5900Op.Invalid, R5900Op.Tnei, R5900Op.Invalid,
        R5900Op.Bltzal, R5900Op.Bgezal, R5900Op.Bltzall, R5900Op.Bgezall, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Mtsab, R5900Op.Mtsah, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid
    };

    private static readonly R5900Op[] MmiTable = new R5900Op[64]
    {
        R5900Op.Madd, R5900Op.Maddu, R5900Op.Msub, R5900Op.Msubu, R5900Op.Plzcw, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Mfhi1, R5900Op.Mthi1, R5900Op.Mflo1, R5900Op.Mtlo1, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Mult1, R5900Op.Multu1, R5900Op.Div1, R5900Op.Divu1, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Madd1, R5900Op.Maddu1, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Pmfhl, R5900Op.Pmthl, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Psllh, R5900Op.Invalid, R5900Op.Psrlh, R5900Op.Psrah,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Psllw, R5900Op.Invalid, R5900Op.Psrlw, R5900Op.Psraw
    };

    private static readonly R5900Op[] Mmi0Table = new R5900Op[32]
    {
        R5900Op.Paddw, R5900Op.Psubw, R5900Op.Pcgtw, R5900Op.Pmaxw, R5900Op.Paddh, R5900Op.Psubh, R5900Op.Pcgth, R5900Op.Pmaxh,
        R5900Op.Paddb, R5900Op.Psubb, R5900Op.Pcgtb, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Paddsw, R5900Op.Psubsw, R5900Op.Pextlw, R5900Op.Ppacw, R5900Op.Paddsh, R5900Op.Psubsh, R5900Op.Pextlh, R5900Op.Ppach,
        R5900Op.Paddsb, R5900Op.Psubsb, R5900Op.Pextlb, R5900Op.Ppacb, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Pext5, R5900Op.Ppac5
    };

    private static readonly R5900Op[] Mmi1Table = new R5900Op[32]
    {
        R5900Op.Invalid, R5900Op.Pabsw, R5900Op.Pceqw, R5900Op.Pminw, R5900Op.Padsbh, R5900Op.Pabsh, R5900Op.Pceqh, R5900Op.Pminh,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Pceqb, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Padduw, R5900Op.Psubuw, R5900Op.Pextuw, R5900Op.Invalid, R5900Op.Padduh, R5900Op.Psubuh, R5900Op.Pextuh, R5900Op.Invalid,
        R5900Op.Paddub, R5900Op.Psubub, R5900Op.Pextub, R5900Op.Qfsrv, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid
    };

    private static readonly R5900Op[] Mmi2Table = new R5900Op[32]
    {
        R5900Op.Pmaddw, R5900Op.Invalid, R5900Op.Psllvw, R5900Op.Psrlvw, R5900Op.Pmsubw, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Pmfhi, R5900Op.Pmflo, R5900Op.Pinth, R5900Op.Invalid, R5900Op.Pmultw, R5900Op.Pdivw, R5900Op.Pcpyld, R5900Op.Invalid,
        R5900Op.Pmaddh, R5900Op.Phmadh, R5900Op.Pand, R5900Op.Pxor, R5900Op.Pmsubh, R5900Op.Phmsbh, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Pexeh, R5900Op.Prevh, R5900Op.Pmulth, R5900Op.Pdivbw, R5900Op.Pexew, R5900Op.Prot3w
    };

    private static readonly R5900Op[] Mmi3Table = new R5900Op[32]
    {
        R5900Op.Pmadduw, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Psravw, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Pmthi, R5900Op.Pmtlo, R5900Op.Pinteh, R5900Op.Invalid, R5900Op.Pmultuw, R5900Op.Pdivuw, R5900Op.Pcpyud, R5900Op.Invalid,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Por, R5900Op.Pnor, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Pexch, R5900Op.Pcpyh, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Pexcw, R5900Op.Invalid
    };

    private static readonly R5900Op[] Cop1STable = new R5900Op[64]
    {
        R5900Op.AddS, R5900Op.SubS, R5900Op.MulS, R5900Op.DivS, R5900Op.SqrtS, R5900Op.AbsS, R5900Op.MovS, R5900Op.NegS,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.RoundWS, R5900Op.TruncWS, R5900Op.CeilWS, R5900Op.FloorWS,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.RsqrtS, R5900Op.Invalid,
        R5900Op.AddaS, R5900Op.SubaS, R5900Op.MulaS, R5900Op.Invalid, R5900Op.MaddS, R5900Op.MsubS, R5900Op.MaddaS, R5900Op.MsubaS,
        R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.CvtWS, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.MaxS, R5900Op.MinS, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.CF, R5900Op.CUn, R5900Op.CEq, R5900Op.CUeq, R5900Op.COlt, R5900Op.CUlt, R5900Op.COle, R5900Op.CUle,
        R5900Op.CSf, R5900Op.CNgle, R5900Op.CSeq, R5900Op.CNgl, R5900Op.CLt, R5900Op.CNge, R5900Op.CLe, R5900Op.CNgt
    };

    private static readonly R5900Op[] VectorTable = new R5900Op[64]
    {
        R5900Op.Vaddbc, R5900Op.Vaddbc, R5900Op.Vaddbc, R5900Op.Vaddbc, R5900Op.Vsubbc, R5900Op.Vsubbc, R5900Op.Vsubbc, R5900Op.Vsubbc,
        R5900Op.Vmaddbc, R5900Op.Vmaddbc, R5900Op.Vmaddbc, R5900Op.Vmaddbc, R5900Op.Vmsubbc, R5900Op.Vmsubbc, R5900Op.Vmsubbc, R5900Op.Vmsubbc,
        R5900Op.Vmaxbc, R5900Op.Vmaxbc, R5900Op.Vmaxbc, R5900Op.Vmaxbc, R5900Op.Vminibc, R5900Op.Vminibc, R5900Op.Vminibc, R5900Op.Vminibc,
        R5900Op.Vmulbc, R5900Op.Vmulbc, R5900Op.Vmulbc, R5900Op.Vmulbc, R5900Op.Vmulq, R5900Op.Vmaxi, R5900Op.Vmuli, R5900Op.Vminii,
        R5900Op.Vaddq, R5900Op.Vmaddq, R5900Op.Vaddi, R5900Op.Vmaddi, R5900Op.Vsubq, R5900Op.Vmsubq, R5900Op.Vsubi, R5900Op.Vmsubi,
        R5900Op.Vadd, R5900Op.Vmadd, R5900Op.Vmul, R5900Op.Vmax, R5900Op.Vsub, R5900Op.Vmsub, R5900Op.Vopmsub, R5900Op.Vmini,
        R5900Op.Viadd, R5900Op.Visub, R5900Op.Viaddi, R5900Op.Invalid, R5900Op.Viand, R5900Op.Vior, R5900Op.Invalid, R5900Op.Invalid,
        R5900Op.Vcallms, R5900Op.Vcallmsr, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid
    };

    private static readonly R5900Op[,] VectorXTable = new R5900Op[4, 32]
    {
        {
            R5900Op.Vaddabc, R5900Op.Vsubabc, R5900Op.Vmaddabc, R5900Op.Vmsubabc, R5900Op.Vitof0, R5900Op.Vftoi0, R5900Op.Vmulabc, R5900Op.Vmulaq,
            R5900Op.Vaddaq, R5900Op.Vsubaq, R5900Op.Vadda, R5900Op.Vsuba, R5900Op.Vmove, R5900Op.Vlqi, R5900Op.Vdiv, R5900Op.Vmtir,
            R5900Op.Vrnext, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
            R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid
        },
        {
            R5900Op.Vaddabc, R5900Op.Vsubabc, R5900Op.Vmaddabc, R5900Op.Vmsubabc, R5900Op.Vitof4, R5900Op.Vftoi4, R5900Op.Vmulabc, R5900Op.Vabs,
            R5900Op.Vmaddaq, R5900Op.Vmsubaq, R5900Op.Vmadda, R5900Op.Vmsuba, R5900Op.Vmr32, R5900Op.Vsqi, R5900Op.Vsqrt, R5900Op.Vmfir,
            R5900Op.Vrget, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
            R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid
        },
        {
            R5900Op.Vaddabc, R5900Op.Vsubabc, R5900Op.Vmaddabc, R5900Op.Vmsubabc, R5900Op.Vitof12, R5900Op.Vftoi12, R5900Op.Vmulabc, R5900Op.Vmulai,
            R5900Op.Vaddai, R5900Op.Vsubai, R5900Op.Vmula, R5900Op.Vopmula, R5900Op.Invalid, R5900Op.Vlqd, R5900Op.Vrsqrt, R5900Op.Vilwr,
            R5900Op.Vrinit, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
            R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid
        },
        {
            R5900Op.Vaddabc, R5900Op.Vsubabc, R5900Op.Vmaddabc, R5900Op.Vmsubabc, R5900Op.Vitof15, R5900Op.Vftoi15, R5900Op.Vmulabc, R5900Op.Vclip,
            R5900Op.Vmaddai, R5900Op.Vmsubai, R5900Op.Invalid, R5900Op.Vnop, R5900Op.Invalid, R5900Op.Vsqd, R5900Op.Vwaitq, R5900Op.Viswr,
            R5900Op.Vrxor, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid,
            R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid, R5900Op.Invalid
        }
    };

    public static R5900Insn Decode(uint word, uint pc)
    {
        uint primary = word >> 26;
        R5900Op op = primary switch
        {
            0 => SpecialTable[word & 63u],
            1 => RegimmTable[(word >> 16) & 31u],
            16 => DecodeCop0(word),
            17 => DecodeCop1(word),
            18 => DecodeCop2(word),
            28 => DecodeMmi(word),
            _ => PrimaryTable[primary]
        };

        return new R5900Insn(pc, word, op);
    }

    private static R5900Op DecodeMmi(uint word)
    {
        uint funct = word & 63u;
        uint sub = (word >> 6) & 31u;
        return funct switch
        {
            8 => Mmi0Table[sub],
            40 => Mmi1Table[sub],
            9 => Mmi2Table[sub],
            41 => Mmi3Table[sub],
            48 => sub <= 4 ? R5900Op.Pmfhl : R5900Op.Invalid,
            49 => sub == 0 ? R5900Op.Pmthl : R5900Op.Invalid,
            _ => MmiTable[funct]
        };
    }

    private static R5900Op DecodeCop0(uint word)
    {
        uint rs = (word >> 21) & 31u;
        if (rs == 0) return R5900Op.Mfc0;
        if (rs == 4) return R5900Op.Mtc0;
        if (rs == 8)
        {
            uint rt = (word >> 16) & 31u;
            return rt switch
            {
                0 => R5900Op.Bc0f,
                1 => R5900Op.Bc0t,
                2 => R5900Op.Bc0fl,
                3 => R5900Op.Bc0tl,
                _ => R5900Op.Invalid
            };
        }
        if (rs == 16)
        {
            return (word & 63u) switch
            {
                1 => R5900Op.Tlbr,
                2 => R5900Op.Tlbwi,
                6 => R5900Op.Tlbwr,
                8 => R5900Op.Tlbp,
                24 => R5900Op.Eret,
                56 => R5900Op.Ei,
                57 => R5900Op.Di,
                _ => R5900Op.Invalid
            };
        }
        return R5900Op.Invalid;
    }

    private static R5900Op DecodeCop1(uint word)
    {
        uint rs = (word >> 21) & 31u;
        switch (rs)
        {
            case 0: return R5900Op.Mfc1;
            case 2: return R5900Op.Cfc1;
            case 4: return R5900Op.Mtc1;
            case 6: return R5900Op.Ctc1;
            case 8:
            {
                uint rt = (word >> 16) & 31u;
                return rt switch
                {
                    0 => R5900Op.Bc1f,
                    1 => R5900Op.Bc1t,
                    2 => R5900Op.Bc1fl,
                    3 => R5900Op.Bc1tl,
                    _ => R5900Op.Invalid
                };
            }
            case 16: return Cop1STable[word & 63u];
            case 20: return (word & 63u) == 32u ? R5900Op.CvtSW : R5900Op.Invalid;
            default: return R5900Op.Invalid;
        }
    }

    private static R5900Op DecodeCop2(uint word)
    {
        uint rs = (word >> 21) & 31u;
        if (rs >= 16)
        {
            uint funct = word & 63u;
            if (funct >= 60) return VectorXTable[funct & 3u, (word >> 6) & 31u];
            return VectorTable[funct];
        }
        return rs switch
        {
            1 => R5900Op.Qmfc2,
            2 => R5900Op.Cfc2,
            5 => R5900Op.Qmtc2,
            6 => R5900Op.Ctc2,
            8 => ((word >> 16) & 31u) switch
            {
                0 => R5900Op.Bc2f,
                1 => R5900Op.Bc2t,
                2 => R5900Op.Bc2fl,
                3 => R5900Op.Bc2tl,
                _ => R5900Op.Invalid
            },
            _ => R5900Op.Invalid
        };
    }

    public static FlowType GetFlow(R5900Op op) => op switch
    {
        R5900Op.Beq or R5900Op.Bne or R5900Op.Blez or R5900Op.Bgtz or R5900Op.Bltz or R5900Op.Bgez or
        R5900Op.Bc0f or R5900Op.Bc0t or R5900Op.Bc1f or R5900Op.Bc1t or R5900Op.Bc2f or R5900Op.Bc2t
            => FlowType.Branch,

        R5900Op.Beql or R5900Op.Bnel or R5900Op.Blezl or R5900Op.Bgtzl or R5900Op.Bltzl or R5900Op.Bgezl or
        R5900Op.Bc0fl or R5900Op.Bc0tl or R5900Op.Bc1fl or R5900Op.Bc1tl or R5900Op.Bc2fl or R5900Op.Bc2tl
            => FlowType.BranchLikely,

        R5900Op.Bltzal or R5900Op.Bgezal or R5900Op.Bltzall or R5900Op.Bgezall or R5900Op.Jal
            => FlowType.Call,

        R5900Op.J => FlowType.Jump,
        R5900Op.Jr => FlowType.JumpRegister,
        R5900Op.Jalr => FlowType.CallRegister,
        R5900Op.Eret or R5900Op.Invalid or R5900Op.Break => FlowType.Stop,
        _ => FlowType.None
    };

    public static bool HasDelaySlot(R5900Op op)
    {
        FlowType f = GetFlow(op);
        return f != FlowType.None && f != FlowType.Stop;
    }

    public static string Reg(uint r) => r < 32 ? GprNames[r] : $"$r{r}";
    public static string FpReg(uint r) => $"$f{r}";
    public static string VuReg(uint r) => $"$vf{r}";

    public static string Disassemble(R5900Insn insn)
    {
        if (insn.Word == 0) return "nop";

        string opName = insn.Op.ToString().ToLowerInvariant();
        uint rs = insn.Rs, rt = insn.Rt, rd = insn.Rd, sa = insn.Sa;
        short simm = insn.Simm;
        ushort uimm = insn.Uimm;

        // Common pseudo-instructions
        if ((insn.Op == R5900Op.Addu || insn.Op == R5900Op.Daddu) && rt == 0)
        {
            return $"move {Reg(rd)}, {Reg(rs)}";
        }
        if (insn.Op == R5900Op.Addiu && rs == 0)
        {
            return $"li {Reg(rt)}, {simm}";
        }
        if (insn.Op == R5900Op.Ori && rs == 0)
        {
            return $"li {Reg(rt)}, 0x{uimm:X4}";
        }

        switch (insn.Op)
        {
            case R5900Op.J:
            case R5900Op.Jal:
                return $"{opName} 0x{insn.JumpTarget:X8}";

            case R5900Op.Jr:
                return $"{opName} {Reg(rs)}";

            case R5900Op.Jalr:
                return rd == 31 ? $"{opName} {Reg(rs)}" : $"{opName} {Reg(rd)}, {Reg(rs)}";

            case R5900Op.Beq:
            case R5900Op.Bne:
            case R5900Op.Beql:
            case R5900Op.Bnel:
                if (rt == 0)
                {
                    string bz = insn.Op == R5900Op.Beq ? "beqz" : (insn.Op == R5900Op.Bne ? "bnez" : (insn.Op == R5900Op.Beql ? "beqzl" : "bnezl"));
                    return $"{bz} {Reg(rs)}, 0x{insn.BranchTarget:X8}";
                }
                return $"{opName} {Reg(rs)}, {Reg(rt)}, 0x{insn.BranchTarget:X8}";

            case R5900Op.Blez:
            case R5900Op.Bgtz:
            case R5900Op.Bltz:
            case R5900Op.Bgez:
            case R5900Op.Blezl:
            case R5900Op.Bgtzl:
            case R5900Op.Bltzl:
            case R5900Op.Bgezl:
            case R5900Op.Bltzal:
            case R5900Op.Bgezal:
            case R5900Op.Bltzall:
            case R5900Op.Bgezall:
                return $"{opName} {Reg(rs)}, 0x{insn.BranchTarget:X8}";

            case R5900Op.Lui:
                return $"lui {Reg(rt)}, 0x{uimm:X4}";

            case R5900Op.Addi:
            case R5900Op.Addiu:
            case R5900Op.Daddi:
            case R5900Op.Daddiu:
            case R5900Op.Slti:
            case R5900Op.Sltiu:
                return $"{opName} {Reg(rt)}, {Reg(rs)}, {simm}";

            case R5900Op.Andi:
            case R5900Op.Ori:
            case R5900Op.Xori:
                return $"{opName} {Reg(rt)}, {Reg(rs)}, 0x{uimm:X4}";

            case R5900Op.Lb:
            case R5900Op.Lh:
            case R5900Op.Lwl:
            case R5900Op.Lw:
            case R5900Op.Lbu:
            case R5900Op.Lhu:
            case R5900Op.Lwr:
            case R5900Op.Lwu:
            case R5900Op.Sb:
            case R5900Op.Sh:
            case R5900Op.Swl:
            case R5900Op.Sw:
            case R5900Op.Sdl:
            case R5900Op.Sdr:
            case R5900Op.Swr:
            case R5900Op.Ld:
            case R5900Op.Sd:
            case R5900Op.Ldl:
            case R5900Op.Ldr:
            case R5900Op.Lq:
            case R5900Op.Sq:
                return $"{opName} {Reg(rt)}, {simm}({Reg(rs)})";

            case R5900Op.Lwc1:
            case R5900Op.Swc1:
                return $"{opName} {FpReg(rt)}, {simm}({Reg(rs)})";

            case R5900Op.Lqc2:
            case R5900Op.Sqc2:
                return $"{opName} {VuReg(rt)}, {simm}({Reg(rs)})";

            case R5900Op.Sll:
            case R5900Op.Srl:
            case R5900Op.Sra:
            case R5900Op.Dsll:
            case R5900Op.Dsrl:
            case R5900Op.Dsra:
            case R5900Op.Dsll32:
            case R5900Op.Dsrl32:
            case R5900Op.Dsra32:
                return $"{opName} {Reg(rd)}, {Reg(rt)}, {sa}";

            case R5900Op.Sllv:
            case R5900Op.Srlv:
            case R5900Op.Srav:
            case R5900Op.Dsllv:
            case R5900Op.Dsrlv:
            case R5900Op.Dsrav:
                return $"{opName} {Reg(rd)}, {Reg(rt)}, {Reg(rs)}";

            case R5900Op.Add:
            case R5900Op.Addu:
            case R5900Op.Sub:
            case R5900Op.Subu:
            case R5900Op.And:
            case R5900Op.Or:
            case R5900Op.Xor:
            case R5900Op.Nor:
            case R5900Op.Slt:
            case R5900Op.Sltu:
            case R5900Op.Dadd:
            case R5900Op.Daddu:
            case R5900Op.Dsub:
            case R5900Op.Dsubu:
            case R5900Op.Movz:
            case R5900Op.Movn:
                return $"{opName} {Reg(rd)}, {Reg(rs)}, {Reg(rt)}";

            case R5900Op.Mult:
            case R5900Op.Multu:
            case R5900Op.Div:
            case R5900Op.Divu:
            case R5900Op.Mult1:
            case R5900Op.Multu1:
            case R5900Op.Div1:
            case R5900Op.Divu1:
                return $"{opName} {Reg(rs)}, {Reg(rt)}";

            case R5900Op.Mfhi:
            case R5900Op.Mflo:
            case R5900Op.Mfhi1:
            case R5900Op.Mflo1:
            case R5900Op.Mfsa:
                return $"{opName} {Reg(rd)}";

            case R5900Op.Mthi:
            case R5900Op.Mtlo:
            case R5900Op.Mthi1:
            case R5900Op.Mtlo1:
            case R5900Op.Mtsa:
                return $"{opName} {Reg(rs)}";

            case R5900Op.Syscall:
                uint code = (insn.Word >> 6) & 0xFFFFF;
                return code != 0 ? $"syscall 0x{code:X5}" : "syscall";

            case R5900Op.Break:
                return "break";

            case R5900Op.Sync:
                return "sync";

            // FPU
            case R5900Op.Mfc1:
                return $"mfc1 {Reg(rt)}, {FpReg(insn.Fs)}";
            case R5900Op.Mtc1:
                return $"mtc1 {Reg(rt)}, {FpReg(insn.Fs)}";
            case R5900Op.Cfc1:
                return $"cfc1 {Reg(rt)}, $fcr{insn.Fs}";
            case R5900Op.Ctc1:
                return $"ctc1 {Reg(rt)}, $fcr{insn.Fs}";

            case R5900Op.AddS:
            case R5900Op.SubS:
            case R5900Op.MulS:
            case R5900Op.DivS:
            case R5900Op.MaxS:
            case R5900Op.MinS:
                return $"{opName}.s {FpReg(insn.Fd)}, {FpReg(insn.Fs)}, {FpReg(insn.Ft)}";

            case R5900Op.SqrtS:
            case R5900Op.AbsS:
            case R5900Op.MovS:
            case R5900Op.NegS:
            case R5900Op.RsqrtS:
            case R5900Op.CvtWS:
            case R5900Op.CvtSW:
                return $"{opName}.s {FpReg(insn.Fd)}, {FpReg(insn.Fs)}";

            case R5900Op.CEq:
            case R5900Op.CLt:
            case R5900Op.CLe:
                return $"c.{opName.Substring(1)}.s {FpReg(insn.Fs)}, {FpReg(insn.Ft)}";

            // COP0
            case R5900Op.Mfc0:
                return $"mfc0 {Reg(rt)}, $c0_{rd}";
            case R5900Op.Mtc0:
                return $"mtc0 {Reg(rt)}, $c0_{rd}";
            case R5900Op.Ei:
                return "ei";
            case R5900Op.Di:
                return "di";
            case R5900Op.Eret:
                return "eret";

            // COP2 / VU0
            case R5900Op.Qmfc2:
                return $"qmfc2 {Reg(rt)}, {VuReg(insn.Fs)}";
            case R5900Op.Qmtc2:
                return $"qmtc2 {Reg(rt)}, {VuReg(insn.Fs)}";
            case R5900Op.Cfc2:
                return $"cfc2 {Reg(rt)}, $vi{insn.Fs}";
            case R5900Op.Ctc2:
                return $"ctc2 {Reg(rt)}, $vi{insn.Fs}";

            default:
                if (insn.Op != R5900Op.Invalid)
                    return $"{opName} {Reg(rd)}, {Reg(rs)}, {Reg(rt)}";
                return $".word 0x{insn.Word:X8}";
        }
    }
}
