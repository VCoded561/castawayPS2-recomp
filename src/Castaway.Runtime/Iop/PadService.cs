using System.Buffers.Binary;
using Silk.NET.Input;

namespace Castaway.Runtime;

[Flags]
public enum Ps2PadButtons : ushort
{
    None     = 0x0000,
    Select   = 0x0001, // bit 0  (btnLo bit 0)
    L3       = 0x0002, // bit 1  (btnLo bit 1)
    R3       = 0x0004, // bit 2  (btnLo bit 2)
    Start    = 0x0008, // bit 3  (btnLo bit 3)
    Up       = 0x0010, // bit 4  (btnLo bit 4)
    Right    = 0x0020, // bit 5  (btnLo bit 5)
    Down     = 0x0040, // bit 6  (btnLo bit 6)
    Left     = 0x0080, // bit 7  (btnLo bit 7)
    L2       = 0x0100, // bit 8  (btnHi bit 0)
    R2       = 0x0200, // bit 9  (btnHi bit 1)
    L1       = 0x0400, // bit 10 (btnHi bit 2)
    R1       = 0x0800, // bit 11 (btnHi bit 3)
    Triangle = 0x1000, // bit 12 (btnHi bit 4)
    Circle   = 0x2000, // bit 13 (btnHi bit 5)
    Cross    = 0x4000, // bit 14 (btnHi bit 6)
    Square   = 0x8000  // bit 15 (btnHi bit 7)
}

public struct DualShock2State
{
    public Ps2PadButtons PressedButtons;
    public byte LeftStickX;
    public byte LeftStickY;
    public byte RightStickX;
    public byte RightStickY;

    public static DualShock2State Neutral => new()
    {
        PressedButtons = Ps2PadButtons.None,
        LeftStickX = 0x80,
        LeftStickY = 0x80,
        RightStickX = 0x80,
        RightStickY = 0x80
    };
}

public sealed unsafe class PadService
{
    public const int SlotByteSize = 128;
    public const int RingBufferByteSize = 256;

    private readonly Dictionary<(int Port, int Slot), uint> _openedPadAreas = new();
    private readonly DualShock2State[] _portStates = [DualShock2State.Neutral, DualShock2State.Neutral];
    private readonly Ps2PadButtons[] _latchedButtons = [Ps2PadButtons.None, Ps2PadButtons.None];
    private Ps2PadButtons _lastLoggedButtons = Ps2PadButtons.None;
    private uint _frameCounter = 1;
    private int _rpcDefaultLogCount;

    public DualShock2State GetPortState(int port = 0) => _portStates[Math.Clamp(port, 0, 1)];

    public void SetPortState(int port, DualShock2State state)
    {
        int p = Math.Clamp(port, 0, 1);
        _portStates[p] = state;
        _latchedButtons[p] |= state.PressedButtons;
    }

    public bool TryGetPadArea(int port, int slot, out uint padAreaAddr) =>
        _openedPadAreas.TryGetValue((port, slot), out padAreaAddr);

    public void OpenPort(int port, int slot, uint padAreaGuestAddr, byte* ram)
    {
        _openedPadAreas[(port, slot)] = padAreaGuestAddr;
        WriteSlot(ram, padAreaGuestAddr, 0, _portStates[Math.Clamp(port, 0, 1)], _frameCounter++);
        WriteSlot(ram, padAreaGuestAddr, 1, _portStates[Math.Clamp(port, 0, 1)], _frameCounter++);
    }

    public void ClosePort(int port, int slot)
    {
        _openedPadAreas.Remove((port, slot));
    }

