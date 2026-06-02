// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Tests;

/// <summary>
/// Minimal flat-array <see cref="ICpuMemory"/> for HLE unit tests.
/// Addresses are treated as offsets into a single backing buffer.
/// </summary>
internal sealed class FakeCpuMemory : ICpuMemory
{
    private readonly byte[] _backing;

    public FakeCpuMemory(int size = 0x1_0000)
    {
        _backing = new byte[size];
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        if (virtualAddress + (ulong)destination.Length > (ulong)_backing.Length)
        {
            return false;
        }

        _backing.AsSpan((int)virtualAddress, destination.Length).CopyTo(destination);
        return true;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        if (virtualAddress + (ulong)source.Length > (ulong)_backing.Length)
        {
            return false;
        }

        source.CopyTo(_backing.AsSpan((int)virtualAddress, source.Length));
        return true;
    }
}
