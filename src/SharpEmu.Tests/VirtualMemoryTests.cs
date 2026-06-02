// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Tests;

public sealed class VirtualMemoryTests
{
    private const ulong BaseAddress = 0x1_0000;

    [Fact]
    public void Map_ThenReadBack_ReturnsFileData()
    {
        var memory = new VirtualMemory();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];

        memory.Map(BaseAddress, memorySize: 0x100, fileOffset: 0, payload, ProgramHeaderFlags.Read);

        Span<byte> readBuffer = stackalloc byte[4];
        Assert.True(memory.TryRead(BaseAddress, readBuffer));
        Assert.Equal(payload, readBuffer.ToArray());
    }

    [Fact]
    public void Map_ZeroFillsBytesBeyondFileData()
    {
        var memory = new VirtualMemory();
        byte[] payload = [0x01, 0x02];

        memory.Map(BaseAddress, memorySize: 0x10, fileOffset: 0, payload, ProgramHeaderFlags.Read);

        Span<byte> readBuffer = stackalloc byte[4];
        Assert.True(memory.TryRead(BaseAddress, readBuffer));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x00, 0x00 }, readBuffer.ToArray());
    }

    [Fact]
    public void TryWrite_ThenTryRead_RoundTrips()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, memorySize: 0x100, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);

        byte[] written = [0x11, 0x22, 0x33, 0x44];
        Assert.True(memory.TryWrite(BaseAddress + 0x10, written));

        Span<byte> readBuffer = stackalloc byte[4];
        Assert.True(memory.TryRead(BaseAddress + 0x10, readBuffer));
        Assert.Equal(written, readBuffer.ToArray());
    }

    [Fact]
    public void TryRead_OutsideMappedRegion_ReturnsFalse()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, memorySize: 0x100, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        Span<byte> readBuffer = stackalloc byte[4];
        Assert.False(memory.TryRead(BaseAddress - 1, readBuffer));
        Assert.False(memory.TryRead(BaseAddress + 0x1000, readBuffer));
    }

    [Fact]
    public void TryRead_SpanningPastRegionEnd_ReturnsFalse()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, memorySize: 0x10, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        Span<byte> readBuffer = stackalloc byte[8];
        Assert.False(memory.TryRead(BaseAddress + 0xC, readBuffer));
    }

    [Fact]
    public void Map_OverlappingRegion_Throws()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, memorySize: 0x100, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        Assert.Throws<InvalidOperationException>(() =>
            memory.Map(BaseAddress + 0x80, memorySize: 0x100, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read));
    }

    [Fact]
    public void Map_AdjacentRegions_DoNotOverlap()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, memorySize: 0x100, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        var exception = Record.Exception(() =>
            memory.Map(BaseAddress + 0x100, memorySize: 0x100, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read));
        Assert.Null(exception);
    }

    [Fact]
    public void Map_ZeroSize_Throws()
    {
        var memory = new VirtualMemory();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            memory.Map(BaseAddress, memorySize: 0, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read));
    }

    [Fact]
    public void Map_FileDataLargerThanMemory_Throws()
    {
        var memory = new VirtualMemory();
        byte[] payload = new byte[0x20];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            memory.Map(BaseAddress, memorySize: 0x10, fileOffset: 0, payload, ProgramHeaderFlags.Read));
    }

    [Fact]
    public void Clear_RemovesAllRegions()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, memorySize: 0x100, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);
        memory.Clear();

        Assert.Empty(memory.SnapshotRegions());

        Span<byte> readBuffer = stackalloc byte[4];
        Assert.False(memory.TryRead(BaseAddress, readBuffer));
    }

    [Fact]
    public void SnapshotRegions_ReflectsMappedRegions()
    {
        var memory = new VirtualMemory();
        memory.Map(BaseAddress, memorySize: 0x100, fileOffset: 0x40, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);

        var regions = memory.SnapshotRegions();
        var region = Assert.Single(regions);
        Assert.Equal(BaseAddress, region.VirtualAddress);
        Assert.Equal(0x100UL, region.MemorySize);
        Assert.Equal(0x40UL, region.FileOffset);
        Assert.Equal(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, region.Protection);
    }
}
