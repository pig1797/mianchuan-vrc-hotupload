using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using LZ4ps;
using SevenZip.Compression.LZMA;

static class Program
{

    sealed class AssetBundleCandidate
    {
        public AssetFileInfo Info;
        public string Name;
    }

    sealed class PipelineCandidate
    {
        public AssetFileInfo Info;
        public string BlueprintId;
        public long GameObjectPathId;
        public string GameObjectName;
        public bool HasAvatarDescriptor;
        public bool IsRootObject;
        public readonly List<AssetBundleCandidate> MatchingBundles = new List<AssetBundleCandidate>();
    }

    sealed class PipelineSelection
    {
        public PipelineCandidate Pipeline;
        public AssetBundleCandidate AssetBundle;
    }

    sealed class LzmaBlockPlan
    {
        public long Offset;
        public long CompressedOffset;
        public uint DecompressedSize;
        public ushort Flags;
        public string CompressedPath;
        public uint CompressedSize;
        public bool ReuseOriginalCompressedData;
    }

    // 世界模式: 处理 wrld_ id + VRCSceneDescriptor, 而不是 avtr_ id + AvatarDescriptor。
    static bool _worldMode;
    static bool _probeWorld;

    static int Main(string[] rawArgs)
    {
        // 提取 --world 标志, 其余按位置参数处理。
        var argList = new System.Collections.Generic.List<string>();
        foreach (var a in rawArgs)
        {
            if (string.Equals(a, "--world", StringComparison.OrdinalIgnoreCase))
                _worldMode = true;
            else if (string.Equals(a, "--probe-world", StringComparison.OrdinalIgnoreCase))
            {
                _probeWorld = true;
                _worldMode = true;
            }
            else
                argList.Add(a);
        }
        var args = argList.ToArray();

        if (args.Length < 3)
        {
            Console.WriteLine("Usage: HotswapTool <in.vrca|in.vrcw> <out> <new_id> [unityVer] [platform] [compression|-1] [blockDirAtEnd] [--world]");
            return 1;
        }

        string inPath = args[0], outPath = args[1], newId = args[2];
        string targetVer = (args.Length >= 4 && !string.IsNullOrEmpty(args[3])) ? args[3] : null;
        int targetPlat = (args.Length >= 5 && !string.IsNullOrEmpty(args[4])) ? int.Parse(args[4]) : -1;
        int requestedCompression = (args.Length >= 6 && !string.IsNullOrEmpty(args[5])) ? int.Parse(args[5]) : -1;
        bool? requestedBlockDirAtEnd = null;
        if (args.Length >= 7 && !string.IsNullOrEmpty(args[6]))
            requestedBlockDirAtEnd = bool.Parse(args[6]);
        string tpk = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "class.tpk");

