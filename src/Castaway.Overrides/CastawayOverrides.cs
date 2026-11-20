using System.Numerics;
using Castaway.Core;
using Castaway.GsRenderer;
using Castaway.GsRenderer.Gs;
using Castaway.Runtime;
using Silk.NET.Input;

namespace Castaway.Overrides;

public sealed class OverrideOptions
{
    public int ResolutionScale { get; set; } = 2;
    public bool EnableWidescreen { get; set; } = false;
    public float TargetAspectRatio { get; set; } = 16f / 9f;
    public bool EnableModernControls { get; set; } = true;
    public float MouseSensitivity { get; set; } = 0.015f;
    public GsRendererMode RendererMode { get; set; } = GsRendererMode.Software;
    public int TargetFps { get; set; } = 60; // 60 FPS default for smooth PS2 NTSC field rate
    public int MsaaSamples { get; set; } = 4; // OpenGL MSAA sample count (0/1 = off, 2, 4, 8, 16)
}

/// <summary>
/// Phase 6 non-destructive enhancements living exclusively in Castaway.Overrides.
/// Uses RuntimeDispatcher address hooks without modifying src/Castaway.Generated/.
/// </summary>
public sealed unsafe class CastawayOverrides
{
    // PS2Display::GetAspectRatio() in SLUS_216.64 (0x00433B00, vtable[+336] on *(0x004A0728))
    // Originally returns hardcoded 4:3 (0x3FAAAAAB) in $f0; called by all 3D camera frustum builders.
    public const uint DisplayGetAspectRatioAddr = 0x00433B00u;
    public const float NativePs2AspectRatio = 4f / 3f;

    public OverrideOptions Options { get; }
    public Action? OnSnapshotRequested { get; set; }
    private GsGraphicsPipeline? _graphics;
    private float _viewportAspectRatio;
    private Vector2 _lastMousePos;
    private bool _hasLastMousePos;
    private float _pendingMouseDeltaX;
    private float _pendingMouseDeltaY;
    private float _pendingScrollDelta;

    public CastawayOverrides(OverrideOptions? options = null)
    {
        Options = options ?? new OverrideOptions();
    }

