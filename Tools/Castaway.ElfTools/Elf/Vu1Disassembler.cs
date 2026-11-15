namespace Castaway.ElfTools.Elf;

/// <summary>
/// Disassembles Sony PS2 VU1 64-bit VLIW microcode bundles (32-bit Upper SIMD + 32-bit Lower Control/Load/Store/XGKICK).
/// </summary>
public static class Vu1Disassembler
{
    private static readonly string[] BcSuffix = { "x", "y", "z", "w" };

    public static string FormatBundle(int index, uint upper, uint lower)
    {
        bool isImm = (upper & 0x80000000u) != 0;
        bool isE = (upper & 0x40000000u) != 0;
        bool isM = (upper & 0x20000000u) != 0;
        bool isD = (upper & 0x10000000u) != 0;
        bool isT = (upper & 0x08000000u) != 0;

        string flags = string.Concat(
            isImm ? "I" : "",
            isE ? "E" : "",
            isM ? "M" : "",
            isD ? "D" : "",
            isT ? "T" : "");
        if (flags.Length > 0)
            flags = "[" + flags + "] ";

        string upStr = DecodeUpper(upper);
        string loStr = isImm
            ? $"LOI {BitConverter.UInt32BitsToSingle(lower):G9} (0x{lower:X8})"
            : DecodeLower(lower, index * 8);

        return $"[{index:D3}] {flags}{upStr,-26} | {loStr}";
    }

