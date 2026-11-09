using System.Text;
using Castaway.ElfTools.Elf;

namespace Castaway.ElfTools.Commands;

public static class ScanTablesCommand
{
    private static readonly Dictionary<uint, string> SyscallNames = new()
    {
        { 0x00, "RFU000_FullReset" },
        { 0x01, "ResetEE" },
        { 0x02, "SetGsCrt" },
        { 0x04, "Exit" },
        { 0x05, "_LoadExecPS2" },
        { 0x06, "ExecPS2" },
        { 0x07, "SetVDCallback" },
        { 0x10, "AddIntcHandler" },
        { 0x11, "RemoveIntcHandler" },
        { 0x12, "AddDmacHandler" },
        { 0x13, "RemoveDmacHandler" },
        { 0x14, "EnableIntc" },
        { 0x15, "DisableIntc" },
        { 0x16, "EnableDmac" },
        { 0x17, "DisableDmac" },
        { 0x18, "SetAlarm" },
        { 0x19, "ReleaseAlarm" },
        { 0x1A, "_iEnableIntc" },
        { 0x1B, "_iDisableIntc" },
        { 0x1C, "_iEnableDmac" },
        { 0x1D, "_iDisableDmac" },
        { 0x1E, "_iSetAlarm" },
        { 0x1F, "_iReleaseAlarm" },
        { 0xFFFFFFE6, "iEnableIntc" },
        { 0xFFFFFFE5, "iDisableIntc" },
        { 0xFFFFFFE4, "iEnableDmac" },
        { 0xFFFFFFE3, "iDisableDmac" },
        { 0xFFFFFFE2, "iSetAlarm" },
        { 0xFFFFFFE1, "iReleaseAlarm" },
        { 0x20, "CreateThread" },
        { 0x21, "DeleteThread" },
        { 0x22, "StartThread" },
        { 0x23, "ExitThread" },
        { 0x24, "ExitDeleteThread" },
        { 0x25, "TerminateThread" },
        { 0x26, "iTerminateThread" },
        { 0xFFFFFFDA, "iTerminateThread" },
        { 0x29, "ChangeThreadPriority" },
        { 0x2A, "iChangeThreadPriority" },
        { 0xFFFFFFD6, "iChangeThreadPriority" },
        { 0x2B, "RotateThreadReadyQueue" },
        { 0x2C, "_iRotateThreadReadyQueue" },
        { 0xFFFFFFD4, "iRotateThreadReadyQueue" },
        { 0x2D, "ReleaseWaitThread" },
        { 0x2E, "iReleaseWaitThread" },
        { 0xFFFFFFD2, "iReleaseWaitThread" },
        { 0x2F, "GetThreadId" },
        { 0xFFFFFFD1, "iGetThreadId" },
        { 0x30, "ReferThreadStatus" },
        { 0x31, "iReferThreadStatus" },
        { 0xFFFFFFCF, "iReferThreadStatus" },
        { 0x32, "SleepThread" },
        { 0x33, "WakeupThread" },
        { 0x34, "iWakeupThread" },
        { 0xFFFFFFCC, "iWakeupThread" },
        { 0x35, "CancelWakeupThread" },
        { 0x36, "iCancelWakeupThread" },
        { 0xFFFFFFCA, "iCancelWakeupThread" },
        { 0x37, "SuspendThread" },
        { 0x38, "iSuspendThread" },
        { 0xFFFFFFC8, "iSuspendThread" },
        { 0x39, "ResumeThread" },
        { 0x3A, "iResumeThread" },
        { 0xFFFFFFC6, "iResumeThread" },
        { 0x3C, "InitMainThread" },
        { 0x3D, "InitializeHeap" },
        { 0x3E, "EndOfHeap" },
        { 0x40, "CreateSema" },
        { 0x41, "DeleteSema" },
        { 0x42, "SignalSema" },
        { 0x43, "iSignalSema" },
        { 0xFFFFFFBD, "iSignalSema" },
        { 0x44, "WaitSema" },
        { 0x45, "PollSema" },
        { 0x46, "iPollSema" },
        { 0xFFFFFFBA, "iPollSema" },
        { 0x47, "ReferSemaStatus" },
        { 0x48, "iReferSemaStatus" },
        { 0xFFFFFFB8, "iReferSemaStatus" },
        { 0x4A, "SetOsdConfigParam" },
        { 0x4B, "GetOsdConfigParam" },
        { 0x50, "CreateEventFlag" },
        { 0x51, "DeleteEventFlag" },
        { 0x52, "SetEventFlag" },
        { 0x53, "iSetEventFlag" },
        { 0x54, "ClearEventFlag" },
        { 0x55, "iClearEventFlag" },
        { 0x56, "WaitEventFlag" },
        { 0x57, "PollEventFlag" },
        { 0x58, "iPollEventFlag" },
        { 0x59, "ReferEventFlagStatus" },
        { 0x5A, "Copy" },
        { 0x5B, "GetEntryAddress" },
        { 0x5C, "EnableIntcHandler" },
        { 0xFFFFFFA4, "iEnableIntcHandler" },
        { 0x5D, "DisableIntcHandler" },
        { 0xFFFFFFA3, "iDisableIntcHandler" },
        { 0x5E, "EnableDmacHandler" },
        { 0xFFFFFFA2, "iEnableDmacHandler" },
        { 0x5F, "DisableDmacHandler" },
        { 0xFFFFFFA1, "iDisableDmacHandler" },
        { 0x64, "FlushCache" },
        { 0x68, "iFlushCache" },
        { 0xFFFFFF98, "iFlushCache" },
        { 0x6E, "SetOsdConfigParam2" },
        { 0x6F, "GetOsdConfigParam2" },
        { 0x70, "GsGetIMR" },
        { 0xFFFFFF90, "iGsGetIMR" },
        { 0x71, "GsPutIMR" },
        { 0xFFFFFF8F, "iGsPutIMR" },
        { 0x73, "SetVSyncFlag" },
        { 0x74, "SetSyscall" },
        { 0x76, "sceSifDmaStat" },
        { 0xFFFFFF8A, "isceSifDmaStat" },
        { 0x77, "sceSifSetDma" },
        { 0xFFFFFF89, "isceSifSetDma" },
        { 0x78, "sceSifSetDChain" },
        { 0xFFFFFF88, "isceSifSetDChain" },
        { 0x79, "sceSifSetReg" },
        { 0x7A, "sceSifGetReg" },
        { 0x7C, "Deci2Call" },
        { 0x7E, "MachineType" },
        { 0x7F, "GetMemorySize" },
        { 0x80, "QueryBootMode" },
        { 0x82, "GetThreadTLS" },
        { 0x83, "FindAddress" },
        { 0xFC, "SetAlarm" },
        { 0xFD, "ReleaseAlarm" },
        { 0xFE, "_iSetAlarm" },
        { 0xFF, "_iReleaseAlarm" },
        { 0xFFFFFF02, "iSetAlarm" },
        { 0xFFFFFF01, "iReleaseAlarm" }
    };

