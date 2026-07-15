using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

[CreateAssetMenu(fileName = "ShaderVariantCollectorSetting", menuName = "ShaderVarianCollector/创建配置文件")]
public class ShaderVariantCollectorSetting : ScriptableObject
{
    private const string DefaultSavePath = "MyShaderVariants.shadervariants";

    private static ShaderVariantCollectorSetting _cachedInstance;

    public static ShaderVariantCollectorSetting LoadOrCreateSettings()
    {
        // 直接缓存实例，避免每帧重复 AssetDatabase.LoadAssetAtPath
        if (_cachedInstance != null) return _cachedInstance;

        // 全局搜索项目中已有的配置文件
        string[] guids = AssetDatabase.FindAssets("t:ShaderVariantCollectorSetting");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            ShaderVariantCollectorSetting settings = AssetDatabase.LoadAssetAtPath<ShaderVariantCollectorSetting>(path);
            if (settings != null)
            {
                _cachedInstance = settings;
                return settings;
            }
        }

        // 全局没有，使用代码位置的默认路径
        string configFolder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(ScriptableObject.CreateInstance<ShaderVariantCollectorSetting>()))), "config");
        if (!System.IO.Directory.Exists(configFolder))
        {
            System.IO.Directory.CreateDirectory(configFolder);
            AssetDatabase.Refresh();
        }

        string defaultPath = System.IO.Path.Combine(configFolder, "ShaderVariantCollectorSetting.asset");
        ShaderVariantCollectorSetting defaultSettings = AssetDatabase.LoadAssetAtPath<ShaderVariantCollectorSetting>(defaultPath);
        if (defaultSettings == null)
        {
            defaultSettings = ScriptableObject.CreateInstance<ShaderVariantCollectorSetting>();
            AssetDatabase.CreateAsset(defaultSettings, defaultPath);
            AssetDatabase.SaveAssets();
        }
        _cachedInstance = defaultSettings;
        return defaultSettings;
    }

    public static ShaderVariantCollectorSetting GetSettings()
    {
        return LoadOrCreateSettings();
    }

    private static bool _dirty;
    private static double _dirtyTime;

    public static void SaveSettings(ShaderVariantCollectorSetting settings)
    {
        EditorUtility.SetDirty(settings);
        // 延迟保存：标记脏，0.5秒后统一写盘，避免每次操作都 SaveAssets
        _dirty = true;
        _dirtyTime = EditorApplication.timeSinceStartup;
        EditorApplication.update += DeferredSave;
    }

    private static void DeferredSave()
    {
        if (!_dirty) { EditorApplication.update -= DeferredSave; return; }
        if (EditorApplication.timeSinceStartup - _dirtyTime < 0.5) return;
        _dirty = false;
        EditorApplication.update -= DeferredSave;
        AssetDatabase.SaveAssets();
    }

    // 字段定义
    public string fileName = DefaultSavePath;
    public string fileSavePath = DefaultSavePath;
    public int processCapacity = 1000;
    public string fileSearchPath = "Assets";
    public string sceneSearchPath = "Assets";
    public List<string> blackPaths = new List<string>() { "Assets" };
    public bool splitByShaderName = false;
    public bool collectSceneVariants = false;
    public bool saveJsonFile = false;
    public List<string> globalKeywords = new List<string>();
    public LocalKeywordCollection localKeywordsCollection = new LocalKeywordCollection();
    public List<string> filterShaderNames = new List<string>();
    public int maxVariantsPerFile = 5;
    public bool saveDebugRawSVC = false;
    public bool analyzeMode = false;
    public List<int> selectedPassTypes = new List<int> { 8, 13 }; // 默认 ShadowCaster + UniversalForward
    public List<CustomLightModeMapping> customLightModes = new List<CustomLightModeMapping>();

    // Get/Set 方法
    public static string GetFileName(string packageName)
    {
        return GetSettings().fileName;
    }

    public static void SetFileName(string packageName, string name)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.fileName = name;
        SaveSettings(settings);
    }

    public static string GeFileSavePath(string packageName)
    {
        return GetSettings().fileSavePath;
    }

    public static void SetFileSavePath(string packageName, string savePath)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.fileSavePath = savePath;
        SaveSettings(settings);
    }

    public static int GeProcessCapacity(string packageName)
    {
        return GetSettings().processCapacity;
    }

    public static void SetProcessCapacity(string packageName, int capacity)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.processCapacity = capacity;
        SaveSettings(settings);
    }

    public static string GeFileSearchPath(string packageName)
    {
        return GetSettings().fileSearchPath;
    }

    public static void SetFileSearchPath(string packageName, string inputPath)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.fileSearchPath = inputPath;
        SaveSettings(settings);
    }

    public static string GeSecneSearchPath(string packageName)
    {
        return GetSettings().sceneSearchPath;
    }

    public static void SetSceneSearchPath(string packageName, string scenePath)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.sceneSearchPath = scenePath;
        SaveSettings(settings);
    }

    public static List<string> GeBlackPath(string packageName)
    {
        return GetSettings().blackPaths;
    }

    public static void SetBlackPath(string packageName, string scenePath)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.blackPaths.Clear();
        settings.blackPaths.Add(scenePath);
        SaveSettings(settings);
    }
    
    public static void SetBlackPaths(string packageName, List<string> paths)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.blackPaths = new List<string>(paths);
        SaveSettings(settings);
    }

    public static bool GetSplitByShaderName(string packageName)
    {
        return GetSettings().splitByShaderName;
    }

    public static void SetSplitByShaderName(string packageName, bool value)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.splitByShaderName = value;
        SaveSettings(settings);
    }

    public static bool GetCollectSceneVariants(string packageName)
    {
        return GetSettings().collectSceneVariants;
    }

    public static void SetCollectSceneVariants(string packageName, bool value)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.collectSceneVariants = value;
        SaveSettings(settings);
    }
    
    public static bool GetSaveJsonFile(string packageName)
    {
        return GetSettings().saveJsonFile;
    }

    public static void SetSaveJsonFile(string packageName, bool value)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.saveJsonFile = value;
        SaveSettings(settings);
    }

    public static List<string> GetGlobalKeywords(string packageName)
    {
        return GetSettings().globalKeywords;
    }

    public static void SetGlobalKeywords(string packageName, string[] keywords)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.globalKeywords = new List<string>(keywords);
        SaveSettings(settings);
    }
    
    public static void SetGlobalKeywords(string packageName, List<string> keywords)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.globalKeywords = new List<string>(keywords);
        SaveSettings(settings);
    }

    public static LocalKeywordCollection GetLocalKeywords(string packageName)
    {
        return GetSettings().localKeywordsCollection;
    }

    public static void SetLocalKeywords(string packageName, LocalKeywordCollection localKeywords)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.localKeywordsCollection = localKeywords;
        SaveSettings(settings);
    }

    public static List<string> GetFilterShaderNames(string packageName)
    {
        return GetSettings().filterShaderNames;
    }

    public static void SetFilterShaderNames(string packageName, string[] filterNames)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.filterShaderNames = new List<string>(filterNames);
        SaveSettings(settings);
    }
    
    public static void SetFilterShaderNames(string packageName, List<string> filterNames)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.filterShaderNames = new List<string>(filterNames);
        SaveSettings(settings);
    }

    public static int GetMaxVariantsPerFile(string packageName)
    {
        return GetSettings().maxVariantsPerFile;
    }

    public static void SetMaxVariantsPerFile(string packageName, int value)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.maxVariantsPerFile = value;
        SaveSettings(settings);
    }

    public static bool GetSaveDebugRawSVC(string packageName)
    {
        return GetSettings().saveDebugRawSVC;
    }

    public static void SetSaveDebugRawSVC(string packageName, bool value)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.saveDebugRawSVC = value;
        SaveSettings(settings);
    }

    public static bool GetAnalyzeMode(string packageName)
    {
        return GetSettings().analyzeMode;
    }

    public static void SetAnalyzeMode(string packageName, bool value)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.analyzeMode = value;
        SaveSettings(settings);
    }

    public static List<int> GetSelectedPassTypes(string packageName)
    {
        var list = GetSettings().selectedPassTypes;
        if (list == null || list.Count == 0)
            return new List<int> { 8, 13 };
        return list;
    }

    public static void SetSelectedPassTypes(string packageName, List<int> value)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.selectedPassTypes = value ?? new List<int> { 8, 13 };
        SaveSettings(settings);
    }

    // 自定义 LightMode 映射
    public static List<CustomLightModeMapping> GetCustomLightModes(string packageName)
    {
        var list = GetSettings().customLightModes;
        if (list == null) list = new List<CustomLightModeMapping>();
        return list;
    }

    public static void SetCustomLightModes(string packageName, List<CustomLightModeMapping> value)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.customLightModes = value ?? new List<CustomLightModeMapping>();
        SaveSettings(settings);
    }

    // ---- 裁剪配置字段 ----
    public string stripSVCPath = "Assets/ResourcesAB/Config/ShaderVarians";
    public List<string> stripAdditionalShaderNames = new List<string>();
    public List<string> stripAdditionalKeywords = new List<string>();
    public List<string> stripPassNames = new List<string>();

    // 裁剪 SVC 路径
    public static string GetStripSVCPath(string packageName)
    {
        return GetSettings().stripSVCPath;
    }

    public static void SetStripSVCPath(string packageName, string path)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.stripSVCPath = path;
        SaveSettings(settings);
    }

    // 额外裁剪着色器
    public static List<string> GetStripAdditionalShaderNames(string packageName)
    {
        return GetSettings().stripAdditionalShaderNames;
    }

    public static void SetStripAdditionalShaderNames(string packageName, List<string> names)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.stripAdditionalShaderNames = new List<string>(names);
        SaveSettings(settings);
    }

    // 额外排除关键字
    public static List<string> GetStripAdditionalKeywords(string packageName)
    {
        return GetSettings().stripAdditionalKeywords;
    }

    public static void SetStripAdditionalKeywords(string packageName, List<string> keywords)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.stripAdditionalKeywords = new List<string>(keywords);
        SaveSettings(settings);
    }

    // 额外裁剪 Pass（按 snippet.passName 精确匹配，如 DepthOnly/ShadowCaster）
    public static List<string> GetStripPassNames(string packageName)
    {
        return GetSettings().stripPassNames;
    }

    public static void SetStripPassNames(string packageName, List<string> passNames)
    {
        ShaderVariantCollectorSetting settings = GetSettings();
        settings.stripPassNames = new List<string>(passNames);
        SaveSettings(settings);
    }
}

/// <summary>
/// 自定义 LightMode 到 PassType 的映射
/// </summary>
[System.Serializable]
public class CustomLightModeMapping
{
    public string lightModeTag;  // 如 "DepthOnlyTree"
    public int passType;         // 如 100

    public CustomLightModeMapping() { }

    public CustomLightModeMapping(string tag, int type)
    {
        lightModeTag = tag;
        passType = type;
    }
}