    public static string DecodeUpper(uint upper)
    {
        uint dest = (upper >> 21) & 0xFu;
        uint ft = (upper >> 16) & 0x1Fu;
        uint fs = (upper >> 11) & 0x1Fu;
        uint fd = (upper >> 6) & 0x1Fu;
        uint op = upper & 0x3Fu;
        string mask = FormatDestMask(dest);

        if ((upper & 0x07FFFFFFu) == 0x000002FFu)
            return "NOP";

        if (op >= 0x3C)
        {
            uint subOp = ((upper >> 4) & 0x7Cu) | (upper & 0x3u);
            string bc = BcSuffix[upper & 3u];
            return subOp switch
            {
                0x00 or 0x01 or 0x02 or 0x03 => $"ADDA{bc}{mask} ACC, vf{fs}, vf{ft}",
                0x04 or 0x05 or 0x06 or 0x07 => $"SUBA{bc}{mask} ACC, vf{fs}, vf{ft}",
                0x08 or 0x09 or 0x0A or 0x0B => $"MADDA{bc}{mask} ACC, vf{fs}, vf{ft}",
                0x0C or 0x0D or 0x0E or 0x0F => $"MSUBA{bc}{mask} ACC, vf{fs}, vf{ft}",
                0x10 => $"ITOF0{mask} vf{ft}, vf{fs}",
                0x11 => $"ITOF4{mask} vf{ft}, vf{fs}",
                0x12 => $"ITOF12{mask} vf{ft}, vf{fs}",
                0x13 => $"ITOF15{mask} vf{ft}, vf{fs}",
                0x14 => $"FTOI0{mask} vf{ft}, vf{fs}",
                0x15 => $"FTOI4{mask} vf{ft}, vf{fs}",
                0x16 => $"FTOI12{mask} vf{ft}, vf{fs}",
                0x17 => $"FTOI15{mask} vf{ft}, vf{fs}",
                0x18 or 0x19 or 0x1A or 0x1B => $"MULA{bc}{mask} ACC, vf{fs}, vf{ft}",
                0x1C => $"MULAq{mask} ACC, vf{fs}, Q",
                0x1D => $"ABS{mask} vf{ft}, vf{fs}",
                0x1E => $"MULAi{mask} ACC, vf{fs}, I",
                0x1F => $"CLIPw.xyz vf{fs}, vf{ft}w",
                0x20 => $"ADDAq{mask} ACC, vf{fs}, Q",
                0x21 => $"MADDAq{mask} ACC, vf{fs}, Q",
                0x22 => $"ADDAi{mask} ACC, vf{fs}, I",
                0x23 => $"MADDAi{mask} ACC, vf{fs}, I",
                0x24 => $"SUBAq{mask} ACC, vf{fs}, Q",
                0x25 => $"MSUBAq{mask} ACC, vf{fs}, Q",
                0x26 => $"SUBAi{mask} ACC, vf{fs}, I",
                0x27 => $"MSUBAi{mask} ACC, vf{fs}, I",
                0x28 => $"ADDA{mask} ACC, vf{fs}, vf{ft}",
                0x29 => $"MADDA{mask} ACC, vf{fs}, vf{ft}",
                0x2A => $"MULA{mask} ACC, vf{fs}, vf{ft}",
                0x2C => $"SUBA{mask} ACC, vf{fs}, vf{ft}",
                0x2D => $"MSUBA{mask} ACC, vf{fs}, vf{ft}",
                0x2E => $"OPMULA.xyz ACC, vf{fs}, vf{ft}",
                0x2F => "NOP",
                _ => $"VU_UPPER_FD(0x{subOp:X2})"
            };
        }

        string bcMain = BcSuffix[op & 3u];
        return op switch
        {
            0x00 or 0x01 or 0x02 or 0x03 => $"ADD{bcMain}{mask} vf{fd}, vf{fs}, vf{ft}",
            0x04 or 0x05 or 0x06 or 0x07 => $"SUB{bcMain}{mask} vf{fd}, vf{fs}, vf{ft}",
            0x08 or 0x09 or 0x0A or 0x0B => $"MADD{bcMain}{mask} vf{fd}, vf{fs}, vf{ft}",
            0x0C or 0x0D or 0x0E or 0x0F => $"MSUB{bcMain}{mask} vf{fd}, vf{fs}, vf{ft}",
            0x10 or 0x11 or 0x12 or 0x13 => $"MAX{bcMain}{mask} vf{fd}, vf{fs}, vf{ft}",
            0x14 or 0x15 or 0x16 or 0x17 => $"MINI{bcMain}{mask} vf{fd}, vf{fs}, vf{ft}",
            0x18 or 0x19 or 0x1A or 0x1B => $"MUL{bcMain}{mask} vf{fd}, vf{fs}, vf{ft}",
            0x1C => $"MULq{mask} vf{fd}, vf{fs}, Q",
            0x1D => $"MAXi{mask} vf{fd}, vf{fs}, I",
            0x1E => $"MULi{mask} vf{fd}, vf{fs}, I",
            0x1F => $"MINIi{mask} vf{fd}, vf{fs}, I",
            0x20 => $"ADDq{mask} vf{fd}, vf{fs}, Q",
            0x21 => $"MADDq{mask} vf{fd}, vf{fs}, Q",
            0x22 => $"ADDi{mask} vf{fd}, vf{fs}, I",
            0x23 => $"MADDi{mask} vf{fd}, vf{fs}, I",
            0x24 => $"SUBq{mask} vf{fd}, vf{fs}, Q",
            0x25 => $"MSUBq{mask} vf{fd}, vf{fs}, Q",
            0x26 => $"SUBi{mask} vf{fd}, vf{fs}, I",
            0x27 => $"MSUBi{mask} vf{fd}, vf{fs}, I",
            0x28 => $"ADD{mask} vf{fd}, vf{fs}, vf{ft}",
            0x29 => $"MADD{mask} vf{fd}, vf{fs}, vf{ft}",
            0x2A => $"MUL{mask} vf{fd}, vf{fs}, vf{ft}",
            0x2B => $"MAX{mask} vf{fd}, vf{fs}, vf{ft}",
            0x2C => $"SUB{mask} vf{fd}, vf{fs}, vf{ft}",
            0x2D => $"MSUB{mask} vf{fd}, vf{fs}, vf{ft}",
            0x2E => $"OPMSUB.xyz vf{fd}, vf{fs}, vf{ft}",
            0x2F => $"MINI{mask} vf{fd}, vf{fs}, vf{ft}",
            _ => $"VU_UPPER(0x{op:X2})"
        };
    }

