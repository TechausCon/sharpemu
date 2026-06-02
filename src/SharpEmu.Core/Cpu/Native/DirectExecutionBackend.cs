// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Logging;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend : INativeCpuBackend, IGuestThreadScheduler, IDisposable
{
	private static readonly SharpEmuLogger Log = SharpEmuLog.For("SharpEmu.Core.Cpu.Native.DirectExecutionBackend");

	private const int ImportLoopHistoryLength = 2048;

	private const int ImportLoopWideDiversityWindow = 768;

	private readonly struct ImportStubEntry
	{
		public ulong Address { get; }

		public string Nid { get; }

		public ImportStubEntry(ulong address, string nid)
		{
			Address = address;
			Nid = nid;
		}
	}

#pragma warning disable CS0649
	private struct EXCEPTION_POINTERS
	{
		public unsafe EXCEPTION_RECORD* ExceptionRecord;

		public unsafe void* ContextRecord;
	}

	private struct EXCEPTION_RECORD
	{
		public uint ExceptionCode;

		public uint ExceptionFlags;

		public unsafe EXCEPTION_RECORD* ExceptionRecord;

		public unsafe void* ExceptionAddress;

		public uint NumberParameters;

		public unsafe fixed ulong ExceptionInformation[15];
	}
#pragma warning restore CS0649

	private delegate int ExceptionHandlerDelegate(void* exceptionInfo);

	private struct MEMORY_BASIC_INFORMATION64
	{
		public ulong BaseAddress;

		public ulong AllocationBase;

		public uint AllocationProtect;

		public uint __alignment1;

		public ulong RegionSize;

		public uint State;

		public uint Protect;

		public uint Type;

		public uint __alignment2;
	}

	private const ulong SYSTEM_RESERVED = 34359738368uL;

	private const ulong CODE_BASE_OFFSET = 4294967296uL;

	private const ulong CODE_BASE_INCR = 268435456uL;

	private const ulong GuestImageScanStart = 34359738368uL;

	private const ulong GuestImageScanEnd = 36507222016uL;

	private const ulong GuestThreadStackBaseAddress = 0x7FFF_E000_0000UL;

	private const ulong GuestThreadTlsBaseAddress = 0x7FFE_0000_0000UL;

	private const ulong GuestThreadStackSize = 0x0020_0000UL;

	private const ulong GuestThreadTlsSize = 0x0001_0000UL;

	private const ulong GuestThreadRegionStride = 0x0100_0000UL;

	private const uint PAGE_EXECUTE_READWRITE = 64u;

	private const uint PAGE_READWRITE = 4u;

	private const uint PAGE_EXECUTE_READ = 32u;

	private const int TlsHandlerRegionSize = 16384;

	private const ulong TlsModuleAllocStart = 140726751354880uL;

	private const ulong TlsModuleAllocStride = 65536uL;

	private readonly IModuleManager _moduleManager;

	private nint _tlsHandlerAddress;

	private nint _tlsBaseAddress;

	private nint _ownedTlsBaseAddress;

	private bool _ownsTlsBaseAddress;

	private int _tlsPatchStubOffset;

	private nint _unresolvedReturnStub;

	private nint _rawExceptionHandler;

	private nint _exceptionHandler;

	private nint _lowIndexedTableScratch;

	private nint _stackGuardCompareScratch;

	private nint _nullObjectStoreScratch;

	private readonly Dictionary<uint, nint> _tlsModuleBases = new Dictionary<uint, nint>();

	private ulong _entryPoint;

	private CpuContext? _cpuContext;

	private ImportStubEntry[] _importEntries = Array.Empty<ImportStubEntry>();

	private readonly List<nint> _importHandlerTrampolines = new List<nint>();

	private long _importDispatchCount;

	private KeyValuePair<string, ulong>[] _runtimeSymbolsByAddress = Array.Empty<KeyValuePair<string, ulong>>();

	private readonly Dictionary<string, ulong> _runtimeSymbolsByName = new Dictionary<string, ulong>(StringComparer.Ordinal);

	private readonly string[] _recentImportTrace = new string[64];

	private int _recentImportTraceCount;

	private int _recentImportTraceWriteIndex;

	private readonly string[] _distinctImportNidHistory = new string[128];

	private int _distinctImportNidHistoryCount;

	private int _distinctImportNidHistoryWriteIndex;

	private string _lastDistinctImportNid = string.Empty;

	private int _consecutiveStrlenImports;

	private bool _strlenPreludeLogged;

	private bool _logStrlenImports;

	private readonly HashSet<ulong> _fallbackTrapStubs = new HashSet<ulong>();

	private readonly HashSet<ulong> _stackChkBypassSites = new HashSet<ulong>();

	private ulong _returnFallbackTarget;

	private static int _rawSentinelRecoveries;

	private int _lastReportedRawSentinelRecoveries;

	private nint _hostRspSlotStorage;

	private ulong _entryReturnSentinelRip;

	private readonly ulong[] _importLoopSignatures = new ulong[ImportLoopHistoryLength];

	private readonly ulong[] _importLoopNidHashes = new ulong[ImportLoopHistoryLength];

	private readonly ulong[] _importLoopReturnRips = new ulong[ImportLoopHistoryLength];

	private int _importLoopSignatureCount;

	private int _importLoopSignatureWriteIndex;

	private int _importLoopPatternHits;

	private readonly Dictionary<string, ulong> _importNidHashCache = new Dictionary<string, ulong>(StringComparer.Ordinal);

	private enum GuestThreadRunState
	{
		Ready,
		Running,
		Blocked,
		Exited,
		Faulted,
	}

	private enum GuestNativeCallExitReason
	{
		Returned,
		Blocked,
		ForcedExit,
		Exception,
	}

	private sealed class GuestThreadState
	{
		public ulong ThreadHandle { get; init; }

		public ulong EntryPoint { get; init; }

		public ulong Argument { get; init; }

		public string Name { get; init; } = string.Empty;

		public CpuContext Context { get; init; } = null!;

		public GuestThreadRunState State { get; set; }

		public string? BlockReason { get; set; }
	}

	private readonly object _guestThreadGate = new object();

	private readonly Queue<GuestThreadState> _readyGuestThreads = new Queue<GuestThreadState>();

	private readonly Dictionary<ulong, GuestThreadState> _guestThreads = new Dictionary<ulong, GuestThreadState>();

	private int _guestThreadPumpDepth;

	private bool _guestThreadYieldRequested;

	private string? _guestThreadYieldReason;

	private bool _forcedGuestExit;

	private ulong _lastAvTraceRip;

	private ulong _lastAvTraceType;

	private ulong _lastAvTraceTarget;

	private int _lastAvTraceRepeatCount;

	private long _lastProgressTimestamp;

	private int _stallWatchdogTriggered;

	private volatile bool _stallWatchdogStop;

	private Thread? _stallWatchdogThread;

	private GCHandle _selfHandle;

	private nint _selfHandlePtr;

	private const int MinTlsPatchInstructionBytes = 9;

	private delegate ulong ImportGatewayDelegate(nint backendHandle, int importIndex, nint argPackPtr);
	private delegate int RawExceptionHandlerDelegate(void* exceptionInfo);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int NativeEntryDelegate();

	private static readonly ImportGatewayDelegate ImportGatewayDelegateInstance = ImportDispatchGatewayManaged;
	private static readonly RawExceptionHandlerDelegate RawVectoredHandlerDelegateInstance = RawVectoredHandlerManaged;
	private static readonly RawExceptionHandlerDelegate RawUnhandledFilterDelegateInstance = RawUnhandledFilterManaged;

	private static readonly nint ImportGatewayPtr =
		Marshal.GetFunctionPointerForDelegate(ImportGatewayDelegateInstance);

	private static readonly nint RawVectoredHandlerPtrManaged =
		Marshal.GetFunctionPointerForDelegate(RawVectoredHandlerDelegateInstance);

	private static readonly nint RawUnhandledFilterPtrManaged =
		Marshal.GetFunctionPointerForDelegate(RawUnhandledFilterDelegateInstance);

	private const int CTX_MXCSR = 52;

	private const int CTX_RAX = 120;

	private const int CTX_RCX = 128;

	private const int CTX_RDX = 136;

	private const int CTX_RBX = 144;

	private const int CTX_RSP = 152;

	private const int CTX_RBP = 160;

	private const int CTX_RSI = 168;

	private const int CTX_RDI = 176;

	private const int CTX_R8 = 184;

	private const int CTX_R9 = 192;

	private const int CTX_R10 = 200;

	private const int CTX_R11 = 208;

	private const int CTX_R12 = 216;

	private const int CTX_R13 = 224;

	private const int CTX_R14 = 232;

	private const int CTX_R15 = 240;

	private const int CTX_RIP = 248;

	private ExceptionHandlerDelegate? _handlerDelegate;

	private GCHandle _handlerHandle;

	private ExceptionHandlerDelegate? _unhandledFilterDelegate;

	private GCHandle _unhandledFilterHandle;

	[ThreadStatic]
	private static int _vectoredHandlerDepth;

	private const uint MEM_COMMIT = 4096u;

	private const uint MEM_RESERVE = 8192u;

	private const uint MEM_FREE = 65536u;

	private const uint MEM_RELEASE = 32768u;

	private const uint PAGE_EXECUTE = 16u;

	private const uint PAGE_EXECUTE_WRITECOPY = 128u;

	private const uint PAGE_GUARD = 256u;

	private const uint PAGE_NOACCESS = 1u;

	public string BackendName => "native-backend";

	public string? LastError { get; private set; }

	private unsafe static ulong ReadCtxU64(void* contextRecord, int offset)
	{
		return *(ulong*)((byte*)contextRecord + offset);
	}

	private unsafe static void WriteCtxU64(void* contextRecord, int offset, ulong value)
	{
		*(ulong*)((byte*)contextRecord + offset) = value;
	}

	private unsafe static uint ReadCtxU32(void* contextRecord, int offset)
	{
		return *(uint*)((byte*)contextRecord + offset);
	}

	private unsafe static void WriteCtxU32(void* contextRecord, int offset, uint value)
	{
		*(uint*)((byte*)contextRecord + offset) = value;
	}

	public unsafe DirectExecutionBackend(IModuleManager moduleManager)
	{
		_moduleManager = moduleManager ?? throw new ArgumentNullException("moduleManager");
		_selfHandle = GCHandle.Alloc(this);
		_selfHandlePtr = GCHandle.ToIntPtr(_selfHandle);
		_tlsBaseAddress = (nint)VirtualAlloc(null, 4096u, 12288u, 4u);
		if (_tlsBaseAddress == 0)
		{
			throw new OutOfMemoryException("Failed to allocate TLS base");
		}
		_ownedTlsBaseAddress = _tlsBaseAddress;
		_ownsTlsBaseAddress = true;
		SeedTlsLayout(_tlsBaseAddress);
		_hostRspSlotStorage = (nint)VirtualAlloc(null, 4096u, 12288u, 4u);
		if (_hostRspSlotStorage == 0)
		{
			throw new OutOfMemoryException("Failed to allocate host stack slot storage");
		}
		_unresolvedReturnStub = CreateUnresolvedReturnStub();
		SetupExceptionHandler();
	}

	public bool TryExecute(CpuContext context, ulong entryPoint, Generation generation, IReadOnlyDictionary<ulong, string> importStubs, IReadOnlyDictionary<string, ulong> runtimeSymbols, CpuExecutionOptions executionOptions, out OrbisGen2Result result)
	{
		Log.Debug("[LOADER][INFO] === Execute START ===");
		Log.Debug($"[LOADER][INFO] EntryPoint: 0x{entryPoint:X16}, ImportStubs: {importStubs.Count}");
		Log.Debug($"[LOADER][INFO] RuntimeSymbols: {runtimeSymbols.Count}");
		Log.Warn(_moduleManager.TryGetExport("QrZZdJ8XsX0", out ExportedFunction export) ? ("[LOADER][INFO] ExportCheck fputs: " + export.LibraryName + ":" + export.Name) : "[LOADER][INFO] ExportCheck fputs: MISSING");
		Log.Warn(_moduleManager.TryGetExport("L-Q3LEjIbgA", out ExportedFunction export2) ? ("[LOADER][INFO] ExportCheck map_direct: " + export2.LibraryName + ":" + export2.Name) : "[LOADER][INFO] ExportCheck map_direct: MISSING");
		_entryPoint = entryPoint;
		_cpuContext = context;
		_returnFallbackTarget = context[CpuRegister.Rsi];
		result = OrbisGen2Result.ORBIS_GEN2_OK;
		LastError = null;
		InitializeRuntimeSymbolIndex(runtimeSymbols);
		_recentImportTraceCount = 0;
		_recentImportTraceWriteIndex = 0;
		_distinctImportNidHistoryCount = 0;
		_distinctImportNidHistoryWriteIndex = 0;
		_lastDistinctImportNid = string.Empty;
		_consecutiveStrlenImports = 0;
		_strlenPreludeLogged = false;
		_logStrlenImports = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_STRLEN"), "1", StringComparison.Ordinal);
		_entryReturnSentinelRip = 0uL;
		_forcedGuestExit = false;
		_importLoopSignatureCount = 0;
		_importLoopSignatureWriteIndex = 0;
		_importLoopPatternHits = 0;
		_importNidHashCache.Clear();
		ClearGuestThreads();
		_stallWatchdogTriggered = 0;
		_stallWatchdogStop = false;
		MarkExecutionProgress();
		BindTlsBase(context);
		var previousGuestThreadScheduler = GuestThreadExecution.Scheduler;
		GuestThreadExecution.Scheduler = this;
		try
		{
			if (!SetupImportStubs(importStubs))
			{
				if (string.IsNullOrEmpty(LastError))
				{
					LastError = "SetupImportStubs failed";
				}
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			CreateTlsHandler();
			PatchTlsPatterns();
			return ExecuteEntry(context, entryPoint, out result);
		}
		catch (Exception ex)
		{
			LastError = "Exception in TryExecute: " + ex.GetType().Name + ": " + ex.Message;
			Log.Error("[LOADER][ERROR] " + LastError, ex);
			Log.Error("[LOADER][ERROR] Stack trace: " + ex.StackTrace, ex);
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
			return false;
		}
		finally
		{
			GuestThreadExecution.Scheduler = previousGuestThreadScheduler;
			Log.Error("[LOADER][INFO] === Execute END (LastError: " + (LastError ?? "null") + ") ===");
		}
	}

	private bool SetupImportStubs(IReadOnlyDictionary<ulong, string> importStubs)
	{
		Log.Debug($"[LOADER][INFO] Setting up {importStubs.Count} import stubs...");
		ClearImportHandlerTrampolines();
		_importEntries = new ImportStubEntry[importStubs.Count];
		_fallbackTrapStubs.Clear();
		HashSet<ulong> hashSet = new HashSet<ulong>(importStubs.Keys);
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		foreach (var (num4, text2) in importStubs)
		{
			_importEntries[num] = new ImportStubEntry(num4, text2);
			if ((num4 >= 34393242112L && num4 <= 34393242624L) || (num4 >= 34393258496L && num4 <= 34393259008L))
			{
				if (_moduleManager.TryGetExport(text2, out ExportedFunction export))
				{
					Log.Debug($"[LOADER][INFO] ImportStubMap: 0x{num4:X16} -> {export.LibraryName}:{export.Name} ({text2})");
				}
				else
				{
					Log.Debug($"[LOADER][INFO] ImportStubMap: 0x{num4:X16} -> {text2}");
				}
			}
			if (TryResolveDirectImportTarget(text2, out var targetAddress, out var resolvedSymbol) && !hashSet.Contains(targetAddress))
			{
				Log.Debug($"[LOADER][DEBUG] SetupImportStubs: Direct bridge for {text2} -> 0x{targetAddress:X16}");
				if (!PatchImportStub((nint)(long)num4, (nint)(long)targetAddress))
				{
					LastError = $"Failed to patch direct import stub at 0x{num4:X16}";
					return false;
				}
				num3++;
				num2++;
				if (num3 <= 48)
				{
					Log.Debug(
						$"[LOADER][INFO] LLE redirect: 0x{num4:X16} {text2} -> {resolvedSymbol}@0x{targetAddress:X16}");
				}
				num++;
				continue;
			}
			nint num5 = CreateImportHandlerTrampoline(num);
			if (num5 == 0)
			{
				LastError = "Failed to create import trampoline for NID " + text2;
				return false;
			}
			Log.Debug($"[LOADER][DEBUG] SetupImportStubs: Trampoline for {text2} -> 0x{num5:X16}");
			if (!PatchImportStub((nint)num4, num5))
			{
				LastError = $"Failed to patch import stub at 0x{num4:X16}";
				return false;
			}
			num2++;
			num++;
		}
		Log.Debug($"[LOADER][INFO] Setup {num2}/{importStubs.Count} import stubs (direct bridge, lle_redirects={num3})");
		int num6 = PatchFallbackTrapStubs(hashSet);
		if (num6 > 0)
		{
			Log.Warn($"[LOADER][WARNING] Applied {num6} fallback PLT trap stubs in 0x801FF7A00..0x801FF7C00");
		}
		return num2 == importStubs.Count;
	}

	private bool TryResolveDirectImportTarget(string nid, out ulong targetAddress, out string resolvedSymbol)
	{
		targetAddress = 0uL;
		resolvedSymbol = string.Empty;
		if (string.IsNullOrWhiteSpace(nid) || string.Equals(nid, RuntimeStubNids.KernelDynlibDlsym, StringComparison.Ordinal))
		{
			return false;
		}
		if (IsHlePreferredNid(nid))
		{
			return false;
		}

		if (_moduleManager.TryGetExport(nid, out ExportedFunction export))
		{
			if (IsKernelLibrary(export.LibraryName))
			{
				Log.Debug($"[LOADER][DEBUG] TryResolveDirectImportTarget: {nid} ({export.LibraryName}:{export.Name}) -> HLE (kernel library)");
				return false;
			}
			if (!IsLibcLibrary(export.LibraryName) || !PreferLleForLibcExport(export.Name))
			{
				return false;
			}
			if (TryResolveRuntimeSymbolAddress(nid, out var value2) && IsDirectImportTargetUsable(value2))
			{
				targetAddress = value2;
				resolvedSymbol = nid;
				return true;
			}
			foreach (string item in EnumerateRuntimeSymbolCandidates(export.Name))
			{
				if (TryResolveRuntimeSymbolAddress(item, out value2) && IsDirectImportTargetUsable(value2))
				{
					targetAddress = value2;
					resolvedSymbol = item;
					return true;
				}
			}
			return false;
		}

		Log.Debug($"[LOADER][DEBUG] TryResolveDirectImportTarget: {nid} not in HLE table, checking runtime symbols...");

		if (TryResolveRuntimeSymbolAddress(nid, out var directValue) && IsDirectImportTargetUsable(directValue))
		{
			targetAddress = directValue;
			resolvedSymbol = nid;
			Log.Debug($"[LOADER][DEBUG] TryResolveDirectImportTarget: {nid} -> runtime symbol 0x{targetAddress:X16}");
			return true;
		}

		if (Aerolib.Instance.TryGetByNid(nid, out var symbolByNid))
		{
			if (!PreferLleForLibcExport(symbolByNid.ExportName))
			{
				return false;
			}
			foreach (string item in EnumerateRuntimeSymbolCandidates(symbolByNid.ExportName))
			{
				if (TryResolveRuntimeSymbolAddress(item, out var value) && IsDirectImportTargetUsable(value))
				{
					targetAddress = value;
					resolvedSymbol = item;
					return true;
				}
			}
		}
		return false;
	}

	private static bool IsHlePreferredNid(string nid)
	{
		return string.Equals(nid, "QrZZdJ8XsX0", StringComparison.Ordinal);
	}

	private static bool IsLibcLibrary(string libraryName)
	{
		return !string.IsNullOrWhiteSpace(libraryName) && libraryName.IndexOf("libc", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool IsKernelLibrary(string libraryName)
	{
		if (string.IsNullOrWhiteSpace(libraryName))
		{
			return false;
		}
		return libraryName.Equals("libKernel", StringComparison.OrdinalIgnoreCase) ||
			   libraryName.Equals("libKernelExt", StringComparison.OrdinalIgnoreCase) ||
			   libraryName.IndexOf("Kernel", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool PreferLleForLibcExport(string exportName)
	{
		if (string.IsNullOrWhiteSpace(exportName))
		{
			return false;
		}
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_LLE_LIBC"), "1", StringComparison.Ordinal))
		{
			return false;
		}
		var value = Environment.GetEnvironmentVariable("SHARPEMU_LLE_LIBC_SAFE_ONLY");
		if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LLE_LIBC_ALL"), "1", StringComparison.Ordinal))
		{
			return true;
		}
		if (string.Equals(value, "0", StringComparison.Ordinal))
		{
			return true;
		}
		if (string.Equals(value, "1", StringComparison.Ordinal))
		{
			return IsSafeLleLibcExport(exportName);
		}
		return IsSafeLleLibcExport(exportName);
	}

	private static bool IsSafeLleLibcExport(string exportName)
	{
		return exportName switch
		{
			"memcpy" or
			"memmove" or
			"memset" or
			"memcmp" => true,
			_ => false,
		};
	}

	private static IEnumerable<string> EnumerateRuntimeSymbolCandidates(string exportName)
	{
		if (string.IsNullOrWhiteSpace(exportName))
		{
			yield break;
		}
		yield return exportName;
		if (exportName.StartsWith("_", StringComparison.Ordinal))
		{
			if (exportName.Length > 1)
			{
				yield return exportName[1..];
			}
			yield break;
		}
		yield return "_" + exportName;
	}

	private static bool IsDirectImportTargetUsable(ulong address)
	{
		return address >= 65536 && !IsUnresolvedSentinel(address);
	}

	private unsafe void BindTlsBase(CpuContext context)
	{
		nint num = (nint)((context.FsBase != 0L) ? context.FsBase : context.GsBase);
		if (num == 0)
		{
			num = _tlsBaseAddress;
		}
		if (num != _tlsBaseAddress)
		{
			_tlsBaseAddress = num;
			_ownsTlsBaseAddress = _tlsBaseAddress == _ownedTlsBaseAddress;
		}
		if (_tlsBaseAddress != 0)
		{
			context.FsBase = (ulong)_tlsBaseAddress;
			context.GsBase = (ulong)_tlsBaseAddress;
			SeedTlsLayout(_tlsBaseAddress);
			UpdateTlsHandlerBase(_tlsBaseAddress);
		}
	}

	private unsafe static void SeedTlsLayout(nint tlsBase)
	{
		ulong num = (ulong)tlsBase;
		*(ulong*)tlsBase = num;
		*(ulong*)(tlsBase + 16) = num;
		*(long*)(tlsBase + 40) = -4548986510476657986L;
		*(ulong*)(tlsBase + 96) = num;
	}

	private unsafe void UpdateTlsHandlerBase(nint tlsBase)
	{
		if (_tlsHandlerAddress == 0)
		{
			return;
		}

		uint oldProtect = default;
		if (!VirtualProtect((void*)_tlsHandlerAddress, 16u, 64u, &oldProtect))
		{
			return;
		}

		try
		{
			*(long*)((byte*)_tlsHandlerAddress + 2) = tlsBase;
		}
		finally
		{
			VirtualProtect((void*)_tlsHandlerAddress, 16u, oldProtect, &oldProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)_tlsHandlerAddress, 16u);
		}
	}

	private unsafe nint CreateImportHandlerTrampoline(int importIndex)
	{
		void* ptr = VirtualAlloc(null, 192u, 12288u, 64u);
		if (ptr == null)
		{
			return 0;
		}
		_importHandlerTrampolines.Add((nint)ptr);
		try
		{
			byte* ptr2 = (byte*)ptr;
			int num = 0;
			ptr2[num++] = 65;
			ptr2[num++] = 87;
			ptr2[num++] = 65;
			ptr2[num++] = 86;
			ptr2[num++] = 65;
			ptr2[num++] = 85;
			ptr2[num++] = 65;
			ptr2[num++] = 84;
			ptr2[num++] = 85;
			ptr2[num++] = 83;
			ptr2[num++] = 65;
			ptr2[num++] = 81;
			ptr2[num++] = 65;
			ptr2[num++] = 80;
			ptr2[num++] = 81;
			ptr2[num++] = 82;
			ptr2[num++] = 86;
			ptr2[num++] = 87;
			ptr2[num++] = 72;
			ptr2[num++] = 185;
			*(long*)(ptr2 + num) = _selfHandlePtr;
			num += 8;
			ptr2[num++] = 186;
			*(int*)(ptr2 + num) = importIndex;
			num += 4;
			ptr2[num++] = 73;
			ptr2[num++] = 137;
			ptr2[num++] = 224;
			ptr2[num++] = 73;
			ptr2[num++] = 137;
			ptr2[num++] = 228;
			ptr2[num++] = 73;
			ptr2[num++] = 187;
			*(long*)(ptr2 + num) = _hostRspSlotStorage;
			num += 8;
			ptr2[num++] = 77;
			ptr2[num++] = 139;
			ptr2[num++] = 27;
			ptr2[num++] = 73;
			ptr2[num++] = 139;
			ptr2[num++] = 35;
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 236;
			ptr2[num++] = 40;
			ptr2[num++] = 72;
			ptr2[num++] = 184;
			*(long*)(ptr2 + num) = ImportGatewayPtr;
			num += 8;
			ptr2[num++] = byte.MaxValue;
			ptr2[num++] = 208;
			ptr2[num++] = 72;
			ptr2[num++] = 131;
			ptr2[num++] = 196;
			ptr2[num++] = 40;
			ptr2[num++] = 76;
			ptr2[num++] = 137;
			ptr2[num++] = 228;
			ptr2[num++] = 95;
			ptr2[num++] = 94;
			ptr2[num++] = 90;
			ptr2[num++] = 89;
			ptr2[num++] = 65;
			ptr2[num++] = 88;
			ptr2[num++] = 65;
			ptr2[num++] = 89;
			ptr2[num++] = 91;
			ptr2[num++] = 93;
			ptr2[num++] = 65;
			ptr2[num++] = 92;
			ptr2[num++] = 65;
			ptr2[num++] = 93;
			ptr2[num++] = 65;
			ptr2[num++] = 94;
			ptr2[num++] = 65;
			ptr2[num++] = 95;
			ptr2[num++] = 195;
			uint num2 = default(uint);
			VirtualProtect(ptr, 192u, 32u, &num2);
			FlushInstructionCache(GetCurrentProcess(), ptr, 192u);
			return (nint)ptr;
		}
		catch
		{
			return 0;
		}
	}

	private unsafe bool PatchImportStub(nint address, nint trampoline)
	{
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)address, 16u, 64u, &flNewProtect))
		{
			Log.Error($"[LOADER][ERROR] VirtualProtect failed for import stub at 0x{address:X16}");
			return false;
		}
		try
		{
			*(sbyte*)address = 72;
			*(sbyte*)(address + 1) = -72;
			*(long*)(address + 2) = trampoline;
			*(sbyte*)(address + 10) = -1;
			*(sbyte*)(address + 11) = -32;
			*(sbyte*)(address + 12) = -112;
			*(sbyte*)(address + 13) = -112;
			*(sbyte*)(address + 14) = -112;
			*(sbyte*)(address + 15) = -112;
			return true;
		}
		finally
		{
			VirtualProtect((void*)address, 16u, flNewProtect, &flNewProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, 16u);
		}
	}

	private int PatchFallbackTrapStubs(HashSet<ulong> mappedImportStubs)
	{
		int num = 0;
		for (ulong num2 = 34393258496uL; num2 <= 34393259008L; num2 += 16)
		{
			if (!mappedImportStubs.Contains(num2) && PatchFallbackTrapStub(num2))
			{
				_fallbackTrapStubs.Add(num2);
				num++;
			}
		}
		return num;
	}

	private unsafe static bool PatchFallbackTrapStub(ulong address)
	{
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)address, 16u, 64u, &flNewProtect))
		{
			return false;
		}
		try
		{
			byte* ptr = (byte*)address;
			*ptr = 204;
			ptr[1] = 195;
			for (int i = 3; i < 16; i++)
			{
				ptr[i] = 144;
			}
			return true;
		}
		finally
		{
			VirtualProtect((void*)address, 16u, flNewProtect, &flNewProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, 16u);
		}
	}

	private unsafe void ClearImportHandlerTrampolines()
	{
		foreach (nint importHandlerTrampoline in _importHandlerTrampolines)
		{
			if (importHandlerTrampoline != 0)
			{
				VirtualFree((void*)importHandlerTrampoline, 0u, 32768u);
			}
		}
		_importHandlerTrampolines.Clear();
	}

	private unsafe void CreateTlsHandler()
	{
		_tlsHandlerAddress = (nint)TryAllocateNearEntry(TlsHandlerRegionSize);
		if (_tlsHandlerAddress == 0)
		{
			_tlsHandlerAddress = (nint)VirtualAlloc(null, TlsHandlerRegionSize, 12288u, 64u);
		}
		if (_tlsHandlerAddress == 0)
		{
			throw new OutOfMemoryException("Failed to allocate TLS handler");
		}
		byte* tlsHandlerAddress = (byte*)_tlsHandlerAddress;
		int num = 0;
		tlsHandlerAddress[num++] = 72;
		tlsHandlerAddress[num++] = 184;
		*(long*)(tlsHandlerAddress + num) = _tlsBaseAddress;
		num += 8;
		tlsHandlerAddress[num++] = 195;
		_tlsPatchStubOffset = (num + 15) & ~15;
		uint num2 = default(uint);
		VirtualProtect((void*)_tlsHandlerAddress, TlsHandlerRegionSize, 32u, &num2);
		FlushInstructionCache(GetCurrentProcess(), (void*)_tlsHandlerAddress, TlsHandlerRegionSize);
		Log.Debug($"[LOADER][INFO] TLS handler at 0x{_tlsHandlerAddress:X16}");
	}

	private unsafe static nint CreateUnresolvedReturnStub()
	{
		void* ptr = VirtualAlloc(null, 4096u, 12288u, 64u);
		if (ptr == null)
		{
			return 0;
		}
		byte* ptr2 = (byte*)ptr;
		*ptr2 = 49;
		ptr2[1] = 192;
		ptr2[2] = 195;
		for (int i = 3; i < 16; i++)
		{
			ptr2[i] = 144;
		}
		uint num = default(uint);
		VirtualProtect(ptr, 4096u, 32u, &num);
		FlushInstructionCache(GetCurrentProcess(), ptr, 16u);
		return (nint)ptr;
	}

	private unsafe void* TryAllocateNearEntry(nuint size)
	{
		ulong entryPoint = _entryPoint;
		ulong baseAddress = entryPoint & 0xFFFFFFFFFFFF0000uL;
		for (long num = 0L; num <= 1879048192; num += 16777216)
		{
			if (TryAllocAt(baseAddress, num, size, out var memory))
			{
				return memory;
			}
			if (num != 0L && TryAllocAt(baseAddress, -num, size, out memory))
			{
				return memory;
			}
		}
		return null;
	}

	private unsafe static bool TryAllocAt(ulong baseAddress, long signedDelta, nuint size, out void* memory)
	{
		memory = null;
		ulong num;
		if (signedDelta >= 0)
		{
			if (baseAddress > (ulong)(-1 - signedDelta))
			{
				return false;
			}
			num = baseAddress + (ulong)signedDelta;
		}
		else
		{
			ulong num2 = (ulong)(-signedDelta);
			if (baseAddress < num2)
			{
				return false;
			}
			num = baseAddress - num2;
		}
		void* ptr = VirtualAlloc((void*)num, size, 12288u, 64u);
		if (ptr == null)
		{
			return false;
		}
		memory = ptr;
		return true;
	}

	private unsafe void PatchTlsPatterns()
	{
		const ulong MaxScanBytes = 33554432uL;
		ulong num = _entryPoint;
		ulong num2 = num + MaxScanBytes;
		int num3 = 0;
		int num4 = 0;
		int num9 = 0;
		while (num < num2)
		{
			if (VirtualQuery((void*)num, out var lpBuffer, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0 || lpBuffer.RegionSize == 0)
			{
				num += 4096uL;
				continue;
			}
			ulong num5 = Math.Max(num, lpBuffer.BaseAddress);
			ulong num6 = lpBuffer.BaseAddress + lpBuffer.RegionSize;
			if (num6 > num2)
			{
				num6 = num2;
			}
			uint num7 = lpBuffer.Protect & 0xFF;
			bool flag = lpBuffer.State == 4096 && (lpBuffer.Protect & PAGE_GUARD) == 0 && num7 != PAGE_NOACCESS;
			bool flag2 = num7 == PAGE_EXECUTE || num7 == 32 || num7 == 64 || num7 == PAGE_EXECUTE_WRITECOPY;
			if (flag && flag2 && num6 > num5 + MinTlsPatchInstructionBytes)
			{
				byte* ptr = (byte*)num5;
				int scanBytes = (int)(num6 - num5);
				for (int i = 0; i <= scanBytes - MinTlsPatchInstructionBytes; i++)
				{
					nint address = (nint)(ptr + i);
					int remainingBytes = scanBytes - i;
					if (TryPatchTlsLoadInstruction(address, ptr + i, remainingBytes))
					{
						num3++;
					}
					else if (remainingBytes >= 12 && TryPatchTlsImmediateStoreInstruction(address, ptr + i))
					{
						num9++;
					}
					else if (TryPatchStackCanaryInstruction(address, ptr + i))
					{
						num4++;
					}
				}
			}
			num = num6 > num ? num6 : num + 4096uL;
		}
		Log.Debug($"[LOADER][INFO] Patched {num3} TLS loads, {num9} TLS stores, {num4} stack-canary accesses");
	}

	private unsafe bool IsPatternMatch(byte* ptr, byte[] pattern)
	{
		for (int i = 0; i < pattern.Length; i++)
		{
			if (ptr[i] != pattern[i])
			{
				return false;
			}
		}
		return true;
	}

	private unsafe bool TryPatchStackCanaryInstruction(nint address, byte* source)
	{
		if (*source != 100)
		{
			return false;
		}
		byte b = 0;
		int num = 1;
		int num2 = 8;
		if (source[1] >= 64 && source[1] <= 79)
		{
			b = source[1];
			num = 2;
			num2 = 9;
		}
		byte b2 = source[num];
		if (b2 != 139 && b2 != 51)
		{
			return false;
		}
		byte b3 = source[num + 1];
		byte b4 = source[num + 2];
		if (b3 >> 6 != 0 || (b3 & 7) != 4 || b4 != 37)
		{
			return false;
		}
		int num3 = *(int*)(source + num + 3);
		if (num3 != 40)
		{
			return false;
		}
		int num4 = ((b3 >> 3) & 7) | (((b & 4) != 0) ? 8 : 0);
		bool flag = (b & 8) != 0;
		int num5 = 64;
		if (flag)
		{
			num5 |= 8;
		}
		if (num4 >= 8)
		{
			num5 |= 5;
		}
		byte b5 = (byte)(0xC0 | ((num4 & 7) << 3) | (num4 & 7));
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)address, (nuint)num2, 64u, &flNewProtect))
		{
			return false;
		}
		try
		{
			*(byte*)address = (byte)num5;
			*(sbyte*)(address + 1) = 49;
			*(byte*)(address + 2) = b5;
			for (int i = 3; i < num2; i++)
			{
				*(sbyte*)(address + i) = -112;
			}
		}
		finally
		{
			VirtualProtect((void*)address, (nuint)num2, flNewProtect, &flNewProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, (nuint)num2);
		}
		return true;
	}

	private unsafe bool TryPatchTlsLoadInstruction(nint address, byte* source, int availableLength)
	{
		if (availableLength < MinTlsPatchInstructionBytes)
		{
			return false;
		}

		var offset = 0;
		while (offset < availableLength && source[offset] == 0x66)
		{
			offset++;
		}

		if (offset >= availableLength || source[offset] != 0x64)
		{
			return false;
		}

		offset++;
		if (offset >= availableLength)
		{
			return false;
		}

		var rex = (byte)0;
		if (source[offset] >= 0x40 && source[offset] <= 0x4F)
		{
			rex = source[offset];
			offset++;
		}

		if (offset + 7 > availableLength || source[offset] != 0x8B)
		{
			return false;
		}

		var modRm = source[offset + 1];
		var sib = source[offset + 2];
		if ((modRm >> 6) != 0 || (modRm & 7) != 4 || sib != 0x25)
		{
			return false;
		}

		var displacement = *(int*)(source + offset + 3);
		if (displacement != 0)
		{
			return false;
		}

		var destinationRegister = ((modRm >> 3) & 7) | (((rex & 4) != 0) ? 8 : 0);
		var instructionLength = offset + 7;
		if (instructionLength < MinTlsPatchInstructionBytes)
		{
			return false;
		}

		return PatchTlsLoadInstruction(address, instructionLength, destinationRegister);
	}

	private unsafe bool PatchTlsLoadInstruction(nint address, int instructionLength, int destinationRegister)
	{
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)address, (nuint)instructionLength, 64u, &flNewProtect))
		{
			return false;
		}
		try
		{
			*(sbyte*)address = -24;
			long num = _tlsHandlerAddress;
			long num2 = address + 5;
			long num3 = num - num2;
			if (num3 < int.MinValue || num3 > int.MaxValue)
			{
				Log.Warn($"[LOADER][WARNING] TLS patch out of rel32 range at 0x{address:X16}");
				return false;
			}

			*(int*)(address + 1) = (int)num3;
			var offset = 5;
			if (destinationRegister != 0)
			{
				*(byte*)(address + offset++) = (byte)(0x48 | (destinationRegister >= 8 ? 1 : 0));
				*(byte*)(address + offset++) = 0x89;
				*(byte*)(address + offset++) = (byte)(0xC0 | (destinationRegister & 7));
			}

			while (offset < instructionLength)
			{
				*(byte*)(address + offset++) = 0x90;
			}

			return true;
		}
		finally
		{
			VirtualProtect((void*)address, (nuint)instructionLength, flNewProtect, &flNewProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, (nuint)instructionLength);
		}
	}

	private unsafe bool TryPatchTlsImmediateStoreInstruction(nint address, byte* source)
	{
		if (source[0] != 100 || source[1] != 199 || source[2] != 4 || source[3] != 37)
		{
			return false;
		}
		int tlsOffset = *(int*)(source + 4);
		int immediateValue = *(int*)(source + 8);
		nint num = CreateTlsImmediateStoreHelper(tlsOffset, immediateValue);
		if (num == 0)
		{
			return false;
		}
		return PatchCallSite(address, 12, num);
	}

	private unsafe nint CreateTlsImmediateStoreHelper(int tlsOffset, int immediateValue)
	{
		nint num = AllocateTlsPatchStub(32);
		if (num == 0)
		{
			return 0;
		}
		byte* ptr = (byte*)num;
		int num2 = 0;
		ptr[num2++] = 80;
		ptr[num2++] = 232;
		long num3 = _tlsHandlerAddress - (num + num2 + 4);
		if (num3 < int.MinValue || num3 > int.MaxValue)
		{
			Log.Warn($"[LOADER][WARNING] TLS store helper out of rel32 range at 0x{num:X16}");
			return 0;
		}
		*(int*)(ptr + num2) = (int)num3;
		num2 += 4;
		ptr[num2++] = 199;
		ptr[num2++] = 128;
		*(int*)(ptr + num2) = tlsOffset;
		num2 += 4;
		*(int*)(ptr + num2) = immediateValue;
		num2 += 4;
		ptr[num2++] = 88;
		ptr[num2++] = 195;
		while (num2 < 32)
		{
			ptr[num2++] = 144;
		}
		uint flNewProtect = default(uint);
		VirtualProtect((void*)num, 32u, 32u, &flNewProtect);
		FlushInstructionCache(GetCurrentProcess(), (void*)num, 32u);
		return num;
	}

	private unsafe nint AllocateTlsPatchStub(int size)
	{
		if (_tlsHandlerAddress == 0 || size <= 0)
		{
			return 0;
		}
		int num = (size + 15) & -16;
		if (_tlsPatchStubOffset + num > TlsHandlerRegionSize)
		{
			Log.Warn("[LOADER][WARNING] TLS patch stub region exhausted.");
			return 0;
		}
		nint result = _tlsHandlerAddress + _tlsPatchStubOffset;
		_tlsPatchStubOffset += num;
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)result, (nuint)num, 64u, &flNewProtect))
		{
			return 0;
		}
		return result;
	}

	private unsafe bool PatchCallSite(nint address, int instructionLength, nint target)
	{
		if (instructionLength < 5)
		{
			return false;
		}
		uint flNewProtect = default(uint);
		if (!VirtualProtect((void*)address, (nuint)instructionLength, 64u, &flNewProtect))
		{
			return false;
		}
		try
		{
			long num = target - (address + 5);
			if (num < int.MinValue || num > int.MaxValue)
			{
				Log.Warn($"[LOADER][WARNING] TLS patch out of rel32 range at 0x{address:X16}");
				return false;
			}
			*(byte*)address = 232;
			*(int*)(address + 1) = (int)num;
			for (int i = 5; i < instructionLength; i++)
			{
				*(byte*)(address + i) = 144;
			}
		}
		finally
		{
			VirtualProtect((void*)address, (nuint)instructionLength, flNewProtect, &flNewProtect);
			FlushInstructionCache(GetCurrentProcess(), (void*)address, (nuint)instructionLength);
		}
		return true;
	}

	private unsafe void TryPreReservePrtAperture(ulong baseAddress, ulong size)
	{
		if (VirtualQuery((void*)baseAddress, out var lpBuffer, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) != 0 && lpBuffer.State != 65536)
		{
			Log.Debug($"[LOADER][INFO] PRT aperture at 0x{baseAddress:X16} already in use (state=0x{lpBuffer.State:X}), will use lazy-commit");
			return;
		}
		ulong num = baseAddress;
		ulong num2 = baseAddress + size;
		int num3 = 0;
		int num4 = 0;
		nuint num5;
		for (; num < num2; num += num5)
		{
			ulong val = num2 - num;
			num5 = (nuint)Math.Min(2097152uL, val);
			void* ptr = VirtualAlloc((void*)num, num5, 8192u, 4u);
			if (ptr != null)
			{
				num3++;
			}
			else
			{
				num4++;
			}
		}
		if (num4 == 0)
		{
			Log.Debug($"[LOADER][INFO] Pre-reserved PRT aperture: 0x{baseAddress:X16}-0x{num2:X16} ({num3} chunks)");
		}
		else
		{
			Log.Error($"[LOADER][INFO] Partial PRT aperture reserve: 0x{baseAddress:X16}-0x{num2:X16} ({num3} chunks OK, {num4} failed)");
		}
		ulong num6 = baseAddress;
		ulong num7 = baseAddress + 67108864;
		int num8 = 0;
		for (; num6 < num7; num6 += 2097152)
		{
			void* ptr2 = VirtualAlloc((void*)num6, 2097152u, 4096u, 4u);
			if (ptr2 != null)
			{
				num8++;
			}
		}
		if (num8 > 0)
		{
			Log.Debug($"[LOADER][INFO] Pre-committed PRT bootstrap: 0x{baseAddress:X16}-0x{num7:X16} ({num8 * 2}MB in {num8} chunks)");
		}
		else
		{
			Log.Error($"[LOADER][WARN] Failed to pre-commit any PRT bootstrap chunks at 0x{baseAddress:X16}");
		}
	}

	public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
	{
		error = null;
		if (request.ThreadHandle == 0 || request.EntryPoint < 65536)
		{
			error = $"invalid thread start request: handle=0x{request.ThreadHandle:X16} entry=0x{request.EntryPoint:X16}";
			return false;
		}
		if (!TryCreateGuestThreadState(creatorContext, request, out var thread, out error))
		{
			return false;
		}
		lock (_guestThreadGate)
		{
			_guestThreads[request.ThreadHandle] = thread;
			_readyGuestThreads.Enqueue(thread);
		}
		Log.Debug(
			$"[LOADER][INFO] Scheduled guest thread '{thread.Name}' handle=0x{thread.ThreadHandle:X16} entry=0x{thread.EntryPoint:X16} arg=0x{thread.Argument:X16}");
		return true;
	}

	public void Pump(CpuContext callerContext, string reason)
	{
		_ = callerContext;
		if (_guestThreadPumpDepth != 0)
		{
			return;
		}
		_guestThreadPumpDepth++;
		try
		{
			for (int i = 0; i < 8; i++)
			{
				GuestThreadState? thread = null;
				lock (_guestThreadGate)
				{
					while (_readyGuestThreads.Count > 0)
					{
						var candidate = _readyGuestThreads.Dequeue();
						if (candidate.State == GuestThreadRunState.Ready)
						{
							thread = candidate;
							thread.State = GuestThreadRunState.Running;
							break;
						}
					}
				}
				if (thread == null)
				{
					return;
				}
				RunGuestThread(thread, reason);
			}
		}
		finally
		{
			_guestThreadPumpDepth--;
		}
	}

	private void ClearGuestThreads()
	{
		lock (_guestThreadGate)
		{
			_readyGuestThreads.Clear();
			_guestThreads.Clear();
		}
	}

	private bool TryCreateGuestThreadState(CpuContext creatorContext, GuestThreadStartRequest request, out GuestThreadState thread, out string? error)
	{
		thread = null!;
		if (!TryGetVirtualMemory(creatorContext, out var virtualMemory))
		{
			error = "creator context memory is not backed by IVirtualMemory";
			return false;
		}
		if (!TryMapGuestThreadRegion(virtualMemory, GuestThreadStackBaseAddress, GuestThreadStackSize, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write, out var stackBase, out error))
		{
			return false;
		}
		if (!TryMapGuestThreadRegion(virtualMemory, GuestThreadTlsBaseAddress, GuestThreadTlsSize, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write, out var tlsBase, out error))
		{
			return false;
		}

		var trackedMemory = new TrackedCpuMemory(virtualMemory);
		var context = new CpuContext(trackedMemory, creatorContext.TargetGeneration)
		{
			Rip = request.EntryPoint,
			Rflags = 0x202,
			FsBase = tlsBase,
			GsBase = tlsBase,
		};
		context[CpuRegister.Rsp] = stackBase + GuestThreadStackSize - sizeof(ulong);
		context[CpuRegister.Rdi] = request.Argument;
		context[CpuRegister.Rsi] = 0;
		context[CpuRegister.Rdx] = 0;
		context[CpuRegister.Rcx] = 0;
		context[CpuRegister.R8] = 0;
		context[CpuRegister.R9] = 0;
		if (!InitializeGuestThreadFrame(context) || !InitializeGuestThreadTls(context, tlsBase))
		{
			error = "failed to initialize guest thread stack/TLS";
			return false;
		}

		thread = new GuestThreadState
		{
			ThreadHandle = request.ThreadHandle,
			EntryPoint = request.EntryPoint,
			Argument = request.Argument,
			Name = string.IsNullOrWhiteSpace(request.Name) ? $"Thread-{request.ThreadHandle:X}" : request.Name,
			Context = context,
			State = GuestThreadRunState.Ready,
		};
		error = null;
		return true;
	}

	private static bool TryGetVirtualMemory(CpuContext context, out IVirtualMemory virtualMemory)
	{
		if (context.Memory is IVirtualMemory directMemory)
		{
			virtualMemory = directMemory;
			return true;
		}
		if (context.Memory is TrackedCpuMemory trackedMemory && trackedMemory.Inner is IVirtualMemory trackedInner)
		{
			virtualMemory = trackedInner;
			return true;
		}

		virtualMemory = null!;
		return false;
	}

	private static bool TryMapGuestThreadRegion(
		IVirtualMemory virtualMemory,
		ulong baseAddress,
		ulong size,
		ProgramHeaderFlags protection,
		out ulong mappedBase,
		out string? error)
	{
		for (int i = 0; i < 64; i++)
		{
			var candidateBase = baseAddress - ((ulong)i * GuestThreadRegionStride);
			if (!IsGuestThreadRegionFree(virtualMemory, candidateBase, size))
			{
				continue;
			}
			try
			{
				virtualMemory.Map(
					candidateBase,
					size,
					fileOffset: 0,
					fileData: ReadOnlySpan<byte>.Empty,
					protection: protection);
				mappedBase = candidateBase;
				error = null;
				return true;
			}
			catch (InvalidOperationException)
			{
			}
		}

		mappedBase = 0;
		error = $"failed to map guest thread region near 0x{baseAddress:X16}";
		return false;
	}

	private static bool IsGuestThreadRegionFree(IVirtualMemory virtualMemory, ulong candidateBase, ulong size)
	{
		var candidateEnd = candidateBase + size;
		foreach (var region in virtualMemory.SnapshotRegions())
		{
			var regionStart = region.VirtualAddress;
			var regionEnd = regionStart + region.MemorySize;
			if (candidateBase < regionEnd && regionStart < candidateEnd)
			{
				return false;
			}
		}

		return true;
	}

	private static bool InitializeGuestThreadFrame(CpuContext context)
	{
		var stackTop = context[CpuRegister.Rsp] + sizeof(ulong);
		var sentinelFrame = AlignDown(stackTop - 0x20, 16);
		var seedRsp = sentinelFrame - sizeof(ulong);
		if (!context.TryWriteUInt64(sentinelFrame, 0) ||
			!context.TryWriteUInt64(sentinelFrame + sizeof(ulong), 0) ||
			!context.TryWriteUInt64(seedRsp, 0))
		{
			return false;
		}

		context[CpuRegister.Rbp] = sentinelFrame;
		context[CpuRegister.Rsp] = seedRsp;
		return true;
	}

	private static bool InitializeGuestThreadTls(CpuContext context, ulong tlsBase)
	{
		return context.TryWriteUInt64(tlsBase + 0x00, tlsBase) &&
			context.TryWriteUInt64(tlsBase + 0x10, tlsBase) &&
			context.TryWriteUInt64(tlsBase + 0x28, 0xC0DEC0DECAFEBABEUL);
	}

	private void RunGuestThread(GuestThreadState thread, string reason)
	{
		Log.Debug(
			$"[LOADER][INFO] Pumping guest thread '{thread.Name}' reason={reason} entry=0x{thread.EntryPoint:X16}");
		var previousContext = _cpuContext;
		var previousTlsBase = _tlsBaseAddress;
		var previousOwnsTlsBase = _ownsTlsBaseAddress;
		var previousForcedGuestExit = _forcedGuestExit;
		var previousLastError = LastError;
		var previousGuestThreadHandle = GuestThreadExecution.EnterGuestThread(thread.ThreadHandle);
		try
		{
			_cpuContext = thread.Context;
			_forcedGuestExit = false;
			LastError = null;
			BindTlsBase(thread.Context);
			var exitReason = ExecuteGuestThreadEntry(thread.Context, thread.EntryPoint, thread.Name, out var blockReason);
			lock (_guestThreadGate)
			{
				switch (exitReason)
				{
					case GuestNativeCallExitReason.Returned:
						thread.State = GuestThreadRunState.Exited;
						break;
					case GuestNativeCallExitReason.Blocked:
						thread.State = GuestThreadRunState.Blocked;
						thread.BlockReason = blockReason;
						break;
					default:
						thread.State = GuestThreadRunState.Faulted;
						thread.BlockReason = blockReason;
						break;
				}
			}
			Log.Debug(
				$"[LOADER][INFO] Guest thread '{thread.Name}' state={thread.State} reason={blockReason ?? "none"}");
		}
		finally
		{
			GuestThreadExecution.RestoreGuestThread(previousGuestThreadHandle);
			_cpuContext = previousContext;
			_tlsBaseAddress = previousTlsBase;
			_ownsTlsBaseAddress = previousOwnsTlsBase;
			if (_tlsBaseAddress != 0)
			{
				SeedTlsLayout(_tlsBaseAddress);
				UpdateTlsHandlerBase(_tlsBaseAddress);
			}
			_forcedGuestExit = previousForcedGuestExit;
			LastError = previousLastError;
		}
	}

	private unsafe GuestNativeCallExitReason ExecuteGuestThreadEntry(CpuContext context, ulong entryPoint, string name, out string? reason)
	{
		reason = null;
		if (context[CpuRegister.Rsp] == 0)
		{
			reason = "guest thread stack pointer is zero";
			return GuestNativeCallExitReason.Exception;
		}
		void* ptr = VirtualAlloc(null, 256u, 12288u, 64u);
		if (ptr == null)
		{
			reason = "failed to allocate executable memory for guest thread stub";
			return GuestNativeCallExitReason.Exception;
		}
		var previousSentinel = _entryReturnSentinelRip;
		var previousHostRspSlotValue = _hostRspSlotStorage != 0 ? *(ulong*)_hostRspSlotStorage : 0;
		var previousYieldRequested = _guestThreadYieldRequested;
		var previousYieldReason = _guestThreadYieldReason;
		try
		{
			byte* ptr2 = (byte*)ptr;
			ulong hostRspSlot = (ulong)ptr + 224uL;
			int offset = 0;
			ptr2[offset++] = 83;
			ptr2[offset++] = 85;
			ptr2[offset++] = 87;
			ptr2[offset++] = 86;
			ptr2[offset++] = 73;
			ptr2[offset++] = 186;
			*(ulong*)(ptr2 + offset) = hostRspSlot;
			offset += 8;
			ptr2[offset++] = 73;
			ptr2[offset++] = 137;
			ptr2[offset++] = 34;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rsp];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 196;
			ptr2[offset++] = 72;
			ptr2[offset++] = 131;
			ptr2[offset++] = 236;
			ptr2[offset++] = 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 189;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rbp];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rdi];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 199;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rsi];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 198;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rdx];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 194;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = context[CpuRegister.Rcx];
			offset += 8;
			ptr2[offset++] = 72;
			ptr2[offset++] = 137;
			ptr2[offset++] = 193;
			ptr2[offset++] = 72;
			ptr2[offset++] = 184;
			*(ulong*)(ptr2 + offset) = entryPoint;
			offset += 8;
			ptr2[offset++] = byte.MaxValue;
			ptr2[offset++] = 208;
			int sentinelOffset = offset + 4;
			ptr2[offset++] = 72;
			ptr2[offset++] = 131;
			ptr2[offset++] = 196;
			ptr2[offset++] = 8;
			ptr2[offset++] = 73;
			ptr2[offset++] = 186;
			*(ulong*)(ptr2 + offset) = hostRspSlot;
			offset += 8;
			ptr2[offset++] = 73;
			ptr2[offset++] = 139;
			ptr2[offset++] = 34;
			ptr2[offset++] = 94;
			ptr2[offset++] = 95;
			ptr2[offset++] = 93;
			ptr2[offset++] = 91;
			ptr2[offset++] = 195;
			ulong sentinel = (ulong)ptr + (ulong)sentinelOffset;
			_entryReturnSentinelRip = sentinel;
			if (!context.TryWriteUInt64(context[CpuRegister.Rsp], sentinel))
			{
				reason = $"failed to patch guest thread return sentinel at 0x{context[CpuRegister.Rsp]:X16}";
				return GuestNativeCallExitReason.Exception;
			}
			uint oldProtect = default(uint);
			VirtualProtect(ptr, 256u, 64u, &oldProtect);
			FlushInstructionCache(GetCurrentProcess(), ptr, 256u);
			if (_hostRspSlotStorage != 0)
			{
				*(ulong*)_hostRspSlotStorage = hostRspSlot;
			}
			_guestThreadYieldRequested = false;
			_guestThreadYieldReason = null;
			try
			{
				var nativeReturn = Marshal.GetDelegateForFunctionPointer<NativeEntryDelegate>((nint)ptr)();
				if (_guestThreadYieldRequested)
				{
					reason = _guestThreadYieldReason ?? "guest thread blocked";
					return GuestNativeCallExitReason.Blocked;
				}
				if (_forcedGuestExit)
				{
					reason = LastError ?? "guest thread forced exit";
					return GuestNativeCallExitReason.ForcedExit;
				}
				reason = $"returned 0x{nativeReturn:X8}";
				return GuestNativeCallExitReason.Returned;
			}
			catch (AccessViolationException ex)
			{
				reason = "access violation: " + ex.Message;
				return GuestNativeCallExitReason.Exception;
			}
			catch (Exception ex)
			{
				reason = ex.GetType().Name + ": " + ex.Message;
				return GuestNativeCallExitReason.Exception;
			}
		}
		finally
		{
			_entryReturnSentinelRip = previousSentinel;
			if (_hostRspSlotStorage != 0)
			{
				*(ulong*)_hostRspSlotStorage = previousHostRspSlotValue;
			}
			_guestThreadYieldRequested = previousYieldRequested;
			_guestThreadYieldReason = previousYieldReason;
			VirtualFree(ptr, 0u, 32768u);
		}
	}

	private static ulong AlignDown(ulong value, ulong alignment)
	{
		if (alignment == 0)
		{
			return value;
		}
		return value & ~(alignment - 1);
	}

	private unsafe bool ExecuteEntry(CpuContext context, ulong entryPoint, out OrbisGen2Result result)
	{
		Log.Debug($"[LOADER][INFO] ExecuteEntry starting at 0x{entryPoint:X16}");
		Log.Debug($"[LOADER][INFO] RSP=0x{context[CpuRegister.Rsp]:X16}, RDI=0x{context[CpuRegister.Rdi]:X16}");
		ulong num = context[CpuRegister.Rsp];
		if (num == 0)
		{
			LastError = "Guest stack pointer is zero";
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
			return false;
		}
		Log.Debug($"[LOADER][INFO] StackTop: 0x{num:X16}");
		void* ptr = VirtualAlloc(null, 256u, 12288u, 64u);
		if (ptr == null)
		{
			LastError = "Failed to allocate executable memory for stub";
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
			return false;
		}
		try
		{
			byte* ptr2 = (byte*)ptr;
			ulong num2 = (ulong)ptr + 224uL;
			int num3 = 0;
			ptr2[num3++] = 83;
			ptr2[num3++] = 85;
			ptr2[num3++] = 87;
			ptr2[num3++] = 86;
			ptr2[num3++] = 73;
			ptr2[num3++] = 186;
			*(ulong*)(ptr2 + num3) = num2;
			num3 += 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 137;
			ptr2[num3++] = 34;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rsp];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 196;
			ptr2[num3++] = 72;
			ptr2[num3++] = 131;
			ptr2[num3++] = 236;
			ptr2[num3++] = 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 189;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rbp];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rdi];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 199;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rsi];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 198;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rdx];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 194;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = context[CpuRegister.Rcx];
			num3 += 8;
			ptr2[num3++] = 72;
			ptr2[num3++] = 137;
			ptr2[num3++] = 193;
			ptr2[num3++] = 72;
			ptr2[num3++] = 184;
			*(ulong*)(ptr2 + num3) = entryPoint;
			num3 += 8;
			ptr2[num3++] = byte.MaxValue;
			ptr2[num3++] = 208;
			int num4 = num3 + 4;
			ptr2[num3++] = 72;
			ptr2[num3++] = 131;
			ptr2[num3++] = 196;
			ptr2[num3++] = 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 186;
			*(ulong*)(ptr2 + num3) = num2;
			num3 += 8;
			ptr2[num3++] = 73;
			ptr2[num3++] = 139;
			ptr2[num3++] = 34;
			ptr2[num3++] = 94;
			ptr2[num3++] = 95;
			ptr2[num3++] = 93;
			ptr2[num3++] = 91;
			ptr2[num3++] = 195;
			ulong value = (ulong)ptr + (ulong)num4;
			_entryReturnSentinelRip = value;
			if (!context.TryWriteUInt64(context[CpuRegister.Rsp], value))
			{
				LastError = $"Failed to patch native return sentinel at 0x{context[CpuRegister.Rsp]:X16}";
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
				return false;
			}
			uint num5 = default(uint);
			VirtualProtect(ptr, 256u, 64u, &num5);
			FlushInstructionCache(GetCurrentProcess(), ptr, 256u);
			if (_hostRspSlotStorage != 0)
			{
				*(ulong*)_hostRspSlotStorage = num2;
			}
			if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_SENTINEL_PROBE"), "1", StringComparison.Ordinal))
			{
				Log.Error("[LOADER][INFO] Running unresolved sentinel probe...");
				Marshal.GetDelegateForFunctionPointer<NativeEntryDelegate>((nint)65534)();
				Log.Debug("[LOADER][INFO] Sentinel probe returned.");
			}
			Log.Debug("[LOADER][INFO] Calling guest entry...");
			StartStallWatchdog();
			int num6 = -1;
			try
			{
				num6 = Marshal.GetDelegateForFunctionPointer<NativeEntryDelegate>((nint)ptr)();
				Log.Debug($"[LOADER][INFO] Guest returned: {num6}");
				Pump(context, "entry_return");
			}
			catch (AccessViolationException ex)
			{
				Log.Error("[LOADER][ERROR] Access Violation during execution: " + ex.Message, ex);
				Log.Error("[LOADER][ERROR] This usually means:", ex);
				Log.Error("  1. Invalid memory access in guest code", ex);
				Log.Error("  2. Unpatched import/TLS call", ex);
				Log.Error("  3. Stack corruption", ex);
				num6 = -1;
			}
			catch (Exception ex2)
			{
				Log.Error("[LOADER][ERROR] Exception during execution: " + ex2.GetType().Name + ": " + ex2.Message, ex2);
				LastError = "Exception during execution: " + ex2.GetType().Name + ": " + ex2.Message;
				num6 = -1;
			}
			if (_forcedGuestExit)
			{
				result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
				if (string.IsNullOrEmpty(LastError))
				{
					LastError = "Detected repeating import loop and forced guest unwind to host.";
				}
				Log.Error("[LOADER][ERROR] " + LastError);
				return false;
			}
			if (num6 == 0)
			{
				result = OrbisGen2Result.ORBIS_GEN2_OK;
				LastError = null;
				return true;
			}
			result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
			if (string.IsNullOrEmpty(LastError))
			{
				LastError = $"Guest entry point returned non-zero: {num6}";
			}
			Log.Error("[LOADER][ERROR] " + LastError);
			return false;
		}
		finally
		{
			StopStallWatchdog();
			_entryReturnSentinelRip = 0uL;
			if (_hostRspSlotStorage != 0)
			{
				*(long*)_hostRspSlotStorage = 0L;
			}
			VirtualFree(ptr, 0u, 32768u);
		}
	}


	private void MarkExecutionProgress()
	{
		Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
	}

	private static int GetStallWatchdogSeconds()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_STALL_WATCHDOG_SECONDS"), out var result))
		{
			return Math.Max(0, result);
		}
		return 20;
	}

	private void StartStallWatchdog()
	{
		int stallWatchdogSeconds = GetStallWatchdogSeconds();
		if (stallWatchdogSeconds <= 0 || _stallWatchdogThread != null)
		{
			return;
		}
		_stallWatchdogStop = false;
		long num = (long)((double)stallWatchdogSeconds * Stopwatch.Frequency);
		_stallWatchdogThread = new Thread(new ThreadStart(delegate
		{
			while (!_stallWatchdogStop)
			{
				Thread.Sleep(200);
				if (_stallWatchdogStop)
				{
					break;
				}
				long num2 = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastProgressTimestamp);
				if (num2 < num)
				{
					continue;
				}
				if (Interlocked.Exchange(ref _stallWatchdogTriggered, 1) != 0)
				{
					continue;
				}
				LastError = $"Execution stalled with no import progress for {stallWatchdogSeconds}s (imports={Volatile.Read(ref _importDispatchCount)}).";
				Log.Error("[LOADER][ERROR] " + LastError);
				LogStallWatchdogSnapshot();
				Console.Error.Flush();
				Environment.Exit(4);
			}
		}))
		{
			IsBackground = true,
			Name = "SharpEmu-StallWatchdog"
		};
		_stallWatchdogThread.Start();
	}

	private void StopStallWatchdog()
	{
		_stallWatchdogStop = true;
		Thread? stallWatchdogThread = _stallWatchdogThread;
		if (stallWatchdogThread == null)
		{
			return;
		}
		if (!ReferenceEquals(Thread.CurrentThread, stallWatchdogThread))
		{
			try
			{
				stallWatchdogThread.Join(300);
			}
			catch
			{
			}
		}
		_stallWatchdogThread = null;
	}

	private void LogStallWatchdogSnapshot()
	{
		try
		{
			var cpuContext = _cpuContext;
			if (cpuContext is null)
			{
				return;
			}
			ulong rsp = cpuContext[CpuRegister.Rsp];
			Log.Error($"[LOADER][ERROR] Stall snapshot: rip=0x{cpuContext.Rip:X16} rsp=0x{rsp:X16} rbp=0x{cpuContext[CpuRegister.Rbp]:X16} rax=0x{cpuContext[CpuRegister.Rax]:X16} rbx=0x{cpuContext[CpuRegister.Rbx]:X16} rcx=0x{cpuContext[CpuRegister.Rcx]:X16} rdx=0x{cpuContext[CpuRegister.Rdx]:X16} rsi=0x{cpuContext[CpuRegister.Rsi]:X16} rdi=0x{cpuContext[CpuRegister.Rdi]:X16}");
			ulong num = cpuContext.Rip & 0xFFFFFFFFFFFFFFF0uL;
			for (int i = 0; i < _importEntries.Length; i++)
			{
				if (_importEntries[i].Address != num)
				{
					continue;
				}
				string text = _importEntries[i].Nid;
				if (_moduleManager.TryGetExport(text, out ExportedFunction export))
				{
					Log.Error($"[LOADER][ERROR] Stall import-stub: rip=0x{num:X16} nid={text} -> {export.LibraryName}:{export.Name}");
				}
				else
				{
					Log.Error($"[LOADER][ERROR] Stall import-stub: rip=0x{num:X16} nid={text}");
				}
				break;
			}
			Span<byte> destination = stackalloc byte[16];
			if (cpuContext.Memory.TryRead(cpuContext.Rip, destination))
			{
				Log.Error($"[LOADER][ERROR] Stall bytes @rip: {BitConverter.ToString(destination.ToArray()).Replace("-", " ")}");
			}
			else if (cpuContext.Memory.TryRead(num, destination))
			{
				Log.Error($"[LOADER][ERROR] Stall bytes @rip_align: {BitConverter.ToString(destination.ToArray()).Replace("-", " ")}");
			}
			if (rsp != 0 && cpuContext.TryReadUInt64(rsp, out var value) && cpuContext.TryReadUInt64(rsp + 8, out var value2))
			{
				Log.Error($"[LOADER][ERROR] Stall stack: [rsp]=0x{value:X16} [rsp+8]=0x{value2:X16}");
			}
		}
		catch
		{
		}
	}


	[DllImport("kernel32.dll")]
	private unsafe static extern void* AddVectoredExceptionHandler(uint first, IntPtr handler);

	[DllImport("kernel32.dll")]
	private unsafe static extern uint RemoveVectoredExceptionHandler(void* handle);

	[DllImport("kernel32.dll")]
	private static extern IntPtr SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter);

	public unsafe void Dispose()
	{
		ClearImportHandlerTrampolines();
		_importEntries = Array.Empty<ImportStubEntry>();
		_runtimeSymbolsByName.Clear();
		_importNidHashCache.Clear();
		StopStallWatchdog();
		if (_exceptionHandler != 0)
		{
			RemoveVectoredExceptionHandler((void*)_exceptionHandler);
			_exceptionHandler = 0;
		}
		if (_rawExceptionHandler != 0)
		{
			RemoveVectoredExceptionHandler((void*)_rawExceptionHandler);
			_rawExceptionHandler = 0;
		}
		if (_handlerHandle.IsAllocated)
		{
			_handlerHandle.Free();
		}
		if (_unhandledFilterHandle.IsAllocated)
		{
			_unhandledFilterHandle.Free();
		}
		if (_selfHandle.IsAllocated)
		{
			_selfHandle.Free();
			_selfHandlePtr = 0;
		}
		if (_ownedTlsBaseAddress != 0)
		{
			VirtualFree((void*)_ownedTlsBaseAddress, 0u, 32768u);
			_ownedTlsBaseAddress = 0;
		}
		_tlsBaseAddress = 0;
		_ownsTlsBaseAddress = false;
		if (_tlsModuleBases.Count > 0)
		{
			foreach (var (_, num3) in _tlsModuleBases)
			{
				if (num3 != 0)
				{
					VirtualFree((void*)num3, 0u, 32768u);
				}
			}
			_tlsModuleBases.Clear();
		}
		if (_tlsHandlerAddress != 0)
		{
			VirtualFree((void*)_tlsHandlerAddress, 0u, 32768u);
			_tlsHandlerAddress = 0;
		}
		if (_hostRspSlotStorage != 0)
		{
			VirtualFree((void*)_hostRspSlotStorage, 0u, 32768u);
			_hostRspSlotStorage = 0;
		}
		if (_unresolvedReturnStub != 0)
		{
			VirtualFree((void*)_unresolvedReturnStub, 0u, 32768u);
			_unresolvedReturnStub = 0;
		}
		if (_lowIndexedTableScratch != 0)
		{
			VirtualFree((void*)_lowIndexedTableScratch, 0u, 32768u);
			_lowIndexedTableScratch = 0;
		}
		if (_stackGuardCompareScratch != 0)
		{
			VirtualFree((void*)_stackGuardCompareScratch, 0u, 32768u);
			_stackGuardCompareScratch = 0;
		}
		if (_nullObjectStoreScratch != 0)
		{
			VirtualFree((void*)_nullObjectStoreScratch, 0u, 32768u);
			_nullObjectStoreScratch = 0;
		}
	}

	[DllImport("kernel32.dll")]
	private unsafe static extern void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private unsafe static extern bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private unsafe static extern bool VirtualProtect(void* lpAddress, nuint dwSize, uint flNewProtect, uint* lpflOldProtect);

	[DllImport("kernel32.dll")]
	private unsafe static extern void* GetCurrentProcess();

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private unsafe static extern bool FlushInstructionCache(void* hProcess, void* lpBaseAddress, nuint dwSize);

	[DllImport("kernel32.dll")]
	private unsafe static extern nuint VirtualQuery(void* lpAddress, out MEMORY_BASIC_INFORMATION64 lpBuffer, nuint dwLength);
}