    public void UpdateFromSilkInput(IInputContext? inputContext, byte* ram)
    {
        if (inputContext != null)
        {
            var state = DualShock2State.Neutral;

            // 1. Poll first connected gamepad
            if (inputContext.Gamepads.Count > 0 && inputContext.Gamepads[0].IsConnected)
            {
                var pad = inputContext.Gamepads[0];
                foreach (var btn in pad.Buttons)
                {
                    if (!btn.Pressed) continue;
                    state.PressedButtons |= btn.Name switch
                    {
                        ButtonName.A => Ps2PadButtons.Cross,
                        ButtonName.B => Ps2PadButtons.Circle,
                        ButtonName.X => Ps2PadButtons.Square,
                        ButtonName.Y => Ps2PadButtons.Triangle,
                        ButtonName.LeftBumper => Ps2PadButtons.L1,
                        ButtonName.RightBumper => Ps2PadButtons.R1,
                        ButtonName.Back => Ps2PadButtons.Select,
                        ButtonName.Start => Ps2PadButtons.Start,
                        ButtonName.LeftStick => Ps2PadButtons.L3,
                        ButtonName.RightStick => Ps2PadButtons.R3,
                        ButtonName.DPadUp => Ps2PadButtons.Up,
                        ButtonName.DPadDown => Ps2PadButtons.Down,
                        ButtonName.DPadLeft => Ps2PadButtons.Left,
                        ButtonName.DPadRight => Ps2PadButtons.Right,
                        _ => Ps2PadButtons.None
                    };
                }

                if (pad.Thumbsticks.Count > 0)
                {
                    state.LeftStickX = FloatAxisToByte(pad.Thumbsticks[0].X);
                    state.LeftStickY = FloatAxisToByte(pad.Thumbsticks[0].Y);
                }
                if (pad.Thumbsticks.Count > 1)
                {
                    state.RightStickX = FloatAxisToByte(pad.Thumbsticks[1].X);
                    state.RightStickY = FloatAxisToByte(pad.Thumbsticks[1].Y);
                }
                if (pad.Triggers.Count > 0 && pad.Triggers[0].Position > 0.25f)
                    state.PressedButtons |= Ps2PadButtons.L2;
                if (pad.Triggers.Count > 1 && pad.Triggers[1].Position > 0.25f)
                    state.PressedButtons |= Ps2PadButtons.R2;
            }

            // 2. Keyboard fallback/overlay
            if (inputContext.Keyboards.Count > 0)
            {
                var kb = inputContext.Keyboards[0];
                if (kb.IsKeyPressed(Key.Enter) || kb.IsKeyPressed(Key.KeypadEnter)) state.PressedButtons |= Ps2PadButtons.Start;
                if (kb.IsKeyPressed(Key.Backspace)) state.PressedButtons |= Ps2PadButtons.Select;
                if (kb.IsKeyPressed(Key.Up)) state.PressedButtons |= Ps2PadButtons.Up;
                if (kb.IsKeyPressed(Key.Down)) state.PressedButtons |= Ps2PadButtons.Down;
                if (kb.IsKeyPressed(Key.Left)) state.PressedButtons |= Ps2PadButtons.Left;
                if (kb.IsKeyPressed(Key.Right)) state.PressedButtons |= Ps2PadButtons.Right;
                if (kb.IsKeyPressed(Key.Z) || kb.IsKeyPressed(Key.Space)) state.PressedButtons |= Ps2PadButtons.Cross;
                if (kb.IsKeyPressed(Key.X)) state.PressedButtons |= Ps2PadButtons.Circle;
                if (kb.IsKeyPressed(Key.C)) state.PressedButtons |= Ps2PadButtons.Square;
                if (kb.IsKeyPressed(Key.V)) state.PressedButtons |= Ps2PadButtons.Triangle;
                if (kb.IsKeyPressed(Key.Q)) state.PressedButtons |= Ps2PadButtons.L1;
                if (kb.IsKeyPressed(Key.E)) state.PressedButtons |= Ps2PadButtons.R1;

                float lx = 0f, ly = 0f;
                if (kb.IsKeyPressed(Key.A)) lx -= 1f;
                if (kb.IsKeyPressed(Key.D)) lx += 1f;
                if (kb.IsKeyPressed(Key.W)) ly -= 1f;
                if (kb.IsKeyPressed(Key.S)) ly += 1f;
                if (lx != 0f) state.LeftStickX = FloatAxisToByte(lx);
                if (ly != 0f) state.LeftStickY = FloatAxisToByte(ly);
            }

            _portStates[0] = state;
            _latchedButtons[0] |= state.PressedButtons;
        }
    }

    public void StepFrame(byte* ram)
    {
        uint frame = ++_frameCounter;
        int slotIdx = (int)(frame & 1);
        foreach (var kvp in _openedPadAreas)
        {
            int port = Math.Clamp(kvp.Key.Port, 0, 1);
            var effectiveState = _portStates[port];
            effectiveState.PressedButtons |= _latchedButtons[port];
            _latchedButtons[port] = Ps2PadButtons.None;

            if (port == 0 && effectiveState.PressedButtons != _lastLoggedButtons)
            {
                _lastLoggedButtons = effectiveState.PressedButtons;
                Console.Error.WriteLine($"[PAD INPUT] frame={frame} Buttons={effectiveState.PressedButtons} (0x{(ushort)effectiveState.PressedButtons:X4})");
            }

            WriteSlot(ram, kvp.Value, slotIdx, effectiveState, frame);
        }
    }