    public static string DecodeLower(uint lower, int bytePc)
    {
        if (lower == 0x8000033Cu || lower == 0x00000000u)
            return "NOP";

        uint top = (lower >> 25) & 0x7Fu;
        uint dest = (lower >> 21) & 0xFu;
        uint it = (lower >> 16) & 0x1Fu;
        uint isReg = (lower >> 11) & 0x1Fu;
        uint id = (lower >> 6) & 0x1Fu;
        int imm11 = SignExtend11((int)(lower & 0x7FFu));
        int branchTarget = (bytePc + 8 + imm11 * 8) & 0x3FF8;
        string mask = FormatDestMask(dest);

        return top switch
        {
            0x00 => $"LQ{mask} vf{it}, {imm11}(vi{isReg})",
            0x01 => $"SQ{mask} vf{isReg}, {imm11}(vi{it})",
            0x04 => $"ILW{mask} vi{it}, {imm11}(vi{isReg})",
            0x05 => $"ISW{mask} vi{it}, {imm11}(vi{isReg})",
            0x08 => $"IADDIU vi{it}, vi{isReg}, {(((lower >> 21) & 0xFu) << 11) | (lower & 0x7FFu)}",
            0x09 => $"ISUBIU vi{it}, vi{isReg}, {(((lower >> 21) & 0xFu) << 11) | (lower & 0x7FFu)}",
            0x10 => $"FCEQ vi01, 0x{lower & 0xFFFFFFu:X6}",
            0x11 => $"FCSET 0x{lower & 0xFFFFFFu:X6}",
            0x12 => $"FCAND vi01, 0x{lower & 0xFFFFFFu:X6}",
            0x13 => $"FCOR vi01, 0x{lower & 0xFFFFFFu:X6}",
            0x14 => $"FSSET 0x{lower & 0xFFFu:X3}",
            0x16 => $"FSAND vi{it}, 0x{lower & 0xFFFu:X3}",
            0x17 => $"FSOR vi{it}, 0x{lower & 0xFFFu:X3}",
            0x18 => $"FMEQ vi{it}, vi{isReg}",
            0x1A => $"FMAND vi{it}, vi{isReg}",
            0x1B => $"FMOR vi{it}, vi{isReg}",
            0x1C => $"FCGET vi{it}",
            0x20 => $"B 0x{branchTarget:X4}",
            0x21 => $"BAL vi{it}, 0x{branchTarget:X4}",
            0x24 => $"JR vi{isReg}",
            0x25 => $"JALR vi{it}, vi{isReg}",
            0x28 => $"IBEQ vi{it}, vi{isReg}, 0x{branchTarget:X4}",
            0x29 => $"IBNE vi{it}, vi{isReg}, 0x{branchTarget:X4}",
            0x2C => $"IBLTZ vi{isReg}, 0x{branchTarget:X4}",
            0x2D => $"IBGTZ vi{isReg}, 0x{branchTarget:X4}",
            0x2E => $"IBLEZ vi{isReg}, 0x{branchTarget:X4}",
            0x2F => $"IBGEZ vi{isReg}, 0x{branchTarget:X4}",
            0x40 => DecodeLower40(lower, mask, id, isReg, it),
            _ => $"VU_LOWER(0x{lower:X8})"
        };
    }

