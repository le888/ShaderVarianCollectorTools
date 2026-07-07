using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

public static class ShaderVariantCollector
{
    public struct MaterialInfo
    {
        public string path;
        public string keyword;
    }
    private enum ESteps
    {
        None,
        Prepare,
        CollectAllMaterial,
        CollectAllScene,
        CollectVariants,
        CollectSleeping,
        CollectWaitToScene,
        CollectSceneVariants,
        CollectSceneSleeping,
        WaitingDone,
        CollectWithWaitGlablKeyWords,
        ApplyGlobalKeywords,
        CollectGlobalKeywordsSleeping,
        CollectGlobalKeywordsClearSleeping,
        SaveCollection,
        SplitWriting,
    }

    private const float WaitMilliseconds = 1000f;
    private const float SleepMilliseconds = 5000f;
    private const float SleepSceneMilliseconds = 5000f;
    private const float StableCheckInterval = 500f; // 每 500ms 检测一次 SVC 是否稳定
    private const float MinSleepMilliseconds = 500f; // 最少等待 500ms

    private static string _savePath;
    private static string _searchPath;
    private static string _scenePath;
    private static List<string> _blackPath;
    private static bool _splitByShaderName;
    private static bool _collectSceneVariants;
    private static List<string> _globalKeywords;
    private static LocalKeywordCollection _localKeywords;
    public static HashSet<string> _filterShaderName;
    private static int _processMaxNum;
    private static Action _completedCallback;
    private static string _currentPackageName = "Default";

    private static ESteps _steps = ESteps.None;
    public static bool IsCollecting => _steps != ESteps.None || _analyzeRunning;
    private static bool _analyzeRunning = false;
    private static bool _cancelRequested = false;
    private static float _analyzeProgress = 0f;
    private static string _analyzeStatus = "";

    // 异步写入状态
    private const int ScanBatchSize = 5;
    private const int WriteBatchSize = 3;
    private static List<(string path, Shader shader, ShaderVariantCollectionManifest.ShaderVariantInfo info)> _writeQueue;
    private static int _writeIndex = 0;
    private static List<string> _writeShaderNames;
    private static string _writeSavePath;
    private static System.Text.StringBuilder _writeDebugLog;
    private static bool _writeDebugRaw;
    private static ShaderVariantCollectionManifest _writeWrapper;
    private static int _writeTotalVariants;

    // 异步拆分状态（渲染模式）
    private static List<(string path, Shader shader, List<ShaderVariantCollection.ShaderVariant> variants)> _splitQueue;
    private static int _splitIndex = 0;
    private static List<string> _splitShaderNames;
    private static string _splitBasePath;
    private static string _splitBaseName;

    // 异步扫描状态
    private static string[] _scanMaterialGuids;
    private static int _scanIndex = 0;
    private static Dictionary<Shader, List<HashSet<string>>> _scanShaderMaterials;
    private static HashSet<string> _scanBlackSet;
    private static HashSet<string> _scanFilterSet;
    private static System.Text.StringBuilder _scanDebugLog;
    private static bool _scanDebugRaw;
    private static int _scanSkipped = 0;
    private static string _scanSavePath;
    private static string _scanSearchPath;
    private static bool _scanSplitByShaderName;
    private static string _scanPackageName;

    // 异步变种生成状态
    private static List<KeyValuePair<Shader, List<HashSet<string>>>> _genShaderList;
    private static int _genIndex = 0;
    private static HashSet<string> _genExcludeKeywords;
    private static List<ShaderVariantCollectionManifest.ShaderVariantInfo> _genVariantInfos;

    public static void Cancel()
    {
        _cancelRequested = true;
    }

    public static float GetAnalyzeProgress()
    {
        return _analyzeProgress;
    }

    public static string GetAnalyzeStatus()
    {
        return _analyzeStatus;
    }
    private static Stopwatch _elapsedTime;
    private static List<MaterialInfo> _allMaterials;
    private static List<MaterialInfo> _rangeMt;
    private static List<GameObject> _allSpheres = new List<GameObject>(1000);
    private static int _currentKeywordIndex = 0;
    private static HashSet<Shader> _processedShaders = new HashSet<Shader>();
    private static int _lastShaderCount = 0;
    private static int _lastVariantCount = 0;
    private static float _stableSince = 0f;
    
    private static List<string> _allScene;
    private static Scene _currentScene;
    private static int _totalMaterialCount = 0;


    /// <summary>
    /// 开始收集
    /// </summary>
    public static void Run(string savePath, string searchPath, string scenePath, List<string> blackPath, string[] filterShaderName, int processMaxNum, bool splitByShaderName, Action completedCallback, string packageName = "Default")
    {
        if (_steps != ESteps.None)
            return;

        if (Path.HasExtension(savePath) == false)
            savePath = $"{savePath}.shadervariants";
        if (Path.GetExtension(savePath) != ".shadervariants")
            throw new System.Exception("Shader variant file extension is invalid.");

        // 注意：先删除再保存，否则ShaderVariantCollection内容将无法及时刷新
        AssetDatabase.DeleteAsset(savePath);
        EditorTools.CreateFileDirectory(savePath);

        _savePath = savePath;
        _searchPath = searchPath;
        _scenePath = scenePath;
        _blackPath = blackPath;
        _splitByShaderName = splitByShaderName;
        _currentPackageName = packageName;
        _collectSceneVariants = ShaderVariantCollectorSetting.GetCollectSceneVariants(packageName);
        _globalKeywords = ShaderVariantCollectorSetting.GetGlobalKeywords(packageName);
        _localKeywords = ShaderVariantCollectorSetting.GetLocalKeywords(packageName);
        _filterShaderName = new HashSet<string>(filterShaderName);
        _processMaxNum = processMaxNum;
        _completedCallback = completedCallback;

        // 确保在开始前重置所有状态
        _currentKeywordIndex = 0;
        _processedShaders.Clear();
        _allSpheres.Clear();
        _lastShaderCount = 0;
        _lastVariantCount = 0;
        _stableSince = 0f;

        // 聚焦到游戏窗口
        EditorTools.FocusUnityGameWindow();

        // 创建临时测试场景
        CreateTempScene();

        _steps = ESteps.Prepare;
        EditorApplication.update += EditorUpdate;
    }

    /// <summary>
    /// 分析模式：直接从材质读取关键字，不渲染，生成 SVC
    /// </summary>
    public static void RunAnalyze(string savePath, string searchPath, List<string> blackPath, string[] filterShaderName, bool splitByShaderName, string packageName = "Default")
    {
        if (_steps != ESteps.None || _analyzeRunning)
            return;

        _analyzeRunning = true;
        _cancelRequested = false;
        _analyzeProgress = 0f;
        _analyzeStatus = "初始化";

        if (Path.HasExtension(savePath) == false)
            savePath = $"{savePath}.shadervariants";
        if (Path.GetExtension(savePath) != ".shadervariants")
            throw new System.Exception("Shader variant file extension is invalid.");

        AssetDatabase.DeleteAsset(savePath);
        EditorTools.CreateFileDirectory(savePath);

        // 保存异步扫描状态
        _scanMaterialGuids = AssetDatabase.FindAssets("t:Material", new[] { searchPath });
        _scanIndex = 0;
        _scanShaderMaterials = new Dictionary<Shader, List<HashSet<string>>>();
        _scanBlackSet = new HashSet<string>(blackPath);
        _scanFilterSet = new HashSet<string>(filterShaderName);
        _scanDebugRaw = ShaderVariantCollectorSetting.GetSaveDebugRawSVC(packageName);
        _scanDebugLog = new System.Text.StringBuilder();
        _scanDebugLog.AppendLine($"[分析模式] 开始 {System.DateTime.Now}");
        _scanDebugLog.AppendLine($"savePath: {savePath}");
        _scanDebugLog.AppendLine($"searchPath: {searchPath}");
        _scanDebugLog.AppendLine($"splitByShaderName: {splitByShaderName}");
        _scanDebugLog.AppendLine();
        _scanSkipped = 0;
        _scanSavePath = savePath;
        _scanSearchPath = searchPath;
        _scanSplitByShaderName = splitByShaderName;
        _scanPackageName = packageName;

        _analyzeStatus = $"扫描材质 0/{_scanMaterialGuids.Length}";
        EditorApplication.update += AnalyzeScanUpdate;
    }

    /// <summary>
    /// 异步扫描材质（每批 BatchSize 个）
    /// </summary>
    private static void AnalyzeScanUpdate()
    {
        if (_cancelRequested)
        {
            Debug.Log("[分析模式] 用户取消扫描");
            EditorApplication.update -= AnalyzeScanUpdate;
            _analyzeRunning = false;
            _analyzeProgress = 0f;
            _analyzeStatus = "";
            return;
        }

        int batchEnd = Mathf.Min(_scanIndex + ScanBatchSize, _scanMaterialGuids.Length);

        for (int i = _scanIndex; i < batchEnd; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(_scanMaterialGuids[i]);
            if (IsBlackPath(path, _scanBlackSet)) { _scanSkipped++; continue; }

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || mat.shader == null) { _scanSkipped++; continue; }
            if (_scanFilterSet.Contains(mat.shader.name)) { _scanSkipped++; continue; }

            var enabledKeywords = new HashSet<string>();
            string shaderPath = AssetDatabase.GetAssetPath(mat.shader);
            bool isShaderGraph = shaderPath.EndsWith(".shadergraph") || shaderPath.EndsWith(".shadersubgraph");
            var shaderDeclaredKeywords = isShaderGraph ? null : GetShaderSupportedKeywords(shaderPath);
            foreach (var kw in mat.shaderKeywords)
            {
                if (string.IsNullOrEmpty(kw)) continue;
                if (shaderDeclaredKeywords == null || shaderDeclaredKeywords.Contains(kw))
                    enabledKeywords.Add(kw);
            }

            if (!_scanShaderMaterials.ContainsKey(mat.shader))
                _scanShaderMaterials[mat.shader] = new List<HashSet<string>>();
            _scanShaderMaterials[mat.shader].Add(enabledKeywords);

            if (_scanDebugRaw)
                _scanDebugLog.AppendLine($"材质: {path} → shader={mat.shader.name}, keywords=[{string.Join(", ", enabledKeywords)}]");
        }

        _scanIndex = batchEnd;
        _analyzeProgress = (float)_scanIndex / _scanMaterialGuids.Length * 0.3f;
        _analyzeStatus = $"扫描材质 {_scanIndex}/{_scanMaterialGuids.Length}";

