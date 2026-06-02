// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Tests;

public sealed class CpuContextTests
{
    private static CpuContext CreateContext()
    {
        return new CpuContext(new FakeCpuMemory(), Generation.Gen5);
    }

    [Fact]
    public void Register_SetThenGet_RoundTrips()
    {
        var context = CreateContext();
        context[CpuRegister.Rbx] = 0x1234_5678_9ABC_DEF0;
        Assert.Equal(0x1234_5678_9ABC_DEF0UL, context[CpuRegister.Rbx]);
    }

    [Fact]
    public void WritingRax_SetsWriteFlag()
    {
        var context = CreateContext();
        context.ClearRaxWriteFlag();
        Assert.False(context.WasRaxWritten);

        context[CpuRegister.Rax] = 42;
        Assert.True(context.WasRaxWritten);
    }

    [Fact]
    public void WritingNonRaxRegister_DoesNotSetRaxWriteFlag()
    {
        var context = CreateContext();
        context.ClearRaxWriteFlag();

        context[CpuRegister.Rcx] = 1;
        Assert.False(context.WasRaxWritten);
    }

    [Fact]
    public void PushThenPop_RestoresValueAndStackPointer()
    {
        var context = CreateContext();
        context[CpuRegister.Rsp] = 0x1000;

        Assert.True(context.PushUInt64(0xCAFEF00D_DEADBEEF));
        Assert.Equal(0x1000UL - sizeof(ulong), context[CpuRegister.Rsp]);

        Assert.True(context.PopUInt64(out var value));
        Assert.Equal(0xCAFEF00D_DEADBEEFUL, value);
        Assert.Equal(0x1000UL, context[CpuRegister.Rsp]);
    }

    [Fact]
    public void TryWriteUInt64_ThenTryReadUInt64_RoundTrips()
    {
        var context = CreateContext();
        Assert.True(context.TryWriteUInt64(0x40, 0x0102_0304_0506_0708));
        Assert.True(context.TryReadUInt64(0x40, out var value));
        Assert.Equal(0x0102_0304_0506_0708UL, value);
    }

    [Fact]
    public void Xmm_SetThenGet_RoundTrips()
    {
        var context = CreateContext();
        context.SetXmmRegister(3, low: 0xAAAA, high: 0xBBBB);
        context.GetXmmRegister(3, out var low, out var high);
        Assert.Equal(0xAAAAUL, low);
        Assert.Equal(0xBBBBUL, high);
    }

    [Fact]
    public void Ymm_SetThenGet_RoundTripsAllLanes()
    {
        var context = CreateContext();
        context.SetYmmRegister(5, 1, 2, 3, 4);
        context.GetYmmRegister(5, out var lowLow, out var lowHigh, out var highLow, out var highHigh);
        Assert.Equal(1UL, lowLow);
        Assert.Equal(2UL, lowHigh);
        Assert.Equal(3UL, highLow);
        Assert.Equal(4UL, highHigh);
    }

    [Fact]
    public void ClearYmmUpper_LeavesXmmLanesIntact()
    {
        var context = CreateContext();
        context.SetYmmRegister(2, lowLow: 0x11, lowHigh: 0x22, highLow: 0x33, highHigh: 0x44);
        context.ClearYmmUpper(2);

        context.GetYmmRegister(2, out var lowLow, out var lowHigh, out var highLow, out var highHigh);
        Assert.Equal(0x11UL, lowLow);
        Assert.Equal(0x22UL, lowHigh);
        Assert.Equal(0UL, highLow);
        Assert.Equal(0UL, highHigh);
    }
}