    private static byte FloatAxisToByte(float axis)
    {
        int v = (int)MathF.Round((Math.Clamp(axis, -1f, 1f) + 1f) * 127.5f);
        return (byte)Math.Clamp(v, 0, 255);
    }

    /// <summary>
    /// Writes one 128-byte libpad2 DMA slot at padArea + (slotIndex * 128).
    /// Layout verified against SLUS_216.64 (0x00479958..0x00479E90 &amp; Ps2PadDevice::UnpackPadState 0x00417158).
    /// </summary>
    public static void WriteSlot(byte* ram, uint padAreaGuestAddr, int slotIndex, DualShock2State state, uint frameCount)
    {
        uint phys = (padAreaGuestAddr + (uint)(slotIndex * SlotByteSize)) & 0x01FFFFFFu;
        if (phys + SlotByteSize > 32 * 1024 * 1024)
            return;

        Span<byte> slot = new Span<byte>(ram + phys, SlotByteSize);
        slot.Clear();

        // Active-low button mask (0 = pressed, 1 = released)
        ushort activeLowButtons = (ushort)~(ushort)state.PressedButtons;
        byte btnLo = (byte)(activeLowButtons & 0xFF);
        byte btnHi = (byte)((activeLowButtons >> 8) & 0xFF);

        // +0..31: DualShock 2 data packet (scePadRead rdata[0..31])
        // Verified against Ps2PadDevice::UnpackPadState (0x00417158):
        //   rdata[2] = active-low btnLo (Select, L3, R3, Start, Up, Right, Down, Left)
        //   rdata[3] = active-low btnHi (L2, R2, L1, R1, Triangle, Circle, Cross, Square)
        //   rdata[4..5] = RightStickX, RightStickY
        //   rdata[6..7] = LeftStickX, LeftStickY
        //   rdata[8..19] = 12 button pressure bytes
        slot[0] = 0x00; // Status OK
        slot[1] = 0x79; // DualShock 2 mode (0x79 = analog + pressure)
        slot[2] = btnLo;
        slot[3] = btnHi;
        slot[4] = state.RightStickX;
        slot[5] = state.RightStickY;
        slot[6] = state.LeftStickX;
        slot[7] = state.LeftStickY;

        // +8..19: 12 button pressure bytes
        slot[8]  = state.PressedButtons.HasFlag(Ps2PadButtons.Right)    ? (byte)0xFF : (byte)0x00;
        slot[9]  = state.PressedButtons.HasFlag(Ps2PadButtons.Left)     ? (byte)0xFF : (byte)0x00;
        slot[10] = state.PressedButtons.HasFlag(Ps2PadButtons.Up)       ? (byte)0xFF : (byte)0x00;
        slot[11] = state.PressedButtons.HasFlag(Ps2PadButtons.Down)     ? (byte)0xFF : (byte)0x00;
        slot[12] = state.PressedButtons.HasFlag(Ps2PadButtons.Triangle) ? (byte)0xFF : (byte)0x00;
        slot[13] = state.PressedButtons.HasFlag(Ps2PadButtons.Circle)   ? (byte)0xFF : (byte)0x00;
        slot[14] = state.PressedButtons.HasFlag(Ps2PadButtons.Cross)    ? (byte)0xFF : (byte)0x00;
        slot[15] = state.PressedButtons.HasFlag(Ps2PadButtons.Square)   ? (byte)0xFF : (byte)0x00;
        slot[16] = state.PressedButtons.HasFlag(Ps2PadButtons.L1)       ? (byte)0xFF : (byte)0x00;
        slot[17] = state.PressedButtons.HasFlag(Ps2PadButtons.R1)       ? (byte)0xFF : (byte)0x00;
        slot[18] = state.PressedButtons.HasFlag(Ps2PadButtons.L2)       ? (byte)0xFF : (byte)0x00;
        slot[19] = state.PressedButtons.HasFlag(Ps2PadButtons.R2)       ? (byte)0xFF : (byte)0x00;

        // +48..55 (0x30..0x37): Actuator descriptors (small motor & large motor)
        // Verified against SLUS_216.64 FUN_004599b8 (0x00459A84..0x00459ADC):
        // act 0: a3=1 -> 1, a3=2 -> 2, a3=3 -> 0
        // act 1: a3=1 -> 1, a3=2 -> 1, a3=3 -> 1
        slot[0x30] = 1; slot[0x31] = 2; slot[0x32] = 0; slot[0x33] = 0;
        slot[0x34] = 1; slot[0x35] = 1; slot[0x36] = 1; slot[0x37] = 20;

        // +64..71 (0x40..0x47) and +80..87 (0x50..0x57): Mode table (0x0004 Digital, 0x0007 DualShock)
        // Verified against SLUS_216.64 FUN_00479de0 (0x00479EF8 loads from 0x50($sp + 2*idx))
        BinaryPrimitives.WriteUInt16LittleEndian(slot[0x40..0x42], 0x0004);
        BinaryPrimitives.WriteUInt16LittleEndian(slot[0x42..0x44], 0x0007);
        BinaryPrimitives.WriteUInt16LittleEndian(slot[0x50..0x52], 0x0004);
        BinaryPrimitives.WriteUInt16LittleEndian(slot[0x52..0x54], 0x0007);

        // +88 (0x58): uint32 frameCount (scePadGetDmaStr compares +88 and +216)
        BinaryPrimitives.WriteUInt32LittleEndian(slot[0x58..0x5C], frameCount);

        // +96 (0x60): int32 data length (32 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(slot[0x60..0x64], 32);

        // +100..114: Status and mode fields
        slot[0x64] = 2;    // modeOk
        slot[0x65] = 0x79; // modeCurId
        slot[0x68] = 1;    // modeCurOffs (DualShock index)
        slot[0x69] = 2;    // numModes
        slot[0x6A] = 2;    // numActuators
        slot[0x70] = 6;    // state = scePadStateStable (6)
        slot[0x71] = 0;    // reqState = scePadReqStateComplete (0)
        slot[0x72] = 1;    // ok = 1
    }

