# The Sims 2: Castaway — PS2 Static Recompilation

A work in progress native PC static recompiler for *The Sims 2: Castaway* (PS2 NTSC-U `SLUS_216.64`), targeting **C# (.NET 9)** and **Silk.NET.OpenGL** (OpenGL 4.6 Core Profile with DSA and Compute Shaders, alongside a software reference rasterizer).

# This project was almost fully vibe coded with Gemini 4 and gemini 3.8 Flash. Any part of the code might be hallucinated or just wrong. Do not use this as an example!

The game runs all the way to the main menu and sim creator. Crashes after sim creation. 3D rendering doesn't work correctly in software or OpenGL modes yet. Software rendering is extremely slow.

The goal of this project is to create a native PC version that is 100% accurate to the PS2 version. A wishlist for the future: modern controls, readable codebase, high resolution and any aspect ratio support. Maybe even coop multiplayer at some point.

Any help is appreciated!

---

## Architecture Overview

```text
Castaway.sln
├── src/
│   ├── Castaway.Core/         # Emotion Engine CPU context (128-bit GPRs, COP0/COP1 FPU, MMI/VU0 instructions, 32MB physical memory)
│   ├── Castaway.Runtime/      # PS2 OS kernel syscalls, DMAC channels (GIF, VIF, SIF), SIF RPC & IOP HLE services
│   ├── Castaway.GsRenderer/   # Graphics Synthesizer (GS) rasterizer (OpenGL 4.6 DSA + Compute Shader swizzling, VIF unpacker, VU1 JIT/AOT recompiler)
│   ├── Castaway.Overrides/    # Non-destructive gameplay hooks (widescreen, arbitrary framerates, rendering enhancements)
│   ├── Castaway.Host/         # Desktop application host, Silk.NET windowing, input dispatch, and frame loop
│   └── Castaway.Generated/    # Destination for AOT transpiled C# source files (generated via Tools/PS2Recomp)
├── Tools/
│   ├── PS2Recomp/             # AOT transpiler: decodes SLUS_216.64 MIPS opcodes and emits sharded C# methods
│   └── Castaway.ElfTools/     # Zero-dependency binary inspection CLI: ELF reader, R5900 decoder, jump table scanner, trace diffing
└── tests/
    └── Castaway.ElfTools.Tests/ # Test suite covering EE instruction accuracy, DMA/VIF decoding, and GS pipeline
```

---

## Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (or newer)
- A legally dumped copy of *The Sims 2: Castaway* (PS2 NTSC-U):
  - `SLUS_216.64`
  - DVD asset files (`Data/DVD/`)
- A GPU supporting **OpenGL 4.6 Core Profile** (for hardware rendering; software rendering mode is also available).

---

## Setup & Building

### 1. Provide Game Assets
Extract your dumped game files into the repository root:
```text
Data/
└── DVD/
    ├── SLUS_216.64
    └── ... (original .ARC game archives)
```

### 2. Transpile the Binary to C#
Run the AOT transpiler to generate the C# source code from your `SLUS_216.64` binary:
```bash
dotnet run --project Tools/PS2Recomp
```
This produces `Funcs_*.cs` and `FunctionTable.cs` inside `src/Castaway.Generated/`.

### 3. Build the Solution
```bash
dotnet build Castaway.sln -c Release
```

### 4. Run the Game
Launch the desktop host:
```bash
# OpenGL 4.6 Hardware Renderer (Default)
dotnet run -c Release --project src/Castaway.Host/Castaway.Host.csproj -- --renderer opengl --fps 30

# Software Reference Rasterizer
dotnet run -c Release --project src/Castaway.Host/Castaway.Host.csproj -- --renderer software --fps 30
```

---

## Running Tests

Hardware accuracy and runtime unit tests can be executed at any time:
```bash
dotnet test tests/Castaway.ElfTools.Tests/Castaway.ElfTools.Tests.csproj
```

---

## Legal & Disclaimer

This repository does **not** contain any copyrighted code, assets, audio, 3D models, or binaries from *The Sims 2: Castaway* or Sony PlayStation 2. To compile and run this project, you must supply your own legally obtained copies of the original game files.