    public void SetViewportAspectRatio(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            float aspect = Math.Clamp((float)width / height, 0.2f, 8.0f);
            Volatile.Write(ref _viewportAspectRatio, aspect);
        }
    }

    public float GetEffectiveAspectRatio()
    {
        if (_graphics != null && _graphics.Mode == GsRendererMode.OpenGl)
        {
            float vpAspect = Volatile.Read(ref _viewportAspectRatio);
            if (vpAspect > 0.0f)
                return vpAspect;
            return Options.EnableWidescreen ? Math.Clamp(Options.TargetAspectRatio, 0.2f, 8.0f) : NativePs2AspectRatio;
        }

        if (Options.EnableWidescreen)
        {
            float vpAspect = Volatile.Read(ref _viewportAspectRatio);
            return vpAspect > 0.0f ? vpAspect : Math.Clamp(Options.TargetAspectRatio, 0.2f, 8.0f);
        }

        return NativePs2AspectRatio;
    }

    public void Install(CastawayRuntime runtime, GsGraphicsPipeline graphics)
    {
        _graphics = graphics;
        _graphics.Mode = Options.RendererMode;
        graphics.OpenGl.SetResolutionScale(Options.ResolutionScale);
        graphics.OpenGl.SetMsaaSamples(Options.MsaaSamples);

        InstallRenderWareUiHook();
        InstallAspectRatioHook();
    }

    private void InstallRenderWareUiHook()
    {
        // 0x00433F40 is an unlabelled leaf RenderWare 2D UI coordinate transformation routine inside
        // sub_00433E48 that converts a 2D bounding rect (a2 = [x0, y0, x1, y1]) relative to an origin
        // (a3 = [ox, oy]) into screen-space destination (a1 = [x0 - ox, (y0 - oy) - 1.0, x1 - ox, (y1 - oy) - 1.0]).
        RuntimeDispatcher.RegisterHook(0x00433F40u, (c, r) =>
        {
            uint ra = c->GetU32(31);
            uint a1 = c->GetU32(5);
            uint a2 = c->GetU32(6);
            uint a3 = c->GetU32(7);
            float a3_0 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a3 + 0));
            float a3_1 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a3 + 4));
            float a2_0 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a2 + 0));
            float a2_1 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a2 + 4));
            float a2_2 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a2 + 8));
            float a2_3 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a2 + 12));

            float out0 = a2_0 - a3_0;
            float out1 = (a2_1 - a3_1) - 1.0f;
            float out2 = a2_2 - a3_0;
            float out3 = (a2_3 - a3_1) - 1.0f;

            Ps2Memory.Write32(c, r, a1 + 0, BitConverter.SingleToUInt32Bits(out0));
            Ps2Memory.Write32(c, r, a1 + 4, BitConverter.SingleToUInt32Bits(out1));
            Ps2Memory.Write32(c, r, a1 + 8, BitConverter.SingleToUInt32Bits(out2));
            Ps2Memory.Write32(c, r, a1 + 12, BitConverter.SingleToUInt32Bits(out3));

            RuntimeDispatcher.OnReturn(c, ra);
        });
    }

    private void InstallAspectRatioHook()
    {
        // Hook PS2Display::GetAspectRatio() at 0x00433B00 (vtable[+336] on *(0x004A0728)).
        // Original MIPS returns hardcoded 4:3 (0x3FAAAAAB) in $f0; returning the live OpenGL
        // viewport aspect ratio scales m[0][0] = cot(fovY/2) / aspect in sub_003DBFD0 (Hor+)
        // and updates world frustum culling planes in sub_003EA738 so any aspect ratio renders
        // without 3D stretching.
        RuntimeDispatcher.RegisterHook(DisplayGetAspectRatioAddr, (ctx, ram) =>
        {
            float aspect = GetEffectiveAspectRatio();
            ctx->SetFpuBits(0, BitConverter.SingleToUInt32Bits(aspect));
            RuntimeDispatcher.OnReturn(ctx, ctx->GetU32(31));
        });
    }

    public void AttachInputEvents(IInputContext input)
    {
        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnMouseScroll;
        }

        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if (key == Key.F8)
        {
            if (_graphics != null)
            {
                int maxSamples = Math.Max(1, _graphics.OpenGl.MaxSupportedMsaaSamples);
                int cur = Options.MsaaSamples <= 1 ? 1 : Options.MsaaSamples;
                int next = cur switch
                {
                    1 => maxSamples >= 2 ? 2 : 1,
                    2 => maxSamples >= 4 ? 4 : 1,
                    4 => maxSamples >= 8 ? 8 : 1,
                    _ => 1
                };
                Options.MsaaSamples = next;
                _graphics.OpenGl.SetMsaaSamples(next);
                Console.WriteLine($"\n[Castaway.Overrides] >>> MSAA toggled to: {(next <= 1 ? "Off" : $"{next}x")} <<<\n");
            }
        }
        else if (key == Key.F9)
        {
            if (_graphics != null)
            {
                _graphics.Mode = _graphics.Mode == GsRendererMode.OpenGl ? GsRendererMode.Software : GsRendererMode.OpenGl;
                Options.RendererMode = _graphics.Mode;
                Console.WriteLine($"\n[Castaway.Overrides] >>> Renderer Mode toggled to: {_graphics.Mode} <<<\n");
            }
        }
        else if (key == Key.F10)
        {
            Options.TargetFps = Options.TargetFps switch
            {
                30 => 60,
                60 => 0,
                _ => 30
            };
            Console.WriteLine($"\n[Castaway.Overrides] >>> Target FPS toggled to: {(Options.TargetFps == 0 ? "Uncapped" : $"{Options.TargetFps} FPS")} <<<\n");
        }
        else if (key == Key.F11)
        {
            OnSnapshotRequested?.Invoke();
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        if (!_hasLastMousePos)
        {
            _lastMousePos = pos;
            _hasLastMousePos = true;
            return;
        }

        Vector2 delta = pos - _lastMousePos;
        _lastMousePos = pos;

        // Only rotate camera when right mouse button is held or cursor is captured
        if (mouse.IsButtonPressed(MouseButton.Right) || mouse.Cursor.CursorMode == CursorMode.Raw)
        {
            _pendingMouseDeltaX += delta.X * Options.MouseSensitivity;
            _pendingMouseDeltaY += delta.Y * Options.MouseSensitivity;
        }
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        _pendingScrollDelta += wheel.Y;
    }

    /// <summary>
    /// Updates PadService from Silk.NET input (Xbox controller + Keyboard) and overlays
    /// Phase 6 mouse-delta right-stick camera rotation &amp; scroll-wheel zoom.
    /// </summary>
    public void UpdateInput(PadService padService, IInputContext? input, byte* ram)
    {
        padService.UpdateFromSilkInput(input, ram);

        if (!Options.EnableModernControls || input == null)
            return;

        var state = padService.GetPortState(0);

        if (input.Mice.Count > 0)
        {
            var mouse = input.Mice[0];
            if (mouse.IsButtonPressed(MouseButton.Left))
                state.PressedButtons |= Ps2PadButtons.Cross;
            if (mouse.IsButtonPressed(MouseButton.Middle))
                state.PressedButtons |= Ps2PadButtons.Triangle;
        }

        if (MathF.Abs(_pendingMouseDeltaX) > 0.001f || MathF.Abs(_pendingMouseDeltaY) > 0.001f)
        {
            float rx = Math.Clamp(_pendingMouseDeltaX, -1f, 1f);
            float ry = Math.Clamp(_pendingMouseDeltaY, -1f, 1f);
            state.RightStickX = (byte)Math.Clamp((int)MathF.Round((rx + 1f) * 127.5f), 0, 255);
            state.RightStickY = (byte)Math.Clamp((int)MathF.Round((ry + 1f) * 127.5f), 0, 255);
            _pendingMouseDeltaX = 0f;
            _pendingMouseDeltaY = 0f;
        }

        if (MathF.Abs(_pendingScrollDelta) > 0.01f)
        {
            // Scroll wheel maps to Right Stick Y (camera zoom in/out)
            float zoomAxis = _pendingScrollDelta > 0f ? -0.85f : 0.85f;
            state.RightStickY = (byte)Math.Clamp((int)MathF.Round((zoomAxis + 1f) * 127.5f), 0, 255);
            _pendingScrollDelta = 0f;
        }

        padService.SetPortState(0, state);
    }
}