    public int HandleRpc(uint sid, uint rpcNum, byte* ram, uint sendBufAddr, int sendSize, uint recvBufAddr, int recvSize)
    {
        uint sendPhys = sendBufAddr & 0x01FFFFFFu;
        uint recvPhys = recvBufAddr & 0x01FFFFFFu;

        if (sendBufAddr == 0 || recvBufAddr == 0)
            return 0;

        Span<byte> send = new Span<byte>(ram + sendPhys, Math.Max(32, sendSize));
        Span<byte> recv = new Span<byte>(ram + recvPhys, Math.Max(32, recvSize));

        uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(send[0..4]);

        switch (cmd)
        {
            case 0x01: // PortOpen (0x01 in new padman, or 0x80000100)
            case 0x80000100:
            {
                int port = BinaryPrimitives.ReadInt32LittleEndian(send[4..8]);
                int slot = BinaryPrimitives.ReadInt32LittleEndian(send[8..12]);
                uint padArea = BinaryPrimitives.ReadUInt32LittleEndian(send[16..20]);
                Console.WriteLine($"[PadService] PortOpen: port={port} slot={slot} padArea=0x{padArea:X8}");
                OpenPort(port, slot, padArea, ram);
                BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 1);
                BinaryPrimitives.WriteInt32LittleEndian(recv[12..16], 1); // recv[3] = 1
                BinaryPrimitives.WriteUInt32LittleEndian(recv[20..24], padArea); // recv[5] = padArea
                break;
            }

            case 0x12: // GetModVersion (checked at 0x0047933C for (ver >> 8) == 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 0x0400);
                BinaryPrimitives.WriteInt32LittleEndian(recv[12..16], 0x0400);
                break;
            }

            case 0x03: // PortClose
            {
                int port = BinaryPrimitives.ReadInt32LittleEndian(send[4..8]);
                int slot = BinaryPrimitives.ReadInt32LittleEndian(send[8..12]);
                Console.WriteLine($"[PadService] PortClose: port={port} slot={slot}");
                ClosePort(port, slot);
                BinaryPrimitives.WriteInt32LittleEndian(recv[12..16], 1);
                BinaryPrimitives.WriteInt32LittleEndian(recv[20..24], 1);
                break;
            }

            default:
            {
                // SetMainMode (0x06), SetActDirect (0x08), SetActAlign (0x09), GetButtonMask (0x0A), SetButtonInfo (0x0B), Init (0x10)
                if (_rpcDefaultLogCount < 30 && cmd != 0x08)
                {
                    _rpcDefaultLogCount++;
                    int p = BinaryPrimitives.ReadInt32LittleEndian(send[4..8]);
                    int sl = BinaryPrimitives.ReadInt32LittleEndian(send[8..12]);
                    Console.WriteLine($"[PadService] RPC cmd=0x{cmd:X2} port={p} slot={sl}");
                }
                BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 1);
                BinaryPrimitives.WriteInt32LittleEndian(recv[12..16], 1);
                BinaryPrimitives.WriteInt32LittleEndian(recv[20..24], 1);
                break;
            }
        }

        return 0;
    }
}