        var probeManager = new AssetsManager();
        var probeBundle = probeManager.LoadBundleFile(inPath, false);
        var originalFlags = probeBundle.file.Header.FileStreamHeader.Flags;
        var originalCompression = probeBundle.originalCompression;
        var originalBlockInfos = CloneBlockInfos(probeBundle.file.BlockAndDirInfo.BlockInfos);
        bool originalBlockDirAtEnd = HasFlag(originalFlags, AssetBundleFSHeaderFlags.BlockAndDirAtEnd);
        // 默认跟随原包的 blockDirAtEnd，避免无必要地改变 UnityFS 结构布局。
        // 仅命令行显式指定时才覆盖。
        bool outputBlockDirAtEnd = requestedBlockDirAtEnd ?? originalBlockDirAtEnd;
        var workingInputPath = inPath;
        var sourceUnpackedPath = "";
        // Keep the canonical AssetsTools.NET path as the production default.
        // The custom parallel UnityFS writer is retained only for local format tests.
        var useNativeWorldLzma = _worldMode &&
            originalCompression == AssetBundleCompressionType.LZMA &&
            originalBlockInfos.Length == 1 &&
            NativeWorldLzmaEnabled() &&
            !IsTruthySetting(Environment.GetEnvironmentVariable("HOTUPLOAD_LEGACY_LZMA_PACK"));
        if (useNativeWorldLzma)
        {
            sourceUnpackedPath = outPath + ".source-unpacked";
            try
            {
                NativeUnpackWorldLzmaSingleBlock(probeBundle.file, inPath, sourceUnpackedPath,
                                                 originalFlags, originalBlockInfos, originalBlockDirAtEnd);
                workingInputPath = sourceUnpackedPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAST] Native world LZMA unpack failed; falling back to the proven loader: " + ex.Message);
                SafeDelete(sourceUnpackedPath);
                sourceUnpackedPath = "";
            }
        }
        else if (originalCompression == AssetBundleCompressionType.LZMA &&
            originalBlockInfos.Length > 1 &&
            !IsTruthySetting(Environment.GetEnvironmentVariable("HOTUPLOAD_LEGACY_LZMA_PACK")))
        {
            sourceUnpackedPath = outPath + ".source-unpacked";
            try
            {
                ParallelUnpackLzmaBundle(probeBundle.file, inPath, sourceUnpackedPath,
                                         originalFlags, originalBlockInfos, originalBlockDirAtEnd);
                workingInputPath = sourceUnpackedPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAST] Parallel LZMA unpack failed; falling back to the proven loader: " + ex.Message);
                SafeDelete(sourceUnpackedPath);
                sourceUnpackedPath = "";
            }
        }
        probeBundle.file.Close();

        if (_probeWorld)
        {
            try
            {
                ProbeWorldMetadata(workingInputPath, tpk);
                return 0;
            }
            finally
            {
                SafeDelete(sourceUnpackedPath);
            }
        }

        var outputCompression = requestedCompression >= 0
            ? (AssetBundleCompressionType)requestedCompression
            : originalCompression;

        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        Console.WriteLine("[1] Loading bundle (LZMA bundles can take 1-3 minutes)...");
        var bun = am.LoadBundleFile(workingInputPath);

        if (_worldMode)
        {
            RunWorldMode(am, bun, workingInputPath, outPath, newId, targetVer, targetPlat,
                         outputCompression, outputBlockDirAtEnd, originalFlags,
                         originalBlockInfos, sourceUnpackedPath, tpk);
            return 0;
        }

        var afi = am.LoadAssetsFileFromBundle(bun, 0, true);
        am.LoadClassDatabaseFromPackage(bun.file.Header.EngineVersion);

        Console.WriteLine("    Header.Version=" + bun.file.Header.Version +
                          " UnityVer=" + afi.file.Metadata.UnityVersion +
                          " Plat=" + afi.file.Metadata.TargetPlatform);
        Console.WriteLine("    Original compression=" + originalCompression +
                          " blockDirAtEnd=" + originalBlockDirAtEnd +
                          " flags=" + (int)originalFlags);

        var pipelineSelection = FindAuthoritativePipeline(am, afi);
        string oldId = pipelineSelection.Pipeline.BlueprintId;
        if (!IsStandardBlueprintId(newId))
            throw new InvalidOperationException(_worldMode
                ? "Target world id is not a standard wrld_ id."
                : "Target avatar id is not a standard avtr_ id.");

        var modifiedAssetIds = new HashSet<long>
        {
            pipelineSelection.Pipeline.Info.PathId,
            pipelineSelection.Pipeline.GameObjectPathId,
            pipelineSelection.AssetBundle.Info.PathId
        };
        var preservedInternalStrings = CaptureAvatarIdStrings(am, afi, oldId, modifiedAssetIds);
        Console.WriteLine("    Preserving internal avatar-id strings outside upload metadata: " + preservedInternalStrings.Count);

        if (targetVer != null)
        {
            bun.file.Header.EngineVersion = targetVer;
            afi.file.Metadata.UnityVersion = targetVer;
            Console.WriteLine("    Unity version -> " + targetVer);
        }
        if (targetPlat >= 0)
        {
            afi.file.Metadata.TargetPlatform = (uint)targetPlat;
            Console.WriteLine("    Platform -> " + targetPlat);
        }

        foreach (var inf in afi.file.GetAssetsOfType((AssetClassID)114))
        {
            if (inf.PathId != pipelineSelection.Pipeline.Info.PathId) continue;
            AssetTypeValueField bf;
            try { bf = am.GetBaseField(afi, inf); } catch { continue; }

            var bp = bf["blueprintId"];
            if (bp == null || bp.FieldName == "DUMMY") continue;

            if (!string.Equals(bp.AsString, oldId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Authoritative PipelineManager changed during structured patch preparation.");
            bp.AsString = newId;

            inf.SetNewData(bf);
            Console.WriteLine("    PipelineManager blueprintId: " + oldId + " -> " + newId);
        }

        foreach (var inf in afi.file.GetAssetsOfType((AssetClassID)142))
        {
            if (inf.PathId != pipelineSelection.AssetBundle.Info.PathId) continue;
            var bf = am.GetBaseField(afi, inf);
            var assetBundleChanges = 0;
            ReplaceStringsRecursive(bf, oldId, newId, ref assetBundleChanges);
            if (assetBundleChanges < 2)
                throw new InvalidOperationException("AssetBundle metadata did not contain the expected prefab/bundle identity fields.");
            inf.SetNewData(bf);
            Console.WriteLine("    AssetBundle metadata fields changed=" + assetBundleChanges);
        }

        string expectedRootName = null;
        foreach (var inf in afi.file.GetAssetsOfType((AssetClassID)1))
        {
            if (inf.PathId != pipelineSelection.Pipeline.GameObjectPathId) continue;
            var bf = am.GetBaseField(afi, inf);
            var nm = bf["m_Name"];
            if (nm == null || nm.FieldName == "DUMMY")
                throw new InvalidOperationException("Avatar root GameObject does not expose a writable name field.");

            var currentRootName = ReadString(nm);
            if (ContainsIgnoreCase(currentRootName, oldId))
            {
                expectedRootName = ReplaceOrdinalIgnoreCase(currentRootName, oldId, newId);
                Console.WriteLine("    GameObject name -> new id");
            }
            else
            {
                expectedRootName = DeriveRootPrefabName(pipelineSelection.AssetBundle.Name, oldId, newId);
                Console.WriteLine("    GameObject name did not contain the old id; normalized from associated AssetBundle metadata");
            }

            nm.AsString = expectedRootName;
            inf.SetNewData(bf);
        }
        if (string.IsNullOrEmpty(expectedRootName))
            throw new InvalidOperationException("Avatar root GameObject could not be updated.");

        Console.WriteLine("[2] Writing modified assets file...");
        bun.file.BlockAndDirInfo.DirectoryInfos[0].SetNewData(afi.file);

        string rawPath = outPath + ".raw";
        Console.WriteLine("[3] Writing raw bundle -> " + rawPath);
        using (var w = new AssetsFileWriter(rawPath))
        {
            bun.file.Write(w, 0L);
        }
        bun.file.Close();
        Console.WriteLine("    Raw size=" + new FileInfo(rawPath).Length);

        var cab = NativeHotswapBridge.RewriteCabNamesInFile(
            rawPath,
            GetCabOverrideSetting(),
            IsTruthySetting(Environment.GetEnvironmentVariable("HOTUPLOAD_RANDOMIZE_CAB")),
            newId);
        if (!cab.Success)
            throw new InvalidOperationException(cab.Error);
        Console.WriteLine("    CAB rewrite: replacements=" + cab.CabReplacements + " " + cab.Summary);
        VerifyAuthoritativeSelection(rawPath, oldId, newId, expectedRootName, tpk, modifiedAssetIds, preservedInternalStrings);

        var compression = outputCompression;
        bool blockDirAtEnd = outputBlockDirAtEnd;

        Console.WriteLine("[4] Pack(compression=" + compression + ", blockDirAtEnd=" + blockDirAtEnd + ") -> " + outPath);
        if (compression == AssetBundleCompressionType.LZMA)
            Console.WriteLine("    LZMA repack can take 1-3 minutes; please wait...");
        var am2 = new AssetsManager();
        var bun2 = am2.LoadBundleFile(rawPath);
        PackBundle(bun2.file, rawPath, outPath, compression, blockDirAtEnd,
                   originalFlags, originalBlockInfos, false, inPath, sourceUnpackedPath);
        bun2.file.Close();
        SafeDelete(sourceUnpackedPath);

        long finalSz = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
        Console.WriteLine("[5] Done. Output size=" + finalSz);
        return 0;
    }


    private static void ProbeWorldMetadata(string inputPath, string tpk)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var bun = am.LoadBundleFile(inputPath);
        try
        {
            am.LoadClassDatabaseFromPackage(bun.file.Header.EngineVersion);
            string worldId = "";
            string sceneName = "";
            string unityVersion = bun.file.Header.EngineVersion ?? "";
            uint platform = 0;
            int dirCount = bun.file.BlockAndDirInfo == null || bun.file.BlockAndDirInfo.DirectoryInfos == null
                ? 0 : bun.file.BlockAndDirInfo.DirectoryInfos.Count;

            for (int index = 0; index < dirCount; index++)
            {
                var dirName = bun.file.BlockAndDirInfo.DirectoryInfos[index].Name ?? "";
                if (IsResourceEntry(dirName)) continue;
                AssetsFileInstance afi;
                try { afi = am.LoadAssetsFileFromBundle(bun, index, true); }
                catch { continue; }
                if (afi == null || afi.file == null) continue;
                if (platform == 0) platform = afi.file.Metadata.TargetPlatform;
                if (string.IsNullOrEmpty(unityVersion)) unityVersion = afi.file.Metadata.UnityVersion;

                foreach (var inf in afi.file.GetAssetsOfType((AssetClassID)114))
                {
                    AssetTypeValueField bf;
                    try { bf = am.GetBaseField(afi, inf); } catch { continue; }
                    var bp = bf["blueprintId"];
                    if (bp == null || bp.FieldName == "DUMMY") continue;
                    var value = ReadString(bp);
                    if (!IsStandardWorldId(value)) continue;
                    if (!string.IsNullOrEmpty(worldId) &&
                        !string.Equals(worldId, value, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Multiple distinct world ids were found while probing.");
                    worldId = value;
                    sceneName = dirName;
                }
            }

            if (string.IsNullOrEmpty(worldId))
                throw new InvalidOperationException("No world PipelineManager was found while probing.");
            Console.WriteLine("WORLD_PROBE|platform=" + platform +
                              "|worldId=" + ProbeValue(worldId) +
                              "|unity=" + ProbeValue(unityVersion) +
                              "|scene=" + ProbeValue(sceneName));
        }
        finally
        {
            bun.file.Close();
        }
    }

    private static string ProbeValue(string value)
    {
        return (value ?? "").Replace("|", "_").Replace("\r", " ").Replace("\n", " ");
    }

    // =====================================================================
    //  世界模式: 世界的 wrld_ PipelineManager 通常在含 SceneDescriptor 的
    //  serialized file 里(不一定是 index 0), 且 bundle 名不含 world id。
    //  因此只需在正确的 serialized file 内改写唯一的 wrld_ blueprintId 字段,
    //  不改 GameObject 名 / AssetBundle 元数据(世界的这些都不含 id)。
    // =====================================================================
    private static void RunWorldMode(
        AssetsManager am, BundleFileInstance bun, string inPath, string outPath, string newId,
        string targetVer, int targetPlat, AssetBundleCompressionType outputCompression,
        bool outputBlockDirAtEnd, AssetBundleFSHeaderFlags originalFlags,
        AssetBundleBlockInfo[] originalBlockInfos, string sourceUnpackedPath,
        string tpk)
    {
        if (!IsStandardWorldId(newId))
            throw new InvalidOperationException("Target world id is not a standard wrld_ id.");

        Console.WriteLine("    Header.Version=" + bun.file.Header.Version +
                          " EngineVer=" + bun.file.Header.EngineVersion);

        am.LoadClassDatabaseFromPackage(bun.file.Header.EngineVersion);
        int dirCount = bun.file.BlockAndDirInfo == null || bun.file.BlockAndDirInfo.DirectoryInfos == null
            ? 0 : bun.file.BlockAndDirInfo.DirectoryInfos.Count;
        Console.WriteLine("    Bundle directory entries=" + dirCount);

        // 必须解析定位: 世界里 wrld_ id 可能作为贴图名/Udon字符串等合法存在(实测见到
        // "VRChat_..._wrld_706b1941..." 的截图文件名), 纯字节全局替换会改坏这些资源。
        // 因此只能解析出 PipelineManager 组件、精确改它的 blueprintId 字段。
        int targetDirIndex = -1;
        AssetsFileInstance targetAfi = null;
        AssetFileInfo targetPipeline = null;
        string oldId = null;

        for (int index = 0; index < dirCount; index++)
        {
            var dirName = bun.file.BlockAndDirInfo.DirectoryInfos[index].Name ?? "";
            if (IsResourceEntry(dirName)) continue;

            AssetsFileInstance afi;
            try { afi = am.LoadAssetsFileFromBundle(bun, index, true); }
            catch (Exception ex)
            {
                Console.WriteLine("    Skip dir " + index + " (" + dirName + "): " + ex.GetType().Name);
                continue;
            }
            if (afi == null || afi.file == null)
            {
                Console.WriteLine("    Skip dir " + index + " (" + dirName + "): not a serialized assets file");
                continue;
            }

            foreach (var inf in afi.file.GetAssetsOfType((AssetClassID)114))
            {
                AssetTypeValueField bf;
                try { bf = am.GetBaseField(afi, inf); } catch { continue; }
                var bp = bf["blueprintId"];
                if (bp == null || bp.FieldName == "DUMMY") continue;
                var value = ReadString(bp);
                if (!IsStandardWorldId(value)) continue;

                if (targetPipeline != null && !string.Equals(value, oldId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Multiple distinct wrld_ PipelineManager ids found; refusing ambiguous world hotswap.");

                targetDirIndex = index;
                targetAfi = afi;
                targetPipeline = inf;
                oldId = value;
            }

            if (targetPipeline != null)
            {
                Console.WriteLine("    Found world PipelineManager in dir " + index +
                                  " (" + dirName + "), id=" + oldId);
                break;
            }
        }

        if (targetPipeline == null || targetAfi == null)
            throw new InvalidOperationException(
                "No PipelineManager with a wrld_ blueprintId was found in the bundle.");

        Console.WriteLine("    World target platform detected: " + targetAfi.file.Metadata.TargetPlatform);
        if (targetPlat >= 0 && targetAfi.file.Metadata.TargetPlatform != (uint)targetPlat)
            throw new InvalidOperationException(
                "World target platform mismatch: expected=" + targetPlat +
                " actual=" + targetAfi.file.Metadata.TargetPlatform + ".");
        if (targetPlat >= 0)
            Console.WriteLine("    World target platform verified: " + targetPlat);

        {
            AssetTypeValueField bf = am.GetBaseField(targetAfi, targetPipeline);
            var bp = bf["blueprintId"];
            if (!string.Equals(bp.AsString, oldId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("World PipelineManager changed during patch preparation.");
            bp.AsString = newId;
            targetPipeline.SetNewData(bf);
            Console.WriteLine("    PipelineManager blueprintId: " + oldId + " -> " + newId);
        }

        Console.WriteLine("[2] Writing modified assets file (dir " + targetDirIndex + ")...");
        bun.file.BlockAndDirInfo.DirectoryInfos[targetDirIndex].SetNewData(targetAfi.file);

        bool forceCab = IsTruthySetting(Environment.GetEnvironmentVariable("HOTUPLOAD_RANDOMIZE_CAB")) ||
                        !string.IsNullOrWhiteSpace(GetCabOverrideSetting());
        string rawPath = outPath + ".raw";
        Console.WriteLine("[3] Writing raw bundle -> " + rawPath);
        using (var w = new AssetsFileWriter(rawPath))
        {
            bun.file.Write(w, 0L);
        }
        bun.file.Close();
        Console.WriteLine("    Raw size=" + new FileInfo(rawPath).Length);

        // 世界与头像不同: 世界的 AssetBundle 名 (scene-standalonewindows64-*.vrcw)
        // 不含 world id, 我们也不改它 —— 因此原始 CAB (= CAB-MD4(bundleName)) 依然自洽,
        // 默认无需重算。头像必须重算是因为其 bundle 名含 avatar id。
        // 仍显式校验票据, 保持授权闸门; 需要时可用 HOTUPLOAD_RANDOMIZE_CAB=1 强制随机化 CAB。
        if (forceCab)
        {
            var cab = NativeHotswapBridge.RewriteCabNamesInFile(
                rawPath,
                GetCabOverrideSetting(),
                IsTruthySetting(Environment.GetEnvironmentVariable("HOTUPLOAD_RANDOMIZE_CAB")),
                newId);
            if (!cab.Success)
                throw new InvalidOperationException(cab.Error);
            Console.WriteLine("    CAB rewrite: replacements=" + cab.CabReplacements + " " + cab.Summary);
        }
        else
        {
            // 世界模式不重算 CAB。此处【不再】重复校验票据:
            // 票据已在进入本流程前(Main 开头)校验过一次,授权闸门已把住;
            // 大世界解包/重打包耗时可能超过票据 20 分钟窗口(实测 1.36GB 世界解包就要 14+ 分钟),
            // 若在此再校验会因"处理太久"误判 authorization refresh required 而失败。
            // 上传侧还有主程序 /confirm 指纹确认(fail-closed)兜底,授权不会被绕过。
            Console.WriteLine("    CAB rewrite skipped (world bundle name unchanged; original CAB stays valid).");
        }

        // 校验: 重新解析确认新 world id 就位且唯一。
        VerifyWorldSelection(rawPath, newId, tpk);

        Console.WriteLine("[4] Pack(compression=" + outputCompression + ", blockDirAtEnd=" + outputBlockDirAtEnd + ") -> " + outPath);
        if (outputCompression == AssetBundleCompressionType.LZMA)
            Console.WriteLine("    LZMA repack can take 1-3 minutes; please wait...");
        var am2 = new AssetsManager();
        var bun2 = am2.LoadBundleFile(rawPath);
        PackBundle(bun2.file, rawPath, outPath, outputCompression, outputBlockDirAtEnd,
                   originalFlags, originalBlockInfos, true, inPath, sourceUnpackedPath);
        bun2.file.Close();
        SafeDelete(sourceUnpackedPath);

        long finalSz = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
        Console.WriteLine("[5] Done. Output size=" + finalSz);

    }

    private static void NativeUnpackWorldLzmaSingleBlock(
        AssetBundleFile sourceBundle,
        string inputPath,
        string outputPath,
        AssetBundleFSHeaderFlags originalFlags,
        AssetBundleBlockInfo[] originalBlockInfos,
        bool blockDirAtEnd)
    {
        if (originalBlockInfos == null || originalBlockInfos.Length != 1 ||
            originalBlockInfos[0].GetCompressionType() != 1)
            throw new InvalidOperationException("Native world unpack requires one LZMA block.");

        var toolDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        var xzPath = Path.Combine(toolDir, "xz.exe");
        if (!File.Exists(xzPath))
            throw new FileNotFoundException("Native xz decoder not found.", xzPath);

        var block = originalBlockInfos[0];
        if (block.CompressedSize <= 5)
            throw new InvalidOperationException("UnityFS LZMA block is too short.");
        var sourceDataOffset = sourceBundle.Header.GetFileDataOffset();
        var lzmaPath = outputPath + ".native-input-" + Guid.NewGuid().ToString("N") + ".lzma";
        var dataPath = outputPath + ".native-data-" + Guid.NewGuid().ToString("N") + ".tmp";
        var buildingPath = outputPath + ".building-" + Guid.NewGuid().ToString("N");

        try
        {
            Console.WriteLine("[FAST] Native single-block world LZMA unpack: compressed=" +
                              block.CompressedSize + " data=" + block.DecompressedSize);
            using (var source = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                               1024 * 1024, FileOptions.SequentialScan))
            using (var target = new FileStream(lzmaPath, FileMode.CreateNew, FileAccess.Write,
                                               FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                source.Position = sourceDataOffset;
                var properties = new byte[5];
                if (ReadExactOrLess(source, properties, properties.Length) != properties.Length)
                    throw new EndOfStreamException("Unable to read UnityFS LZMA properties.");
                target.Write(properties, 0, properties.Length);
                WriteUInt64LittleEndian(target, block.DecompressedSize);
                using (var limited = new LimitedReadStream(source, block.CompressedSize - 5L))
                    limited.CopyTo(target, 1024 * 1024);
                target.Flush();
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = xzPath,
                Arguments = "--format=lzma --decompress --single-stream --stdout \"" +
                            lzmaPath.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var process = new System.Diagnostics.Process())
            {
                process.StartInfo = psi;
                if (!process.Start())
                    throw new InvalidOperationException("Unable to start native xz decoder.");
                var stderrTask = Task.Factory.StartNew(delegate { return process.StandardError.ReadToEnd(); });
                using (var target = new FileStream(dataPath, FileMode.CreateNew, FileAccess.Write,
                                                   FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                {
                    process.StandardOutput.BaseStream.CopyTo(target, 1024 * 1024);
                    target.Flush();
                }
                process.WaitForExit();
                var stderr = stderrTask.Result;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Native xz decoder exited with code " +
                                                        process.ExitCode + ": " + stderr);
            }

            if (new FileInfo(dataPath).Length != block.DecompressedSize)
                throw new InvalidOperationException("Native LZMA decoded length mismatch: " +
                                                    new FileInfo(dataPath).Length + " != " + block.DecompressedSize);

            var unpackedBlock = new AssetBundleBlockInfo
            {
                DecompressedSize = block.DecompressedSize,
                CompressedSize = block.DecompressedSize,
                Flags = (ushort)(block.Flags & ~0x3f)
            };
            var bundleInfo = new AssetBundleBlockAndDirInfo
            {
                Hash = new Hash128(new byte[16]),
                BlockInfos = new[] { unpackedBlock },
                DirectoryInfos = sourceBundle.BlockAndDirInfo.DirectoryInfos
            };
            byte[] infoBytes;
            using (var infoStream = new MemoryStream())
            using (var infoWriter = new AssetsFileWriter(infoStream))
            {
                infoWriter.BigEndian = true;
                bundleInfo.Write(infoWriter);
                infoBytes = infoStream.ToArray();
            }
            var compressedInfo = LZ4Codec.Encode32HC(infoBytes, 0, infoBytes.Length);
            if (compressedInfo == null || compressedInfo.Length == 0)
                throw new InvalidOperationException("Unable to compress native-unpacked UnityFS directory.");

            var retainedFlags = originalFlags &
                (AssetBundleFSHeaderFlags.OldWebPluginCompatibility |
                 AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart);
            var outputFlags = AssetBundleFSHeaderFlags.LZ4HCCompressed |
                              AssetBundleFSHeaderFlags.HasDirectoryInfo |
                              retainedFlags;
            if (blockDirAtEnd)
                outputFlags |= AssetBundleFSHeaderFlags.BlockAndDirAtEnd;

            var outputHeader = new AssetBundleHeader
            {
                Signature = sourceBundle.Header.Signature,
                Version = sourceBundle.Header.Version,
                GenerationVersion = sourceBundle.Header.GenerationVersion,
                EngineVersion = sourceBundle.Header.EngineVersion,
                FileStreamHeader = new AssetBundleFSHeader
                {
                    TotalFileSize = 0,
                    CompressedSize = (uint)compressedInfo.Length,
                    DecompressedSize = (uint)infoBytes.Length,
                    Flags = outputFlags
                }
            };

            using (var writer = new AssetsFileWriter(buildingPath))
            {
                outputHeader.Write(writer);
                if (outputHeader.Version >= 7) writer.Align16();
                if (!blockDirAtEnd)
                {
                    writer.Write(compressedInfo);
                    if ((outputFlags & AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart) != 0)
                        writer.Align16();
                }
                using (var input = new FileStream(dataPath, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                    input.CopyTo(writer.BaseStream, 1024 * 1024);
                if (blockDirAtEnd)
                    writer.Write(compressedInfo);
                outputHeader.FileStreamHeader.TotalFileSize = writer.Position;
                writer.Position = 0;
                outputHeader.Write(writer);
                writer.BaseStream.Flush();
            }

            VerifyUnpackedBundle(buildingPath, block.DecompressedSize, 1,
                                 sourceBundle.BlockAndDirInfo.DirectoryInfos.Count);
            SafeDelete(outputPath);
            File.Move(buildingPath, outputPath);
            Console.WriteLine("[FAST] Native world LZMA unpack complete.");
        }
        finally
        {
            SafeDelete(lzmaPath);
            SafeDelete(dataPath);
            SafeDelete(buildingPath);
        }
    }

    private static void WriteUInt64LittleEndian(Stream stream, ulong value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void ParallelUnpackLzmaBundle(
        AssetBundleFile sourceBundle,
        string inputPath,
        string outputPath,
        AssetBundleFSHeaderFlags originalFlags,
        AssetBundleBlockInfo[] originalBlockInfos,
        bool blockDirAtEnd)
    {
        if (originalBlockInfos == null || originalBlockInfos.Length < 2)
            throw new InvalidOperationException("Parallel unpack requires multiple LZMA blocks.");

        var plans = new List<LzmaBlockPlan>();
        long compressedOffset = 0;
        long decompressedOffset = 0;
        for (var i = 0; i < originalBlockInfos.Length; i++)
        {
            if (originalBlockInfos[i].GetCompressionType() != 1)
                throw new InvalidOperationException("Mixed or non-LZMA data blocks are not eligible for parallel unpack.");
            plans.Add(new LzmaBlockPlan
            {
                Offset = decompressedOffset,
                CompressedOffset = compressedOffset,
                DecompressedSize = originalBlockInfos[i].DecompressedSize,
                CompressedSize = originalBlockInfos[i].CompressedSize,
                Flags = (ushort)(originalBlockInfos[i].Flags & ~0x3f)
            });
            compressedOffset += originalBlockInfos[i].CompressedSize;
            decompressedOffset += originalBlockInfos[i].DecompressedSize;
        }

        var sourceDataOffset = sourceBundle.Header.GetFileDataOffset();
        var maxThreads = GetPositiveIntSetting("HOTUPLOAD_LZMA_THREADS",
            Math.Max(1, Math.Min(4, Environment.ProcessorCount / 3)));
        maxThreads = Math.Max(1, Math.Min(Math.Min(8, plans.Count), maxThreads));
        Console.WriteLine("[FAST] Parallel LZMA unpack: blocks=" + plans.Count +
                          " workers=" + maxThreads +
                          " data=" + decompressedOffset);

        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads };
            Parallel.For(0, plans.Count, options, delegate(int index)
            {
                var plan = plans[index];
                plan.CompressedPath = outputPath + ".unpack-" + index + "-" + Guid.NewGuid().ToString("N") + ".tmp";
                using (var source = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                   1024 * 1024, FileOptions.SequentialScan))
                using (var target = new FileStream(plan.CompressedPath, FileMode.CreateNew, FileAccess.Write,
                                                   FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                {
                    source.Position = sourceDataOffset + plan.CompressedOffset;
                    SevenZipHelper.StreamDecompress(source, target, plan.CompressedSize, plan.DecompressedSize);
                    target.Flush();
                }
                var outputLength = new FileInfo(plan.CompressedPath).Length;
                if (outputLength != plan.DecompressedSize)
                    throw new InvalidOperationException("Decompressed LZMA block length mismatch: " + outputLength);
                Console.WriteLine("[FAST] LZMA unpack block " + (index + 1) + "/" + plans.Count + " complete.");
            });

            var unpackedBlockInfos = new AssetBundleBlockInfo[plans.Count];
            for (var i = 0; i < plans.Count; i++)
            {
                unpackedBlockInfos[i] = new AssetBundleBlockInfo
                {
                    DecompressedSize = plans[i].DecompressedSize,
                    CompressedSize = plans[i].DecompressedSize,
                    Flags = plans[i].Flags
                };
            }

            var bundleInfo = new AssetBundleBlockAndDirInfo
            {
                Hash = new Hash128(new byte[16]),
                BlockInfos = unpackedBlockInfos,
                DirectoryInfos = sourceBundle.BlockAndDirInfo.DirectoryInfos
            };
            byte[] infoBytes;
            using (var infoStream = new MemoryStream())
            using (var infoWriter = new AssetsFileWriter(infoStream))
            {
                infoWriter.BigEndian = true;
                bundleInfo.Write(infoWriter);
                infoBytes = infoStream.ToArray();
            }
            var compressedInfo = LZ4Codec.Encode32HC(infoBytes, 0, infoBytes.Length);
            if (compressedInfo == null || compressedInfo.Length == 0)
                throw new InvalidOperationException("Unable to compress unpacked UnityFS directory.");

            var retainedFlags = originalFlags &
                (AssetBundleFSHeaderFlags.OldWebPluginCompatibility |
                 AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart);
            var outputFlags = AssetBundleFSHeaderFlags.LZ4HCCompressed |
                              AssetBundleFSHeaderFlags.HasDirectoryInfo |
                              retainedFlags;
            if (blockDirAtEnd)
                outputFlags |= AssetBundleFSHeaderFlags.BlockAndDirAtEnd;

            var outputHeader = new AssetBundleHeader
            {
                Signature = sourceBundle.Header.Signature,
                Version = sourceBundle.Header.Version,
                GenerationVersion = sourceBundle.Header.GenerationVersion,
                EngineVersion = sourceBundle.Header.EngineVersion,
                FileStreamHeader = new AssetBundleFSHeader
                {
                    TotalFileSize = 0,
                    CompressedSize = (uint)compressedInfo.Length,
                    DecompressedSize = (uint)infoBytes.Length,
                    Flags = outputFlags
                }
            };

            var buildingPath = outputPath + ".building-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var writer = new AssetsFileWriter(buildingPath))
                {
                    outputHeader.Write(writer);
                    if (outputHeader.Version >= 7) writer.Align16();
                    if (!blockDirAtEnd)
                    {
                        writer.Write(compressedInfo);
                        if ((outputFlags & AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart) != 0)
                            writer.Align16();
                    }
                    for (var i = 0; i < plans.Count; i++)
                    {
                        using (var input = new FileStream(plans[i].CompressedPath, FileMode.Open, FileAccess.Read,
                                                          FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                            input.CopyTo(writer.BaseStream, 1024 * 1024);
                    }
                    if (blockDirAtEnd)
                        writer.Write(compressedInfo);
                    outputHeader.FileStreamHeader.TotalFileSize = writer.Position;
                    writer.Position = 0;
                    outputHeader.Write(writer);
                    writer.BaseStream.Flush();
                }

                VerifyUnpackedBundle(buildingPath, decompressedOffset, plans.Count,
                                     sourceBundle.BlockAndDirInfo.DirectoryInfos.Count);
                SafeDelete(outputPath);
                File.Move(buildingPath, outputPath);
            }
            finally
            {
                SafeDelete(buildingPath);
            }
        }
        finally
        {
            for (var i = 0; i < plans.Count; i++)
                SafeDelete(plans[i].CompressedPath);
        }
    }

    private static void VerifyUnpackedBundle(
        string path, long expectedDataLength, int expectedBlockCount, int expectedDirectoryCount)
    {
        var manager = new AssetsManager();
        var bundle = manager.LoadBundleFile(path, false);
        try
        {
            if (bundle.file.DataIsCompressed)
                throw new InvalidOperationException("Parallel-unpacked bundle still reports compressed data.");
            var blocks = bundle.file.BlockAndDirInfo.BlockInfos;
            if (blocks == null || blocks.Length != expectedBlockCount)
                throw new InvalidOperationException("Parallel-unpacked block count mismatch.");
            long length = 0;
            for (var i = 0; i < blocks.Length; i++)
            {
                if (blocks[i].GetCompressionType() != 0)
                    throw new InvalidOperationException("Parallel-unpacked bundle contains a compressed data block.");
                length += blocks[i].DecompressedSize;
            }
            if (length != expectedDataLength)
                throw new InvalidOperationException("Parallel-unpacked data length mismatch.");
            if (bundle.file.BlockAndDirInfo.DirectoryInfos.Count != expectedDirectoryCount)
                throw new InvalidOperationException("Parallel-unpacked directory count mismatch.");
            if (bundle.file.Header.FileStreamHeader.TotalFileSize != new FileInfo(path).Length)
                throw new InvalidOperationException("Parallel-unpacked header size mismatch.");
        }
        finally
        {
            bundle.file.Close();
        }
        Console.WriteLine("[FAST] Parallel LZMA unpack structure verified.");
    }

    private static AssetBundleBlockInfo[] CloneBlockInfos(AssetBundleBlockInfo[] source)
    {
        if (source == null) return new AssetBundleBlockInfo[0];
        var result = new AssetBundleBlockInfo[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            result[i] = new AssetBundleBlockInfo
            {
                DecompressedSize = source[i].DecompressedSize,
                CompressedSize = source[i].CompressedSize,
                Flags = source[i].Flags
            };
        }
        return result;
    }

    private static void PackBundle(
        AssetBundleFile bundle,
        string rawPath,
        string outPath,
        AssetBundleCompressionType compression,
        bool blockDirAtEnd,
        AssetBundleFSHeaderFlags originalFlags,
        AssetBundleBlockInfo[] originalBlockInfos,
        bool worldMode,
        string originalInputPath,
        string sourceUnpackedPath)
    {
        var legacyLzma = IsTruthySetting(Environment.GetEnvironmentVariable("HOTUPLOAD_LEGACY_LZMA_PACK"));
        if (compression == AssetBundleCompressionType.LZMA && worldMode && !legacyLzma &&
            NativeWorldLzmaEnabled())
        {
            try
            {
                PackWorldLzmaNativeSingleBlock(bundle, rawPath, outPath, blockDirAtEnd,
                                               originalFlags, originalBlockInfos);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAST] Native world LZMA failed; falling back to the proven packer: " + ex.Message);
                SafeDelete(outPath);
                using (var writer = new AssetsFileWriter(outPath))
                    bundle.Pack(writer, compression, blockDirAtEnd);
                return;
            }
        }

        if (!worldMode && compression == AssetBundleCompressionType.LZMA &&
            !legacyLzma && NativeAvatarLzma.Enabled)
        {
            NativeAvatarLzma.PackWithFallback(bundle, rawPath, outPath, blockDirAtEnd);
            return;
        }

        // Avatars still use full single-stream packing. Reusing untouched LZMA
        // blocks is only a local optimization and has not passed the remote
        // VRChat security-analysis contract for every avatar layout.
        // It remains available only through the explicit experimental switch.
        bool incrementalLzma = false;
        bool experimentalLzma = IsTruthySetting(Environment.GetEnvironmentVariable("HOTUPLOAD_EXPERIMENTAL_PARALLEL_LZMA"));
        if (compression != AssetBundleCompressionType.LZMA ||
            (!incrementalLzma && !experimentalLzma) ||
            legacyLzma)
        {
            using (var writer = new AssetsFileWriter(outPath))
                bundle.Pack(writer, compression, blockDirAtEnd);
            return;
        }

        try
        {
            PackLzmaParallel(bundle, rawPath, outPath, blockDirAtEnd,
                             originalFlags, originalBlockInfos, worldMode,
                             originalInputPath, sourceUnpackedPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[FAST] Parallel LZMA pack failed; falling back to the proven packer: " + ex.Message);
            SafeDelete(outPath);
            using (var writer = new AssetsFileWriter(outPath))
                bundle.Pack(writer, compression, blockDirAtEnd);
        }
    }

    private static void PackWorldLzmaNativeSingleBlock(
        AssetBundleFile bundle,
        string rawPath,
        string outPath,
        bool blockDirAtEnd,
        AssetBundleFSHeaderFlags originalFlags,
        AssetBundleBlockInfo[] originalBlockInfos)
    {
        long dataLength = bundle.DataReader.BaseStream.Length;
        long rawDataOffset = bundle.Header.GetFileDataOffset();
        if (originalBlockInfos == null || originalBlockInfos.Length != 1 ||
            originalBlockInfos[0].DecompressedSize != dataLength)
            throw new InvalidOperationException("Native world LZMA currently requires an unchanged single-block layout.");
        if (dataLength <= 0 || dataLength > uint.MaxValue)
            throw new InvalidOperationException("Native world LZMA data length is outside the UnityFS block limit.");

        var toolDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        var xzPath = Path.Combine(toolDir, "xz.exe");
        if (!File.Exists(xzPath))
            throw new FileNotFoundException("Native xz encoder not found.", xzPath);

        var compressedPath = outPath + ".native-lzma-" + Guid.NewGuid().ToString("N") + ".tmp";
        var packingPath = outPath + ".packing-" + Guid.NewGuid().ToString("N");
        try
        {
            Console.WriteLine("[FAST] Native single-block world LZMA: data=" + dataLength);
            RunNativeLzmaEncoder(xzPath, rawPath, rawDataOffset, dataLength, compressedPath);
            var compressedLength = new FileInfo(compressedPath).Length;
            if (compressedLength <= 5 || compressedLength > uint.MaxValue)
                throw new InvalidOperationException("Native LZMA output length is invalid: " + compressedLength);

            var blockInfos = new[]
            {
                new AssetBundleBlockInfo
                {
                    DecompressedSize = (uint)dataLength,
                    CompressedSize = (uint)compressedLength,
                    Flags = (ushort)((originalBlockInfos[0].Flags & ~0x3f) | 1)
                }
            };
            var bundleInfo = new AssetBundleBlockAndDirInfo
            {
                Hash = new Hash128((byte[])bundle.BlockAndDirInfo.Hash.data.Clone()),
                BlockInfos = blockInfos,
                DirectoryInfos = bundle.BlockAndDirInfo.DirectoryInfos
            };

            byte[] infoBytes;
            using (var infoStream = new MemoryStream())
            using (var infoWriter = new AssetsFileWriter(infoStream))
            {
                infoWriter.BigEndian = true;
                bundleInfo.Write(infoWriter);
                infoBytes = infoStream.ToArray();
            }
            var compressedInfo = CompressDirectoryInfo(infoBytes, originalFlags);
            if (compressedInfo == null || compressedInfo.Length == 0)
                throw new InvalidOperationException("Unable to compress native world UnityFS directory.");

            var outputFlags = originalFlags;
            if (blockDirAtEnd)
                outputFlags |= AssetBundleFSHeaderFlags.BlockAndDirAtEnd;
            else
                outputFlags &= ~AssetBundleFSHeaderFlags.BlockAndDirAtEnd;

            var outputHeader = new AssetBundleHeader
            {
                Signature = bundle.Header.Signature,
                Version = bundle.Header.Version,
                GenerationVersion = bundle.Header.GenerationVersion,
                EngineVersion = bundle.Header.EngineVersion,
                FileStreamHeader = new AssetBundleFSHeader
                {
                    TotalFileSize = 0,
                    CompressedSize = (uint)compressedInfo.Length,
                    DecompressedSize = (uint)infoBytes.Length,
                    Flags = outputFlags
                }
            };

            using (var writer = new AssetsFileWriter(packingPath))
            {
                outputHeader.Write(writer);
                if (outputHeader.Version >= 7) writer.Align16();
                if (!blockDirAtEnd)
                {
                    writer.Write(compressedInfo);
                    if ((outputFlags & AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart) != 0)
                        writer.Align16();
                }
                using (var input = new FileStream(compressedPath, FileMode.Open, FileAccess.Read,
                                                  FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                    input.CopyTo(writer.BaseStream, 1024 * 1024);
                if (blockDirAtEnd)
                    writer.Write(compressedInfo);
                outputHeader.FileStreamHeader.TotalFileSize = writer.Position;
                writer.Position = 0;
                outputHeader.Write(writer);
                writer.BaseStream.Flush();
            }

            VerifyPackedLzmaBundle(packingPath, dataLength, 1,
                                   bundle.BlockAndDirInfo.DirectoryInfos.Count, outputFlags);
            SafeDelete(outPath);
            File.Move(packingPath, outPath);
            Console.WriteLine("[FAST] Native world LZMA complete: " + dataLength + " -> " + compressedLength);
        }
        finally
        {
            SafeDelete(compressedPath);
            SafeDelete(packingPath);
        }
    }

    private static void RunNativeLzmaEncoder(
        string xzPath, string rawPath, long rawDataOffset, long dataLength, string outputPath)
    {
        var inputPath = outputPath + ".input-" + Guid.NewGuid().ToString("N") + ".tmp";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = xzPath,
            Arguments = "--format=lzma --lzma1=dict=2MiB,lc=3,lp=0,pb=2,mf=bt4,nice=32 --stdout",
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            // Redirected StandardInput is a text StreamWriter on .NET Framework
            // and prepends EF BB BF on this runtime.  xz therefore reads a
            // temporary binary slice instead of stdin.
            using (var source = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                               1024 * 1024, FileOptions.SequentialScan))
            using (var target = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write,
                                               FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                source.Position = rawDataOffset;
                using (var limited = new LimitedReadStream(source, dataLength))
                    limited.CopyTo(target, 1024 * 1024);
                target.Flush();
            }
            psi.Arguments += " \"" + inputPath.Replace("\"", "\\\"") + "\"";

            using (var process = new System.Diagnostics.Process())
            {
                process.StartInfo = psi;
                if (!process.Start())
                    throw new InvalidOperationException("Unable to start native xz encoder.");

                var stderrTask = Task.Factory.StartNew(delegate { return process.StandardError.ReadToEnd(); });
                using (var target = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write,
                                                   FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                {
                    var header = new byte[13];
                    var headerRead = ReadExactOrLess(process.StandardOutput.BaseStream, header, header.Length);
                    if (headerRead != header.Length)
                        throw new InvalidOperationException("Native xz did not emit a complete .lzma header.");
                    target.Write(header, 0, 5);
                    process.StandardOutput.BaseStream.CopyTo(target, 1024 * 1024);
                    target.Flush();
                }

                process.WaitForExit();
                var stderr = stderrTask.Result;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Native xz exited with code " + process.ExitCode + ": " + stderr);
            }
        }
        finally
        {
            SafeDelete(inputPath);
        }
    }

    private static void PackLzmaParallel(
        AssetBundleFile bundle,
        string rawPath,
        string outPath,
        bool blockDirAtEnd,
        AssetBundleFSHeaderFlags originalFlags,
        AssetBundleBlockInfo[] originalBlockInfos,
        bool worldMode,
        string originalInputPath,
        string sourceUnpackedPath)
    {
        long dataLength = bundle.DataReader.BaseStream.Length;
        long rawDataOffset = bundle.Header.GetFileDataOffset();
        var plans = BuildLzmaBlockPlans(originalBlockInfos, dataLength, worldMode);
        if (originalBlockInfos == null || originalBlockInfos.Length < 2 ||
            plans.Count != originalBlockInfos.Length)
            throw new InvalidOperationException("Original UnityFS block layout cannot be preserved for this modified data length.");
        if (string.IsNullOrEmpty(originalInputPath) || !File.Exists(originalInputPath) ||
            string.IsNullOrEmpty(sourceUnpackedPath) || !File.Exists(sourceUnpackedPath))
            throw new InvalidOperationException("Incremental LZMA pack requires the original and unpacked source bundles.");

        long originalDataOffset;
        long sourceRawDataOffset;
        Hash128 originalDirectoryHash;
        var originalManager = new AssetsManager();
        var originalBundle = originalManager.LoadBundleFile(originalInputPath, false);
        try
        {
            originalDataOffset = originalBundle.file.Header.GetFileDataOffset();
            originalDirectoryHash = new Hash128((byte[])originalBundle.file.BlockAndDirInfo.Hash.data.Clone());
        }
        finally
        {
            originalBundle.file.Close();
        }
        var sourceManager = new AssetsManager();
        var sourceBundle = sourceManager.LoadBundleFile(sourceUnpackedPath, false);
        try
        {
            sourceRawDataOffset = sourceBundle.file.Header.GetFileDataOffset();
        }
        finally
        {
            sourceBundle.file.Close();
        }

        long originalCompressedOffset = 0;
        var reusedBlocks = 0;
        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            plan.CompressedOffset = originalCompressedOffset;
            originalCompressedOffset += originalBlockInfos[i].CompressedSize;
            plan.ReuseOriginalCompressedData =
                plan.DecompressedSize == originalBlockInfos[i].DecompressedSize &&
                FileRangesEqual(sourceUnpackedPath, sourceRawDataOffset + plan.Offset,
                                rawPath, rawDataOffset + plan.Offset, plan.DecompressedSize);
            if (plan.ReuseOriginalCompressedData)
            {
                plan.CompressedSize = originalBlockInfos[i].CompressedSize;
                reusedBlocks++;
            }
        }
        var maxThreads = GetPositiveIntSetting("HOTUPLOAD_LZMA_THREADS",
            Math.Max(1, Math.Min(4, Environment.ProcessorCount / 3)));
        maxThreads = Math.Max(1, Math.Min(Math.Min(8, plans.Count), maxThreads));

        Console.WriteLine("[FAST] Incremental LZMA pack: blocks=" + plans.Count +
                          " reused=" + reusedBlocks +
                          " recompress=" + (plans.Count - reusedBlocks) +
                          " workers=" + maxThreads +
                          " data=" + dataLength);

        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads };
            Parallel.For(0, plans.Count, options, delegate(int index)
            {
                var plan = plans[index];
                if (plan.ReuseOriginalCompressedData)
                    return;
                plan.CompressedPath = outPath + ".lzma-" + index + "-" + Guid.NewGuid().ToString("N") + ".tmp";
                using (var source = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                   1024 * 1024, FileOptions.SequentialScan))
                {
                    source.Position = rawDataOffset + plan.Offset;
                    using (var limited = new LimitedReadStream(source, plan.DecompressedSize))
                    using (var target = new FileStream(plan.CompressedPath, FileMode.CreateNew, FileAccess.Write,
                                                       FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                    {
                        SevenZipHelper.Compress(limited, target, null);
                        target.Flush();
                    }
                }

                var compressedLength = new FileInfo(plan.CompressedPath).Length;
                if (compressedLength <= 0 || compressedLength > uint.MaxValue)
                    throw new InvalidOperationException("Invalid compressed LZMA block length: " + compressedLength);
                plan.CompressedSize = (uint)compressedLength;
                Console.WriteLine("[FAST] LZMA block " + (index + 1) + "/" + plans.Count +
                                  " complete: " + plan.DecompressedSize + " -> " + plan.CompressedSize);
            });

            var blockInfos = new AssetBundleBlockInfo[plans.Count];
            for (var i = 0; i < plans.Count; i++)
            {
                blockInfos[i] = new AssetBundleBlockInfo
                {
                    DecompressedSize = plans[i].DecompressedSize,
                    CompressedSize = plans[i].CompressedSize,
                    Flags = plans[i].Flags
                };
            }

            var bundleInfo = new AssetBundleBlockAndDirInfo
            {
                Hash = originalDirectoryHash,
                BlockInfos = blockInfos,
                DirectoryInfos = bundle.BlockAndDirInfo.DirectoryInfos
            };

            byte[] infoBytes;
            using (var infoStream = new MemoryStream())
            using (var infoWriter = new AssetsFileWriter(infoStream))
            {
                infoWriter.BigEndian = true;
                bundleInfo.Write(infoWriter);
                infoBytes = infoStream.ToArray();
            }
            var compressedInfo = CompressDirectoryInfo(infoBytes, originalFlags);
            if (compressedInfo == null || compressedInfo.Length == 0)
                throw new InvalidOperationException("Unable to compress UnityFS block directory.");

            var outputFlags = originalFlags;
            if (blockDirAtEnd)
                outputFlags |= AssetBundleFSHeaderFlags.BlockAndDirAtEnd;
            else
                outputFlags &= ~AssetBundleFSHeaderFlags.BlockAndDirAtEnd;

            var outputHeader = new AssetBundleHeader
            {
                Signature = bundle.Header.Signature,
                Version = bundle.Header.Version,
                GenerationVersion = bundle.Header.GenerationVersion,
                EngineVersion = bundle.Header.EngineVersion,
                FileStreamHeader = new AssetBundleFSHeader
                {
                    TotalFileSize = 0,
                    CompressedSize = (uint)compressedInfo.Length,
                    DecompressedSize = (uint)infoBytes.Length,
                    Flags = outputFlags
                }
            };

            var packingPath = outPath + ".packing-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var writer = new AssetsFileWriter(packingPath))
                {
                    outputHeader.Write(writer);
                    if (outputHeader.Version >= 7) writer.Align16();

                    if (!blockDirAtEnd)
                    {
                        writer.Write(compressedInfo);
                        if ((outputFlags & AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart) != 0)
                            writer.Align16();
                    }

                    for (var i = 0; i < plans.Count; i++)
                    {
                        var plan = plans[i];
                        if (plan.ReuseOriginalCompressedData)
                        {
                            using (var input = new FileStream(originalInputPath, FileMode.Open, FileAccess.Read,
                                                              FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                            {
                                input.Position = originalDataOffset + plan.CompressedOffset;
                                CopyExact(input, writer.BaseStream, plan.CompressedSize);
                            }
                        }
                        else
                        {
                            using (var input = new FileStream(plan.CompressedPath, FileMode.Open, FileAccess.Read,
                                                              FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                                input.CopyTo(writer.BaseStream, 1024 * 1024);
                        }
                    }

                    if (blockDirAtEnd)
                        writer.Write(compressedInfo);

                    outputHeader.FileStreamHeader.TotalFileSize = writer.Position;
                    writer.Position = 0;
                    outputHeader.Write(writer);
                    writer.BaseStream.Flush();
                }

                VerifyPackedLzmaBundle(packingPath, dataLength, plans.Count,
                                       bundle.BlockAndDirInfo.DirectoryInfos.Count, outputFlags);
                SafeDelete(outPath);
                File.Move(packingPath, outPath);
            }
            finally
            {
                SafeDelete(packingPath);
            }
        }
        finally
        {
            for (var i = 0; i < plans.Count; i++)
                SafeDelete(plans[i].CompressedPath);
        }
    }

    private static byte[] CompressDirectoryInfo(byte[] infoBytes, AssetBundleFSHeaderFlags flags)
    {
        var compression = (int)(flags & AssetBundleFSHeaderFlags.CompressionMask);
        if (compression == 0)
            return infoBytes;
        if (compression == 2)
            return LZ4Codec.Encode32(infoBytes, 0, infoBytes.Length);
        if (compression == 3)
            return LZ4Codec.Encode32HC(infoBytes, 0, infoBytes.Length);
        if (compression == 1)
        {
            using (var source = new MemoryStream(infoBytes, false))
            using (var target = new MemoryStream())
            {
                SevenZipHelper.Compress(source, target, null);
                return target.ToArray();
            }
        }
        throw new InvalidOperationException("Unsupported UnityFS directory compression: " + compression);
    }

    private static bool FileRangesEqual(string leftPath, long leftOffset, string rightPath, long rightOffset, long length)
    {
        const int bufferSize = 1024 * 1024;
        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];
        using (var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                         bufferSize, FileOptions.SequentialScan))
        using (var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                          bufferSize, FileOptions.SequentialScan))
        {
            left.Position = leftOffset;
            right.Position = rightOffset;
            long remaining = length;
            while (remaining > 0)
            {
                var wanted = (int)Math.Min(bufferSize, remaining);
                var leftRead = ReadExactOrLess(left, leftBuffer, wanted);
                var rightRead = ReadExactOrLess(right, rightBuffer, wanted);
                if (leftRead != wanted || rightRead != wanted)
                    return false;
                for (var i = 0; i < wanted; i++)
                    if (leftBuffer[i] != rightBuffer[i])
                        return false;
                remaining -= wanted;
            }
        }
        return true;
    }

    private static int ReadExactOrLess(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private static void CopyExact(Stream source, Stream target, long count)
    {
        var buffer = new byte[1024 * 1024];
        long remaining = count;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = source.Read(buffer, 0, wanted);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of original compressed UnityFS block.");
            target.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static List<LzmaBlockPlan> BuildLzmaBlockPlans(
        AssetBundleBlockInfo[] originalBlockInfos, long dataLength, bool worldMode)
    {
        var plans = new List<LzmaBlockPlan>();
        long originalLength = 0;
        if (originalBlockInfos != null)
            for (var i = 0; i < originalBlockInfos.Length; i++)
                originalLength += originalBlockInfos[i].DecompressedSize;

        if (originalBlockInfos != null && originalBlockInfos.Length > 1 && originalLength == dataLength)
        {
            long offset = 0;
            for (var i = 0; i < originalBlockInfos.Length; i++)
            {
                plans.Add(new LzmaBlockPlan
                {
                    Offset = offset,
                    DecompressedSize = originalBlockInfos[i].DecompressedSize,
                    Flags = (ushort)((originalBlockInfos[i].Flags & ~0x3f) | 1)
                });
                offset += originalBlockInfos[i].DecompressedSize;
            }
            return plans;
        }

        var defaultBlockMb = worldMode ? 64 : 32;
        var blockMb = GetPositiveIntSetting("HOTUPLOAD_LZMA_BLOCK_MB", defaultBlockMb);
        blockMb = Math.Max(8, Math.Min(256, blockMb));
        long blockSize = (long)blockMb * 1024 * 1024;
        ushort flags = 0x41;
        if (originalBlockInfos != null && originalBlockInfos.Length > 0)
            flags = (ushort)((originalBlockInfos[0].Flags & ~0x3f) | 1);

        long position = 0;
        while (position < dataLength)
        {
            uint take = (uint)Math.Min(blockSize, dataLength - position);
            plans.Add(new LzmaBlockPlan
            {
                Offset = position,
                DecompressedSize = take,
                Flags = flags
            });
            position += take;
        }
        if (plans.Count == 0)
            throw new InvalidOperationException("UnityFS data stream is empty.");
        return plans;
    }

    private static void VerifyPackedLzmaBundle(
        string path, long expectedDataLength, int expectedBlockCount, int expectedDirectoryCount,
        AssetBundleFSHeaderFlags expectedFlags)
    {
        var manager = new AssetsManager();
        var packed = manager.LoadBundleFile(path, false);
        try
        {
            if (packed.originalCompression != AssetBundleCompressionType.LZMA)
                throw new InvalidOperationException("Packed bundle compression changed unexpectedly.");
            var blocks = packed.file.BlockAndDirInfo.BlockInfos;
            if (blocks == null || blocks.Length != expectedBlockCount)
                throw new InvalidOperationException("Packed bundle block count mismatch.");
            long length = 0;
            for (var i = 0; i < blocks.Length; i++)
            {
                if (blocks[i].GetCompressionType() != 1)
                    throw new InvalidOperationException("Packed bundle contains a non-LZMA data block.");
                length += blocks[i].DecompressedSize;
            }
            if (length != expectedDataLength)
                throw new InvalidOperationException("Packed bundle data length mismatch.");
            if (packed.file.BlockAndDirInfo.DirectoryInfos.Count != expectedDirectoryCount)
                throw new InvalidOperationException("Packed bundle directory count mismatch.");
            if (packed.file.Header.FileStreamHeader.TotalFileSize != new FileInfo(path).Length)
                throw new InvalidOperationException("Packed bundle header size mismatch.");
            if (packed.file.Header.FileStreamHeader.Flags != expectedFlags)
                throw new InvalidOperationException("Packed bundle header flags changed unexpectedly.");
        }
        finally
        {
            packed.file.Close();
        }
        Console.WriteLine("[FAST] Parallel LZMA output structure verified.");
    }

    private static int GetPositiveIntSetting(string name, int fallback)
    {
        int value;
        var raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out value) && value > 0
            ? value
            : fallback;
    }

    private static void SafeDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    sealed class LimitedReadStream : Stream
    {
        readonly Stream _inner;
        readonly long _length;
        long _position;

        public LimitedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _length; } }
        public override long Position
        {
            get { return _position; }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0) return 0;
            if (count > remaining) count = (int)Math.Min(int.MaxValue, remaining);
            var read = _inner.Read(buffer, offset, count);
            _position += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    private static void VerifyWorldSelection(string rawPath, string expectedWorldId, string tpk)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var bundle = am.LoadBundleFile(rawPath);
        try
        {
            am.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
            int dirCount = bundle.file.BlockAndDirInfo.DirectoryInfos.Count;
            int found = 0;
            for (int index = 0; index < dirCount; index++)
            {
                var dirName = bundle.file.BlockAndDirInfo.DirectoryInfos[index].Name ?? "";
                if (IsResourceEntry(dirName)) continue;
                AssetsFileInstance afi;
                try { afi = am.LoadAssetsFileFromBundle(bundle, index, true); } catch { continue; }
                if (afi == null || afi.file == null) continue;
                foreach (var inf in afi.file.GetAssetsOfType((AssetClassID)114))
                {
                    AssetTypeValueField bf;
                    try { bf = am.GetBaseField(afi, inf); } catch { continue; }
                    var bp = bf["blueprintId"];
                    if (bp == null || bp.FieldName == "DUMMY") continue;
                    var value = ReadString(bp);
                    if (!IsStandardWorldId(value)) continue;
                    found++;
                    if (!string.Equals(value, expectedWorldId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Post-patch world id mismatch: found " + value);
                }
            }
            if (found == 0)
                throw new InvalidOperationException("Post-patch verification failed: no wrld_ PipelineManager found.");
            Console.WriteLine("    Structured verification passed: world id=" + expectedWorldId + " count=" + found);
        }
        finally
        {
            bundle.file.Close();
        }
    }

    private static bool HasFlag(AssetBundleFSHeaderFlags flags, AssetBundleFSHeaderFlags flag)
    {
        return (((int)flags) & (int)flag) != 0;
    }


    private static string GetCabOverrideSetting()
    {
        var value = Environment.GetEnvironmentVariable("HOTUPLOAD_CAB_OVERRIDE");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsTruthySetting(string value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NativeWorldLzmaEnabled()
    {
        var value = Environment.GetEnvironmentVariable("HOTUPLOAD_NATIVE_WORLD_LZMA");
        // The native path is now the verified default.  Operators can still
        // force the canonical managed path with HOTUPLOAD_NATIVE_WORLD_LZMA=0
        // or HOTUPLOAD_LEGACY_LZMA_PACK=1 for diagnostics.
        return string.IsNullOrWhiteSpace(value) || IsTruthySetting(value);
    }

    private static PipelineSelection FindAuthoritativePipeline(AssetsManager am, AssetsFileInstance afi)
    {
        var gameObjectNames = new Dictionary<long, string>();
        foreach (var info in afi.file.GetAssetsOfType((AssetClassID)1))
        {
            AssetTypeValueField field;
            try { field = am.GetBaseField(afi, info); } catch { continue; }
            gameObjectNames[info.PathId] = ReadString(field["m_Name"]);
        }

        var rootGameObjects = new HashSet<long>();
        foreach (var info in afi.file.GetAssetsOfType((AssetClassID)4))
        {
            AssetTypeValueField field;
            try { field = am.GetBaseField(afi, info); } catch { continue; }
            var gameObjectPath = ReadPPtrPath(field["m_GameObject"]);
            var parentPath = ReadPPtrPath(field["m_Father"]);
            if (gameObjectPath != 0 && parentPath == 0)
                rootGameObjects.Add(gameObjectPath);
        }

        var descriptorGameObjects = new HashSet<long>();
        foreach (var info in afi.file.GetAssetsOfType((AssetClassID)114))
        {
            AssetTypeValueField field;
            try { field = am.GetBaseField(afi, info); } catch { continue; }
            bool isDescriptor = _worldMode ? LooksLikeSceneDescriptor(field) : LooksLikeAvatarDescriptor(field);
            if (!isDescriptor) continue;
            var gameObjectPath = ReadPPtrPath(field["m_GameObject"]);
            if (gameObjectPath != 0)
                descriptorGameObjects.Add(gameObjectPath);
        }

        var bundles = new List<AssetBundleCandidate>();
        foreach (var info in afi.file.GetAssetsOfType((AssetClassID)142))
        {
            AssetTypeValueField field;
            try { field = am.GetBaseField(afi, info); } catch { continue; }
            var name = ReadString(field["m_AssetBundleName"]);
            if (string.IsNullOrEmpty(name))
                name = ReadString(field["m_Name"]);
            if (!string.IsNullOrEmpty(name))
                bundles.Add(new AssetBundleCandidate { Info = info, Name = name });
        }

        var candidates = new List<PipelineCandidate>();
        foreach (var info in afi.file.GetAssetsOfType((AssetClassID)114))
        {
            AssetTypeValueField field;
            try { field = am.GetBaseField(afi, info); } catch { continue; }
            var blueprint = ReadString(field["blueprintId"]);
            if (!IsStandardBlueprintId(blueprint)) continue;

            var gameObjectPath = ReadPPtrPath(field["m_GameObject"]);
            string gameObjectName;
            gameObjectNames.TryGetValue(gameObjectPath, out gameObjectName);
            var candidate = new PipelineCandidate
            {
                Info = info,
                BlueprintId = blueprint,
                GameObjectPathId = gameObjectPath,
                GameObjectName = gameObjectName ?? "",
                HasAvatarDescriptor = descriptorGameObjects.Contains(gameObjectPath),
                IsRootObject = rootGameObjects.Contains(gameObjectPath)
            };
            foreach (var bundle in bundles)
                if (ContainsIgnoreCase(bundle.Name, blueprint))
                    candidate.MatchingBundles.Add(bundle);
            candidates.Add(candidate);
        }

        Console.WriteLine("    Structured pipeline scan: candidates=" + candidates.Count +
                          " descriptorObjects=" + descriptorGameObjects.Count +
                          " bundleNames=" + bundles.Count);
        foreach (var candidate in candidates)
        {
            Console.WriteLine("      candidate path=" + candidate.Info.PathId +
                              " go='" + candidate.GameObjectName + "'" +
                              " descriptor=" + candidate.HasAvatarDescriptor +
                              " root=" + candidate.IsRootObject +
                              " bundleMatches=" + candidate.MatchingBundles.Count +
                              " id=" + candidate.BlueprintId);
        }

        var descriptorMatches = FilterCandidates(candidates, true, false);
        PipelineCandidate selected = descriptorMatches.Count == 1 ? descriptorMatches[0] : null;
        if (selected == null && descriptorMatches.Count > 1)
        {
            var descriptorBundleMatches = FilterCandidates(descriptorMatches, true, true);
            if (descriptorBundleMatches.Count == 1)
                selected = descriptorBundleMatches[0];
        }

        if (selected == null && descriptorMatches.Count == 0)
        {
            var bundleMatches = FilterCandidates(candidates, false, true);
            if (bundleMatches.Count == 1 &&
                (bundleMatches[0].IsRootObject || ContainsIgnoreCase(bundleMatches[0].GameObjectName, bundleMatches[0].BlueprintId)))
                selected = bundleMatches[0];
        }

        if (selected == null && candidates.Count == 1 && candidates[0].MatchingBundles.Count == 1)
            selected = candidates[0];

        if (selected == null)
            throw new InvalidOperationException("Unable to uniquely identify the avatar-root PipelineManager; upload stopped before bundle mutation.");
        if (selected.MatchingBundles.Count != 1)
            throw new InvalidOperationException("The authoritative PipelineManager does not map to exactly one AssetBundle name; upload stopped.");

        Console.WriteLine("    Authoritative PipelineManager: path=" + selected.Info.PathId +
                          " go='" + selected.GameObjectName + "'" +
                          " id=" + selected.BlueprintId);
        Console.WriteLine("    Associated AssetBundle: path=" + selected.MatchingBundles[0].Info.PathId +
                          " name='" + selected.MatchingBundles[0].Name + "'");
        return new PipelineSelection
        {
            Pipeline = selected,
            AssetBundle = selected.MatchingBundles[0]
        };
    }

    private static List<PipelineCandidate> FilterCandidates(
        List<PipelineCandidate> candidates,
        bool requireDescriptor,
        bool requireBundleMatch)
    {
        var result = new List<PipelineCandidate>();
        foreach (var candidate in candidates)
        {
            if (requireDescriptor && !candidate.HasAvatarDescriptor) continue;
            if (requireBundleMatch && candidate.MatchingBundles.Count != 1) continue;
            result.Add(candidate);
        }
        return result;
    }

    private static bool LooksLikeAvatarDescriptor(AssetTypeValueField field)
    {
        var score = 0;
        if (HasField(field, "lipSync")) score++;
        if (HasField(field, "VisemeSkinnedMesh")) score++;
        if (HasField(field, "MouthOpenBlendShapeName")) score++;
        if (HasField(field, "baseAnimationLayers")) score++;
        if (HasField(field, "specialAnimationLayers")) score++;
        if (HasField(field, "customEyeLookSettings")) score++;
        return score >= 4;
    }

    // VRCSceneDescriptor 识别 (世界模式), 与 WorldPreflightTool 一致的评分启发式。
    private static bool LooksLikeSceneDescriptor(AssetTypeValueField field)
    {
        var score = 0;
        string[] names = new[]
        {
            "spawns", "spawnRadius", "spawnOrder", "spawnOrientation", "ReferenceCamera",
            "RespawnHeightY", "ObjectBehaviourAtRespawnHeight", "DynamicPrefabs", "DynamicMaterials",
            "NetworkIDs", "interactThruLayers"
        };
        foreach (var name in names)
            if (HasField(field, name)) score++;
        return score >= 3;
    }

    private static bool HasField(AssetTypeValueField field, string name)
    {
        if (field == null) return false;
        var child = field[name];
        return child != null && child.FieldName != "DUMMY";
    }

    private static long ReadPPtrPath(AssetTypeValueField field)
    {
        if (field == null || field.FieldName == "DUMMY") return 0;
        var path = field["m_PathID"];
        if (path == null || path.FieldName == "DUMMY") return 0;
        try { return path.AsLong; } catch { return 0; }
    }

    private static string ReadString(AssetTypeValueField field)
    {
        if (field == null || field.FieldName == "DUMMY") return "";
        try { return field.AsString ?? ""; } catch { return ""; }
    }

    private static bool IsResourceEntry(string name)
    {
        var normalized = (name ?? "").Replace('\\', '/');
        var lower = normalized.ToLowerInvariant();
        return lower.StartsWith("gi/", StringComparison.OrdinalIgnoreCase) ||
               lower.EndsWith(".ress", StringComparison.OrdinalIgnoreCase) ||
               lower.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStandardAvatarId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 41 ||
            !value.StartsWith("avtr_", StringComparison.OrdinalIgnoreCase))
            return false;
        Guid ignored;
        return Guid.TryParse(value.Substring(5), out ignored);
    }

    private static bool IsStandardWorldId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 41 ||
            !value.StartsWith("wrld_", StringComparison.OrdinalIgnoreCase))
            return false;
        Guid ignored;
        return Guid.TryParse(value.Substring(5), out ignored);
    }

    // 按当前模式判断 blueprint id 是否合法 (头像 avtr_ / 世界 wrld_)。
    private static bool IsStandardBlueprintId(string value)
    {
        return _worldMode ? IsStandardWorldId(value) : IsStandardAvatarId(value);
    }

    private static bool ContainsIgnoreCase(string value, string needle)
    {
        return !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(needle) &&
               value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string DeriveRootPrefabName(string assetBundleName, string oldAvatarId, string newAvatarId)
    {
        var name = ReplaceOrdinalIgnoreCase(assetBundleName, oldAvatarId, newAvatarId);
        name = name.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name.Substring(slash + 1);

        const string bundleSuffix = ".prefab.unity3d";
        if (name.EndsWith(bundleSuffix, StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - bundleSuffix.Length);

        if (string.IsNullOrEmpty(name) || !ContainsIgnoreCase(name, newAvatarId))
            throw new InvalidOperationException("Associated AssetBundle name cannot provide a valid target root prefab name.");
        return name;
    }

    private static void VerifyAuthoritativeSelection(
        string rawPath,
        string oldAvatarId,
        string expectedAvatarId,
        string expectedRootName,
        string tpk,
        HashSet<long> modifiedAssetIds,
        List<string> expectedPreservedStrings)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var bundle = am.LoadBundleFile(rawPath);
        try
        {
            var assets = am.LoadAssetsFileFromBundle(bundle, 0, true);
            am.LoadClassDatabaseFromPackage(bundle.file.Header.EngineVersion);
            var selection = FindAuthoritativePipeline(am, assets);
            if (!string.Equals(selection.Pipeline.BlueprintId, expectedAvatarId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Post-patch PipelineManager verification failed.");
            if (!ContainsIgnoreCase(selection.AssetBundle.Name, expectedAvatarId))
                throw new InvalidOperationException("Post-patch AssetBundle association verification failed.");
            if (!string.Equals(selection.Pipeline.GameObjectName, expectedRootName, StringComparison.Ordinal))
                throw new InvalidOperationException("Post-patch avatar root prefab name verification failed.");
            var actualPreservedStrings = CaptureAvatarIdStrings(am, assets, oldAvatarId, modifiedAssetIds);
            if (!StringListsEqual(expectedPreservedStrings, actualPreservedStrings))
                throw new InvalidOperationException("Internal avatar resource strings changed outside the targeted upload metadata.");
            Console.WriteLine("    Structured verification passed: root PipelineManager and AssetBundle both use " + expectedAvatarId);
            Console.WriteLine("    Internal resource preservation passed: unchanged strings=" + actualPreservedStrings.Count);
        }
        finally
        {
            bundle.file.Close();
        }
    }

    private static List<string> CaptureAvatarIdStrings(
        AssetsManager am,
        AssetsFileInstance assets,
        string avatarId,
        HashSet<long> excludedAssetIds)
    {
        var values = new List<string>();
        foreach (var info in assets.file.AssetInfos)
        {
            if (excludedAssetIds.Contains(info.PathId)) continue;
            AssetTypeValueField field;
            try { field = am.GetBaseField(assets, info); } catch { continue; }
            CaptureAvatarIdStringsRecursive(
                field,
                info.TypeId + ":" + info.PathId + ":" + field.FieldName,
                avatarId,
                values);
        }
        values.Sort(StringComparer.Ordinal);
        return values;
    }

    private static void CaptureAvatarIdStringsRecursive(
        AssetTypeValueField field,
        string path,
        string avatarId,
        List<string> values)
    {
        if (field == null) return;
        if (string.Equals(field.TypeName, "string", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = field.AsString;
                if (!string.IsNullOrEmpty(text) && ContainsIgnoreCase(text, avatarId))
                    values.Add(path + "=" + text);
            }
            catch { }
        }

        if (field.Children == null) return;
        for (var i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            CaptureAvatarIdStringsRecursive(child, path + "." + child.FieldName + "[" + i + "]", avatarId, values);
        }
    }

    private static bool StringListsEqual(List<string> left, List<string> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        return true;
    }


    private static void ReplaceStringsRecursive(
        AssetTypeValueField field,
        string oldId,
        string newId,
        ref int changedStrings)
    {
        if (field == null) return;

        if (string.Equals(field.TypeName, "string", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = field.AsString;
                if (!string.IsNullOrEmpty(text) && text.IndexOf(oldId, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    field.AsString = ReplaceOrdinalIgnoreCase(text, oldId, newId);
                    changedStrings++;
                }
            }
            catch
            {
            }
        }

        if (field.Children == null) return;
        foreach (var child in field.Children)
            ReplaceStringsRecursive(child, oldId, newId, ref changedStrings);
    }

    private static string ReplaceOrdinalIgnoreCase(string text, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(oldValue)) return text;

        var result = new System.Text.StringBuilder(text.Length);
        var index = 0;
        while (true)
        {
            var found = text.IndexOf(oldValue, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                result.Append(text, index, text.Length - index);
                return result.ToString();
            }

            result.Append(text, index, found - index);
            result.Append(newValue);
            index = found + oldValue.Length;
        }
    }
}
