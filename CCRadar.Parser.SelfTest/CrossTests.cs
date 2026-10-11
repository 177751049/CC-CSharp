using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using CCRadar.Parser.Codec;
using CCRadar.Parser.Core;

namespace CCRadar.Parser.SelfTest;

/// <summary>
/// 阶段 2 自测：AssetCrypto(SHAKE-256)/S2cDec/Sna/Blocks/RepDec4/RepDec6/RepMove4
/// + 黄金对拍（Python 权威实现生成的 cross_test.json，逐项语义 diff）。
/// </summary>
public static class CrossTests
{
    public static int Failures;
    public static int Passed;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Interlocked.Increment(ref Passed);
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            Interlocked.Increment(ref Failures);
            Console.WriteLine($"  [失败] {name}");
        }
    }

    /// <summary>JSON 语义比较：数值跨 int/double 精确（1e-9 容差），对象/数组/标量递归。</summary>
    private static bool SemEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonValue av && b is JsonValue bv)
        {
            if (av.TryGetValue<double>(out var ad) && bv.TryGetValue<double>(out var bd))
            {
                // 布尔不是数值。
                if (av.TryGetValue<bool>(out var ab) && bv.TryGetValue<bool>(out var bb)) return ab == bb;
                return Math.Abs(ad - bd) < 1e-9;
            }
            return av.ToJsonString() == bv.ToJsonString();
        }
        if (a is JsonObject ao && b is JsonObject bo)
        {
            if (ao.Count != bo.Count) return false;
            foreach (var (key, value) in ao)
            {
                if (!bo.TryGetPropertyValue(key, out var bv2) || !SemEquals(value, bv2)) return false;
            }
            return true;
        }
        if (a is JsonArray aa && b is JsonArray ba)
        {
            if (aa.Count != ba.Count) return false;
            for (var i = 0; i < aa.Count; i++)
            {
                if (!SemEquals(aa[i], ba[i])) return false;
            }
            return true;
        }
        return false;
    }

    public static void RunAll()
    {
        TestShake256Vectors();
        TestAssetCryptoRoundTrip();
        TestS2cDec();
        TestCrossVectorFile();
        TestPlayerstateSchemaLoad();
    }

    // [10] SHAKE-256 与 Python hashlib 交叉验证。
    private static void TestShake256Vectors()
    {
        Console.WriteLine("[10] SHAKE-256 交叉验证（Python hashlib 向量）");

        // 隔离验证：pad=0x06 时即 SHA3-256，与已知向量对照 → 验证 Keccak-f 置换本体。
        var sha3 = Shake256Xof.HashData("abc"u8, 32, padByte: 0x06);
        var sha3Hex = Convert.ToHexString(sha3).ToLowerInvariant();
        Console.WriteLine($"  sha3(abc) = {sha3Hex}");
        Check(sha3Hex == "3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532",
            "Keccak-f 置换（经 SHA3-256 已知向量）正确");

        // 置换单步对照（官方零状态向量）。
        var permuted = Shake256Xof.DebugPermute(new ulong[25]);
        var permHex = string.Join(" ", permuted.Take(4).Select(v => v.ToString("x16")));
        Console.WriteLine($"  perm(zero) 首四 lane = {permHex}");
        Check(permHex == "f1258f7940e1dde7 84d5ccf933c0478a d598261ea65aa9ee bd1547306f80494d",
            "Keccak-f[1600] 零状态置换与官方向量一致");

        var abc = Shake256Xof.HashData("abc"u8, 32);
        Check(Convert.ToHexString(abc).ToLowerInvariant() ==
              "483366601360a8771c6863080cc4114d8db44530f8f1e1ee4f94ea37e78b5739",
            "shake256(abc, 32B) 与 Python hashlib 一致");

        var key = Convert.FromHexString("1f714614c0f21a3f7d58d069581198da9a56f07a04a6bd6ad4307fd174ee0b2b");
        var empty = Shake256Xof.HashData(key, 16);
        Check(Convert.ToHexString(empty).ToLowerInvariant() == "ed42250758eaf573e4d27f1e9360da29",
            "keystream(空 purpose) 前 16B 一致");
        var flowcache = Shake256Xof.HashData([.. key, .. (ReadOnlySpan<byte>)"flowcache"u8], 16);
        Check(Convert.ToHexString(flowcache).ToLowerInvariant() == "f16db25da2cae16d40ab50efbe2c53c9",
            "keystream(flowcache purpose) 前 16B 一致");
    }

    // [11] AssetCrypto 加解密往返 + NRA1 格式。
    private static void TestAssetCryptoRoundTrip()
    {
        Console.WriteLine("[11] AssetCrypto NRA1 加解密");
        var random = new Random(777);
        var plain = new byte[1000 + random.Next(1000)];
        random.NextBytes(plain);
        var encrypted = AssetCrypto.Encrypt(plain);
        Check(encrypted.AsSpan(0, 4).SequenceEqual(AssetCrypto.Magic), "密文带 NRA1 魔数");
        Check(!encrypted.AsSpan(4).SequenceEqual(plain), "密文体确已混淆");
        var decrypted = AssetCrypto.Decrypt(encrypted);
        Check(decrypted.AsSpan().SequenceEqual(plain), "解密还原逐字节一致");

        // purpose 区分：同一数据不同用途产生不同密文。
        var withPurpose = AssetCrypto.Encrypt(plain, AssetCrypto.PurposeFlowcache);
        Check(!withPurpose.AsSpan(4).SequenceEqual(encrypted.AsSpan(4)), "purpose 派生流互不相同");
        Check(AssetCrypto.Decrypt(withPurpose, AssetCrypto.PurposeFlowcache).AsSpan().SequenceEqual(plain),
            "带 purpose 解密还原一致");

        // 无魔数原样返回（decrypt_blob 语义）。
        Check(AssetCrypto.DecryptBlob(plain).AsSpan().SequenceEqual(plain), "无魔数原样返回");
    }

    // [12] S2C 去混淆。
    private static void TestS2cDec()
    {
        Console.WriteLine("[12] S2C 去混淆");
        var random = new Random(888);
        var table = new byte[512];
        random.NextBytes(table);
        var plaintext = new byte[1300];
        random.NextBytes(plaintext);

        // 编码方向（模型 P[i] = C[i] ^ C[i-1] ^ K[i%512] 的逆）：
        // C[i] = P[i] ^ C[i-1] ^ K[i%512] —— 注意反馈是**密文** C[i-1]（C[-1]=0）。
        var cipher = new byte[plaintext.Length];
        var prevCipher = 0;
        for (var i = 0; i < plaintext.Length; i++)
        {
            cipher[i] = (byte)(plaintext[i] ^ prevCipher ^ table[i & 511]);
            prevCipher = cipher[i];
        }
        var decoded = S2cDec.DecodeS2c(cipher, table);
        Check(decoded.AsSpan().SequenceEqual(plaintext), "decode_s2c 还原一致");

        // recover_table：众数恢复在明文非均匀时有 mode(P) 残差（Python 原注释），
        // 用全 0 明文（P=0 → D=C[i]^C[i-1] 恒等于 K[j]）验证精确恢复路径。
        var zeroPlain = new byte[512];
        var recoveredInput = new List<byte[]>();
        for (var d = 0; d < 40; d++)
        {
            var c2 = new byte[512];
            var prev2 = 0;
            for (var i = 0; i < zeroPlain.Length; i++)
            {
                c2[i] = (byte)(zeroPlain[i] ^ prev2 ^ table[i & 511]);
                prev2 = c2[i];
            }
            recoveredInput.Add(c2);
        }
        var recovered = S2cDec.RecoverTable(recoveredInput, minCount: 8);
        Check(recovered.SequenceEqual(table), "recover_table 众数恢复一致");
    }

    // [13] 黄金对拍：Python 权威向量逐项 diff。
    private static void TestCrossVectorFile()
    {
        Console.WriteLine("[13] 黄金对拍（Python 权威 cross_test.json）");
        var path = Path.Combine(AppContext.BaseDirectory, "cross_test.json");
        if (!File.Exists(path))
        {
            Check(false, $"找不到向量文件 {path}（先运行 tools/gen_cross_test.py）");
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var catalogCache = new Dictionary<string, RepCatalog>();

        foreach (var caseEl in doc.RootElement.EnumerateArray())
        {
            var kind = caseEl.GetProperty("kind").GetString();
            var payload = Convert.FromHexString(caseEl.GetProperty("payload").GetString()!);
            var pbits = caseEl.GetProperty("payload_bits").GetInt32();
            switch (kind)
            {
                case "repdec4":
                case "repdec6":
                {
                    var className = caseEl.GetProperty("class_name").GetString()!;
                    if (!catalogCache.TryGetValue(className, out var catalog))
                    {
                        catalog = ParseCatalogFromJson(caseEl.GetProperty("cmds"), className);
                        catalogCache[className] = catalog;
                    }
                    var (events, position, status) = kind == "repdec4"
                        ? RepDec4.DecodeBlock(payload, 0, pbits, catalog, [])
                        : RepDec6.DecodeBlock(payload, 0, pbits, catalog, []);
                    Check(status == caseEl.GetProperty("expected_status").GetString(),
                        $"[{className}] repdec 状态一致（实际 {status}）");
                    Check(position == caseEl.GetProperty("expected_position").GetInt32(),
                        $"[{className}] 结束位一致（实际 {position}）");
                    var expected = caseEl.GetProperty("expected_events");
                    Check(events.Count == expected.GetArrayLength(),
                        $"[{className}] 事件数一致（实际 {events.Count}）");
                    var allMatch = true;
                    for (var i = 0; i < Math.Min(events.Count, expected.GetArrayLength()); i++)
                    {
                        var actual = JsonSerializer.SerializeToNode(events[i].ToDictionary());
                        var want = JsonSerializer.SerializeToNode(expected[i]);
                        if (!SemEquals(actual, want))
                        {
                            allMatch = false;
                            Console.WriteLine($"    事件 {i} 差异：");
                            Console.WriteLine($"      C#:    {actual?.ToJsonString(new JsonSerializerOptions { WriteIndented = false })}");
                            Console.WriteLine($"      期望:  {want?.ToJsonString()}");
                        }
                    }
                    Check(allMatch, $"[{className}] 事件逐项语义一致");
                    break;
                }
                case "sna":
                {
                    var hasExports = caseEl.GetProperty("has_exports").GetBoolean();
                    var hasMust = caseEl.GetProperty("has_must").GetBoolean();
                    var (info, streamStart) = Sna.ParsePrefix(payload, pbits, hasExports, hasMust);
                    var expectedInfo = caseEl.GetProperty("expected_info");
                    var expectedStart = caseEl.GetProperty("expected_stream_start").GetInt32();
                    Check(streamStart == expectedStart, $"[sna] stream_start 一致（实际 {streamStart}）");
                    var actualNode = JsonSerializer.SerializeToNode(info);
                    var wantNode = JsonSerializer.SerializeToNode(JsonHelpers.ToObject(expectedInfo));
                    Check(SemEquals(actualNode, wantNode), "[sna] parse_prefix 输出语义一致");
                    break;
                }
                case "blocks":
                {
                    var tile = Blocks.Tile(payload, pbits, 0);
                    Check(tile is not null, "[blocks] tile 非 null");
                    if (tile is null) break;
                    var expected = caseEl.GetProperty("expected_blocks");
                    Check(tile.Count == expected.GetArrayLength(),
                        $"[blocks] 块数一致（实际 {tile.Count}）");
                    var allMatch = true;
                    for (var i = 0; i < Math.Min(tile.Count, expected.GetArrayLength()); i++)
                    {
                        var b = tile[i];
                        var e = expected[i];
                        var dict = new Dictionary<string, object?>
                        {
                            ["start"] = b.Start, ["has_rep"] = (long)b.HasRep, ["is_actor"] = (long)b.IsActor,
                            ["sub"] = b.Sub is { } sub ? sub : null,
                            ["stably"] = b.Stably is { } st ? (long)st : null,
                            ["cls"] = b.Cls is { } cls ? cls : null,
                            ["deleted"] = b.Deleted, ["num_bits"] = b.NumBits,
                            ["payload_start"] = b.PayloadStart, ["payload_end"] = b.PayloadEnd,
                        };
                        var actualNode = JsonSerializer.SerializeToNode(dict);
                        var wantNode = JsonSerializer.SerializeToNode(JsonHelpers.ToObject(e));
                        if (!SemEquals(actualNode, wantNode))
                        {
                            allMatch = false;
                            Console.WriteLine($"    块 {i} 差异：C# {actualNode?.ToJsonString()} vs 期望 {wantNode?.ToJsonString()}");
                        }
                    }
                    Check(allMatch, "[blocks] 块逐项语义一致");
                    break;
                }
                case "repmove4":
                {
                    var start = caseEl.GetProperty("start").GetInt32();
                    var end = caseEl.GetProperty("end").GetInt32();
                    var (frame, _) = RepMove4.ReadFrame4(payload, start, end);
                    var actualNode = JsonSerializer.SerializeToNode(frame);
                    var wantNode = JsonSerializer.SerializeToNode(JsonHelpers.ToObject(caseEl.GetProperty("expected_frame")));
                    Check(SemEquals(actualNode, wantNode), "[repmove4] 帧输出语义一致");
                    break;
                }
            }
        }
    }

    /// <summary>从对拍 JSON 的 cmds 数组构造 RepCatalog（同时覆盖 catalog 解析路径）。</summary>
    private static RepCatalog ParseCatalogFromJson(JsonElement cmds, string className)
    {
        var list = new List<RepCmd>();
        foreach (var c in cmds.EnumerateArray())
        {
            list.Add(new RepCmd
            {
                Index = c.GetProperty("index").GetInt32(),
                Type = c.GetProperty("type").GetInt32(),
                EndCmd = c.TryGetProperty("end_cmd", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : null,
                ParentIndex = c.TryGetProperty("parent_index", out var pi) && pi.ValueKind == JsonValueKind.Number ? pi.GetInt32() : 0,
                ObjectOffset = c.TryGetProperty("object_offset", out var oo) && oo.ValueKind == JsonValueKind.Number ? oo.GetInt32() : 0,
            });
        }
        return new RepCatalog { ClassName = className, Cmds = list };
    }

    // [14] 真实 catalog/meta/schema 加载（数据文件完整性）。
    private static void TestPlayerstateSchemaLoad()
    {
        Console.WriteLine("[14] 真实数据文件加载");
        var catalogs = RepDec4.LoadCatalogs();
        var psCatalog = catalogs["BP_DFMPlayerState_C"];
        Console.WriteLine($"  [诊断] PlayerState catalog: cmds={psCatalog.Cmds.Count} parents={psCatalog.Parents.Count} extraKeys={string.Join(",", psCatalog.Extra.Keys)}");
        if (psCatalog.Parents.Count > 0)
        {
            var p0 = psCatalog.Parents[0];
            Console.WriteLine($"  [诊断] parents[0] keys={string.Join(",", p0.Keys)} index={p0.GetValueOrDefault("index")}");
        }
        Check(catalogs.Count > 50, $"RepLayout catalog 加载 {catalogs.Count} 类");
        Check(catalogs.ContainsKey("BP_DFMPlayerState_C"), "含 BP_DFMPlayerState_C");
        Check(catalogs.ContainsKey("BP_SOLPlayerState_C"),
            "BP_SOLPlayerState_C 别名回退生效");

        var propmeta = RepDec4.LoadPropmeta();
        Check(propmeta.Count > 20, $"prop_meta 加载 {propmeta.Count} 类");
        Check(propmeta.ContainsKey("BP_DFMPlayerState_C"), "prop_meta 含 PlayerState");

        var legacySchema = PlayerstateProtocol.Schema("legacy");
        Check(legacySchema.Meta.Count > 100, $"legacy schema meta {legacySchema.Meta.Count} 项");
        Check(legacySchema.Parents.Count > 50, $"legacy schema parents {legacySchema.Parents.Count} 项");
        var currentSchema = PlayerstateProtocol.Schema("current");
        Check(currentSchema.Meta.Count > 100, $"current schema meta {currentSchema.Meta.Count} 项");

        // departure 元数据（current 的 PlayerMatchInfo/ExitState）。
        var hasDeparture = currentSchema.Meta.Values
            .Where(m => m is not null)
            .Any(m => m!.ContainsKey("departure_name"));
        Check(hasDeparture, "current schema 生成 departure_name（PlayerMatchInfo 映射）");

        Check(PlayerstateProtocol.SelectProfile("/Game/BluePrints/GameModes/TestGame/BP_SOLPlayerState") == "current",
            "select_profile 识别 current 包路径");
        Check(PlayerstateProtocol.SelectProfile("Default__BP_DFMPlayerState_C") == "legacy",
            "select_profile 识别 legacy CDO");
        Check(PlayerstateProtocol.SelectProfile("/Game/Other/Unknown") is null,
            "select_profile 拒绝未知路径");
    }
}
