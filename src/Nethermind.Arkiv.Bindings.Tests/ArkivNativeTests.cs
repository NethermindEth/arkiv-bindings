// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Arkiv.Native.Test;

/// <summary>The bindings over Rabdi's genesis and blocks 1-700: results equal to the headers, and the errors mapped.</summary>
[TestFixture]
public class ArkivNativeTests
{
    private const string GenesisHash = "a65a2b9e25129f0e8ccdf494dfd66ec744bdf400c806b832b1da86b0b939cbce";
    private const string GenesisRoot = "18f6f20576ca53378a20d9bf891eae550cdfdbc1473b05fe104e48f61720a9ec";

    private sealed record Vector(ulong Number, byte[] Rlp, byte[][] Senders, byte[] StateRoot, ulong GasUsed);

    private static readonly string Genesis = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "rabdi-genesis.json"));
    private static readonly Lazy<Vector[]> Vectors = new(LoadVectors);

    private static Vector[] LoadVectors()
    {
        using GZipStream gzip = new(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "rabdi-vectors-1-700.jsonl.gz")), CompressionMode.Decompress);
        using StreamReader reader = new(gzip);
        List<Vector> vectors = [];
        while (reader.ReadLine() is { } line)
        {
            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement v = json.RootElement;
            vectors.Add(new Vector(
                v.GetProperty("number").GetUInt64(),
                Hex(v.GetProperty("rlp").GetString()!),
                [.. v.GetProperty("senders").EnumerateArray().Select(s => Hex(s.GetString()!))],
                Hex(v.GetProperty("stateRoot").GetString()!),
                v.GetProperty("gasUsed").GetUInt64()));
        }
        return [.. vectors];
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.StartsWith("0x", StringComparison.Ordinal) ? hex[2..] : hex);

    private static ulong OpenChain(string? directory, out byte[] opened)
    {
        opened = ArkivNative.Open(Genesis, directory);
        return BinaryPrimitives.ReadUInt64LittleEndian(opened);
    }

    private static byte[] Execute(ulong handle, Vector block, bool commit = true) =>
        ArkivNative.ExecuteBlock(handle, block.Rlp, [.. block.Senders.SelectMany(s => s)], commit);

    [Test]
    public void Opens_at_rabdis_genesis()
    {
        ulong handle = OpenChain(null, out byte[] opened);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Convert.ToHexStringLower(opened.AsSpan(8, 32)), Is.EqualTo(GenesisRoot));
                Assert.That(Convert.ToHexStringLower(opened.AsSpan(40, 32)), Is.EqualTo(GenesisHash));
                Assert.That(Convert.ToHexStringLower(ArkivNative.Head(handle).AsSpan(8, 32)), Is.EqualTo(GenesisHash));
            }
        }
        finally
        {
            ArkivNative.Close(handle);
        }
    }

    [Test]
    public void Every_block_reaches_its_headers_state_root_and_gas()
    {
        ulong handle = OpenChain(null, out _);
        try
        {
            foreach (Vector block in Vectors.Value)
            {
                byte[] result = Execute(handle, block);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(result.AsSpan(0, 32).ToArray(), Is.EqualTo(block.StateRoot), $"block {block.Number}");
                    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(32)), Is.EqualTo(block.GasUsed), $"block {block.Number}");
                }
            }
            Assert.That(ArkivNative.Head(handle).AsSpan(40, 32).ToArray(), Is.EqualTo(Vectors.Value[^1].StateRoot));
        }
        finally
        {
            ArkivNative.Close(handle);
        }
    }

    [Test]
    public void Errors_carry_their_status()
    {
        ulong handle = OpenChain(null, out _);
        try
        {
            ArkivNativeException offHead = Assert.Throws<ArkivNativeException>(() => Execute(handle, Vectors.Value[1]))!;
            ArkivNativeException garbage = Assert.Throws<ArkivNativeException>(() => ArkivNative.ExecuteBlock(handle, [1, 2, 3], [], commit: true))!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(offHead.Status, Is.EqualTo(ArkivStatus.NotOnHead));
                Assert.That(garbage.Status, Is.EqualTo(ArkivStatus.BadInput));
            }
        }
        finally
        {
            ArkivNative.Close(handle);
        }
    }

    [Test]
    public void A_closed_handle_is_refused_and_closing_twice_is_harmless()
    {
        ulong handle = OpenChain(null, out _);
        ArkivNative.Close(handle);
        ArkivNative.Close(handle);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Assert.Throws<ArkivNativeException>(() => ArkivNative.Head(handle))!.Status, Is.EqualTo(ArkivStatus.BadInput));
            Assert.That(Assert.Throws<ArkivNativeException>(() => ArkivNative.Head(0))!.Status, Is.EqualTo(ArkivStatus.BadInput));
            Assert.That(Assert.Throws<ArkivNativeException>(() => ArkivNative.BuildFinish(12345))!.Status, Is.EqualTo(ArkivStatus.BadInput));
        }
    }

    [Test]
    public void Using_a_library_that_does_not_exist_is_refused() =>
        Assert.That(() => ArkivNative.UseLibrary(Path.Combine(Path.GetTempPath(), "no-such-arkiv-ffi.so")), Throws.TypeOf<FileNotFoundException>());

    [TestCase(0)]
    [TestCase(19)]
    [TestCase(21)]
    public void An_address_of_another_length_is_refused_before_the_native_call(int length)
    {
        ulong handle = OpenChain(null, out _);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => ArkivNative.Account(handle, new byte[length]), Throws.ArgumentException);
                Assert.That(() => ArkivNative.BuildApply(0, [], new byte[length]), Throws.ArgumentException);
            }
        }
        finally
        {
            ArkivNative.Close(handle);
        }
    }

    [Test]
    public void A_store_lists_its_accounts_and_reads_them_back()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arkiv-bindings-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Block 409 carries Rabdi's first plain transfer.
            Vector transfer = Vectors.Value[408];
            ulong handle = OpenChain(directory, out _);
            foreach (Vector block in Vectors.Value.Take(409)) Execute(handle, block);
            ArkivNative.Close(handle);

            byte[] listed = ArkivNative.StoreAccounts(directory);
            byte[] sender = transfer.Senders[1];
            handle = OpenChain(directory, out _);
            try
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(listed.Chunk(20).Any(a => a.SequenceEqual(sender)), Is.True, "the transfer's sender is listed");
                    Assert.That(ArkivNative.Account(handle, sender)[0], Is.EqualTo(1), "and holds an account");
                }
            }
            finally
            {
                ArkivNative.Close(handle);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
