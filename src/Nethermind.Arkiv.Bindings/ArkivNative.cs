// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;

namespace Nethermind.Arkiv.Native;

/// <summary>What a call into the native library came to; mirrors <c>arkiv_ffi::Status</c>.</summary>
public enum ArkivStatus
{
    Ok = 0,
    StateRootMismatch = 1,
    InvalidBlock = 2,
    NotOnHead = 3,
    BadInput = 4,
    Internal = 5,
}

/// <summary>What <see cref="ArkivNative.ExecuteBlock"/> does with a block that executes to its header's root; mirrors <c>arkiv_ffi::Apply</c>.</summary>
public enum ArkivApply
{
    /// <summary>Nothing: the store does not change.</summary>
    DryRun = 0,
    /// <summary>Commit it: the block becomes the store's head.</summary>
    Commit = 1,
    /// <summary>
    /// Hold it, sealed and uncommitted, until <see cref="ArkivNative.Adopt"/> commits it. A sibling then executes over
    /// the same head, and whichever block is adopted commits while the other leaves no trace: GolemDB has no rewind, so
    /// a committed block could only be replaced by restoring the store from a copy.
    /// </summary>
    Hold = 2,
}

/// <summary>A native call that did not succeed, with the native side's message.</summary>
public sealed class ArkivNativeException(ArkivStatus status, string message) : Exception($"{status}: {message}")
{
    public ArkivStatus Status { get; } = status;
    public string NativeMessage { get; } = message;
}

/// <summary>
/// The C ABI of <c>libarkiv_ffi</c> (<c>native/arkiv-ffi</c>): Arkiv's own executor and GolemDB store, built from the
/// Arkiv release this package's version names. Every buffer the library returns is copied out and freed here, so
/// nothing native outlives a call; a failed call throws <see cref="ArkivNativeException"/>.
/// </summary>
/// <remarks>
/// Integers in the byte layouts below are little-endian unless stated otherwise.
/// <para>
/// A handle is a number the library checks, not a pointer: an unknown, closed or twice-closed handle fails the call
/// with <see cref="ArkivStatus.BadInput"/>, and a call in progress keeps its chain alive through a concurrent
/// <see cref="Close"/>.
/// </para>
/// <para>
/// A commit or a hold (<see cref="ExecuteBlock"/> with <see cref="ArkivApply.Commit"/> or <see cref="ArkivApply.Hold"/>)
/// and <see cref="Adopt"/> must not run concurrently with any other call on the same chain: they move the head that
/// reads and executions run against. Everything else may run concurrently, dry runs and builds of the same block
/// included.
/// </para>
/// </remarks>
public static unsafe partial class ArkivNative
{
    private const string Library = "arkiv_ffi";
    private const int AddressLength = 20;
    private static string? _libraryPath;
    private static int _resolverSet;

    static ArkivNative() =>
        (AssemblyLoadContext.GetLoadContext(typeof(ArkivNative).Assembly) ?? AssemblyLoadContext.Default).ResolvingUnmanagedDll += OnResolvingUnmanagedDll;

    /// <summary>Loads the native library from <paramref name="path"/> instead of the package's own.</summary>
    /// <remarks>The library loads once per process, so this must come before the first call, and only the first path counts.</remarks>
    /// <exception cref="FileNotFoundException">There is no file at <paramref name="path"/>: no other library is loaded in its place.</exception>
    public static void UseLibrary(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The Arkiv native library to use does not exist.", path);
        _libraryPath ??= path;
        if (Interlocked.Exchange(ref _resolverSet, 1) == 0) NativeLibrary.SetDllImportResolver(typeof(ArkivNative).Assembly, ResolveConfigured);
    }