    private static string DecodeLower40(uint lower, string mask, uint id, uint isReg, uint it)
    {
        uint op = lower & 0x3Fu;
        if (op < 0x3C)
        {
            int imm5 = SignExtend5((int)id);
            return op switch
            {
                0x30 => $"IADD vi{id}, vi{isReg}, vi{it}",
                0x31 => $"ISUB vi{id}, vi{isReg}, vi{it}",
                0x32 => $"IADDI vi{it}, vi{isReg}, {imm5}",
                0x34 => $"IAND vi{id}, vi{isReg}, vi{it}",
                0x35 => $"IOR vi{id}, vi{isReg}, vi{it}",
                _ => $"VU_L40(0x{op:X2})"
            };
        }

        uint subOp = ((lower >> 4) & 0x7Cu) | (lower & 0x3u);
        uint fsf = ((lower >> 21) & 0x3u);
        uint ftf = ((lower >> 23) & 0x3u);
        return subOp switch
        {
            0x30 => $"MOVE{mask} vf{it}, vf{isReg}",
            0x31 => $"MR32{mask} vf{it}, vf{isReg}",
            0x34 => $"LQI{mask} vf{it}, (vi{isReg}++)",
            0x35 => $"SQI{mask} vf{isReg}, (vi{it}++)",
            0x36 => $"LQD{mask} vf{it}, (--vi{isReg})",
            0x37 => $"SQD{mask} vf{isReg}, (--vi{it})",
            0x38 => $"DIV Q, vf{isReg}{BcSuffix[fsf]}, vf{it}{BcSuffix[ftf]}",
            0x39 => $"SQRT Q, vf{it}{BcSuffix[ftf]}",
            0x3A => $"RSQRT Q, vf{isReg}{BcSuffix[fsf]}, vf{it}{BcSuffix[ftf]}",
            0x3B => "WAITQ",
            0x3C => $"MTIR vi{it}, vf{isReg}{BcSuffix[fsf]}",
            0x3D => $"MFIR{mask} vf{it}, vi{isReg}",
            0x3E => $"ILWR{mask} vi{it}, (vi{isReg})",
            0x3F => $"ISWR{mask} vi{it}, (vi{isReg})",
            0x40 => $"RINIT R, vf{isReg}{BcSuffix[fsf]}",
            0x41 => $"RGET{mask} vf{it}, R",
            0x42 => $"RNEXT{mask} vf{it}, R",
            0x43 => $"RXOR R, vf{isReg}{BcSuffix[fsf]}",
            0x64 => $"MFP{mask} vf{it}, P",
            0x68 => $"XTOP vi{it}",
            0x69 => $"XITOP vi{it}",
            0x6C => $"XGKICK vi{isReg}",
            0x70 => $"ESADD P, vf{isReg}",
            0x71 => $"ERSADD P, vf{isReg}",
            0x72 => $"ELENG P, vf{isReg}",
            0x73 => $"ERLENG P, vf{isReg}",
            0x74 => $"EATANxy P, vf{isReg}",
            0x75 => $"EATANxz P, vf{isReg}",
            0x76 => $"ESUM P, vf{isReg}",
            0x77 => $"ERCPR P, vf{isReg}{BcSuffix[fsf]}",
            0x78 => $"ESQRT P, vf{isReg}{BcSuffix[fsf]}",
            0x79 => $"ERSQRT P, vf{isReg}{BcSuffix[fsf]}",
            0x7B => $"WAITP",
            0x7C => $"ESIN P, vf{isReg}{BcSuffix[fsf]}",
            0x7D => $"EATAN P, vf{isReg}{BcSuffix[fsf]}",
            0x7E => $"EEXP P, vf{isReg}{BcSuffix[fsf]}",
            _ => $"VU_LFD(0x{subOp:X2})"
        };
    }

    public static string FormatDestMask(uint dest)
    {
        if (dest == 0xFu) return ".xyzw";
        if (dest == 0u) return "";
        return "." +
               ((dest & 8u) != 0 ? "x" : "") +
               ((dest & 4u) != 0 ? "y" : "") +
               ((dest & 2u) != 0 ? "z" : "") +
               ((dest & 1u) != 0 ? "w" : "");
    }

    public static int SignExtend11(int v) => (v & 0x400) != 0 ? (v | ~0x7FF) : (v & 0x7FF);
    public static int SignExtend5(int v) => (v & 0x10) != 0 ? (v | ~0x1F) : (v & 0x1F);
}
