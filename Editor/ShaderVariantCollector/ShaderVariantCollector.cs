using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using Sirenix.Utilities;
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

    private const float WaitMilliseconds = 2000f;
    private const float SleepMilliseconds = 5000f;
    private const float SleepSceneMilliseconds = 10000f;

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

        // 聚焦到游戏窗口
        EditorTools.FocusUnityGameWindow();

        // 创建临时测试场景
        CreateTempScene();

        _steps = ESteps.Prepare;
        EditorApplication.update += EditorUpdate;
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

            if (_allMaterials.Count > 0)
            {
                _elapsedTime = Stopwatch.StartNew();
                if (_globalKeywords.Count <= 0)
                {
                    _steps = ESteps.CollectSleeping;    
                }
                
            }
            else if(_steps != ESteps.CollectWithWaitGlablKeyWords)
            {
                _elapsedTime = Stopwatch.StartNew();
                _steps = ESteps.CollectWaitToScene;
            }
        }

        if (_steps == ESteps.CollectWithWaitGlablKeyWords)
        {
            if (_elapsedTime.ElapsedMilliseconds > SleepMilliseconds)
            {
                _steps = ESteps.ApplyGlobalKeywords;
            }

        }

        if (_steps == ESteps.ApplyGlobalKeywords)
        {
            _elapsedTime = Stopwatch.StartNew();
            if (_currentKeywordIndex >= _globalKeywords.Count)
            {
                // 确保在完成所有全局关键字处理后，重置所有关键字状态
                foreach (var keyword in _globalKeywords)
                {
                    HandleKeyWorld(keyword, false);
                }
                _currentKeywordIndex = 0;
                _steps = ESteps.CollectVariants;
                return;
            }

            // 先禁用所有全局关键字，确保干净的状态
            foreach (var keyword in _globalKeywords)
            {
                HandleKeyWorld(keyword, false);
            }
            
            // 启用当前关键字
            var currentKeyword = _globalKeywords[_currentKeywordIndex];
            HandleKeyWorld(currentKeyword, true);
            Debug.Log("应用全局关键字: " + currentKeyword);
            _currentKeywordIndex++;
            _steps = ESteps.CollectGlobalKeywordsSleeping;
        }

        if (_steps == ESteps.CollectGlobalKeywordsSleeping)
        {
            if (_elapsedTime.ElapsedMilliseconds > SleepMilliseconds)
            {
                _elapsedTime.Stop();
                // 在应用下一个关键字之前，重新创建材质球体
                if (_currentKeywordIndex < _globalKeywords.Count)
                {
                    DestroyAllSpheres();
                    OnlyCreate(_rangeMt);
                    _elapsedTime = Stopwatch.StartNew();
                }
                _steps = ESteps.ApplyGlobalKeywords;
                return;
            }
        }

        // if (_steps == ESteps.CollectGlobalKeywordsClearSleeping)
        // {
        //     if (_elapsedTime.ElapsedMilliseconds > SleepMilliseconds)
        //     {
        //         _elapsedTime.Stop();
        //         _steps = ESteps.ApplyGlobalKeywords;
        //         return;
        //     }
        // }

        if (_steps == ESteps.CollectWaitToScene)
        {
            if (_elapsedTime.ElapsedMilliseconds > SleepMilliseconds)
            {
                DestroyAllSpheres();
                _elapsedTime.Stop();
                _steps = ESteps.CollectSceneVariants;
            }
        }

        if (_steps == ESteps.CollectSleeping)
        {
            if (_elapsedTime.ElapsedMilliseconds > SleepMilliseconds)
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
        if (materials.Count<= 0)
        {
            return;
        }
        OnlyCreate(materials);

        // 如果有全局关键字，开始逐个应用
        if (_globalKeywords != null && _globalKeywords.Count > 0)
        {
            _elapsedTime = Stopwatch.StartNew();
            _currentKeywordIndex = 0;
            foreach (var keyword in _globalKeywords)
            {
                HandleKeyWorld(keyword, false);
            }
            _steps = ESteps.CollectWithWaitGlablKeyWords;
        }
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
            
            // 获取Always Included Shaders列表
            var alwaysIncludedShaderNames = GetAlwaysIncludedShaderNames();
            var hideShader = GetURPHiddenShaderNames();
            alwaysIncludedShaderNames.AddRange(hideShader);
            // 过滤掉Always Included Shaders
            wrapper.ShaderVariantInfos.RemoveAll(info => alwaysIncludedShaderNames.Contains(info.ShaderName));
            //过滤掉 shader 路径包含 Resources
            wrapper.ShaderVariantInfos.RemoveAll(info => info.AssetPath.Contains("Resources"));
            // 过滤掉指定的着色器名称
            if (_filterShaderName != null && _filterShaderName.Count > 0)
            {
                wrapper.ShaderVariantInfos.RemoveAll(info => _filterShaderName.Contains(info.ShaderName));
            }
            
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
                
                foreach (var shaderInfo in wrapper.ShaderVariantInfos)
                {
                    string shaderName = shaderInfo.ShaderName;
                    // 处理shader名称，将路径分隔符替换为下划线
                    shaderName = shaderName.Replace('/', '_').Replace('\\', '_');
                    // 直接使用shader名称作为文件名，不包含原始文件名
                    string shaderSavePath = Path.Combine(basePath, $"{shaderName}.shadervariants");
                    //添加保存的shader名称
                    shaderNamesSave.Add(shaderName);
                    
                    // 创建新的ShaderVariantCollection
                    ShaderVariantCollection shaderSvc = new ShaderVariantCollection();
                    
                    // 添加该shader的所有变体
                    Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderInfo.AssetPath);
                    if (shader != null)
                    {
                        foreach (var variant in shaderInfo.ShaderVariantElements)
                        {
                            shaderSvc.Add(new ShaderVariantCollection.ShaderVariant(shader, variant.PassType, variant.Keywords));
                        }
                    }
                    
                    // 保存shader变体集
                    AssetDatabase.CreateAsset(shaderSvc, shaderSavePath);
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