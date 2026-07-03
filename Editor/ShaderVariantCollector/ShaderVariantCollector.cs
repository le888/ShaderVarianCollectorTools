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
        if (_steps != ESteps.None)
            return;

        if (Path.HasExtension(savePath) == false)
            savePath = $"{savePath}.shadervariants";
        if (Path.GetExtension(savePath) != ".shadervariants")
            throw new System.Exception("Shader variant file extension is invalid.");

        AssetDatabase.DeleteAsset(savePath);
        EditorTools.CreateFileDirectory(savePath);

        var filterSet = new HashSet<string>(filterShaderName);
        var blackSet = new HashSet<string>(blackPath);
        var excludeKeywords = new HashSet<string>(ShaderVariantCollectorSetting.GetGlobalKeywords(packageName));
        bool debugRaw = ShaderVariantCollectorSetting.GetSaveDebugRawSVC(packageName);

        var debugLog = new System.Text.StringBuilder();
        debugLog.AppendLine($"[分析模式] 开始 {System.DateTime.Now}");
        debugLog.AppendLine($"savePath: {savePath}");
        debugLog.AppendLine($"searchPath: {searchPath}");
        debugLog.AppendLine($"splitByShaderName: {splitByShaderName}");
        debugLog.AppendLine($"excludeKeywords: {string.Join(", ", excludeKeywords)}");
        debugLog.AppendLine();

        Debug.Log("[分析模式] 开始扫描材质...");

        // 1. 扫描所有材质，按 shader 分组，收集每个材质的启用关键字
        var shaderMaterials = new Dictionary<Shader, List<HashSet<string>>>();
        string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { searchPath });
        int skipped = 0;

        foreach (string guid in materialGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (IsBlackPath(path, blackSet)) { skipped++; continue; }

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || mat.shader == null) { skipped++; continue; }
            if (filterSet.Contains(mat.shader.name)) { skipped++; continue; }

            // 收集材质启用的关键字
            var enabledKeywords = new HashSet<string>();
            foreach (var kw in mat.shaderKeywords)
            {
                if (!string.IsNullOrEmpty(kw))
                    enabledKeywords.Add(kw);
            }

            if (!shaderMaterials.ContainsKey(mat.shader))
                shaderMaterials[mat.shader] = new List<HashSet<string>>();
            shaderMaterials[mat.shader].Add(enabledKeywords);

            if (debugRaw)
                debugLog.AppendLine($"材质: {path} → shader={mat.shader.name}, keywords=[{string.Join(", ", enabledKeywords)}]");
        }

        debugLog.AppendLine();
        debugLog.AppendLine($"扫描完成: {shaderMaterials.Count} 个 shader, {materialGuids.Length - skipped} 个材质, 跳过 {skipped} 个");
        Debug.Log($"[分析模式] 扫描完成: {shaderMaterials.Count} 个 shader, 跳过 {skipped} 个材质");

        // 2. 为每个 shader 生成变种
        var allVariantInfos = new List<ShaderVariantCollectionManifest.ShaderVariantInfo>();

        foreach (var kvp in shaderMaterials)
        {
            Shader shader = kvp.Key;
            var materialKeywords = kvp.Value;
            string shaderPath = AssetDatabase.GetAssetPath(shader);

            // 按 pass 解析 multi_compile 组，只收集用户选择的 pass type
            var allPassInfos = GetMultiCompileGroupsByPass(shaderPath);
            var selectedPassTypes = new HashSet<int>(ShaderVariantCollectorSetting.GetSelectedPassTypes(packageName));
            var passInfos = new List<PassInfo>();
            foreach (var p in allPassInfos)
            {
                if (selectedPassTypes.Contains((int)p.PassType))
                    passInfos.Add(p);
            }
            debugLog.AppendLine($"  [pass解析] {shader.name}: {allPassInfos.Count} 个 pass, 收集 {passInfos.Count} 个");
            if (passInfos.Count == 0)
            {
                debugLog.AppendLine($"  [跳过] {shader.name}: 无法解析 pass");
                continue;
            }

            // 收集该 shader 所有材质中启用的关键字
            var allEnabledKeywords = new HashSet<string>();
            foreach (var kwSet in materialKeywords)
                allEnabledKeywords.UnionWith(kwSet);

            // 构建变种列表
            var shaderInfo = new ShaderVariantCollectionManifest.ShaderVariantInfo
            {
                AssetPath = shaderPath,
                ShaderName = shader.name,
            };

            // 为每个 pass 生成变种（使用各自 pass 的 multi_compile 组）
            int totalVariantsForShader = 0;
            foreach (var passInfo in passInfos)
            {
                // 过滤该 pass 的排除关键字组
                var passGroups = new List<HashSet<string>>();
                foreach (var group in passInfo.Groups)
                {
                    bool excluded = false;
                    foreach (string kw in group)
                    {
                        if (excludeKeywords.Contains(kw)) { excluded = true; break; }
                    }
                    if (excluded) continue;
                    if (group.Count >= 1)
                        passGroups.Add(new HashSet<string>(group));
                }

                // 收集该 pass 的组关键字
                var passGroupKeywords = new HashSet<string>();
                foreach (var group in passGroups)
                    passGroupKeywords.UnionWith(group);

                // 非组关键字：只加该 pass 源码中实际声明的关键字（shader_feature 等）
                var passAllKeywords = GetPassDeclaredKeywordsByLightMode(shaderPath, passInfo.Name);
                var passNonGroupKeywords = new List<string>();
                foreach (string kw in allEnabledKeywords)
                {
                    if (!passGroupKeywords.Contains(kw) && passAllKeywords.Contains(kw))
                        passNonGroupKeywords.Add(kw);
                }
                passNonGroupKeywords.Sort();

                // 生成该 pass 的关键字组合
                var passCombinations = GenerateGroupCombinations(passGroups);

                debugLog.AppendLine($"  Pass: {passInfo.Name} → {passInfo.PassType}");
                debugLog.AppendLine($"    组: {passGroups.Count}, 组合: {passCombinations.Count}");

                if (passCombinations.Count == 0)
                {
                    shaderInfo.ShaderVariantElements.Add(new ShaderVariantCollectionManifest.ShaderVariantElement
                    {
                        PassType = passInfo.PassType,
                        Keywords = passNonGroupKeywords.ToArray()
                    });
                    totalVariantsForShader++;
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

                        shaderInfo.ShaderVariantElements.Add(new ShaderVariantCollectionManifest.ShaderVariantElement
                        {
                            PassType = passInfo.PassType,
                            Keywords = finalKeywords.ToArray()
                        });
                        totalVariantsForShader++;
                    }
                }
            }

            shaderInfo.ShaderVariantCount = shaderInfo.ShaderVariantElements.Count;
            allVariantInfos.Add(shaderInfo);

            debugLog.AppendLine($"  总变种数: {shaderInfo.ShaderVariantCount}");
            foreach (var v in shaderInfo.ShaderVariantElements)
                debugLog.AppendLine($"    pass={v.PassType} kw=[{string.Join(", ", v.Keywords)}]");

            Debug.Log($"[分析模式] {shader.name}: {shaderInfo.ShaderVariantCount} 个变种");
        }

        // 3. 写入 SVC
        var wrapper = new ShaderVariantCollectionManifest
        {
            ShaderVariantInfos = allVariantInfos
        };

        // 分析模式：直接通过 SerializedObject 写入 SVC（变种未编译，ShaderVariant 验证会失败）
        int totalVariants = 0;
        int maxVariantsPerFile = ShaderVariantCollectorSetting.GetMaxVariantsPerFile(packageName);

        if (splitByShaderName)
        {
            foreach (var info in wrapper.ShaderVariantInfos)
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(info.AssetPath);
                if (shader == null) { debugLog.AppendLine($"  [跳过] shader 未找到: {info.AssetPath}"); continue; }

                string basePath = Path.GetDirectoryName(savePath);
                string shaderName = info.ShaderName.Replace('/', '_').Replace('\\', '_');

                if (maxVariantsPerFile <= 0 || info.ShaderVariantElements.Count <= maxVariantsPerFile)
                {
                    // 不拆分
                    string shaderSavePath = Path.Combine(basePath, $"{shaderName}.shadervariants");
                    debugLog.AppendLine($"  [写入] {info.ShaderName}: {info.ShaderVariantElements.Count} 个变种 → {shaderSavePath}");
                    WriteShaderVariantFileRaw(shaderSavePath, shader, info);
                }
                else
                {
                    // 按 pass 分组，每组独立拆分
                    var passGroups = new Dictionary<int, List<ShaderVariantCollectionManifest.ShaderVariantElement>>();
                    foreach (var v in info.ShaderVariantElements)
                    {
                        int pt = (int)v.PassType;
                        if (!passGroups.ContainsKey(pt))
                            passGroups[pt] = new List<ShaderVariantCollectionManifest.ShaderVariantElement>();
                        passGroups[pt].Add(v);
                    }

                    int fileIndex = 0;
                    foreach (var kvp in passGroups)
                    {
                        var passVariants = kvp.Value;
                        for (int offset = 0; offset < passVariants.Count; offset += maxVariantsPerFile)
                        {
                            int count = Mathf.Min(maxVariantsPerFile, passVariants.Count - offset);
                            var chunk = new ShaderVariantCollectionManifest.ShaderVariantInfo
                            {
                                AssetPath = info.AssetPath,
                                ShaderName = info.ShaderName,
                                ShaderVariantElements = passVariants.GetRange(offset, count)
                            };
                            chunk.ShaderVariantCount = count;

                            string shaderSavePath = Path.Combine(basePath, $"{shaderName}_{fileIndex}.shadervariants");
                            debugLog.AppendLine($"  [写入] {info.ShaderName}_{fileIndex}: pass={kvp.Key} {count} 个变种 → {shaderSavePath}");
                            WriteShaderVariantFileRaw(shaderSavePath, shader, chunk);
                            fileIndex++;
                        }
                    }
                }

                totalVariants += info.ShaderVariantElements.Count;
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

            debugLog.AppendLine($"  [写入] 合并: {mergedInfo.ShaderVariantCount} 个变种 → {savePath}");

            Shader firstShader = AssetDatabase.LoadAssetAtPath<Shader>(mergedInfo.AssetPath);
            if (firstShader != null)
                WriteShaderVariantFileRaw(savePath, firstShader, mergedInfo);
            totalVariants = mergedInfo.ShaderVariantCount;
        }

        // 保存 debug 日志
        if (debugRaw)
        {
            string debugDir = Path.GetDirectoryName(Path.GetDirectoryName(savePath));
            string debugPath = Path.Combine(debugDir, "debug", "debug.txt");
            EditorTools.CreateFileDirectory(debugPath);
            debugLog.AppendLine();
            debugLog.AppendLine($"完成: {wrapper.ShaderVariantInfos.Count} 个 shader, {totalVariants} 个变种");
            File.WriteAllText(debugPath, debugLog.ToString());
            Debug.Log($"[分析模式] Debug 日志: {debugPath}");
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        Debug.Log($"[分析模式] 完成: {wrapper.ShaderVariantInfos.Count} 个 shader, {totalVariants} 个变种");

        _steps = ESteps.None;
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
            ShaderVariantCollectionHelper.ClearCurrentShaderVariantCollection();
            _steps = ESteps.CollectAllMaterial;
            return; //等待一帧
        }

        if (_steps == ESteps.CollectAllMaterial)
        {
            _allMaterials = GetAllMaterials();
            _steps = ESteps.CollectAllScene;
            return; //等待一帧
        }

        if (_steps == ESteps.CollectAllScene)
        {
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
            if (IsSVCStable())
            {
                DestroyAllSpheres();
                _elapsedTime.Stop();
                _steps = ESteps.CollectSceneVariants;
            }
        }

        if (_steps == ESteps.CollectSleeping)
        {
            if (IsSVCStable())
            {
                DestroyAllSpheres();
                _elapsedTime.Stop();
                _steps = ESteps.CollectVariants;
            }
        }

        if (_steps == ESteps.CollectSceneSleeping)
        {
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
                _steps = ESteps.WaitingDone;
            }
            else
            {
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
            // 注意：一定要延迟保存才会起效
            if (_elapsedTime.ElapsedMilliseconds > WaitMilliseconds)
            {
                _elapsedTime.Stop();
                _steps = ESteps.None;

                // 保存结果并创建清单
                SaveShaderVariantCollection();
                // CreateManifest();

                Debug.Log($"搜集SVC完毕！");
                EditorApplication.update -= EditorUpdate;
                _completedCallback?.Invoke();
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

    private static void SaveShaderVariantCollection()
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
                List<string> shaderNamesSave = new List<string>();
                string basePath = Path.GetDirectoryName(_savePath);
                string baseName = Path.GetFileNameWithoutExtension(_savePath);
                
                // 在保存之前删除目标路径下的所有.shadervariants文件
                if (Directory.Exists(basePath))
                {
                    Directory.Delete(basePath, true);
                    Directory.CreateDirectory(basePath);
                }
                
                int maxVariantsPerFile = ShaderVariantCollectorSetting.GetMaxVariantsPerFile(_currentPackageName);

                foreach (var shaderInfo in wrapper.ShaderVariantInfos)
                {
                    string shaderName = shaderInfo.ShaderName;
                    // 处理shader名称，将路径分隔符替换为下划线
                    shaderName = shaderName.Replace('/', '_').Replace('\\', '_');

                    Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderInfo.AssetPath);
                    if (shader == null) continue;

                    // 收集有效变种（try/catch 过滤不支持的关键字组合）
                    var validVariants = new List<ShaderVariantCollection.ShaderVariant>();
                    foreach (var variant in shaderInfo.ShaderVariantElements)
                    {
                        try
                        {
                            validVariants.Add(new ShaderVariantCollection.ShaderVariant(shader, variant.PassType, variant.Keywords));
                        }
                        catch (ArgumentException)
                        {
                            // 该 pass 不支持此关键字组合，跳过
                        }
                    }

                    Debug.Log($"[拆分保存] {shaderName}: manifest变种={shaderInfo.ShaderVariantElements.Count}, 有效变种={validVariants.Count}");

                    // 不拆分或变种数未超限，一个文件；否则拆分
                    if (maxVariantsPerFile <= 0 || validVariants.Count <= maxVariantsPerFile)
                    {
                        string shaderSavePath = Path.Combine(basePath, $"{shaderName}.shadervariants");
                        shaderNamesSave.Add(shaderName);
                        WriteShaderVariantFile(shaderSavePath, shader, validVariants);
                    }
                    else
                    {
                        // 按数量拆分为多个文件
                        int fileIndex = 0;
                        for (int offset = 0; offset < validVariants.Count; offset += maxVariantsPerFile)
                        {
                            int count = Mathf.Min(maxVariantsPerFile, validVariants.Count - offset);
                            string shaderSavePath = Path.Combine(basePath, $"{shaderName}_{fileIndex}.shadervariants");
                            shaderNamesSave.Add($"{shaderName}_{fileIndex}");

                            var chunk = validVariants.GetRange(offset, count);
                            WriteShaderVariantFile(shaderSavePath, shader, chunk);
                            fileIndex++;
                        }
                    }
                }

                //保存shader名称为txt文件，每个shader名称一行
                string shaderNamesPath = Path.Combine(basePath, $"{baseName}_shaderNames.txt");
                File.WriteAllLines(shaderNamesPath, shaderNamesSave);

                // 如果选择了拆分保存，删除完整的变体集文件
                AssetDatabase.DeleteAsset(_savePath);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
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
    /// 策略：解析 shader 中所有 multi_compile 组，去掉组关键字得到基础变种，再重新排列组合
    /// 无组关键字的变种（如 ShadowCaster）直接保留
    /// </summary>
    private static void AddGlobalKeywordVariantsToManifest(ShaderVariantCollectionManifest wrapper, List<string> excludeKeywords, HashSet<string> materialKeywords = null)
    {
        int addedCount = 0;
        var excludeSet = new HashSet<string>(excludeKeywords);

        foreach (var shaderInfo in wrapper.ShaderVariantInfos)
        {
            string shaderPath = shaderInfo.AssetPath;
            List<HashSet<string>> allGroups = GetMultiCompileGroups(shaderPath);
            HashSet<string> shaderGlobalKeywords = GetShaderSupportedKeywords(shaderPath);

            // 过滤掉包含排除关键字的组，并裁剪每组只保留材质实际启用且 shader 支持的关键字
            var processGroups = new List<HashSet<string>>();
            foreach (var group in allGroups)
            {
                bool excluded = false;
                foreach (string kw in group)
                {
                    if (excludeSet.Contains(kw))
                    {
                        excluded = true;
                        break;
                    }
                }
                if (excluded) continue;

                var filtered = new HashSet<string>();
                foreach (string kw in group)
                {
                    if (shaderGlobalKeywords.Contains(kw) && (materialKeywords == null || materialKeywords.Contains(kw)))
                        filtered.Add(kw);
                }
                if (filtered.Count >= 1)
                    processGroups.Add(filtered);
            }

            var allGroupKeywords = new HashSet<string>();
            foreach (var group in processGroups)
                allGroupKeywords.UnionWith(group);

            // 收集已有变种（快照）
            var existingVariants = new List<(PassType passType, string[] keywords)>();
            foreach (var variant in shaderInfo.ShaderVariantElements)
            {
                existingVariants.Add((variant.PassType, variant.Keywords));
            }

            // 清空变种列表，重新构建
            shaderInfo.ShaderVariantElements.Clear();
            shaderInfo.ShaderVariantCount = 0;
            var seenVariants = new HashSet<string>();

            foreach (var (passType, baseKeywords) in existingVariants)
            {
                // 第一步：去掉组中的关键字，得到基础关键字（trim 并过滤空串）
                var cleanKeywords = new List<string>();
                foreach (string kw in baseKeywords)
                {
                    string trimmed = kw.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !allGroupKeywords.Contains(trimmed))
                        cleanKeywords.Add(trimmed);
                }
                cleanKeywords.Sort();

                // 检查原始变种是否包含组关键字
                bool hasGroupKeyword = false;
                foreach (string kw in baseKeywords)
                {
                    if (allGroupKeywords.Contains(kw.Trim()))
                    {
                        hasGroupKeyword = true;
                        break;
                    }
                }

                // 如果当前变种没有组关键字（如 ShadowCaster），直接保留原始
                if (!hasGroupKeyword)
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
                    continue;
                }

                // 第二步：用当前 pass 的组关键字重新排列组合（笛卡尔积）
                var combinations = GenerateGroupCombinations(processGroups);

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
                if (!trimmed.StartsWith("#pragma multi_compile")) continue;

                string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var keywords = new HashSet<string>();
                for (int i = 1; i < parts.Length; i++)
                {
                    string kw = parts[i].Trim();
                    if (!string.IsNullOrEmpty(kw) && kw != "_" && !kw.StartsWith("multi_compile"))
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
        public List<HashSet<string>> Groups;
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
                            var groups = ParseMultiCompileFromLines(lines, passStartLine, i);
                            passes.Add(new PassInfo
                            {
                                Name = passTags,
                                PassType = LightModeToPassType(passTags),
                                Groups = groups
                            });
                            passStartLine = -1;
                            passBraceStart = -1;
                            passTags = "";
                        }
                    }
                }

                // 收集 Pass 内的 Tags
                if (passStartLine >= 0 && (trimmed.StartsWith("LightMode") || trimmed.StartsWith("\"LightMode\"")))
                {
                    // 格式: "LightMode" = "ForwardBase"
                    int eqIdx = trimmed.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        string value = trimmed.Substring(eqIdx + 1).Trim().Trim('"');
                        passTags = value;
                    }
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
            if (!trimmed.StartsWith("#pragma multi_compile")) continue;

            string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var keywords = new HashSet<string>();
            for (int j = 1; j < parts.Length; j++)
            {
                string kw = parts[j].Trim();
                if (!string.IsNullOrEmpty(kw) && kw != "_" && !kw.StartsWith("multi_compile"))
                    keywords.Add(kw);
            }
            if (keywords.Count >= 1)
                groups.Add(keywords);
        }
        return groups;
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
                                passTypes.Add(LightModeToPassType(currentLightMode));
                                debugLog?.AppendLine($"    [pass解析] 行{lineNum}: pass 块结束, LightMode={currentLightMode}, passType={LightModeToPassType(currentLightMode)}");
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

                if (passStartDepth >= 0 && (trimmed.StartsWith("LightMode") || trimmed.StartsWith("\"LightMode\"")))
                {
                    int eqIdx = trimmed.IndexOf('=');
                    if (eqIdx >= 0)
                        currentLightMode = trimmed.Substring(eqIdx + 1).Trim().Trim('"');
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
                if (passStartDepth >= 0 && (trimmed.StartsWith("LightMode") || trimmed.StartsWith("\"LightMode\"")))
                {
                    int eqIdx = trimmed.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        currentLightMode = trimmed.Substring(eqIdx + 1).Trim().Trim('"');
                        if (currentLightMode == lightModeTag || lightModeTag == null)
                            inTargetPass = true;
                    }
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

    // passType 值来自渲染收集的 SVC 文件（与 Unity PassType 枚举不一致）
    private static PassType LightModeToPassType(string lightMode)
    {
        switch (lightMode)
        {
            // URP pass — 真实 passType 值（从 SVC 确认）
            case "UniversalForward":
            case "UniversalForwardOnly": return (PassType)13;
            case "UniversalGBuffer": return (PassType)14;
            case "DepthOnly": return (PassType)15;
            case "DepthNormals": return (PassType)16;
            case "Universal2D": return (PassType)17;
            case "ShadowCaster": return (PassType)8; // SVC 中确认是 8，不是 PassType.ShadowCaster(7)
            case "Meta": return (PassType)9;
            // 标准 Unity pass
            case "ForwardBase": return PassType.ForwardBase;
            case "ForwardAdd": return PassType.ForwardAdd;
            case "Deferred": return PassType.Deferred;
            case "MotionVectors": return PassType.MotionVectors;
            case "Vertex": return PassType.Vertex;
            case "VertexLMRGBM": return PassType.VertexLMRGBM;
            case "VertexLM": return PassType.VertexLM;
            default: return (PassType)13;
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