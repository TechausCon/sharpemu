<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu Roadmap

This document captures the current architecture, the present state of the
project, and a phased plan for further development. It is a living document;
update it as priorities shift.

> [!NOTE]
> SharpEmu is in an early alpha stage. The goal of this roadmap is to turn a
> "boots into init code" emulator into one that can present frames and,
> eventually, run real titles.

## How SharpEmu Works (1-minute primer)

The PS5 uses an AMD x86-64 CPU. Instead of interpreting or recompiling guest
code, SharpEmu **executes guest x86-64 code natively on the host CPU**
(`DirectExecutionBackend`) and intercepts system calls / library imports via
**High-Level Emulation (HLE)**.

```
eboot.bin
  -> SelfLoader        (map ELF @ 0x800000000, parse .dynamic, NID imports,
                        relocations, INT3/RET import stubs)
  -> preload sce_module/*.prx
  -> module initializers
  -> DirectExecutionBackend.ExecuteEntry
        host stub `call`s into native guest code
        import PLT slots -> RWX trampolines -> ImportGateway
        -> ModuleManager.TryDispatch(nid) -> C# HLE function -> RAX
        Windows VEH handles faults / unresolved-symbol sentinels
```

### Projects

| Project            | Responsibility                                                        |
| ------------------ | --------------------------------------------------------------------- |
| `SharpEmu.CLI`     | Entry point; relaunches as a "mitigated child" (CET/CFG off) on Win   |
| `SharpEmu.Core`    | SELF/ELF loader, virtual memory, CPU dispatcher, native exec backend  |
| `SharpEmu.HLE`     | NID dispatch, `CpuContext`, Aerolib symbol catalog, interfaces        |
| `SharpEmu.Libs`    | HLE implementations of Orbis libraries (~259 exports)                 |
| `SharpEmu.Logging` | Logging                                                               |

## Current State

**Working:** decrypted ELF/FSELF loading, native execution of CRT/init code,
NID-based dynamic linking, libc + a large part of libKernel (direct/flexible
memory, mmap/mprotect, host filesystem, pthreads/mutex/cond, event queues, APR
I/O), and a cooperative guest-thread scheduler.

**Reaches:** `sceVideoOut` / AGC / APR stages during game initialization.

**Missing (the blockers to playability):**

| Subsystem                                   | Status                          |
| ------------------------------------------- | ------------------------------- |
| GPU (GNM/AGC, command processor, shaders)   | **absent**                      |
| Frame presentation (`sceVideoOutFlip`, ...) | **absent** (VideoOut is a stub) |
| Audio (AudioOut / AJM / Audio3d)            | absent                          |
| Input (Pad / DualSense)                     | absent                          |
| Network, SaveData, Trophies, NP/PSN, IME    | absent                          |
| Runtime SELF decryption                     | not implemented (needs decrypted input) |

## Known Technical Debt / Risks

1. **No test project.** The single biggest risk for an emulator this fragile.
2. **`DirectExecutionBackend` is fragile and monolithic** (~4000 lines across 4
   files): binary-patch hacks (stack-canary removal, TLS redirection), a global
   host-RSP slot, sentinel "recovery" by scanning the stack, a watchdog that
   calls `Environment.Exit(4)`.
3. **Dead / orphaned code:** `JitStubs` / `StubManager` are disconnected from the
   active path; several fields are written but never read
   (`_globalUnresolvedReturnStub`, `TryPatchEa020eLookupCall`, ...).
4. **Debug `Console.Error` output is baked into the production path** — should go
   through `SharpEmuLog` with log levels.
5. **Build is pinned** via `global.json` (`10.0.103`, `rollForward: disable`) and
   fails on machines with a different .NET 10 feature band.
6. **Cooperative threading** without true parallelism; `pthread_join` is a stub.

## Development Plan

### Phase 0 — Foundation & Hygiene (small, do first)

- [ ] Fix `global.json`: set `rollForward` to `latestFeature`/`latestPatch` or
      bump the version so the build works across .NET 10 SDKs.
- [ ] Add a test project (`SharpEmu.Tests`, xUnit). Start with deterministic,
      pure components: loader (ELF parsing / relocations), `PhysicalVirtualMemory`,
      `ModuleManager` dispatch, NID hashing, Aerolib.
- [ ] Route debug logging through `SharpEmuLog` (remove `Console.Error` "[DEBUG]"
      spam from hot paths).
- [ ] Decide on dead code: wire up or delete `JitStubs` / `StubManager` and unused
      fields.

### Phase 1 — CPU Backend Stability (foundation for everything)

- [ ] Modularize `DirectExecutionBackend`: separate stub setup, TLS patching,
      exception handling, and thread scheduling behind interfaces (testable).
- [ ] Proper guest-threading model: correct `pthread_join` / `cond_wait`;
      a real scheduler or one host thread per guest thread.
- [ ] Structured fault handling instead of log-only in the main VEH.

### Phase 2 — Visible Progress: Graphics Bring-up (the big goal)

1. [ ] Complete VideoOut: `sceVideoOutFlip`, `GetFlipStatus`, `AddFlipEvent`,
       and correct vblank event delivery.
2. [ ] Intercept and parse GNM/AGC command buffers (PM4 packets).
3. [ ] GPU backend on Vulkan (e.g. via Silk.NET): present a framebuffer to a host
       window, then incrementally translate shaders (GCN/RDNA2 ISA -> SPIR-V).
       Multi-month effort; first milestone = a single blitted frame / clear color.

### Phase 3 — Playability

- [ ] Pad input (`scePad`) — small effort, high value.
- [ ] AudioOut.
- [ ] Fuller sysmodule/PRX loading, SaveData, dialogs.

### Phase 4 — Reach

- [ ] Cross-platform (Linux/macOS): abstract the Windows-specific P/Invoke in the
      backend.
- [ ] Optional runtime SELF decryption.

## Suggested Starting Point

Begin with **Phase 0** (fix the build + first test project). Without a green
build and a safety net of tests, every subsequent change to the fragile native
backend is high risk.