        if (_scanIndex >= _scanMaterialGuids.Length)
        {
            // 扫描完成
            EditorApplication.update -= AnalyzeScanUpdate;
            _scanDebugLog.AppendLine();
            _scanDebugLog.AppendLine($"扫描完成: {_scanShaderMaterials.Count} 个 shader, {_scanMaterialGuids.Length - _scanSkipped} 个材质, 跳过 {_scanSkipped} 个");
            Debug.Log($"[分析模式] 扫描完成: {_scanShaderMaterials.Count} 个 shader, 跳过 {_scanSkipped} 个材质");

            // 生成变种并启动异步写入
            AnalyzeGenerateAndWrite();
        }
    }

    /// <summary>
    /// 扫描完成后：启动异步变种生成
    /// </summary>
    private static void AnalyzeGenerateAndWrite()
    {
        _genExcludeKeywords = new HashSet<string>(ShaderVariantCollectorSetting.GetGlobalKeywords(_scanPackageName));
        _genShaderList = new List<KeyValuePair<Shader, List<HashSet<string>>>>(_scanShaderMaterials);
        _genIndex = 0;
        _genVariantInfos = new List<ShaderVariantCollectionManifest.ShaderVariantInfo>();

        _analyzeProgress = 0.3f;
        _analyzeStatus = $"分析 shader 0/{_genShaderList.Count}";
        EditorApplication.update += AnalyzeGenerateUpdate;
    }

    /// <summary>
    /// 异步生成变种（每帧处理一个 shader）
    /// </summary>
    private static void AnalyzeGenerateUpdate()
    {
        if (_cancelRequested)
        {
            EditorApplication.update -= AnalyzeGenerateUpdate;
            _analyzeRunning = false;
            _analyzeProgress = 0f;
            _analyzeStatus = "";
            return;
        }

        // 每帧处理一个 shader
        if (_genIndex < _genShaderList.Count)
        {
            var kvp = _genShaderList[_genIndex];
            Shader shader = kvp.Key;
            var materialKeywords = kvp.Value;
            string shaderPath = AssetDatabase.GetAssetPath(shader);

            bool isShaderGraph = shaderPath.EndsWith(".shadergraph") || shaderPath.EndsWith(".shadersubgraph");
            List<PassInfo> passInfos;
            List<PassInfo> allPassInfos = new List<PassInfo>();

            if (isShaderGraph)
            {
                var selectedPassTypes = ShaderVariantCollectorSetting.GetSelectedPassTypes(_scanPackageName);
                var defaultPassType = selectedPassTypes.Contains(13) ? (PassType)13 : (selectedPassTypes.Count > 0 ? (PassType)selectedPassTypes[0] : (PassType)13);
                passInfos = new List<PassInfo>
                {
                    new PassInfo { Name = "ShaderGraph", PassType = defaultPassType, Groups = new List<HashSet<string>>() }
                };
            }
            else
            {
                allPassInfos = GetMultiCompileGroupsByPass(shaderPath);
                var selectedPassTypes = new HashSet<int>(ShaderVariantCollectorSetting.GetSelectedPassTypes(_scanPackageName));
                passInfos = new List<PassInfo>();
                foreach (var p in allPassInfos)
                {
                    if (selectedPassTypes.Contains((int)p.PassType))
                        passInfos.Add(p);
                }
            }

            if (passInfos.Count > 0)
            {
                var allEnabledKeywords = new HashSet<string>();
                foreach (var kwSet in materialKeywords)
                    allEnabledKeywords.UnionWith(kwSet);

                var shaderInfo = new ShaderVariantCollectionManifest.ShaderVariantInfo
                {
                    AssetPath = shaderPath,
                    ShaderName = shader.name,
                };

                foreach (var passInfo in passInfos)
                {
                    var passGroups = new List<HashSet<string>>();
                    foreach (var group in passInfo.Groups)
                    {
                        bool excluded = false;
                        foreach (string kw in group)
                        {
                            if (_genExcludeKeywords.Contains(kw)) { excluded = true; break; }
                        }
                        if (excluded) continue;
                        if (group.Count >= 1)
                            passGroups.Add(new HashSet<string>(group));
                    }

                    var passGroupKeywords = new HashSet<string>();
                    foreach (var group in passGroups)
                        passGroupKeywords.UnionWith(group);

                    var passNonGroupKeywords = new List<string>();
                    foreach (string kw in allEnabledKeywords)
                    {
                        if (!passGroupKeywords.Contains(kw))
                            passNonGroupKeywords.Add(kw);
                    }
                    passNonGroupKeywords.Sort();

                    var passCombinations = GenerateGroupCombinations(passGroups);

                    if (passCombinations.Count == 0)
                    {
                        // 排除包含被排除关键字的变种
                        bool hasExcluded = false;
                        foreach (string kw in passNonGroupKeywords)
                        {
                            if (_genExcludeKeywords.Contains(kw)) { hasExcluded = true; break; }
                        }
                        if (!hasExcluded)
                        {
                            shaderInfo.ShaderVariantElements.Add(new ShaderVariantCollectionManifest.ShaderVariantElement
                            {
                                PassType = passInfo.PassType,
                                Keywords = passNonGroupKeywords.ToArray()
                            });
                        }
                    }
                    else
                    {
                        foreach (var combo in passCombinations)
                        {
                            var finalKeywords = new List<string>(passNonGroupKeywords);
                            foreach (string kw in combo)
                            {
                                string trimmed = kw.Trim();
                                if (!string.IsNullOrEmpty(trimmed))
                                    finalKeywords.Add(trimmed);
                            }
                            finalKeywords.Sort();

                            // 排除包含被排除关键字的变种
                            bool hasExcludedCombo = false;
                            foreach (string kw in finalKeywords)
                            {
                                if (_genExcludeKeywords.Contains(kw)) { hasExcludedCombo = true; break; }
                            }
                            if (hasExcludedCombo) continue;

                            shaderInfo.ShaderVariantElements.Add(new ShaderVariantCollectionManifest.ShaderVariantElement
                            {
                                PassType = passInfo.PassType,
                                Keywords = finalKeywords.ToArray()
                            });
                        }
                    }
                }

                shaderInfo.ShaderVariantCount = shaderInfo.ShaderVariantElements.Count;

                // ---- 自定义 pass 关键字注入（分析模式）----
                // 每个自定义 pass 独立计算基准：材质关键字 ∩ 该 pass 声明关键字
                var customPassesAnalyze = new List<PassInfo>();
                foreach (var p in allPassInfos)
                {
                    if ((int)p.PassType == -1)
                        customPassesAnalyze.Add(p);
                }

                if (customPassesAnalyze.Count > 0)
                {
                    // 收集默认 pass 已有变种中的材质关键字
                    var materialKeywords = new HashSet<string>();
                    foreach (var elem in shaderInfo.ShaderVariantElements)
                    {
                        if ((int)elem.PassType == 0 || (int)elem.PassType == 13)
                        {
                            foreach (string kw in elem.Keywords)
                            {
                                if (!string.IsNullOrEmpty(kw))
                                    materialKeywords.Add(kw.Trim());
                            }
                        }
                    }

                    var seenVariants = new HashSet<string>();
                    foreach (var elem in shaderInfo.ShaderVariantElements)
                    {
                        seenVariants.Add($"{(int)elem.PassType}|{string.Join(" ", elem.Keywords)}");
                    }

                    foreach (var customPass in customPassesAnalyze)
                    {
                        int customAdded = 0;

                        // 该 pass 声明的所有关键字
                        var passDeclared = customPass.AllKeywords ?? new HashSet<string>();

                        // 该 pass 的 multi_compile 组
                        var customGroups = new List<HashSet<string>>();
                        foreach (var group in customPass.Groups)
                        {
                            bool excluded = false;
                            foreach (string kw in group)
                            {
                                if (_genExcludeKeywords.Contains(kw)) { excluded = true; break; }
                            }
                            if (excluded) continue;
                            if (group.Count >= 1)
                                customGroups.Add(new HashSet<string>(group));
                        }

                        var customGroupKeywords = new HashSet<string>();
                        foreach (var group in customGroups)
                            customGroupKeywords.UnionWith(group);

                        // 基准 = 材质关键字 ∩ 该 pass 声明关键字
                        var passBase = new HashSet<string>();
                        foreach (string kw in materialKeywords)
                        {
                            if (passDeclared.Contains(kw))
                                passBase.Add(kw);
                        }

                        var cleanBase = new List<string>();
                        foreach (string kw in passBase)
                        {
                            if (!customGroupKeywords.Contains(kw))
                                cleanBase.Add(kw);
                        }
                        cleanBase.Sort();

                        var combinations = GenerateGroupCombinations(customGroups);

                        foreach (var combo in combinations)
                        {
                            var finalKws = new List<string>(cleanBase);
                            foreach (string kw in combo)
                            {
                                string trimmed = kw.Trim();
                                if (!string.IsNullOrEmpty(trimmed))
                                    finalKws.Add(trimmed);
                            }
                            finalKws.Sort();

                            string key = $"0|{string.Join(" ", finalKws)}";
                            if (seenVariants.Contains(key)) continue;
                            seenVariants.Add(key);

                            shaderInfo.ShaderVariantElements.Add(new ShaderVariantCollectionManifest.ShaderVariantElement
                            {
                                PassType = (PassType)0,
                                Keywords = finalKws.ToArray()
                            });
                            customAdded++;
                        }

                        if (customAdded > 0)
                            Debug.Log($"[分析模式][自定义pass注入] {shader.name} LightMode=\"{customPass.Name}\": 基准=[{string.Join(",", passBase)}] 注入 {customAdded} 个变种");
                    }
                }

                shaderInfo.ShaderVariantCount = shaderInfo.ShaderVariantElements.Count;
                _genVariantInfos.Add(shaderInfo);
                Debug.Log($"[分析模式] {shader.name}: {shaderInfo.ShaderVariantCount} 个变种");
            }

            _genIndex++;
            _analyzeProgress = 0.3f + 0.2f * _genIndex / _genShaderList.Count;
            _analyzeStatus = $"分析 shader {_genIndex}/{_genShaderList.Count}: {shader.name}";
        }

        if (_genIndex >= _genShaderList.Count)
        {
            // 生成完成，构建写入队列
            EditorApplication.update -= AnalyzeGenerateUpdate;
            AnalyzeBuildWriteQueue();
        }
    }

    /// <summary>
    /// 生成完成后构建写入队列并启动异步写入
    /// </summary>
    private static void AnalyzeBuildWriteQueue()
    {
        var wrapper = new ShaderVariantCollectionManifest { ShaderVariantInfos = _genVariantInfos };
        int maxVariantsPerFile = ShaderVariantCollectorSetting.GetMaxVariantsPerFile(_scanPackageName);
        _writeQueue = new List<(string, Shader, ShaderVariantCollectionManifest.ShaderVariantInfo)>();
        _writeShaderNames = new List<string>();
        _writeSavePath = _scanSavePath;
        _writeDebugLog = _scanDebugLog;
        _writeDebugRaw = _scanDebugRaw;
        _writeWrapper = wrapper;
        _writeTotalVariants = 0;

        if (_scanSplitByShaderName)
        {
            string basePath = Path.GetDirectoryName(_scanSavePath);
            if (Directory.Exists(basePath))
            {
                Directory.Delete(basePath, true);
                Directory.CreateDirectory(basePath);
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            foreach (var info in wrapper.ShaderVariantInfos)
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(info.AssetPath);
                if (shader == null) continue;

                string shaderName = info.ShaderName.Replace('/', '_').Replace('\\', '_');

                if (maxVariantsPerFile <= 0 || info.ShaderVariantElements.Count <= maxVariantsPerFile)
                {
                    string shaderSavePath = Path.Combine(basePath, $"{shaderName}.shadervariants");
                    _writeShaderNames.Add(shaderName);
                    _writeQueue.Add((shaderSavePath, shader, info));
                }
                else
                {
                    int fileIndex = 0;
                    for (int offset = 0; offset < info.ShaderVariantElements.Count; offset += maxVariantsPerFile)
                    {
                        int count = Mathf.Min(maxVariantsPerFile, info.ShaderVariantElements.Count - offset);
                        var chunk = new ShaderVariantCollectionManifest.ShaderVariantInfo
                        {
                            AssetPath = info.AssetPath,
                            ShaderName = info.ShaderName,
                            ShaderVariantElements = info.ShaderVariantElements.GetRange(offset, count)
                        };
                        chunk.ShaderVariantCount = count;

                        string shaderSavePath = Path.Combine(basePath, $"{shaderName}_{fileIndex}.shadervariants");
                        _writeShaderNames.Add($"{shaderName}_{fileIndex}");
                        _writeQueue.Add((shaderSavePath, shader, chunk));
                        fileIndex++;
                    }
                }

                _writeTotalVariants += info.ShaderVariantElements.Count;
            }
        }
        else
        {
            var mergedInfo = new ShaderVariantCollectionManifest.ShaderVariantInfo
            {
                AssetPath = wrapper.ShaderVariantInfos.Count > 0 ? wrapper.ShaderVariantInfos[0].AssetPath : "",
                ShaderName = wrapper.ShaderVariantInfos.Count > 0 ? wrapper.ShaderVariantInfos[0].ShaderName : "",
            };
            foreach (var info in wrapper.ShaderVariantInfos)
                mergedInfo.ShaderVariantElements.AddRange(info.ShaderVariantElements);
            mergedInfo.ShaderVariantCount = mergedInfo.ShaderVariantElements.Count;

            Shader firstShader = AssetDatabase.LoadAssetAtPath<Shader>(mergedInfo.AssetPath);
            if (firstShader != null)
                _writeQueue.Add((_scanSavePath, firstShader, mergedInfo));
            _writeTotalVariants = mergedInfo.ShaderVariantCount;
        }

        _writeIndex = 0;
        _analyzeProgress = 0.5f;
        _analyzeStatus = $"写入变种 0/{_writeQueue.Count}";
        EditorApplication.update += AnalyzeWriteUpdate;
    }

    private static void AnalyzeWriteUpdate()
    {
        if (_cancelRequested)
        {
            Debug.Log("[分析模式] 用户取消写入");
            EditorApplication.update -= AnalyzeWriteUpdate;
            _analyzeRunning = false;
            _analyzeProgress = 0f;
            _analyzeStatus = "";
            return;
        }

        int batchEnd = Mathf.Min(_writeIndex + WriteBatchSize, _writeQueue.Count);

        for (int i = _writeIndex; i < batchEnd; i++)
        {
            var (path, shader, info) = _writeQueue[i];
            WriteShaderVariantFileRaw(path, shader, info);
        }

        // 每批保存一次
        AssetDatabase.SaveAssets();

        _writeIndex = batchEnd;
        _analyzeProgress = 0.5f + 0.5f * _writeIndex / _writeQueue.Count;
        _analyzeStatus = $"写入变种 {_writeIndex}/{_writeQueue.Count}";

        if (_writeIndex >= _writeQueue.Count)
        {
            // 写入完成
            EditorApplication.update -= AnalyzeWriteUpdate;

            // 保存 shader 名称列表
            if (_writeShaderNames.Count > 0)
            {
                string basePath = Path.GetDirectoryName(_writeSavePath);
                string baseName = Path.GetFileNameWithoutExtension(_writeSavePath);
                string shaderNamesPath = Path.Combine(basePath, $"{baseName}_shaderNames.txt");
                File.WriteAllLines(shaderNamesPath, _writeShaderNames);
            }

            // 保存 debug 日志
            if (_writeDebugRaw)
            {
                string debugDir = Path.GetDirectoryName(Path.GetDirectoryName(_writeSavePath));
                string debugPath = Path.Combine(debugDir, "debug", "debug.txt");
                EditorTools.CreateFileDirectory(debugPath);
                _writeDebugLog.AppendLine();
                _writeDebugLog.AppendLine($"完成: {_writeWrapper.ShaderVariantInfos.Count} 个 shader, {_writeTotalVariants} 个变种");
                File.WriteAllText(debugPath, _writeDebugLog.ToString());
                Debug.Log($"[分析模式] Debug 日志: {debugPath}");
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            Debug.Log($"[分析模式] 完成: {_writeQueue.Count} 个文件, {_writeTotalVariants} 个变种");

            _writeQueue = null;
            _steps = ESteps.None;
            _analyzeRunning = false;
            _analyzeProgress = 0f;
            _analyzeStatus = "";
        }
    }

    private static bool IsBlackPath(string path, HashSet<string> blackSet)
    {
        foreach (string black in blackSet)
        {
            if (!string.IsNullOrEmpty(black) && path.Contains(black))
                return true;
        }
        return false;
    }
    
    
    private static void EditorUpdate()
    {
        if (_steps == ESteps.None)
            return;

        // 添加GUI显示
        // if (Event.current.type == EventType.Repaint)
        // {
        //     string statusText = $"当前状态: {_steps}";
        //     string timeText = _elapsedTime != null ? $"经过时间: {_elapsedTime.ElapsedMilliseconds}ms" : "经过时间: 0ms";
        //     
        //     Debug.Log( $"{statusText} {timeText}");
        // }

        if (_steps == ESteps.Prepare)
        {
            _analyzeProgress = 0f;
            _analyzeStatus = "准备中";
            ShaderVariantCollectionHelper.ClearCurrentShaderVariantCollection();
            _steps = ESteps.CollectAllMaterial;
            return; //等待一帧
        }

        if (_steps == ESteps.CollectAllMaterial)
        {
            _analyzeProgress = 0.05f;
            _analyzeStatus = "扫描材质";
            _allMaterials = GetAllMaterials();
            _analyzeProgress = 0.1f;
            _analyzeStatus = $"扫描完成: {_allMaterials.Count} 个材质";
            _steps = ESteps.CollectAllScene;
            return; //等待一帧
        }

        if (_steps == ESteps.CollectAllScene)
        {
            _analyzeProgress = 0.12f;
            _analyzeStatus = "扫描场景";
            if (_collectSceneVariants)
            {
                _allScene = GetAllScenes(_scenePath);
                _steps = ESteps.CollectVariants;
            }
            else
            {
                _allScene = new List<string>();
                _steps = ESteps.CollectVariants;
            }
            _totalMaterialCount = _allMaterials.Count;
            return; //等待一帧
        }

        if (_steps == ESteps.CollectVariants)
        {
            int count = Mathf.Min(_processMaxNum, _allMaterials.Count);
            _rangeMt = _allMaterials.GetRange(0, count);
            _allMaterials.RemoveRange(0, count);
            CollectVariants(_rangeMt);

            // 重置 SVC 稳定检测状态
            _lastShaderCount = ShaderVariantCollectionHelper.GetCurrentShaderVariantCollectionShaderCount();
            _lastVariantCount = ShaderVariantCollectionHelper.GetCurrentShaderVariantCollectionVariantCount();
            _stableSince = 0f;

            // 更新进度
            int processedCount = _totalMaterialCount - _allMaterials.Count;
            _analyzeProgress = 0.15f + 0.7f * processedCount / Mathf.Max(1, _totalMaterialCount);
            _analyzeStatus = $"收集变种 {processedCount}/{_totalMaterialCount}";

            if (_allMaterials.Count > 0)
            {
                _elapsedTime = Stopwatch.StartNew();
                _steps = ESteps.CollectSleeping;
            }
            else
            {
                _elapsedTime = Stopwatch.StartNew();
                _steps = ESteps.CollectWaitToScene;
            }
        }

        if (_steps == ESteps.CollectWaitToScene)
        {
            _analyzeStatus = "等待变种稳定";
            if (IsSVCStable())
            {
                DestroyAllSpheres();
                _elapsedTime.Stop();
                _steps = ESteps.CollectSceneVariants;
            }
        }

        if (_steps == ESteps.CollectSleeping)
        {
            int processedCount = _totalMaterialCount - _allMaterials.Count;
            _analyzeProgress = 0.15f + 0.7f * processedCount / Mathf.Max(1, _totalMaterialCount);
            _analyzeStatus = $"编译变种 {processedCount}/{_totalMaterialCount}";
            if (IsSVCStable())
            {
                DestroyAllSpheres();
                _elapsedTime.Stop();
                _steps = ESteps.CollectVariants;
            }
        }

        if (_steps == ESteps.CollectSceneSleeping)
        {
            _analyzeProgress = 0.85f;
            _analyzeStatus = "收集场景变种";
            if (_elapsedTime.ElapsedMilliseconds > SleepSceneMilliseconds)
            {
                DestoryLoadScene();
                _elapsedTime.Stop();
                _steps = ESteps.CollectSceneVariants;
            }
            else
            {
                UpdateSceneCamera(_elapsedTime.ElapsedMilliseconds);
            }
        }

        if (_steps == ESteps.CollectSceneVariants)
        {
            if (_allScene.Count == 0)
            {
                DestoryLoadScene();
                _analyzeProgress = 0.9f;
                _analyzeStatus = "保存中";
                _steps = ESteps.WaitingDone;
            }
            else
            {
                _analyzeProgress = 0.85f;
                _analyzeStatus = "收集场景变种";
                var scenePath = _allScene[0];
                _allScene.RemoveAt(0);
                //加载场景
                CollectScene(scenePath);
                _elapsedTime = Stopwatch.StartNew();
                _steps = ESteps.CollectSceneSleeping;
            }
           
        }
        if (_steps == ESteps.WaitingDone)
        {
            _analyzeProgress = 0.9f;
            _analyzeStatus = "保存变种集";
            // 注意：一定要延迟保存才会起效
            if (_elapsedTime.ElapsedMilliseconds > WaitMilliseconds)
            {
                _elapsedTime.Stop();
                _steps = ESteps.SaveCollection;
            }
        }

        if (_steps == ESteps.SaveCollection)
        {
            _analyzeProgress = 0.9f;
            _analyzeStatus = "保存变种集";

            // 执行保存（不含拆分），并构建拆分队列
            SaveShaderVariantCollection(buildSplitQueue: true);

            if (_splitByShaderName && _splitQueue != null && _splitQueue.Count > 0)
            {
                _splitIndex = 0;
                _steps = ESteps.SplitWriting;
            }
            else
            {
                FinishCollection();
            }
        }

        if (_steps == ESteps.SplitWriting)
        {
            int batchEnd = Mathf.Min(_splitIndex + WriteBatchSize, _splitQueue.Count);

            for (int i = _splitIndex; i < batchEnd; i++)
            {
                var (path, shader, variants) = _splitQueue[i];
                WriteShaderVariantFile(path, shader, variants);
            }

            AssetDatabase.SaveAssets();
            _splitIndex = batchEnd;

            _analyzeProgress = 0.9f + 0.09f * _splitIndex / Mathf.Max(1, _splitQueue.Count);
            _analyzeStatus = $"拆分文件 {_splitIndex}/{_splitQueue.Count}";

            if (_splitIndex >= _splitQueue.Count)
            {
                FinishSplitCollection();
            }
        }
    }

    private static void CreateTempScene()
    {
        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);
    }

    private static List<string> GetAllMaterialsFromPrefabs(string searchPath)
    {
        List<string> materialsPaths = new List<string>();
        // Find all prefab assets in the specified path
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new string[] { searchPath });
        foreach (string guid in prefabGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null)
            {
                // Get all Renderer components in the prefab, including children
                Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer renderer in renderers)
                {
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        string materialPath = AssetDatabase.GetAssetPath(material);
                        if (!string.IsNullOrEmpty(materialPath))
                        {
                            materialsPaths.Add(materialPath);
                        }
                    }
                }
            }
        }

        return materialsPaths.Distinct().ToList(); // Remove duplicates and return the list
    }

    static void HandleKeyWorld(string keyworlds, bool enable = true)
    {
        var keys = keyworlds.Split(" ");
        foreach (var key in keys)
        {
            if (enable)
            {
                Shader.EnableKeyword(key.Trim());   
            }
            else
            {
                Shader.DisableKeyword(key.Trim());
            }
        }
    }
    
    static void HandleLocalKeyWorld(Material mt,string keyworlds, bool enable = true)
    {
        var keys = keyworlds.Split(" ");
        foreach (var key in keys)
        {
            if (enable)
            {
                mt.EnableKeyword(key.Trim());   
            }
            else
            {
                mt.DisableKeyword(key.Trim());
            }
        }
    }
    
    public static List<MaterialInfo> GetAllMaterials()
    {
        List<MaterialInfo> materials = new List<MaterialInfo>();
        string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { _searchPath });
    
        foreach (string guid in materialGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!IsBlack(path))
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material != null && material.shader != null)
                {
                    // 过滤着色器名称
                    if (_filterShaderName == null || !_filterShaderName.Contains(material.shader.name))
                    {
                        materials.Add(new MaterialInfo()
                        {
                            path = path,
                            keyword =""
                        });
                        
                        //local 关键字
                        if (_localKeywords != null)
                        {
                            List<string> keywords = _localKeywords.GetKeywordsForShader(material.shader.name);
                            if (keywords.Count > 0)
                            {
                                foreach (var keyword in keywords)
                                {
                                    materials.Add(new  MaterialInfo()
                                    {
                                        path = path,
                                        keyword = keyword
                                    });
                                }
                            }
                        }
                    }
                }
            }
        }
    
        return materials;
    }
    
    // private static List<string> GetAllMaterials()
    // {
    //     List<string> materials = GetAllMaterialsFromPrefabs(_searchPath);
    //
    //     return materials;
    // }

    public static List<string> GetAllScenes(string searchPath)
    {
        List<string> scenePaths = new List<string>();
        string[] sceneGUIDs = AssetDatabase.FindAssets("t:Scene", new[] { searchPath });
        foreach (string guid in sceneGUIDs)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!IsBlack(path))
            {
                scenePaths.Add(path);
            }
        }
        return scenePaths;
    }

    private static bool IsBlack(string path)
    {
        if (_blackPath == null || _blackPath.Count == 0)
        {
            return false;
        }

        foreach (string black in _blackPath)
        {
            if (black != "" && path.Contains(black))
            {
                return true;
            }
        }
        
        return false;
    }
    
    private static void OnlyCreate(List<MaterialInfo> materials)
    {
        Camera camera = Camera.main;
        if (camera == null)
            throw new System.Exception("Not found main camera.");

        // 设置主相机
        float aspect = camera.aspect;
        int totalMaterials = materials.Count;
        float height = Mathf.Sqrt(totalMaterials / aspect) + 1;
        float width = Mathf.Sqrt(totalMaterials / aspect) * aspect + 1;
        float halfHeight = Mathf.CeilToInt(height / 2f);
        float halfWidth = Mathf.CeilToInt(width / 2f);
        camera.orthographic = true;
        camera.orthographicSize = halfHeight;
        camera.transform.position = new Vector3(0f, 0f, -10f);

        // 创建测试球体
        int xMax = (int)(width - 1);
        int x = 0, y = 0;
        int progressValue = 0;
        for (int i = 0; i < materials.Count; i++)
        {
            var material = materials[i];
            var position = new Vector3(x - halfWidth + 1f, y - halfHeight + 1f, 0f);
            var go = CreateSphere(material, position, i);
            if (go != null)
            {
                _allSpheres.Add(go);
            }
            if (x == xMax)
            {
                x = 0;
                y++;
            }
            else
            {
                x++;
            }

            EditorTools.DisplayProgressBar("照射所有材质球", ++progressValue, materials.Count);
        }

        EditorTools.ClearProgressBar();
    }
    private static void CollectVariants(List<MaterialInfo> materials)
    {
        if (materials.Count <= 0)
        {
            return;
        }
        OnlyCreate(materials);
    }
    
    private static void CollectScene(string scenePath)
    
    {
        _currentScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
         if (_currentScene == null)
              return;
         
         Camera camera = Camera.main;
         if (camera == null)
             throw new System.Exception("Not found main camera.");

        // 设置主相机 在位置(0,100,0)处，朝向(0,0,0)
        camera.transform.position = new Vector3(0, 100, 0);
        camera.transform.LookAt(Vector3.zero);
    }
    
    private static void UpdateSceneCamera(float elapsedTimeElapsedMilliseconds)
    {
        Camera camera = Camera.main;
        if (camera == null)
            throw new System.Exception("Not found main camera.");

        // 设置主相机 在位置(0,100,0)处，朝向(0,0,0)
        var starPos = new Vector3(0, 100, 0);
        var endPos = new Vector3(1000, 100, 1000);
        camera.transform.position = Vector3.Lerp(starPos, endPos, elapsedTimeElapsedMilliseconds / SleepSceneMilliseconds);
    }

    // 添加KeywordHolder组件用于管理关键字的生命周期
    private class KeywordHolder : MonoBehaviour
    {
        public Material material;
        public List<string> appliedKeywords = new List<string>();

        private void OnDestroy()
        {
            // 在GameObject销毁时，禁用之前启用的所有局部关键字
            if (material != null)
            {
                foreach (var keyword in appliedKeywords)
                {
                    HandleLocalKeyWorld(material, keyword, false);
                }
            }
        }
    }
    
    private static GameObject CreateSphere(MaterialInfo materialInfo, Vector3 position, int index)
    {
        var materialO = AssetDatabase.LoadAssetAtPath<Material>(materialInfo.path);
        //创建一个材质实列
        var material = Material.Instantiate(materialO);
        if (material == null)
        {
            return null;
        }
        
        var shader = material.shader;
        if (shader == null)
            return null;
        
        //过滤shader _filterShaderName
        
        // if (_filterShaderName != null && _filterShaderName.Contains(shader.name))
        // {
        //     return null;
        // }

        if (!string.IsNullOrEmpty(materialInfo.keyword))
        {
            HandleLocalKeyWorld(material, materialInfo.keyword, true);
        }

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.GetComponent<Renderer>().sharedMaterial = material;
        go.transform.position = position;
        go.name = $"Sphere_{index} | {material.name}";
        
        //// 应用局部关键字并添加KeywordHolder组件来管理关键字生命周期
        // if (_localKeywords != null)
        // {
        //     List<string> keywords = _localKeywords.GetKeywordsForShader(shader.name);
        //     if (keywords.Count > 0)
        //     {
        //         var keywordHolder = go.AddComponent<KeywordHolder>();
        //         keywordHolder.material = material;
        //         keywordHolder.appliedKeywords.AddRange(keywords);
        //         
        //         foreach (var keyword in keywords)
        //         {
        //             HandleLocalKeyWorld(material, keyword, true);    
        //         }
        //     }
        // }
        
        return go;
    }

    /// <summary>
    /// 检测 SVC 是否已稳定（数量不再变化）
    /// 最少等待 MinSleepMilliseconds，之后每 StableCheckInterval 检查一次
    /// </summary>
    private static bool IsSVCStable()
    {
        float elapsed = _elapsedTime.ElapsedMilliseconds;

        // 超时强制通过
        if (elapsed > SleepMilliseconds)
            return true;

        // 最少等待
        if (elapsed < MinSleepMilliseconds)
            return false;

        int currentShader = ShaderVariantCollectionHelper.GetCurrentShaderVariantCollectionShaderCount();
        int currentVariant = ShaderVariantCollectionHelper.GetCurrentShaderVariantCollectionVariantCount();

        if (currentShader != _lastShaderCount || currentVariant != _lastVariantCount)
        {
            // 数量还在变化，重置稳定计时
            _lastShaderCount = currentShader;
            _lastVariantCount = currentVariant;
            _stableSince = elapsed;
            return false;
        }

        // 数量没变，检查是否已稳定超过 StableCheckInterval
        if (elapsed - _stableSince >= StableCheckInterval)
            return true;

        return false;
    }

    private static void DestroyAllSpheres()
    {
        // 确保在销毁球体前，KeywordHolder组件的OnDestroy会被调用，清理关键字状态
        foreach (var go in _allSpheres)
        {
            if (go != null)
            {
                GameObject.DestroyImmediate(go);
            }
        }

        _allSpheres.Clear();

        // 尝试释放编辑器加载的资源
        EditorUtility.UnloadUnusedAssetsImmediate(true);
    }
    
    private static void DestoryLoadScene()
    {
        if (_currentScene != null)
        {
            EditorSceneManager.CloseScene(_currentScene, true);    
        }
        
    }

    /// <summary>
    /// 保存变种集。当 buildSplitQueue=true 且启用拆分时，只构建拆分队列而不执行写入（由 SplitWriting 异步处理）。
    /// </summary>
    private static void SaveShaderVariantCollection(bool buildSplitQueue = false)
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        // 总是先保存完整的变体集，这样我们才能从中提取各个shader的变体
        ShaderVariantCollectionHelper.SaveCurrentShaderVariantCollection(_savePath);
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        ShaderVariantCollection svc = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(_savePath);
        if (svc != null)
        {
            var wrapper = ShaderVariantCollectionManifest.Extract(svc);

            // Debug 模式：保存原始渲染收集的变体文件
            bool debugRaw = ShaderVariantCollectorSetting.GetSaveDebugRawSVC(_currentPackageName);
            if (debugRaw)
            {
                string basePath = Path.GetDirectoryName(_savePath);
                string parentDir = Path.GetDirectoryName(basePath);
                string debugDir = Path.Combine(parentDir, "debug");
                EditorTools.CreateDirectory(debugDir);
                string debugPath = Path.Combine(debugDir, Path.GetFileName(_savePath).Replace(".shadervariants", "_RAW.shadervariants"));
                AssetDatabase.CopyAsset(_savePath, debugPath);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                Debug.Log($"[Debug] 原始渲染变体已保存: {debugPath}");
            }

            int beforePermutation = 0;
            foreach (var info in wrapper.ShaderVariantInfos) beforePermutation += info.ShaderVariantElements.Count;
            Debug.Log($"[变种统计] 排列组合前: {wrapper.ShaderVariantInfos.Count} shader, {beforePermutation} 变种");

            // 自动处理 multi_compile 组，重新排列组合变种
            AddGlobalKeywordVariantsToManifest(wrapper, _globalKeywords ?? new List<string>());

            int afterPermutation = 0;
            foreach (var info in wrapper.ShaderVariantInfos) afterPermutation += info.ShaderVariantElements.Count;
            Debug.Log($"[变种统计] 排列组合后: {wrapper.ShaderVariantInfos.Count} shader, {afterPermutation} 变种");

            // 获取Always Included Shaders列表
            var alwaysIncludedShaderNames = GetAlwaysIncludedShaderNames();
            var hideShader = GetURPHiddenShaderNames();
            alwaysIncludedShaderNames.UnionWith(hideShader);
            // 过滤掉Always Included Shaders
            wrapper.ShaderVariantInfos.RemoveAll(info => alwaysIncludedShaderNames.Contains(info.ShaderName));
            //过滤掉 shader 路径包含 Resources
            wrapper.ShaderVariantInfos.RemoveAll(info => info.AssetPath.Contains("Resources"));
            // 过滤掉指定的着色器名称
            if (_filterShaderName != null && _filterShaderName.Count > 0)
            {
                wrapper.ShaderVariantInfos.RemoveAll(info => _filterShaderName.Contains(info.ShaderName));
            }

            int afterFilter = 0;
            foreach (var info in wrapper.ShaderVariantInfos) afterFilter += info.ShaderVariantElements.Count;
            Debug.Log($"[变种统计] 过滤后: {wrapper.ShaderVariantInfos.Count} shader, {afterFilter} 变种");

            string jsonData = JsonUtility.ToJson(wrapper, true);

            // 根据设置决定是否保存JSON文件
            bool saveJsonFile = ShaderVariantCollectorSetting.GetSaveJsonFile(_currentPackageName);
            if (saveJsonFile)
            {
                string savePath = _savePath.Replace(".shadervariants", ".json");
                File.WriteAllText(savePath, jsonData);
            }

            if (_splitByShaderName)
            {
                string basePath = Path.GetDirectoryName(_savePath);
                string baseName = Path.GetFileNameWithoutExtension(_savePath);

                // 在保存之前删除目标路径下的所有.shadervariants文件
                if (Directory.Exists(basePath))
                {
                    Directory.Delete(basePath, true);
                    Directory.CreateDirectory(basePath);
                }

                int maxVariantsPerFile = ShaderVariantCollectorSetting.GetMaxVariantsPerFile(_currentPackageName);

                if (buildSplitQueue)
                {
                    // 构建异步拆分队列，不在此处写入
                    _splitQueue = new List<(string, Shader, List<ShaderVariantCollection.ShaderVariant>)>();
                    _splitShaderNames = new List<string>();
                    _splitBasePath = basePath;
                    _splitBaseName = baseName;

                    foreach (var shaderInfo in wrapper.ShaderVariantInfos)
                    {
                        string shaderName = shaderInfo.ShaderName.Replace('/', '_').Replace('\\', '_');
                        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderInfo.AssetPath);
                        if (shader == null) continue;

                        var validVariants = new List<ShaderVariantCollection.ShaderVariant>();
                        foreach (var variant in shaderInfo.ShaderVariantElements)
                        {
                            try
                            {
                                validVariants.Add(new ShaderVariantCollection.ShaderVariant(shader, variant.PassType, variant.Keywords));
                            }
                            catch (ArgumentException) { }
                        }

                        Debug.Log($"[拆分保存] {shaderName}: manifest变种={shaderInfo.ShaderVariantElements.Count}, 有效变种={validVariants.Count}");

                        if (maxVariantsPerFile <= 0 || validVariants.Count <= maxVariantsPerFile)
                        {
                            string shaderSavePath = Path.Combine(basePath, $"{shaderName}.shadervariants");
                            _splitShaderNames.Add(shaderName);
                            _splitQueue.Add((shaderSavePath, shader, validVariants));
                        }
                        else
                        {
                            int fileIndex = 0;
                            for (int offset = 0; offset < validVariants.Count; offset += maxVariantsPerFile)
                            {
                                int count = Mathf.Min(maxVariantsPerFile, validVariants.Count - offset);
                                string shaderSavePath = Path.Combine(basePath, $"{shaderName}_{fileIndex}.shadervariants");
                                _splitShaderNames.Add($"{shaderName}_{fileIndex}");
                                _splitQueue.Add((shaderSavePath, shader, validVariants.GetRange(offset, count)));
                                fileIndex++;
                            }
                        }
                    }
                }
                else
                {
                    // 同步拆分（兼容旧调用路径）
                    List<string> shaderNamesSave = new List<string>();
                    int totalShaders = wrapper.ShaderVariantInfos.Count;

                    for (int si = 0; si < totalShaders; si++)
                    {
                        var shaderInfo = wrapper.ShaderVariantInfos[si];
                        string shaderName = shaderInfo.ShaderName.Replace('/', '_').Replace('\\', '_');

                        _analyzeProgress = 0.9f + 0.09f * (si + 1) / Mathf.Max(1, totalShaders);
                        _analyzeStatus = $"拆分文件 {si + 1}/{totalShaders}: {shaderName}";

                        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderInfo.AssetPath);
                        if (shader == null) continue;

                        var validVariants = new List<ShaderVariantCollection.ShaderVariant>();
                        foreach (var variant in shaderInfo.ShaderVariantElements)
                        {
                            try
                            {
                                validVariants.Add(new ShaderVariantCollection.ShaderVariant(shader, variant.PassType, variant.Keywords));
                            }
                            catch (ArgumentException) { }
                        }

                        Debug.Log($"[拆分保存] {shaderName}: manifest变种={shaderInfo.ShaderVariantElements.Count}, 有效变种={validVariants.Count}");

                        if (maxVariantsPerFile <= 0 || validVariants.Count <= maxVariantsPerFile)
                        {
                            string shaderSavePath = Path.Combine(basePath, $"{shaderName}.shadervariants");
                            shaderNamesSave.Add(shaderName);
                            WriteShaderVariantFile(shaderSavePath, shader, validVariants);
                        }
                        else
                        {
                            int fileIndex = 0;
                            for (int offset = 0; offset < validVariants.Count; offset += maxVariantsPerFile)
                            {
                                int count = Mathf.Min(maxVariantsPerFile, validVariants.Count - offset);
                                string shaderSavePath = Path.Combine(basePath, $"{shaderName}_{fileIndex}.shadervariants");
                                shaderNamesSave.Add($"{shaderName}_{fileIndex}");
                                WriteShaderVariantFile(shaderSavePath, shader, validVariants.GetRange(offset, count));
                                fileIndex++;
                            }
                        }
                    }

                    string shaderNamesPath = Path.Combine(basePath, $"{baseName}_shaderNames.txt");
                    File.WriteAllLines(shaderNamesPath, shaderNamesSave);
                    AssetDatabase.DeleteAsset(_savePath);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                }
            }
        }

        if (!buildSplitQueue || !_splitByShaderName)
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }
    }

    /// <summary>
    /// 收集完成（无拆分或拆分队列为空时调用）
    /// </summary>
    private static void FinishCollection()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        Debug.Log($"搜集SVC完毕！");
        EditorApplication.update -= EditorUpdate;
        _steps = ESteps.None;
        _analyzeProgress = 0f;
        _analyzeStatus = "";
        _completedCallback?.Invoke();
    }

    /// <summary>
    /// 异步拆分完成后调用
    /// </summary>
    private static void FinishSplitCollection()
    {
        // 保存 shader 名称列表
        if (_splitShaderNames != null && _splitShaderNames.Count > 0)
        {
            string shaderNamesPath = Path.Combine(_splitBasePath, $"{_splitBaseName}_shaderNames.txt");
            File.WriteAllLines(shaderNamesPath, _splitShaderNames);
        }

        // 删除完整的变体集文件
        AssetDatabase.DeleteAsset(_savePath);
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        _splitQueue = null;
        _splitShaderNames = null;

        FinishCollection();
    }

    /// <summary>
    /// 绕过 ShaderVariantCollection.Add() 的限制，通过 SerializedObject 直接写入变种
    /// </summary>
    private static void WriteShaderVariantFile(string savePath, Shader shader, List<ShaderVariantCollection.ShaderVariant> variants)
    {
        ShaderVariantCollection svc = new ShaderVariantCollection();
        AssetDatabase.CreateAsset(svc, savePath);

        using (var so = new SerializedObject(svc))
        {
            var shadersArray = so.FindProperty("m_Shaders.Array");
            // 清空默认数据
            shadersArray.arraySize = 0;

            // 构建 shader -> variants 映射
            var shaderVariants = new Dictionary<Shader, List<(PassType passType, string[] keywords)>>();
            foreach (var sv in variants)
            {
                if (!shaderVariants.ContainsKey(sv.shader))
                    shaderVariants[sv.shader] = new List<(PassType, string[])>();
                shaderVariants[sv.shader].Add((sv.passType, sv.keywords));
            }

            int shaderIndex = 0;
            foreach (var kvp in shaderVariants)
            {
                shadersArray.InsertArrayElementAtIndex(shaderIndex);
                var shaderEntry = shadersArray.GetArrayElementAtIndex(shaderIndex);

                // 设置 shader 引用
                var firstProp = shaderEntry.FindPropertyRelative("first");
                firstProp.objectReferenceValue = kvp.Key;

                // 设置 variants
                var secondProp = shaderEntry.FindPropertyRelative("second");
                var variantsArray = secondProp.FindPropertyRelative("variants");
                variantsArray.arraySize = kvp.Value.Count;

                for (int v = 0; v < kvp.Value.Count; v++)
                {
                    var variantProp = variantsArray.GetArrayElementAtIndex(v);
                    variantProp.FindPropertyRelative("passType").intValue = (int)kvp.Value[v].passType;
                    variantProp.FindPropertyRelative("keywords").stringValue = string.Join(" ", kvp.Value[v].keywords);
                }

                shaderIndex++;
            }

            so.ApplyModifiedProperties();
        }

        EditorUtility.SetDirty(svc);
        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// 直接通过 SerializedObject 写入变种，跳过 ShaderVariant 验证
    /// 用于分析模式（passType 值可能不匹配枚举）
    /// </summary>
    private static void WriteShaderVariantFileRaw(string savePath, Shader shader, ShaderVariantCollectionManifest.ShaderVariantInfo info)
    {
        ShaderVariantCollection svc = new ShaderVariantCollection();
        AssetDatabase.CreateAsset(svc, savePath);

        using (var so = new SerializedObject(svc))
        {
            var shadersArray = so.FindProperty("m_Shaders.Array");
            shadersArray.arraySize = 0;

            shadersArray.InsertArrayElementAtIndex(0);
            var shaderEntry = shadersArray.GetArrayElementAtIndex(0);
            shaderEntry.FindPropertyRelative("first").objectReferenceValue = shader;

            var secondProp = shaderEntry.FindPropertyRelative("second");
            var variantsArray = secondProp.FindPropertyRelative("variants");
            variantsArray.arraySize = info.ShaderVariantElements.Count;

            for (int v = 0; v < info.ShaderVariantElements.Count; v++)
            {
                var variantProp = variantsArray.GetArrayElementAtIndex(v);
                variantProp.FindPropertyRelative("passType").intValue = (int)info.ShaderVariantElements[v].PassType;
                variantProp.FindPropertyRelative("keywords").stringValue = string.Join(" ", info.ShaderVariantElements[v].Keywords);
            }

            so.ApplyModifiedProperties();
        }

        EditorUtility.SetDirty(svc);
        AssetDatabase.SaveAssets();
    }

    /// <summary>
    /// 向 manifest 中注入 multi_compile 变种
    /// 策略：按 pass 解析 shader 中的 multi_compile 组，去掉组关键字得到基础变种，再按 pass 重新排列组合
    /// 无组关键字的变种（如 ShadowCaster）直接保留
    /// </summary>
    private static void AddGlobalKeywordVariantsToManifest(ShaderVariantCollectionManifest wrapper, List<string> excludeKeywords, HashSet<string> materialKeywords = null)
    {
        int addedCount = 0;
        var excludeSet = new HashSet<string>(excludeKeywords);

        foreach (var shaderInfo in wrapper.ShaderVariantInfos)
        {
            string shaderPath = shaderInfo.AssetPath;
            HashSet<string> shaderGlobalKeywords = GetShaderSupportedKeywords(shaderPath);

            // 按 pass 解析 multi_compile 组，构建 passType → groups 映射
            var allPassInfos = GetMultiCompileGroupsByPass(shaderPath);
            Debug.Log($"[变种重组] {shaderInfo.ShaderName}: shaderPath={shaderPath}, 文件存在={File.Exists(shaderPath)}, 解析pass数={allPassInfos.Count}");
            var passTypeToGroups = new Dictionary<int, List<HashSet<string>>>();
            foreach (var passInfo in allPassInfos)
            {
                if ((int)passInfo.PassType == -1) continue; // 自定义 pass 单独处理
                var filtered = new List<HashSet<string>>();
                foreach (var group in passInfo.Groups)
                {
                    bool excluded = false;
                    foreach (string kw in group)
                    {
                        if (excludeSet.Contains(kw)) { excluded = true; break; }
                    }
                    if (excluded) continue;

                    var groupFiltered = new HashSet<string>();
                    foreach (string kw in group)
                    {
                        if (shaderGlobalKeywords.Contains(kw) && (materialKeywords == null || materialKeywords.Contains(kw)))
                            groupFiltered.Add(kw);
                    }
                    if (groupFiltered.Count >= 1)
                        filtered.Add(groupFiltered);
                }
                passTypeToGroups[(int)passInfo.PassType] = filtered;
            }

            // 收集已有变种（快照）
            var existingVariants = new List<(PassType passType, string[] keywords)>();
            foreach (var variant in shaderInfo.ShaderVariantElements)
            {
                existingVariants.Add((variant.PassType, variant.Keywords));
            }

            Debug.Log($"[变种重组] {shaderInfo.ShaderName}: 已有变种={existingVariants.Count}, pass组数={passTypeToGroups.Count}");
            foreach (var kvp in passTypeToGroups)
            {
                var groupStrs = new List<string>();
                foreach (var g in kvp.Value)
                    groupStrs.Add("{" + string.Join(",", g) + "}");
                Debug.Log($"  passType={kvp.Key}: {string.Join(" | ", groupStrs)}");
            }
            // 打印前 5 个已有变种用于调试
            for (int vi = 0; vi < Mathf.Min(5, existingVariants.Count); vi++)
            {
                var v = existingVariants[vi];
                Debug.Log($"  已有变种[{vi}]: passType={(int)v.passType} keywords=[{string.Join(", ", v.keywords)}]");
            }

            // 清空变种列表，重新构建
            shaderInfo.ShaderVariantElements.Clear();
            shaderInfo.ShaderVariantCount = 0;
            var seenVariants = new HashSet<string>();

            foreach (var (passType, baseKeywords) in existingVariants)
            {
                // 获取当前 pass 的组（没有则跳过重组，直接保留原始变种）
                if (!passTypeToGroups.TryGetValue((int)passType, out var currentPassGroups))
                    currentPassGroups = new List<HashSet<string>>();

                var currentPassGroupKeywords = new HashSet<string>();
                foreach (var group in currentPassGroups)
                    currentPassGroupKeywords.UnionWith(group);

                // 第一步：去掉当前 pass 组中的关键字，得到基础关键字（trim 并过滤空串）
                var cleanKeywords = new List<string>();
                foreach (string kw in baseKeywords)
                {
                    string trimmed = kw.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !currentPassGroupKeywords.Contains(trimmed))
                        cleanKeywords.Add(trimmed);
                }
                cleanKeywords.Sort();

                // 检查原始变种是否包含当前 pass 的组关键字
                bool hasGroupKeyword = false;
                foreach (string kw in baseKeywords)
                {
                    if (currentPassGroupKeywords.Contains(kw.Trim()))
                    {
                        hasGroupKeyword = true;
                        break;
                    }
                }

                // 如果当前变种没有组关键字（如 ShadowCaster），直接保留原始
                if (!hasGroupKeyword)
                {
                    // 排除包含被排除关键字的变种
                    bool hasExcluded = false;
                    foreach (string kw in cleanKeywords)
                    {
                        if (excludeSet.Contains(kw)) { hasExcluded = true; break; }
                    }
                    if (!hasExcluded)
                    {
                        string key = $"{(int)passType}|{string.Join(" ", cleanKeywords)}";
                        if (!seenVariants.Contains(key))
                        {
                            seenVariants.Add(key);
                            shaderInfo.ShaderVariantElements.Add(new ShaderVariantCollectionManifest.ShaderVariantElement
                            {
                                PassType = passType,
                                Keywords = cleanKeywords.ToArray()
                            });
                            shaderInfo.ShaderVariantCount++;
                            addedCount++;
                        }
                    }
                    continue;
                }

                // 第二步：用当前 pass 的组关键字重新排列组合（笛卡尔积）
                var combinations = GenerateGroupCombinations(currentPassGroups);

                foreach (var combo in combinations)
                {
                    var finalKeywords = new List<string>(cleanKeywords);
                    foreach (string kw in combo)
                    {
                        string trimmed = kw.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                            finalKeywords.Add(trimmed);
                    }
                    finalKeywords.Sort();

                    // 排除包含被排除关键字的变种
                    bool hasExcludedCombo = false;
                    foreach (string kw in finalKeywords)
                    {
                        if (excludeSet.Contains(kw)) { hasExcludedCombo = true; break; }
                    }
                    if (hasExcludedCombo) continue;

                    // 去重：基于 passType + 关键字字符串
                    string key = $"{(int)passType}|{string.Join(" ", finalKeywords)}";
                    if (seenVariants.Contains(key)) continue;
                    seenVariants.Add(key);

                    shaderInfo.ShaderVariantElements.Add(new ShaderVariantCollectionManifest.ShaderVariantElement
                    {
                        PassType = passType,
                        Keywords = finalKeywords.ToArray()
                    });
                    shaderInfo.ShaderVariantCount++;
                    addedCount++;
                }
            }

            Debug.Log($"[变种重组] {shaderInfo.ShaderName}: 重组后变种数={shaderInfo.ShaderVariantCount}");

            // ---- 自定义 pass 关键字注入 ----
            // 自定义 pass（LightMode 不在标准映射中）无法单独收集变种，
            // 每个自定义 pass 独立计算基准：取材质关键字 ∩ 该 pass 声明关键字，
            // 然后与该 pass 的 multi_compile 组做笛卡尔积，注入到默认 pass 变种中。
            var customPasses = new List<PassInfo>();
            foreach (var passInfo in allPassInfos)
            {
                if ((int)passInfo.PassType == -1)
                    customPasses.Add(passInfo);
            }

            if (customPasses.Count > 0)
            {
                // 收集默认 pass（passType 0 和 13）已有变种中的所有材质关键字
                var materialKeywords = new HashSet<string>();
                foreach (var variant in shaderInfo.ShaderVariantElements)
                {
                    if ((int)variant.PassType == 0 || (int)variant.PassType == 13)
                    {
                        foreach (string kw in variant.Keywords)
                        {
                            if (!string.IsNullOrEmpty(kw))
                                materialKeywords.Add(kw.Trim());
                        }
                    }
                }

                foreach (var customPass in customPasses)
                {
                    int customAdded = 0;

                    // 该 pass 声明的所有关键字（multi_compile + shader_feature_local）
                    var passDeclared = customPass.AllKeywords ?? new HashSet<string>();

                    // 该 pass 的 multi_compile 组（用于笛卡尔积）
                    var customGroups = new List<HashSet<string>>();
                    foreach (var group in customPass.Groups)
                    {
                        bool excluded = false;
                        foreach (string kw in group)
                        {
                            if (excludeSet.Contains(kw)) { excluded = true; break; }
                        }
                        if (excluded) continue;
                        if (group.Count >= 1)
                            customGroups.Add(new HashSet<string>(group));
                    }

                    var customGroupKeywords = new HashSet<string>();
                    foreach (var group in customGroups)
                        customGroupKeywords.UnionWith(group);

                    // 基准 = 材质关键字 ∩ 该 pass 声明关键字
                    var passBase = new HashSet<string>();
                    foreach (string kw in materialKeywords)
                    {
                        if (passDeclared.Contains(kw))
                            passBase.Add(kw);
                    }

                    // 去掉 multi_compile 组关键字，得到干净的基准
                    var cleanBase = new List<string>();
                    foreach (string kw in passBase)
                    {
                        if (!customGroupKeywords.Contains(kw))
                            cleanBase.Add(kw);
                    }
                    cleanBase.Sort();

                    // 用 multi_compile 组做笛卡尔积
                    var combinations = GenerateGroupCombinations(customGroups);

                    foreach (var combo in combinations)
                    {
                        var finalKws = new List<string>(cleanBase);
                        foreach (string kw in combo)
                        {
                            string trimmed = kw.Trim();
                            if (!string.IsNullOrEmpty(trimmed))
                                finalKws.Add(trimmed);
                        }
                        finalKws.Sort();

                        bool hasExcluded = false;
                        foreach (string kw in finalKws)
                        {
                            if (excludeSet.Contains(kw)) { hasExcluded = true; break; }
                        }
                        if (hasExcluded) continue;

                        // 注入到默认 pass（passType 0）
                        string key = $"0|{string.Join(" ", finalKws)}";
                        if (seenVariants.Contains(key)) continue;
                        seenVariants.Add(key);

                        shaderInfo.ShaderVariantElements.Add(new ShaderVariantCollectionManifest.ShaderVariantElement
                        {
                            PassType = (PassType)0,
                            Keywords = finalKws.ToArray()
                        });
                        shaderInfo.ShaderVariantCount++;
                        addedCount++;
                        customAdded++;
                    }

                    Debug.Log($"[自定义pass注入] {shaderInfo.ShaderName} LightMode=\"{customPass.Name}\": 基准=[{string.Join(",", passBase)}] 注入 {customAdded} 个变种");
                }
            }
        }

        if (addedCount > 0)
        {
            wrapper.VariantTotalCount += addedCount;
            Debug.Log($"[multi_compile 变种重组] 处理了 {addedCount} 个变种");
        }
    }

    private const int MaxCombinationsPerVariant = 256;

    /// <summary>
    /// 生成多个 multi_compile 组的笛卡尔积，上限 MaxCombinationsPerVariant
    /// </summary>
    private static List<List<string>> GenerateGroupCombinations(List<HashSet<string>> groups)
    {
        // 先计算总组合数
        long total = 1;
        foreach (var group in groups)
        {
            total *= (group.Count + 1); // +1 for "skip this group"
        }
        total -= 1; // 减去全部跳过的情况

        if (total > MaxCombinationsPerVariant)
        {
            Debug.LogWarning($"[multi_compile] 组合数 {total} 超过上限 {MaxCombinationsPerVariant}，只取前 {MaxCombinationsPerVariant} 个。请在排除列表中添加不需要的关键字以减少组合。");
        }

        var result = new List<List<string>>();
        GenerateGroupCombinationsRecursive(groups, 0, new List<string>(), result);
        return result;
    }

    private static void GenerateGroupCombinationsRecursive(List<HashSet<string>> groups, int index, List<string> current, List<List<string>> result)
    {
        if (result.Count >= MaxCombinationsPerVariant) return;

        if (index >= groups.Count)
        {
            // 保留空组合（_ 默认状态：不启用任何组关键字）
            result.Add(new List<string>(current));
            return;
        }

        // 不选该组的任何关键字
        GenerateGroupCombinationsRecursive(groups, index + 1, current, result);

        // 选该组的每个关键字
        foreach (string kw in groups[index])
        {
            if (result.Count >= MaxCombinationsPerVariant) return;
            current.Add(kw);
            GenerateGroupCombinationsRecursive(groups, index + 1, current, result);
            current.RemoveAt(current.Count - 1);
        }
    }

    /// <summary>
    /// 从 shader 源码解析所有 #pragma multi_compile 声明的互斥关键字组（不区分 pass）
    /// 只解析 multi_compile（全局），跳过 multi_compile_local（局部）
    /// </summary>
    private static List<HashSet<string>> GetMultiCompileGroups(string shaderPath)
    {
        var groups = new List<HashSet<string>>();
        if (string.IsNullOrEmpty(shaderPath) || !File.Exists(shaderPath))
            return groups;

        try
        {
            string source = File.ReadAllText(shaderPath);
            var lines = source.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (IsCommentLine(trimmed)) continue;
                if (!IsMultiCompileDirective(trimmed)) continue;

                string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var keywords = new HashSet<string>();
                for (int i = 2; i < parts.Length; i++)
                {
                    string kw = parts[i].Trim();
                    if (!string.IsNullOrEmpty(kw) && kw != "_")
                        keywords.Add(kw);
                }
                if (keywords.Count >= 1)
                    groups.Add(keywords);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[multi_compile] 解析 shader 源码失败: {shaderPath}, {e.Message}");
        }

        return groups;
    }

    private struct PassInfo
    {
        public string Name;
        public PassType PassType;
        public List<HashSet<string>> Groups;      // multi_compile 组（用于笛卡尔积）
        public HashSet<string> AllKeywords;        // 该 pass 声明的所有关键字（multi_compile + shader_feature）
    }

    /// <summary>
    /// 按 pass 解析 shader 源码中的 #pragma multi_compile 声明
    /// </summary>
    private static List<PassInfo> GetMultiCompileGroupsByPass(string shaderPath)
    {
        var passes = new List<PassInfo>();
        if (string.IsNullOrEmpty(shaderPath) || !File.Exists(shaderPath))
            return passes;

        try
        {
            string source = File.ReadAllText(shaderPath);
            var lines = source.Split('\n');
            Debug.Log($"[GetMultiCompileGroupsByPass] {shaderPath}: 共 {lines.Length} 行");

            int braceDepth = 0;
            int passStartLine = -1;
            int passBraceStart = -1;
            string passTags = "";

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                // 检测 Pass 块的开始
                if (trimmed == "Pass" || trimmed == "Pass {")
                {
                    passStartLine = i;
                    passBraceStart = -1;
                    passTags = "";
                    if (trimmed == "Pass {")
                        passBraceStart = braceDepth;
                    Debug.Log($"[GetMultiCompileGroupsByPass] 行{i+1}: 发现Pass, trimmed=\"{trimmed}\", braceDepth={braceDepth}");
                }

                // 追踪花括号深度
                foreach (char c in line)
                {
                    if (c == '{')
                    {
                        braceDepth++;
                        if (passStartLine >= 0 && passBraceStart < 0)
                            passBraceStart = braceDepth;
                    }
                    else if (c == '}')
                    {
                        braceDepth--;
                        // Pass 块结束
                        if (passStartLine >= 0 && passBraceStart >= 0 && braceDepth < passBraceStart)
                        {
                            if (string.IsNullOrEmpty(passTags))
                            {
                                Debug.LogWarning($"[Shader变种收集] {shaderPath} 第{passStartLine + 1}行: Pass 缺少 LightMode 标签，已跳过。请为该 Pass 添加 Tags {{ \"LightMode\" = \"xxx\" }} 以确保变种正确收集。");
                            }
                            else
                            {
                                var groups = ParseMultiCompileFromLines(lines, passStartLine, i);
                                var allKeywords = ParseAllKeywordsFromLines(lines, passStartLine, i);

                                // 先检查是否在自定义映射中配置了
                                bool hasCustomMapping = false;
                                var customMappings = ShaderVariantCollectorSetting.GetCustomLightModes(_currentPackageName);
                                foreach (var m in customMappings)
                                {
                                    if (m.lightModeTag == passTags) { hasCustomMapping = true; break; }
                                }

                                if (hasCustomMapping)
                                {
                                    // 配置了自定义映射 → 标记为 passType -1，走注入逻辑
                                    Debug.Log($"[GetMultiCompileGroupsByPass] 行{passStartLine+1}-{i+1}: 自定义pass LightMode=\"{passTags}\" 已配置映射，将注入变种（声明关键字 {allKeywords.Count} 个）。");
                                    passes.Add(new PassInfo
                                    {
                                        Name = passTags,
                                        PassType = (PassType)(-1),
                                        Groups = groups,
                                        AllKeywords = allKeywords
                                    });
                                }
                                else
                                {
                                    var passType = LightModeToPassType(passTags);
                                    if ((int)passType == -1)
                                    {
                                        Debug.LogWarning($"[Shader变种收集] {shaderPath} 第{passStartLine + 1}行: Pass LightMode=\"{passTags}\" 未在自定义映射中配置，已跳过。请在设置面板的自定义 LightMode 映射中添加 \"{passTags}\"。");
                                    }
                                    else
                                    {
                                        Debug.Log($"[GetMultiCompileGroupsByPass] 行{passStartLine+1}-{i+1}: 添加pass LightMode=\"{passTags}\" passType={passType} groups={groups.Count} allKeywords={allKeywords.Count}");
                                        passes.Add(new PassInfo
                                        {
                                            Name = passTags,
                                            PassType = passType,
                                            Groups = groups,
                                            AllKeywords = allKeywords
                                        });
                                    }
                                }
                            }
                            passStartLine = -1;
                            passBraceStart = -1;
                            passTags = "";
                        }
                    }
                }

                // 收集 Pass 内的 Tags（支持 Tags{"LightMode" = "xxx"} 写在同一行的情况）
                if (passStartLine >= 0 && trimmed.Contains("LightMode"))
                {
                    passTags = ExtractFirstQuotedValue(trimmed);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[multi_compile] 解析 shader 源码失败: {shaderPath}, {e.Message}");
        }

        return passes;
    }

    private static List<HashSet<string>> ParseMultiCompileFromLines(string[] lines, int startLine, int endLine)
    {
        var groups = new List<HashSet<string>>();
        for (int i = startLine; i <= endLine; i++)
        {
            string trimmed = lines[i].Trim();
            if (IsCommentLine(trimmed)) continue;
            if (!IsMultiCompileDirective(trimmed)) continue;

            string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var keywords = new HashSet<string>();
            for (int j = 2; j < parts.Length; j++)
            {
                string kw = parts[j].Trim();
                if (!string.IsNullOrEmpty(kw) && kw != "_")
                    keywords.Add(kw);
            }
            if (keywords.Count >= 1)
                groups.Add(keywords);
        }
        return groups;
    }

    /// <summary>
    /// 解析指定行范围内所有关键字声明（multi_compile + multi_compile_local + shader_feature + shader_feature_local）
    /// </summary>
    private static HashSet<string> ParseAllKeywordsFromLines(string[] lines, int startLine, int endLine)
    {
        var keywords = new HashSet<string>();
        for (int i = startLine; i <= endLine; i++)
        {
            string trimmed = lines[i].Trim();
            if (IsCommentLine(trimmed)) continue;
            if (!trimmed.StartsWith("#pragma ")) continue;

            string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string directive = parts[1];
            if (directive == "multi_compile" || directive == "multi_compile_local" ||
                directive.StartsWith("shader_feature"))
            {
                for (int j = 2; j < parts.Length; j++)
                {
                    string kw = parts[j].Trim();
                    if (!string.IsNullOrEmpty(kw) && kw != "_")
                        keywords.Add(kw);
                }
            }
        }
        return keywords;
    }

    /// <summary>
    /// 从 shader 源码获取所有 pass 的 passType 列表
    /// </summary>
    private static List<PassType> GetShaderPassTypes(string shaderPath)
    {
        return GetShaderPassTypesDebug(shaderPath, null);
    }

    private static List<PassType> GetShaderPassTypesDebug(string shaderPath, System.Text.StringBuilder debugLog)
    {
        var passTypes = new List<PassType>();
        if (string.IsNullOrEmpty(shaderPath))
        {
            debugLog?.AppendLine($"    [pass解析] shaderPath 为空");
            return passTypes;
        }
        if (!File.Exists(shaderPath))
        {
            debugLog?.AppendLine($"    [pass解析] 文件不存在: {shaderPath}");
            return passTypes;
        }

        try
        {
            string source = File.ReadAllText(shaderPath);
            debugLog?.AppendLine($"    [pass解析] 读取文件成功, 长度={source.Length}");
            var lines = source.Split('\n');
            int braceDepth = 0;
            bool waitingPassBrace = false;
            int passStartDepth = -1;
            string currentLightMode = null;
            int lineNum = 0;

            foreach (string line in lines)
            {
                lineNum++;
                string trimmed = line.Trim();

                if (IsCommentLine(trimmed))
                    continue;

                if (trimmed == "Pass" || trimmed == "Pass {")
                {
                    waitingPassBrace = true;
                    passStartDepth = -1;
                    currentLightMode = null;
                    debugLog?.AppendLine($"    [pass解析] 行{lineNum}: 发现 Pass");
                }

                foreach (char c in line)
                {
                    if (c == '{')
                    {
                        braceDepth++;
                        if (waitingPassBrace)
                        {
                            passStartDepth = braceDepth;
                            waitingPassBrace = false;
                            debugLog?.AppendLine($"    [pass解析] 行{lineNum}: pass 块开始, depth={passStartDepth}");
                        }
                    }
                    else if (c == '}')
                    {
                        if (passStartDepth >= 0 && braceDepth == passStartDepth)
                        {
                            if (currentLightMode != null)
                            {
                                var passType = LightModeToPassType(currentLightMode);
                                if ((int)passType != -1)
                                {
                                    passTypes.Add(passType);
                                    debugLog?.AppendLine($"    [pass解析] 行{lineNum}: pass 块结束, LightMode={currentLightMode}, passType={passType}");
                                }
                                else
                                {
                                    Debug.LogWarning($"[Shader变种收集] {shaderPath} 行{lineNum}: Pass LightMode=\"{currentLightMode}\" 未在 LightModeToPassType 中映射，已跳过。");
                                    debugLog?.AppendLine($"    [pass解析] 行{lineNum}: pass 块结束, LightMode={currentLightMode}, 未映射已跳过");
                                }
                            }
                            else
                            {
                                debugLog?.AppendLine($"    [pass解析] 行{lineNum}: pass 块结束, 无 LightMode");
                            }
                            passStartDepth = -1;
                            currentLightMode = null;
                        }
                        braceDepth--;
                    }
                }

                if (passStartDepth >= 0 && trimmed.Contains("LightMode"))
                {
                    currentLightMode = ExtractFirstQuotedValue(trimmed);
                    debugLog?.AppendLine($"    [pass解析] 行{lineNum}: LightMode={currentLightMode}");
                }
            }

            debugLog?.AppendLine($"    [pass解析] 完成: {passTypes.Count} 个 pass");
        }
        catch (Exception e)
        {
            debugLog?.AppendLine($"    [pass解析] 异常: {e.Message}");
        }

        return passTypes;
    }

    /// <summary>
    /// 解析指定 pass 块中声明的所有关键字（multi_compile + shader_feature）
    /// </summary>
    private static HashSet<string> GetPassDeclaredKeywords(string shaderPath, string passName)
    {
        var keywords = new HashSet<string>();
        if (string.IsNullOrEmpty(shaderPath) || !File.Exists(shaderPath))
            return keywords;

        try
        {
            string source = File.ReadAllText(shaderPath);
            var lines = source.Split('\n');
            int braceDepth = 0;
            bool waitingPassBrace = false;
            int passStartDepth = -1;
            string currentPassName = null;
            bool inTargetPass = false;

            foreach (string line in lines)
            {
                string trimmed = line.Trim();

                if (IsCommentLine(trimmed))
                    continue;

                if (trimmed == "Pass" || trimmed == "Pass {")
                {
                    waitingPassBrace = true;
                    passStartDepth = -1;
                    currentPassName = null;
                    inTargetPass = false;
                }

                foreach (char c in line)
                {
                    if (c == '{')
                    {
                        braceDepth++;
                        if (waitingPassBrace)
                        {
                            passStartDepth = braceDepth;
                            waitingPassBrace = false;
                        }
                    }
                    else if (c == '}')
                    {
                        braceDepth--;
                        if (passStartDepth >= 0 && braceDepth < passStartDepth)
                        {
                            passStartDepth = -1;
                            inTargetPass = false;
                        }
                    }
                }

                // 检测 pass 名称
                if (passStartDepth >= 0 && trimmed.StartsWith("Name "))
                {
                    currentPassName = trimmed.Substring(5).Trim().Trim('"');
                    if (currentPassName == passName || passName == null)
                        inTargetPass = true;
                }

                // 在目标 pass 中收集关键字声明
                if (inTargetPass && (trimmed.StartsWith("#pragma multi_compile") || trimmed.StartsWith("#pragma shader_feature")))
                {
                    string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < parts.Length; i++)
                    {
                        string kw = parts[i].Trim();
                        if (!string.IsNullOrEmpty(kw) && kw != "_" && !kw.StartsWith("multi_compile") && !kw.StartsWith("shader_feature"))
                            keywords.Add(kw);
                    }
                }
            }
        }
        catch { }

        return keywords;
    }

    /// <summary>
    /// 按 LightMode 标签匹配 pass，获取该 pass 中声明的所有关键字
    /// </summary>
    private static HashSet<string> GetPassDeclaredKeywordsByLightMode(string shaderPath, string lightModeTag)
    {
        var keywords = new HashSet<string>();
        if (string.IsNullOrEmpty(shaderPath) || !File.Exists(shaderPath))
            return keywords;

        try
        {
            string source = File.ReadAllText(shaderPath);
            var lines = source.Split('\n');
            int braceDepth = 0;
            bool waitingPassBrace = false;
            int passStartDepth = -1;
            string currentLightMode = null;
            bool inTargetPass = false;

            foreach (string line in lines)
            {
                string trimmed = line.Trim();

                if (IsCommentLine(trimmed))
                    continue;

                if (trimmed == "Pass" || trimmed == "Pass {")
                {
                    waitingPassBrace = true;
                    passStartDepth = -1;
                    currentLightMode = null;
                    inTargetPass = false;
                }

                foreach (char c in line)
                {
                    if (c == '{')
                    {
                        braceDepth++;
                        if (waitingPassBrace)
                        {
                            passStartDepth = braceDepth;
                            waitingPassBrace = false;
                        }
                    }
                    else if (c == '}')
                    {
                        braceDepth--;
                        if (passStartDepth >= 0 && braceDepth < passStartDepth)
                        {
                            passStartDepth = -1;
                            inTargetPass = false;
                        }
                    }
                }

                // 检测 LightMode 标签
                if (passStartDepth >= 0 && trimmed.Contains("LightMode"))
                {
                    currentLightMode = ExtractFirstQuotedValue(trimmed);
                    if (currentLightMode == lightModeTag || lightModeTag == null)
                        inTargetPass = true;
                }

                // 在目标 pass 中收集关键字声明
                if (inTargetPass && (trimmed.StartsWith("#pragma multi_compile") || trimmed.StartsWith("#pragma shader_feature")))
                {
                    string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < parts.Length; i++)
                    {
                        string kw = parts[i].Trim();
                        if (!string.IsNullOrEmpty(kw) && kw != "_" && !kw.StartsWith("multi_compile") && !kw.StartsWith("shader_feature"))
                            keywords.Add(kw);
                    }
                }
            }
        }
        catch { }

        return keywords;
    }

    /// <summary>
    /// 从行中提取第一个引号对内的值（如 "LightMode" = "ShadowCaster" "Queue" = "AlphaTest" → "ShadowCaster"）
    /// </summary>
    private static string ExtractFirstQuotedValue(string line)
    {
        int eqIdx = line.IndexOf('=');
        if (eqIdx < 0) return "";
        string afterEq = line.Substring(eqIdx + 1);
        int firstQuote = afterEq.IndexOf('"');
        if (firstQuote < 0) return afterEq.Trim();
        int secondQuote = afterEq.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0) return afterEq.Substring(firstQuote + 1).Trim();
        return afterEq.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
    }

    /// <summary>
    /// 检查行是否是注释（// 在 #pragma 之前）
    /// </summary>
    private static bool IsCommentLine(string trimmed)
    {
        if (trimmed.StartsWith("//")) return true;
        int commentIdx = trimmed.IndexOf("//");
        int pragmaIdx = trimmed.IndexOf("#pragma");
        if (commentIdx >= 0 && (pragmaIdx < 0 || commentIdx < pragmaIdx)) return true;
        return false;
    }

    /// <summary>
    /// 检查行是否是 multi_compile 声明（包括 multi_compile_local，排除 multi_compile_fragment 等后缀变体）
    /// multi_compile_local 是 shader 级关键字，所有使用该 shader 的材质都可能开启，需要参与组合
    /// </summary>
    private static bool IsMultiCompileDirective(string trimmed)
    {
        if (!trimmed.StartsWith("#pragma ")) return false;
        string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        // 匹配 "multi_compile" 和 "multi_compile_local"，排除 "multi_compile_fragment" 等
        return parts[1] == "multi_compile" || parts[1] == "multi_compile_local";
    }

    // passType 值来自渲染收集的 SVC 文件（与 Unity PassType 枚举不一致）
    // 未知的 LightMode 返回 -1，调用方需要处理
    private static PassType LightModeToPassType(string lightMode)
    {
        // 先检查自定义映射
        var customMappings = ShaderVariantCollectorSetting.GetCustomLightModes(_currentPackageName);
        foreach (var mapping in customMappings)
        {
            if (mapping.lightModeTag == lightMode)
                return (PassType)mapping.passType;
        }

        switch (lightMode)
        {
            // URP pass — 真实 passType 值（从构建日志确认）
            case "UniversalForward":
            case "UniversalForwardOnly": return (PassType)13;
            case "UniversalGBuffer": return (PassType)14;
            case "DepthOnly": return (PassType)100;
            case "DepthNormals": return (PassType)16;
            case "Universal2D": return (PassType)17;
            case "ShadowCaster": return (PassType)8;
            case "Meta": return (PassType)9;
            // 标准 Unity pass
            case "ForwardBase": return PassType.ForwardBase;
            case "ForwardAdd": return PassType.ForwardAdd;
            case "Deferred": return PassType.Deferred;
            case "MotionVectors": return PassType.MotionVectors;
            case "Vertex": return PassType.Vertex;
            case "VertexLMRGBM": return PassType.VertexLMRGBM;
            case "VertexLM": return PassType.VertexLM;
            default: return (PassType)(-1);
        }
    }

    /// <summary>
    /// 获取 shader 源码中声明的所有关键字
    /// </summary>
    private static HashSet<string> GetShaderSupportedKeywords(string shaderPath)
    {
        var keywords = new HashSet<string>();
        if (string.IsNullOrEmpty(shaderPath) || !File.Exists(shaderPath))
            return keywords;

        try
        {
            string source = File.ReadAllText(shaderPath);
            var lines = source.Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("#pragma multi_compile") || trimmed.StartsWith("#pragma shader_feature"))
                {
                    string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < parts.Length; i++)
                    {
                        string kw = parts[i].Trim();
                        if (!string.IsNullOrEmpty(kw) && kw != "_")
                        {
                            keywords.Add(kw);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[关键字注入] 读取 shader 失败: {shaderPath}, {e.Message}");
        }

        return keywords;
    }



    public static HashSet<string> GetAlwaysIncludedShaderNames()
    {
        HashSet<string> shaderNames = new HashSet<string>();

        var graphicsSettings = GraphicsSettings.GetGraphicsSettings();
        var serializedObject = new SerializedObject(graphicsSettings);
        var shadersProperty = serializedObject.FindProperty("m_AlwaysIncludedShaders");

        for (int i = 0; i < shadersProperty.arraySize; i++)
        {
            var shaderProp = shadersProperty.GetArrayElementAtIndex(i);
            var shader = shaderProp.objectReferenceValue as Shader;
            if (shader != null)
            {
                shaderNames.Add(shader.name);
            }
        }

        return shaderNames;
    }
    
    /// <summary>
    /// 获取 URP 包中所有 Hidden 开头的 Shader 名称集合（HashSet）
    /// </summary>
    public static HashSet<string> GetURPHiddenShaderNames()
    {
        HashSet<string> urpHiddenShaders = new HashSet<string>();

        // 查找所有 Shader 资源
        string[] guids = AssetDatabase.FindAssets("t:Shader");

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);

            // 只保留 URP 包路径下的 shader
            if (path.StartsWith("Packages/com.unity.render-pipelines."))
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader != null && shader.name.StartsWith("Hidden/"))
                {
                    urpHiddenShaders.Add(shader.name);
                }
            }
        }

        return urpHiddenShaders;
    }
}