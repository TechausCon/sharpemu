// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Tests;

public sealed class ElfHeaderTests
{
    private static byte[] BuildHeader(
        byte elfClass = 2,
        byte endianness = 1,
        byte abiVersion = 2,
        ushort type = 0xFE10,
        ushort machine = 0x3E,
        ulong entryPoint = 0x8000_0010,
        ushort programHeaderCount = 3,
        bool validMagic = true)
    {
        var bytes = new byte[Marshal.SizeOf<ElfHeader>()];
        if (validMagic)
        {
            bytes[0] = 0x7F;
            bytes[1] = (byte)'E';
            bytes[2] = (byte)'L';
            bytes[3] = (byte)'F';
        }

        bytes[4] = elfClass;
        bytes[5] = endianness;
        bytes[6] = 1; // EI_VERSION
        bytes[8] = abiVersion;

        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], machine);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 1); // e_version
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], 0x40); // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(span[54..], 0x38); // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(span[56..], programHeaderCount);
        return bytes;
    }

    [Fact]
    public void ElfHeader_HasExpectedSize()
    {
        Assert.Equal(64, Marshal.SizeOf<ElfHeader>());
    }

    [Fact]
    public void Parse_ValidHeader_ExposesFields()
    {
        var header = MemoryMarshal.Read<ElfHeader>(BuildHeader());

        Assert.True(header.HasElfMagic);
        Assert.True(header.Is64Bit);
        Assert.True(header.IsLittleEndian);
        Assert.Equal(2, header.AbiVersion);
        Assert.Equal(0x3E, header.Machine);
        Assert.Equal(0x8000_0010UL, header.EntryPoint);
        Assert.Equal(0x40UL, header.ProgramHeaderOffset);
        Assert.Equal(3, header.ProgramHeaderCount);
    }

    [Fact]
    public void Parse_MissingMagic_ReportsNoElfMagic()
    {
        var header = MemoryMarshal.Read<ElfHeader>(BuildHeader(validMagic: false));
        Assert.False(header.HasElfMagic);
    }

    [Fact]
    public void Parse_32BitClass_ReportsNot64Bit()
    {
        var header = MemoryMarshal.Read<ElfHeader>(BuildHeader(elfClass: 1));
        Assert.False(header.Is64Bit);
    }

    [Fact]
    public void Parse_BigEndian_ReportsNotLittleEndian()
    {
        var header = MemoryMarshal.Read<ElfHeader>(BuildHeader(endianness: 2));
        Assert.False(header.IsLittleEndian);
    }
}