    // Loads the configured path or fails: falling back to the package's library would run another binary unnoticed.
    private static nint ResolveConfigured(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library && _libraryPath is not null ? NativeLibrary.Load(_libraryPath) : nint.Zero;

    /// <summary>Finds the package's library under <c>runtimes/&lt;rid&gt;/native</c> beside the assembly, where a build that is not RID-specific puts it.</summary>
    private static nint OnResolvingUnmanagedDll(Assembly context, string name)
    {
        if (context != typeof(ArkivNative).Assembly || !Library.Equals(name, StringComparison.Ordinal)) return nint.Zero;

        (string platform, string file) = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ("linux", $"lib{name}.so")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ("osx", $"lib{name}.dylib")
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ("win", $"{name}.dll")
            : throw new PlatformNotSupportedException();
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        return NativeLibrary.Load($"runtimes/{platform}-{arch}/native/{file}", context, DllImportSearchPath.AssemblyDirectory);
    }

    [LibraryImport(Library)]
    private static partial int arkiv_open(byte* genesis, nuint genesisLen, byte* dir, nuint dirLen, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial void arkiv_close(ulong handle);

    [LibraryImport(Library)]
    private static partial void arkiv_free(byte* ptr, nuint len);

    [LibraryImport(Library)]
    private static partial int arkiv_head(ulong handle, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_execute_block(ulong handle, byte* block, nuint blockLen, byte* senders, nuint sendersLen, int apply, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_adopt(ulong handle, byte* hash, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_account(ulong handle, byte* address, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_call(ulong handle, byte* request, nuint requestLen, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_rpc(ulong handle, byte* method, nuint methodLen, byte* parameters, nuint parametersLen, ulong headNumber, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_purge_keys(ulong handle, ulong block, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_build_begin(ulong handle, byte* header, nuint headerLen, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_build_apply(ulong session, byte* tx, nuint txLen, byte* sender, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_build_finish(ulong session, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial void arkiv_build_abort(ulong session);

    [LibraryImport(Library)]
    private static partial int arkiv_store_accounts(byte* dir, nuint dirLen, out byte* outPtr, out nuint outLen);

    private static void RequireAddress(ReadOnlySpan<byte> address, string name)
    {
        if (address.Length != AddressLength) throw new ArgumentException($"An address is {AddressLength} bytes, not {address.Length}.", name);
    }

    /// <summary>Copies the returned buffer out, frees it, and throws for any status but <see cref="ArkivStatus.Ok"/>.</summary>
    private static byte[] Take(int status, byte* ptr, nuint len)
    {
        if (len > int.MaxValue)
        {
            arkiv_free(ptr, len);
            throw new InvalidOperationException($"The Arkiv native library returned {len} bytes, more than a buffer holds.");
        }
        byte[] bytes = len == 0 ? [] : new ReadOnlySpan<byte>(ptr, (int)len).ToArray();
        arkiv_free(ptr, len);
        return status == (int)ArkivStatus.Ok ? bytes : throw new ArkivNativeException((ArkivStatus)status, Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// Opens a chain from its genesis file (with <c>config.arkiv.golemdb</c>) over the GolemDB store in
    /// <paramref name="directory"/>, created and seeded with genesis when empty; null opens an in-memory store.
    /// </summary>
    /// <returns><c>handle (8) ‖ genesis state root (32) ‖ genesis block hash (32)</c>.</returns>
    public static byte[] Open(string genesisJson, string? directory)
    {
        byte[] genesis = Encoding.UTF8.GetBytes(genesisJson);
        byte[] dir = Encoding.UTF8.GetBytes(directory ?? string.Empty);
        fixed (byte* g = genesis)
        fixed (byte* d = dir)
        {
            int status = arkiv_open(g, (nuint)genesis.Length, d, (nuint)dir.Length, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    /// <summary>Closes a handle from <see cref="Open"/>; it must not be used again.</summary>
    public static void Close(ulong handle) => arkiv_close(handle);

    /// <summary>The store's head.</summary>
    /// <returns><c>commit number (8) ‖ block hash it is tagged with (32) ‖ state root (32)</c>.</returns>
    public static byte[] Head(ulong handle)
    {
        int status = arkiv_head(handle, out byte* ptr, out nuint len);
        return Take(status, ptr, len);
    }

    /// <summary>
    /// Executes a block (its RLP) on the store's head, given its transactions' senders (20 bytes each, in order), and when
    /// the state root reached is the header's, applies it as <paramref name="apply"/> says.
    /// </summary>
    /// <returns>
    /// The block's result: <c>state root (32) ‖ gas used (8) ‖ requests empty (1) ‖ receipts ‖ accounts written ‖
    /// entities touched</c>, as <c>native/arkiv-ffi/src/wire.rs</c> lays it out. The accounts and entities are read from
    /// the block's own state, so a held block reports what it will commit.
    /// </returns>
    /// <exception cref="ArkivNativeException">
    /// <see cref="ArkivStatus.StateRootMismatch"/> (the message is the root reached), <see cref="ArkivStatus.InvalidBlock"/>,
    /// <see cref="ArkivStatus.NotOnHead"/> when the block's parent is not the head. Nothing is applied on any of them.
    /// </exception>
    /// <remarks>
    /// A block Arkiv charges nothing for (no transaction) changes no state and commits nothing, as on arkiv-reth; it
    /// becomes the head the next block builds on, in memory. After the process restarts the head is the last block that
    /// changed state, so the blocks applied since are applied again (each changes nothing). Held blocks are in memory
    /// too: after a restart they are executed again.
    /// </remarks>
    public static byte[] ExecuteBlock(ulong handle, ReadOnlySpan<byte> blockRlp, ReadOnlySpan<byte> senders, ArkivApply apply)
    {
        fixed (byte* b = blockRlp)
        fixed (byte* s = senders)
        {
            int status = arkiv_execute_block(handle, b, (nuint)blockRlp.Length, s, (nuint)senders.Length, (int)apply, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    /// <summary>
    /// Commits the block <paramref name="blockHash"/> (32 bytes), held by <see cref="ExecuteBlock"/>: it becomes the
    /// store's head, and every other block held at its height or below is dropped.
    /// </summary>
    /// <exception cref="ArkivNativeException">
    /// <see cref="ArkivStatus.NotOnHead"/> when the block is not held over the store's head (never held, dropped by an
    /// earlier adoption, or held before a restart): execute it again to apply it.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="blockHash"/> is not 32 bytes.</exception>
    public static void Adopt(ulong handle, ReadOnlySpan<byte> blockHash)
    {
        if (blockHash.Length != 32) throw new ArgumentException($"A block hash is 32 bytes, not {blockHash.Length}.", nameof(blockHash));
        fixed (byte* h = blockHash)
        {
            int status = arkiv_adopt(handle, h, out byte* ptr, out nuint len);
            Take(status, ptr, len);
        }
    }

    /// <summary>An account at the head.</summary>
    /// <returns><c>exists (1) ‖ balance (32, big-endian) ‖ nonce (8)</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="address"/> is not 20 bytes.</exception>
    public static byte[] Account(ulong handle, ReadOnlySpan<byte> address)
    {
        // The native side reads 20 bytes from the pointer, unchecked.
        RequireAddress(address, nameof(address));
        fixed (byte* a = address)
        {
            int status = arkiv_account(handle, a, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    /// <summary>Runs a call at the head without committing (<c>eth_call</c>, <c>eth_estimateGas</c>).</summary>
    /// <param name="handle">An open chain.</param>
    /// <param name="requestJson">The call, as JSON: <c>from, to, data, value, gas, gasPrice, nonce, blockNumber, timestamp, baseFee, blockGasLimit, beneficiary</c>.</param>
    /// <returns>JSON: <c>{ status: success | revert | halt, gasUsed, output, error }</c>.</returns>
    public static byte[] Call(ulong handle, ReadOnlySpan<byte> requestJson)
    {
        fixed (byte* r = requestJson)
        {
            int status = arkiv_call(handle, r, (nuint)requestJson.Length, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    /// <summary>An <c>arkiv_*</c> JSON-RPC method answered at the head, numbered <paramref name="headNumber"/>.</summary>
    /// <returns>JSON holding <c>result</c> or <c>error</c>, as arkiv-reth answers.</returns>
    public static byte[] Rpc(ulong handle, string method, string parametersJson, ulong headNumber)
    {
        byte[] m = Encoding.UTF8.GetBytes(method);
        byte[] p = Encoding.UTF8.GetBytes(parametersJson);
        fixed (byte* mp = m)
        fixed (byte* pp = p)
        {
            int status = arkiv_rpc(handle, mp, (nuint)m.Length, pp, (nuint)p.Length, headNumber, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    /// <summary>Every account of the GolemDB store in <paramref name="directory"/>, which must not be open in this process.</summary>
    /// <returns>20 bytes per account.</returns>
    public static byte[] StoreAccounts(string directory)
    {
        byte[] dir = Encoding.UTF8.GetBytes(directory);
        fixed (byte* d = dir)
        {
            int status = arkiv_store_accounts(d, (nuint)dir.Length, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    /// <summary>The entity keys the protocol purge of block <paramref name="block"/> names, chosen at the head (its parent) as arkiv-reth chooses them.</summary>
    /// <returns>32 bytes per key.</returns>
    public static byte[] PurgeKeys(ulong handle, ulong block)
    {
        int status = arkiv_purge_keys(handle, block, out byte* ptr, out nuint len);
        return Take(status, ptr, len);
    }

    /// <summary>Opens a build of the block whose header (RLP) is given; its parent must be the head.</summary>
    /// <returns>A session for <see cref="BuildApply"/>, then <see cref="BuildFinish"/> or <see cref="BuildAbort"/>.</returns>
    public static ulong BuildBegin(ulong handle, ReadOnlySpan<byte> headerRlp)
    {
        fixed (byte* h = headerRlp)
        {
            int status = arkiv_build_begin(handle, h, (nuint)headerRlp.Length, out byte* ptr, out nuint len);
            return BinaryPrimitives.ReadUInt64LittleEndian(Take(status, ptr, len));
        }
    }

    /// <summary>Applies one EIP-2718 transaction from <paramref name="sender"/> (20 bytes) to an open build.</summary>
    /// <returns>Its receipt: <c>success (1) ‖ cumulative gas (8) ‖ logs</c>.</returns>
    /// <exception cref="ArkivNativeException"><see cref="ArkivStatus.InvalidBlock"/> with the reason Arkiv's executor refused it; the build goes on without it.</exception>
    /// <exception cref="ArgumentException"><paramref name="sender"/> is not 20 bytes.</exception>
    public static byte[] BuildApply(ulong session, ReadOnlySpan<byte> tx, ReadOnlySpan<byte> sender)
    {
        // The native side reads 20 bytes from the pointer, unchecked.
        RequireAddress(sender, nameof(sender));
        fixed (byte* t = tx)
        fixed (byte* s = sender)
        {
            int status = arkiv_build_apply(session, t, (nuint)tx.Length, s, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    /// <summary>Finishes a build and seals its state without committing it; the session ends.</summary>
    /// <returns>The block's result, laid out as <see cref="ExecuteBlock"/>'s.</returns>
    public static byte[] BuildFinish(ulong session)
    {
        int status = arkiv_build_finish(session, out byte* ptr, out nuint len);
        return Take(status, ptr, len);
    }

    /// <summary>Abandons a build; the session ends.</summary>
    public static void BuildAbort(ulong session) => arkiv_build_abort(session);
}
