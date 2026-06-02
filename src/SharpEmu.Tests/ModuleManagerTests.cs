// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Tests;

public sealed class ModuleManagerTests
{
    private const string EchoNid = "TESTECHO00001";
    private const string ConstNid = "TESTCONST0001";

    private static ModuleManager CreateRegisteredManager()
    {
        var manager = new ModuleManager();
        manager.RegisterFromAssembly(typeof(ModuleManagerTests).Assembly, Generation.Gen5);
        manager.Freeze();
        return manager;
    }

    [Fact]
    public void RegisterFromAssembly_RegistersDecoratedExports()
    {
        var manager = CreateRegisteredManager();

        Assert.True(manager.TryGetExport(EchoNid, out var export));
        Assert.Equal("testEcho", export.Name);
        Assert.True(manager.TryGetExportByName("testEcho", out _));
    }

    [Fact]
    public void Dispatch_KnownNid_InvokesHandlerAndReturnsOk()
    {
        var manager = CreateRegisteredManager();
        var context = new CpuContext(new FakeCpuMemory(), Generation.Gen5)
        {
            [CpuRegister.Rdi] = 0x1111,
        };

        var result = manager.Dispatch(EchoNid, context);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        // testEcho copies Rdi into Rax explicitly.
        Assert.Equal(0x1111UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Dispatch_HandlerReturnValue_WrittenToRaxWhenNotSetExplicitly()
    {
        var manager = CreateRegisteredManager();
        var context = new CpuContext(new FakeCpuMemory(), Generation.Gen5);

        manager.Dispatch(ConstNid, context);

        // testConst returns 7 without touching Rax; ModuleManager must copy it.
        Assert.Equal(7UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Dispatch_UnknownNid_ReturnsNotFound()
    {
        var manager = CreateRegisteredManager();
        var context = new CpuContext(new FakeCpuMemory(), Generation.Gen5);

        var result = manager.Dispatch("DOESNOTEXIST0", context);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND), context[CpuRegister.Rax]);
    }

    [Fact]
    public void Dispatch_GenerationMismatch_ReturnsNotFound()
    {
        var manager = CreateRegisteredManager();
        // Export targets Gen5, but the calling context is Gen4.
        var context = new CpuContext(new FakeCpuMemory(), Generation.Gen4);

        var result = manager.Dispatch(EchoNid, context);

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void RegisterFromAssembly_WhenFrozen_Throws()
    {
        var manager = new ModuleManager();
        manager.Freeze();

        Assert.Throws<InvalidOperationException>(() =>
            manager.RegisterFromAssembly(typeof(ModuleManagerTests).Assembly, Generation.Gen5));
    }

    /// <summary>
    /// Export fixtures discovered by <see cref="ModuleManager.RegisterFromAssembly"/> via reflection.
    /// </summary>
    public static class Fixtures
    {
        [SysAbiExport(Nid = EchoNid, ExportName = "testEcho", Target = Generation.Gen5)]
        public static int Echo(CpuContext context)
        {
            context[CpuRegister.Rax] = context[CpuRegister.Rdi];
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        [SysAbiExport(Nid = ConstNid, ExportName = "testConst", Target = Generation.Gen5)]
        public static int Const()
        {
            return 7;
        }
    }
}
