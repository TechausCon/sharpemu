// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Linq;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Tests;

public sealed class AerolibTests
{
    [Fact]
    public void EmptyCatalog_ContainsNoSymbols()
    {
        var empty = Aerolib.Empty;

        Assert.False(empty.TryGetByNid("anyNid", out _));
        Assert.False(empty.TryGetByExportName("anyExport", out _));
    }

    [Fact]
    public void Instance_LoadsEmbeddedSymbols()
    {
        var aerolib = Aerolib.Instance;
        Assert.True(aerolib.Count > 0);
    }

    [Fact]
    public void Instance_NidAndExportNameLookups_AreConsistent()
    {
        var aerolib = Aerolib.Instance;
        var sample = aerolib.GetAllNidNames().First();

        Assert.True(aerolib.TryGetByNid(sample.Key, out var byNid));
        Assert.Equal(sample.Value, byNid.ExportName);

        Assert.True(aerolib.TryGetByExportName(sample.Value, out var byName));
        Assert.Equal(sample.Key, byName.Nid);
    }

    [Fact]
    public void Instance_ContainsNid_MatchesTryGet()
    {
        var aerolib = Aerolib.Instance;
        var sampleNid = aerolib.GetAllNidNames().First().Key;

        Assert.True(aerolib.ContainsNid(sampleNid));
        Assert.False(aerolib.ContainsNid("definitely-not-a-real-nid"));
    }
}