    public class JumpTableInfo
    {
        public uint JrVAddr { get; set; }
        public uint TableBaseVAddr { get; set; }
        public int EntryCount { get; set; }
        public List<uint> Targets { get; set; } = new();
    }

    public class FunctionMeta
    {
        public uint Start { get; set; }
        public uint End { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsLeaf { get; set; } = true;
        public bool UsesFpu { get; set; }
        public bool UsesVu0 { get; set; }
        public bool UsesMmi { get; set; }
        public bool HasMmio { get; set; }
        public bool UsesScratchpad { get; set; }
        public uint? SyscallId { get; set; }
        public List<uint> JumpTables { get; set; } = new();
    }

    public static int Run(string[] args)
    {
        string? outFile = null;
        bool reportSyscalls = false;
        string? elfPath = null;
        int maxLines = 90;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "--out" || arg == "-o") && i + 1 < args.Length)
            {
                outFile = args[++i];
            }
            else if (arg == "--report-syscalls")
            {
                reportSyscalls = true;
            }
            else if (arg == "--elf" && i + 1 < args.Length)
            {
                elfPath = args[++i];
            }
            else if (arg == "--max-lines" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out maxLines);
            }
        }

        string resolvedElf = Ps2ElfReader.FindDefaultElfPath(elfPath);
        var reader = new Ps2ElfReader(resolvedElf);

        var textSection = reader.TextSection;
        if (textSection == null)
        {
            Console.WriteLine("Error: ELF has no .text section.");
            return 1;
        }

        uint startVAddr = textSection.VAddr;
        uint endVAddr = textSection.VAddr + textSection.Size;

        SortedSet<uint> functionStarts = new();
        HashSet<uint> jalTargets = new();
        functionStarts.Add(reader.EntryPoint);
        functionStarts.Add(startVAddr);
        jalTargets.Add(reader.EntryPoint);
        jalTargets.Add(startVAddr);

        Dictionary<uint, List<uint>> syscallCallers = new();
        SortedDictionary<uint, uint> mmioByInst = new();
        SortedDictionary<uint, int> mmioSummary = new();
        List<JumpTableInfo> jumpTables = new();
        HashSet<uint> jumpTableTargets = new();

        // Pass 1: Discover JAL targets, syscalls, jump tables, MMIO, and prologue idioms
        uint[] luiRegs = new uint[32];
        bool[] luiValid = new bool[32];
        HashSet<uint> addressConstants = new();
        List<uint> jTargets = new();
        bool clearLuiAfterDelaySlot = false;

        for (uint v = startVAddr; v < endVAddr; v += 4)
        {
            uint word = reader.ReadUInt32(v);
            var insn = R5900Decoder.Decode(word, v);

            // JAL targets -> Function Entry
            if (insn.Op == R5900Op.Jal)
            {
                if (reader.IsInText(insn.JumpTarget))
                {
                    functionStarts.Add(insn.JumpTarget);
                    jalTargets.Add(insn.JumpTarget);
                }
            }
            else if (insn.Op == R5900Op.J)
            {
                if (reader.IsInText(insn.JumpTarget))
                {
                    jTargets.Add(insn.JumpTarget);
                }
            }

            // Standard PS2 Prologue: ADDIU $sp, $sp, -imm
            if (insn.Op == R5900Op.Addiu && insn.Rs == 29 && insn.Rt == 29 && insn.Simm < 0)
            {
                uint realStart = ResolvePrologueStart(reader, v, startVAddr);
                functionStarts.Add(realStart);
            }

            // SYSCALL detection: opcode 0x0000000C
            if (insn.Op == R5900Op.Syscall)
            {
                uint sysId = 0;
                for (uint back = 4; back <= 16 && v >= startVAddr + back; back += 4)
                {
                    uint prevWord = reader.ReadUInt32(v - back);
                    var prevInsn = R5900Decoder.Decode(prevWord, v - back);
                    if (prevInsn.Rt == 3) // $v1
                    {
                        if (prevInsn.Op == R5900Op.Addiu && prevInsn.Rs == 0)
                        {
                            sysId = (uint)prevInsn.Simm;
                            break;
                        }
                        if (prevInsn.Op == R5900Op.Ori && prevInsn.Rs == 0)
                        {
                            sysId = prevInsn.Uimm;
                            break;
                        }
                    }
                }
                if (!syscallCallers.TryGetValue(sysId, out var callers))
                {
                    callers = new List<uint>();
                    syscallCallers[sysId] = callers;
                }
                callers.Add(v);
            }

            // Jump table detection: JR $reg where $reg != $ra
            if (insn.Op == R5900Op.Jr && insn.Rs != 31 && insn.Rs != 0)
            {
                var jt = TryRecoverJumpTable(reader, v, insn.Rs, startVAddr);
                if (jt != null)
                {
                    jumpTables.Add(jt);
                    foreach (var target in jt.Targets)
                    {
                        jumpTableTargets.Add(target);
                    }
                }
            }

            // MMIO accesses & 32-bit address constants via LUI
            if (insn.Op == R5900Op.Lui)
            {
                if (insn.Rt != 0)
                {
                    luiRegs[insn.Rt] = (uint)insn.Uimm << 16;
                    luiValid[insn.Rt] = true;
                }
            }
            else if ((insn.Op == R5900Op.Daddu || insn.Op == R5900Op.Addu || insn.Op == R5900Op.Or ||
                      insn.Op == R5900Op.Dadd || insn.Op == R5900Op.Add) &&
                     insn.Rd != 0 && (insn.Rt == 0 || insn.Rs == 0))
            {
                uint src = insn.Rt == 0 ? insn.Rs : insn.Rt;
                if (src != 0 && luiValid[src])
                {
                    luiRegs[insn.Rd] = luiRegs[src];
                    luiValid[insn.Rd] = true;
                }
                else
                {
                    luiValid[insn.Rd] = false;
                }
            }
            else if ((insn.Op == R5900Op.Addiu || insn.Op == R5900Op.Ori || insn.Op == R5900Op.Daddiu || insn.IsLoad || insn.IsStore) && luiValid[insn.Rs])
            {
                uint eff = insn.Op == R5900Op.Ori ? (luiRegs[insn.Rs] | insn.Uimm) : (uint)(luiRegs[insn.Rs] + insn.Simm);
                if (eff >= 0x10000000 && eff < 0x12002000)
                {
                    mmioByInst[v] = eff;
                    mmioSummary[eff] = mmioSummary.GetValueOrDefault(eff, 0) + 1;
                }
                if ((insn.Op == R5900Op.Addiu || insn.Op == R5900Op.Ori || insn.Op == R5900Op.Daddiu) && reader.IsInText(eff) && (eff & 3) == 0)
                {
                    addressConstants.Add(eff);
                }
                if ((insn.Op == R5900Op.Addiu || insn.Op == R5900Op.Ori || insn.Op == R5900Op.Daddiu) && insn.Rt != 0)
                {
                    luiRegs[insn.Rt] = eff;
                    luiValid[insn.Rt] = true;
                }
                else
                {
                    uint written = insn.WrittenGpr;
                    if (written != 0 && luiValid[written])
                    {
                        luiValid[written] = false;
                    }
                }
            }
            else
            {
                uint written = insn.WrittenGpr;
                if (written != 0 && luiValid[written])
                {
                    luiValid[written] = false;
                }
            }

            if (clearLuiAfterDelaySlot)
            {
                Array.Clear(luiValid, 0, 32);
                clearLuiAfterDelaySlot = false;
            }
            else if (insn.IsReturn)
            {
                clearLuiAfterDelaySlot = true;
            }
        }

        // Pass 2: Function pointers in all non-text data sections (.rodata, .data, .ctors, .dtors, .sdata, etc.)
        foreach (var sec in reader.Sections.Where(s => !s.IsExecutable && !s.IsNoBits && s.Size >= 4 && s.VAddr != 0))
        {
            for (uint v = sec.VAddr; v + 4 <= sec.VAddr + sec.Size; v += 4)
            {
                if (!reader.IsVAddrValid(v)) break;
                uint val = reader.ReadUInt32(v);
                if (reader.IsInText(val) && (val & 3) == 0 && !jumpTableTargets.Contains(val))
                {
                    if (IsValidFunctionCandidate(reader, val, startVAddr) || ReplaceShiftedPrologue(reader, functionStarts, jalTargets, val))
                    {
                        functionStarts.Add(val);
                    }
                }
            }
        }

        // Also promote LUI+ADDIU/ORI address constants and tail-jump targets that sit on function boundaries
        foreach (uint cand in addressConstants)
        {
            if (!jumpTableTargets.Contains(cand) &&
                (IsValidFunctionCandidate(reader, cand, startVAddr) || ReplaceShiftedPrologue(reader, functionStarts, jalTargets, cand)))
            {
                functionStarts.Add(cand);
            }
        }
        foreach (uint jTarget in jTargets)
        {
            if (!jumpTableTargets.Contains(jTarget) &&
                (IsPrecededByFunctionEnd(reader, jTarget, startVAddr) || ReplaceShiftedPrologue(reader, functionStarts, jalTargets, jTarget)))
            {
                functionStarts.Add(jTarget);
            }
        }

        // Pass 3: Syscall stubs around 0x00482140..0x004828C0
        for (uint stub = 0x00482150; stub <= 0x004828C0; stub += 16)
        {
            if (reader.IsInText(stub))
            {
                uint w = reader.ReadUInt32(stub);
                var insn = R5900Decoder.Decode(w, stub);
                if (insn.Op == R5900Op.Addiu && insn.Rs == 0 && insn.Rt == 3)
                {
                    functionStarts.Add(stub);
                    jalTargets.Add(stub);
                }
            }
        }

        var jumpTableMap = jumpTables.GroupBy(j => j.JrVAddr).ToDictionary(g => g.Key, g => g.First());

        // Pass 3.5: Remove false-positive splits that lie inside intra-function branch or jump-table spans
        for (uint v = startVAddr; v < endVAddr; v += 4)
        {
            uint word = reader.ReadUInt32(v);
            var insn = R5900Decoder.Decode(word, v);
            if (insn.IsBranch && insn.Op is not (R5900Op.Bltzal or R5900Op.Bgezal or R5900Op.Bltzall or R5900Op.Bgezall))
            {
                uint target = insn.BranchTarget;
                if (reader.IsInText(target) && Math.Abs((long)target - (long)v) < 0x10000)
                {
                    uint lo = Math.Min(v, target) + 4u;
                    uint hi = Math.Max(v, target);
                    if (lo <= hi)
                    {
                        var inside = functionStarts.GetViewBetween(lo, hi).Where(s => !jalTargets.Contains(s)).ToList();
                        foreach (uint s in inside)
                        {
                            functionStarts.Remove(s);
                        }
                    }
                }
            }
            else if (jumpTableMap.TryGetValue(v, out var jt))
            {
                foreach (uint target in jt.Targets)
                {
                    uint lo = Math.Min(v, target) + 4u;
                    uint hi = Math.Max(v, target);
                    if (lo <= hi)
                    {
                        var inside = functionStarts.GetViewBetween(lo, hi).Where(s => !jalTargets.Contains(s)).ToList();
                        foreach (uint s in inside)
                        {
                            functionStarts.Remove(s);
                        }
                    }
                }
            }
        }

        // Ensure every cross-function J/B* target is registered in functionStarts
        var prelimStarts = functionStarts.Where(s => reader.IsInText(s)).OrderBy(s => s).ToList();
        for (int i = 0; i < prelimStarts.Count; i++)
        {
            uint start = prelimStarts[i];
            var sec = reader.FindSectionContaining(start);
            uint secEnd = sec != null ? (sec.VAddr + sec.Size) : endVAddr;
            uint end = (i + 1 < prelimStarts.Count && prelimStarts[i + 1] <= secEnd) ? prelimStarts[i + 1] : secEnd;

            for (uint v = start; v < end; v += 4)
            {
                if (!reader.IsVAddrValid(v)) break;
                var insn = R5900Decoder.Decode(reader.ReadUInt32(v), v);
                if (insn.Op == R5900Op.J)
                {
                    uint target = insn.JumpTarget;
                    if (reader.IsInText(target) && (target < start || target >= end))
                    {
                        ReplaceShiftedPrologue(reader, functionStarts, jalTargets, target);
                        functionStarts.Add(target);
                    }
                }
                else if (insn.IsBranch)
                {
                    uint target = insn.BranchTarget;
                    if (reader.IsInText(target) && (target < start || target >= end))
                    {
                        ReplaceShiftedPrologue(reader, functionStarts, jalTargets, target);
                        functionStarts.Add(target);
                    }
                }
            }
        }

        // Pass 4: Build Function Metadata and analyze instruction characteristics
        var sortedStarts = functionStarts.Where(s => reader.IsInText(s)).OrderBy(s => s).ToList();
        List<FunctionMeta> functions = new();

        for (int i = 0; i < sortedStarts.Count; i++)
        {
            uint start = sortedStarts[i];
            var sec = reader.FindSectionContaining(start);
            uint secEnd = sec != null ? (sec.VAddr + sec.Size) : endVAddr;
            uint end = (i + 1 < sortedStarts.Count && sortedStarts[i + 1] <= secEnd) ? sortedStarts[i + 1] : secEnd;

            var meta = new FunctionMeta
            {
                Start = start,
                End = end,
                Name = GetInitialFunctionName(start)
            };

            // Analyze function body
            for (uint v = start; v < end; v += 4)
            {
                if (!reader.IsVAddrValid(v)) break;
                uint word = reader.ReadUInt32(v);
                var insn = R5900Decoder.Decode(word, v);

                if (insn.IsCall)
                {
                    meta.IsLeaf = false;
                }

                if (IsFpuInstruction(insn.Op))
                {
                    meta.UsesFpu = true;
                }

                if (IsVu0Instruction(insn.Op))
                {
                    meta.UsesVu0 = true;
                }

                if (IsMmiInstruction(insn.Op))
                {
                    meta.UsesMmi = true;
                }

                if (mmioByInst.ContainsKey(v))
                {
                    meta.HasMmio = true;
                }

                // Check scratchpad RAM: 0x70000000..0x70003FFF
                if (insn.Op == R5900Op.Lui && insn.Uimm == 0x7000)
                {
                    meta.UsesScratchpad = true;
                }

                if (insn.Op == R5900Op.Syscall)
                {
                    // Check previous instruction for syscall ID
                    if (v >= start + 4 && reader.IsVAddrValid(v - 4))
                    {
                        var prev = R5900Decoder.Decode(reader.ReadUInt32(v - 4), v - 4);
                        if (prev.Rt == 3 && prev.Op == R5900Op.Addiu && prev.Rs == 0)
                        {
                            meta.SyscallId = (uint)prev.Simm;
                            if (SyscallNames.TryGetValue(meta.SyscallId.Value, out var sysName))
                            {
                                meta.Name = $"sce_{sysName}";
                            }
                        }
                    }
                }

                if (jumpTableMap.TryGetValue(v, out var jt))
                {
                    meta.JumpTables.Add(jt.TableBaseVAddr);
                }
            }

            functions.Add(meta);
        }

        // Summary Report
        List<string> lines = new();
        lines.Add($"=== PS2 ELF Analysis: {Path.GetFileName(resolvedElf)} ===");
        lines.Add($"Discovered Functions: {functions.Count}");
        lines.Add($"Discovered Jump Tables: {jumpTables.Count}");
        lines.Add($"Distinct Syscalls: {syscallCallers.Count}");
        lines.Add($"Distinct MMIO Access Points: {mmioByInst.Count}");
        lines.Add($"Leaf Functions: {functions.Count(f => f.IsLeaf)}");
        lines.Add($"FPU Functions: {functions.Count(f => f.UsesFpu)}");
        lines.Add($"VU0 Macro Functions: {functions.Count(f => f.UsesVu0)}");
        lines.Add($"128-bit MMI Functions: {functions.Count(f => f.UsesMmi)}");

        if (reportSyscalls || outFile == null)
        {
            lines.Add("");
            lines.Add("--- EE Kernel Syscalls Invoked ---");
            foreach (var kvp in syscallCallers.OrderBy(k => k.Key))
            {
                string name = SyscallNames.TryGetValue(kvp.Key, out var n) ? n : "UnknownSyscall";
                lines.Add($"  Syscall 0x{kvp.Key:X2} ({name,-24}) : {kvp.Value.Count} callsites (e.g. 0x{kvp.Value[0]:X8})");
            }

            lines.Add("");
            lines.Add("--- Top MMIO / Hardware Registers Accessed ---");
            foreach (var kvp in mmioSummary.OrderByDescending(k => k.Value).Take(15))
            {
                string desc = GetMmioDesc(kvp.Key);
                lines.Add($"  0x{kvp.Key:X8} ({desc,-20}) : {kvp.Value} access(es)");
            }

            if (jumpTables.Count > 0)
            {
                lines.Add("");
                lines.Add("--- Discovered Jump Tables (Sample) ---");
                foreach (var jt in jumpTables.Take(10))
                {
                    lines.Add($"  JR @ 0x{jt.JrVAddr:X8} -> TableBase 0x{jt.TableBaseVAddr:X8} (Entries: {jt.EntryCount})");
                }
            }
        }

        if (!string.IsNullOrEmpty(outFile))
        {
            Console.WriteLine($"Writing TOML manifest to {outFile}...");
            WriteToml(outFile, functions, jumpTables, syscallCallers, mmioByInst, reader);
            lines.Add($"Successfully generated {outFile}.");
        }

        foreach (var l in lines.Take(maxLines))
        {
            Console.WriteLine(l);
        }

        return 0;
    }

    private static bool ReplaceShiftedPrologue(Ps2ElfReader reader, SortedSet<uint> functionStarts, HashSet<uint> jalTargets, uint cand)
    {
        if (!reader.IsInText(cand) || (cand & 3u) != 0) return false;
        for (uint shift = 4; shift <= 8; shift += 4)
        {
            uint next = cand + shift;
            if (functionStarts.Contains(next) && !jalTargets.Contains(next) && reader.IsInText(next))
            {
                var nextInsn = R5900Decoder.Decode(reader.ReadUInt32(next), next);
                if (nextInsn.Op == R5900Op.Addiu && nextInsn.Rs == 29 && nextInsn.Rt == 29 && nextInsn.Simm < 0)
                {
                    functionStarts.Remove(next);
                    return true;
                }
            }
        }
        return false;
    }

    private static uint ResolvePrologueStart(Ps2ElfReader reader, uint spAddiuVAddr, uint textStart)
    {
        // Sometimes the compiler schedules 1 or 2 instructions (e.g. lui, move, li) before addiu $sp, $sp, -imm
        // right after the previous function's terminator (plus optional alignment NOPs).
        for (uint back = 4; back <= 8 && spAddiuVAddr >= textStart + back; back += 4)
        {
            uint cand = spAddiuVAddr - back;
            uint w = reader.ReadUInt32(cand);
            if (w == 0) continue; // NOP padding is not the start of a function
            var insn = R5900Decoder.Decode(w, cand);
            if (insn.IsBranch || insn.IsJump || insn.IsCall || insn.IsReturn ||
                insn.Op == R5900Op.Syscall || insn.Op == R5900Op.Break || insn.Op == R5900Op.Eret)
            {
                break;
            }
            if (IsPrecededByFunctionEnd(reader, cand, textStart))
            {
                return cand;
            }
        }
        return spAddiuVAddr;
    }

    private static bool IsValidFunctionCandidate(Ps2ElfReader reader, uint vaddr, uint textStart)
    {
        if (!reader.IsInText(vaddr) || (vaddr & 3u) != 0) return false;
        uint firstWord = reader.ReadUInt32(vaddr);
        if (firstWord == 0) return false; // Skip pure NOP padding words

        var firstInsn = R5900Decoder.Decode(firstWord, vaddr);
        if (firstInsn.Op == R5900Op.Invalid) return false;

        // Standard prologue idioms
        if ((firstInsn.Op == R5900Op.Addiu && firstInsn.Rs == 29 && firstInsn.Rt == 29 && firstInsn.Simm < 0) ||
            (firstInsn.Op == R5900Op.Sd && firstInsn.Rs == 29) ||
            (firstInsn.Op == R5900Op.Sq && firstInsn.Rs == 29) ||
            (firstInsn.Op == R5900Op.Sw && firstInsn.Rs == 29))
        {
            return true;
        }

        // Or preceded by an unconditional function terminator (allowing leaf vtable stubs like jr $ra / lw / li / etc.)
        return IsPrecededByFunctionEnd(reader, vaddr, textStart);
    }

    private static bool IsPrecededByFunctionEnd(Ps2ElfReader reader, uint vaddr, uint textStart)
    {
        if (vaddr <= textStart) return true;
        uint prev = vaddr - 4;

        // Skip alignment NOPs (up to 16 instructions) between the previous function's end and this candidate
        int skippedNops = 0;
        while (prev > textStart && reader.ReadUInt32(prev) == 0 && skippedNops < 16)
        {
            prev -= 4;
            skippedNops++;
        }

        if (prev < textStart) return true;

        // If we skipped at least one NOP and the instruction at 'prev' is the delay slot of a return/jump/unconditional-branch,
        // or if we didn't skip any NOPs and 'prev' is itself the delay slot of 'prev - 4':
        if (prev >= textStart + 4)
        {
            uint ctrlWord = reader.ReadUInt32(prev - 4);
            var ctrlInsn = R5900Decoder.Decode(ctrlWord, prev - 4);
            if (IsUnconditionalTerminatorWithDelaySlot(ctrlInsn, vaddr))
            {
                return true;
            }
        }

        // What if the last instruction of the previous function had a NOP in its delay slot, which we skipped above?
        // In that case, 'prev' is the control instruction itself (if skippedNops >= 1)!
        if (skippedNops >= 1)
        {
            uint ctrlWord = reader.ReadUInt32(prev);
            var ctrlInsn = R5900Decoder.Decode(ctrlWord, prev);
            if (IsUnconditionalTerminatorWithDelaySlot(ctrlInsn, vaddr))
            {
                return true;
            }
        }

        // Also check single-instruction terminators without delay slot (BREAK, ERET, SYSCALL)
        uint directWord = reader.ReadUInt32(prev);
        var directInsn = R5900Decoder.Decode(directWord, prev);
        if (directInsn.Op == R5900Op.Break || directInsn.Op == R5900Op.Eret)
        {
            return true;
        }

        // Also handle 8-byte [leftover_insn, NOP] linker alignment pairs after jr $ra + delay_slot
        if ((vaddr & 7u) == 0 && reader.IsInText(vaddr + 4))
        {
            uint firstWord = reader.ReadUInt32(vaddr);
            var firstInsn = R5900Decoder.Decode(firstWord, vaddr);
            bool isPaddingPairItself = reader.ReadUInt32(vaddr + 4) == 0 && !(firstInsn.Op == R5900Op.Jr && firstInsn.Rs == 31);
            if (!isPaddingPairItself)
            {
                uint p = vaddr;
                for (int pair = 0; pair < 8 && p >= textStart + 16; pair++)
                {
                    if (reader.ReadUInt32(p - 4) != 0)
                        break;
                    p -= 8;
                    uint q = p;
                    int pairNops = 0;
                    while (q > textStart && reader.ReadUInt32(q - 4) == 0 && pairNops < 16)
                    {
                        q -= 4;
                        pairNops++;
                    }
                    if (q >= textStart + 8)
                    {
                        uint ctrlWord = reader.ReadUInt32(q - 8);
                        var ctrlInsn = R5900Decoder.Decode(ctrlWord, q - 8);
                        if (IsUnconditionalTerminatorWithDelaySlot(ctrlInsn, q))
                        {
                            return true;
                        }
                    }
                    if (pairNops >= 1 && q >= textStart + 4)
                    {
                        uint ctrlWord = reader.ReadUInt32(q - 4);
                        var ctrlInsn = R5900Decoder.Decode(ctrlWord, q - 4);
                        if (IsUnconditionalTerminatorWithDelaySlot(ctrlInsn, q))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool IsUnconditionalTerminatorWithDelaySlot(R5900Insn insn, uint candidateVAddr)
    {
        if (insn.Op == R5900Op.Jr && insn.Rs == 31)
            return true;
        if (insn.Op == R5900Op.J)
            return true;
        if (insn.Op == R5900Op.Beq && insn.Rs == insn.Rt && insn.BranchTarget < candidateVAddr)
            return true;
        if ((insn.Op == R5900Op.Bgez || insn.Op == R5900Op.Blez) && insn.Rs == 0 && insn.BranchTarget < candidateVAddr)
            return true;
        return false;
    }

    private static string GetInitialFunctionName(uint addr) => addr switch
    {
        0x00100000 => "crt0_entry",
        0x00100008 => "_start",
        0x003E4610 => "GameMain_AppEntry",
        0x00418C00 => "IopReboot_LoadModules",
        _ => $"sub_{addr:X8}"
    };

    private static bool IsFpuInstruction(R5900Op op) => op switch
    {
        R5900Op.AddS or R5900Op.SubS or R5900Op.MulS or R5900Op.DivS or
        R5900Op.SqrtS or R5900Op.AbsS or R5900Op.MovS or R5900Op.NegS or
        R5900Op.RsqrtS or R5900Op.AddaS or R5900Op.SubaS or R5900Op.MulaS or
        R5900Op.MaddS or R5900Op.MsubS or R5900Op.MaddaS or R5900Op.MsubaS or
        R5900Op.CvtWS or R5900Op.CvtSW or R5900Op.MaxS or R5900Op.MinS or
        R5900Op.CF or R5900Op.CEq or R5900Op.CLt or R5900Op.CLe or
        R5900Op.Lwc1 or R5900Op.Swc1 or R5900Op.Mfc1 or R5900Op.Mtc1 or
        R5900Op.Cfc1 or R5900Op.Ctc1 or R5900Op.Bc1f or R5900Op.Bc1t => true,
        _ => false
    };

    private static bool IsVu0Instruction(R5900Op op) => op switch
    {
        R5900Op.Qmfc2 or R5900Op.Qmtc2 or R5900Op.Cfc2 or R5900Op.Ctc2 or
        R5900Op.Lqc2 or R5900Op.Sqc2 or R5900Op.Vadd or R5900Op.Vsub or
        R5900Op.Vmul or R5900Op.Vmax or R5900Op.Vmini or R5900Op.Vclip or
        R5900Op.Vmove or R5900Op.Vcallms or R5900Op.Vcallmsr => true,
        _ => false
    };

    private static bool IsMmiInstruction(R5900Op op) => op switch
    {
        R5900Op.Lq or R5900Op.Sq or R5900Op.Madd or R5900Op.Maddu or
        R5900Op.Plzcw or R5900Op.Pmfhl or R5900Op.Pmthl or R5900Op.Psllh or
        R5900Op.Psllw or R5900Op.Paddw or R5900Op.Psubw or R5900Op.Paddsw or
        R5900Op.Psubsw or R5900Op.Pextlw or R5900Op.Ppacw or R5900Op.Pabsw or
        R5900Op.Pceqw or R5900Op.Pminw or R5900Op.Padduw or R5900Op.Psubuw or
        R5900Op.Pmaddw or R5900Op.Pmsubw or R5900Op.Pmultw or R5900Op.Pdivw or
        R5900Op.Pand or R5900Op.Pxor or R5900Op.Por or R5900Op.Pnor => true,
        _ => false
    };

    private static JumpTableInfo? TryRecoverJumpTable(Ps2ElfReader reader, uint jrVAddr, uint jrReg, uint textStart)
    {
        uint windowStart = (jrVAddr > textStart + 8192) ? (jrVAddr - 8192) : textStart;
        // Narrow windowStart to the nearest preceding function prologue (addiu $sp, $sp, -imm)
        for (uint back = jrVAddr - 4; back >= windowStart; back -= 4)
        {
            var bInsn = R5900Decoder.Decode(reader.ReadUInt32(back), back);
            if (bInsn.Op == R5900Op.Addiu && bInsn.Rs == 29 && bInsn.Rt == 29 && bInsn.Simm < 0)
            {
                windowStart = ResolvePrologueStart(reader, back, textStart);
                break;
            }
            if (back == textStart) break;
        }

        uint[] regVal = new uint[32];
        bool[] regValid = new bool[32];
        int[] regLimit = new int[32];
        int[] idxLimit = new int[32];

        int lastBranchLimit = -1;
        int lastSltLimit = -1;
        uint sllReg = 0;
        uint sllSrcReg = 0;
        uint tableBase = 0;
        uint ptrReg = 0;

        for (uint v = windowStart; v < jrVAddr; v += 4)
        {
            uint word = reader.ReadUInt32(v);
            var insn = R5900Decoder.Decode(word, v);

            if (insn.Op == R5900Op.Sltiu || insn.Op == R5900Op.Slti)
            {
                lastSltLimit = insn.Simm;
                if (insn.Rt != 0) regLimit[insn.Rt] = insn.Simm;
                if (insn.Rs != 0) idxLimit[insn.Rs] = insn.Simm;
            }
            else if (insn.Op == R5900Op.Andi && insn.Rt != 0 && insn.Uimm > 0 && insn.Uimm <= 255 && ((insn.Uimm + 1) & insn.Uimm) == 0)
            {
                idxLimit[insn.Rt] = insn.Uimm + 1;
            }
            else if (insn.IsBranch)
            {
                if (insn.Rs != 0 && regLimit[insn.Rs] > 0)
                    lastBranchLimit = regLimit[insn.Rs];
                else if (insn.Rt != 0 && regLimit[insn.Rt] > 0)
                    lastBranchLimit = regLimit[insn.Rt];
            }
            else if (insn.Op == R5900Op.Jr)
            {
                sllReg = 0;
                sllSrcReg = 0;
                tableBase = 0;
                ptrReg = 0;
                lastBranchLimit = -1;
                lastSltLimit = -1;
            }
            else if (insn.Op == R5900Op.Sll && insn.Sa == 2)
            {
                sllReg = insn.Rd;
                sllSrcReg = insn.Rt;
            }
            else if (insn.Op == R5900Op.Lui)
            {
                regVal[insn.Rt] = (uint)insn.Uimm << 16;
                regValid[insn.Rt] = true;
                regLimit[insn.Rt] = 0;
            }
            else if ((insn.Op == R5900Op.Addiu || insn.Op == R5900Op.Addi) && regValid[insn.Rs])
            {
                regVal[insn.Rt] = (uint)(regVal[insn.Rs] + insn.Simm);
                regValid[insn.Rt] = true;
                regLimit[insn.Rt] = 0;
            }
            else if (insn.Op == R5900Op.Ori && regValid[insn.Rs])
            {
                regVal[insn.Rt] = regVal[insn.Rs] | insn.Uimm;
                regValid[insn.Rt] = true;
                regLimit[insn.Rt] = 0;
            }
            else if (insn.Op == R5900Op.Addu || insn.Op == R5900Op.Daddu || insn.Op == R5900Op.Or)
            {
                if (insn.Rt == 0 || insn.Rs == 0)
                {
                    uint src = insn.Rt == 0 ? insn.Rs : insn.Rt;
                    if (insn.Rd != 0)
                    {
                        regVal[insn.Rd] = regVal[src];
                        regValid[insn.Rd] = regValid[src];
                        regLimit[insn.Rd] = regLimit[src];
                        idxLimit[insn.Rd] = idxLimit[src];
                    }
                }
                else if (sllReg != 0)
                {
                    if (insn.Rs == sllReg && regValid[insn.Rt])
                    {
                        tableBase = regVal[insn.Rt];
                        ptrReg = insn.Rd;
                    }
                    else if (insn.Rt == sllReg && regValid[insn.Rs])
                    {
                        tableBase = regVal[insn.Rs];
                        ptrReg = insn.Rd;
                    }
                }
            }
            else if ((insn.Op == R5900Op.Lw || insn.Op == R5900Op.Lwu) && insn.Rt == jrReg)
            {
                if (insn.Rs == ptrReg && tableBase != 0 && insn.Simm != 0 && (tableBase & 0xFFFFu) == 0)
                {
                    tableBase = (uint)(tableBase + insn.Simm);
                }
                else if (ptrReg == 0 && regValid[insn.Rs])
                {
                    tableBase = (uint)(regVal[insn.Rs] + insn.Simm);
                }
            }
            else
            {
                if (insn.Rd != 0 && insn.Rd != sllReg && insn.Rd != ptrReg)
                {
                    regValid[insn.Rd] = false;
                    regLimit[insn.Rd] = 0;
                }
                if (insn.Rt != 0 && insn.Rt != sllReg && insn.Rt != ptrReg && !insn.IsStore)
                {
                    regValid[insn.Rt] = false;
                    regLimit[insn.Rt] = 0;
                }
            }
        }

        if (tableBase > 0 && reader.IsVAddrValid(tableBase))
        {
            int limit = -1;
            if (sllSrcReg != 0 && idxLimit[sllSrcReg] > 0 && idxLimit[sllSrcReg] <= 512)
                limit = idxLimit[sllSrcReg];
            else if (lastBranchLimit > 0 && lastBranchLimit <= 512)
                limit = lastBranchLimit;
            else if (lastSltLimit > 0 && lastSltLimit <= 512)
                limit = lastSltLimit;

            if (limit > 0 && limit <= 512)
            {
                var jt = new JumpTableInfo
                {
                    JrVAddr = jrVAddr,
                    TableBaseVAddr = tableBase,
                    EntryCount = limit
                };

                for (int i = 0; i < limit; i++)
                {
                    uint entryVAddr = tableBase + (uint)(i * 4);
                    if (!reader.IsVAddrValid(entryVAddr)) break;
                    uint target = reader.ReadUInt32(entryVAddr);
                    jt.Targets.Add(target);
                }

                if (jt.Targets.Count == limit && jt.Targets.All(t => reader.IsInText(t)))
                {
                    return jt;
                }
            }

            // Fallback: scan consecutive valid intra-function code pointers starting at tableBase
            var fallbackTargets = new List<uint>();
            for (int i = 0; i < 512; i++)
            {
                uint entryVAddr = tableBase + (uint)(i * 4);
                if (!reader.IsVAddrValid(entryVAddr)) break;
                uint target = reader.ReadUInt32(entryVAddr);
                if (!reader.IsInText(target) || (target & 3u) != 0) break;
                if (target < windowStart || Math.Abs((long)target - (long)jrVAddr) >= 0x4000) break;
                fallbackTargets.Add(target);
            }

            if (fallbackTargets.Count >= 2)
            {
                return new JumpTableInfo
                {
                    JrVAddr = jrVAddr,
                    TableBaseVAddr = tableBase,
                    EntryCount = fallbackTargets.Count,
                    Targets = fallbackTargets
                };
            }
        }

        return null;
    }

    private static string GetMmioDesc(uint addr) => addr switch
    {
        0x10000000 => "DMA_CHANNEL_0 (VIF0)",
        0x10001000 => "DMA_CHANNEL_1 (VIF1)",
        0x10002000 => "DMA_CHANNEL_2 (GIF)",
        0x10005000 => "DMA_CHANNEL_5 (SIF0)",
        0x10006000 => "DMA_CHANNEL_6 (SIF1)",
        0x1000e000 => "DMAC_D_CTRL",
        0x1000e010 => "DMAC_D_STAT",
        0x1000f000 => "INTC_I_STAT",
        0x1000f010 => "INTC_I_MASK",
        0x1000f130 => "T0_COUNT",
        0x1000f200 => "GS_CSR (Privileged)",
        0x10003000 => "VIF0_STAT",
        0x10003800 => "VIF1_STAT",
        _ => "MMIO / HW Reg"
    };

    private static void WriteToml(
        string filePath,
        List<FunctionMeta> functions,
        List<JumpTableInfo> jumpTables,
        Dictionary<uint, List<uint>> syscalls,
        SortedDictionary<uint, uint> mmioByInst,
        Ps2ElfReader reader)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# castaway_functions.toml — SLUS_216.64 Comprehensive Recompilation Map");
        sb.AppendLine($"# Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC by Castaway.ElfTools");
        sb.AppendLine();
        sb.AppendLine("[meta]");
        sb.AppendLine($"elf = \"{Path.GetFileName(reader.FilePath)}\"");
        sb.AppendLine($"entry_point = \"0x{reader.EntryPoint:X8}\"");
        sb.AppendLine($"total_functions = {functions.Count}");
        sb.AppendLine($"total_jump_tables = {jumpTables.Count}");
        sb.AppendLine($"total_syscalls = {syscalls.Count}");
        sb.AppendLine($"total_mmio_instructions = {mmioByInst.Count}");
        sb.AppendLine();

        // 1. Jump Tables Section
        sb.AppendLine("# Jump tables detected and validated in .rodata");
        foreach (var jt in jumpTables)
        {
            sb.AppendLine("[[jump_tables]]");
            sb.AppendLine($"jr_vaddr = \"0x{jt.JrVAddr:X8}\"");
            sb.AppendLine($"table_base = \"0x{jt.TableBaseVAddr:X8}\"");
            sb.AppendLine($"entries = {jt.EntryCount}");
            sb.AppendLine($"targets = [{string.Join(", ", jt.Targets.Select(t => $"\"0x{t:X8}\""))}]");
            sb.AppendLine();
        }

        // 2. Syscalls Section
        sb.AppendLine("# Kernel Syscalls invoked by guest binary");
        foreach (var kvp in syscalls.OrderBy(k => k.Key))
        {
            string name = SyscallNames.TryGetValue(kvp.Key, out var n) ? n : "UnknownSyscall";
            sb.AppendLine("[[syscalls]]");
            sb.AppendLine($"id = {kvp.Key}");
            sb.AppendLine($"name = \"{name}\"");
            sb.AppendLine($"callsites = [{string.Join(", ", kvp.Value.Take(10).Select(c => $"\"0x{c:X8}\""))}]");
            sb.AppendLine();
        }

        // 3. MMIO Map
        sb.AppendLine("# Hardware MMIO Accesses");
        sb.AppendLine("[mmio]");
        foreach (var kvp in mmioByInst.Take(100))
        {
            sb.AppendLine($"\"0x{kvp.Key:X8}\" = \"0x{kvp.Value:X8}\"");
        }
        sb.AppendLine();

        // 4. Function Bounds & Classification
        sb.AppendLine("# Discovered Function Bounds and Flags");
        foreach (var f in functions)
        {
            sb.AppendLine("[[functions]]");
            sb.AppendLine($"start = \"0x{f.Start:X8}\"");
            sb.AppendLine($"end = \"0x{f.End:X8}\"");
            sb.AppendLine($"name = \"{f.Name}\"");
            sb.AppendLine($"is_leaf = {f.IsLeaf.ToString().ToLowerInvariant()}");
            sb.AppendLine($"uses_fpu = {f.UsesFpu.ToString().ToLowerInvariant()}");
            sb.AppendLine($"uses_vu0 = {f.UsesVu0.ToString().ToLowerInvariant()}");
            sb.AppendLine($"uses_mmi = {f.UsesMmi.ToString().ToLowerInvariant()}");
            if (f.HasMmio) sb.AppendLine("has_mmio = true");
            if (f.UsesScratchpad) sb.AppendLine("uses_scratchpad = true");
            if (f.SyscallId.HasValue) sb.AppendLine($"syscall_id = {f.SyscallId.Value}");
            if (f.JumpTables.Count > 0)
            {
                sb.AppendLine($"jump_tables = [{string.Join(", ", f.JumpTables.Select(j => $"\"0x{j:X8}\""))}]");
            }
            sb.AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString());
    }
}
