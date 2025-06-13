using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public class ShaderVariantCollectorSetting : ScriptableObject
{
    private const string DefaultSavePath = "MyShaderVariants.shadervariants";

    public static ShaderVariantCollectorSetting LoadOrCreateSettings()
    {
        string configFolder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(ScriptableObject.CreateInstance<ShaderVariantCollectorSetting>()))), "config");
        if (!System.IO.Directory.Exists(configFolder))
        {
            System.IO.Directory.CreateDirectory(configFolder);
            AssetDatabase.Refresh();
        }
        
        string path = System.IO.Path.Combine(configFolder, "ShaderVariantCollectorSetting.asset");
        ShaderVariantCollectorSetting settings = AssetDatabase.LoadAssetAtPath<ShaderVariantCollectorSetting>(path);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<ShaderVariantCollectorSetting>();
            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.SaveAssets();
        }
        return settings;
    }

    public static ShaderVariantCollectorSetting GetSettings()
    {
        return LoadOrCreateSettings();
    }

    public static void SaveSettings(ShaderVariantCollectorSetting settings)
    {
        EditorUtility.SetDirty(settings);
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
    public List<string> globalKeywords = new List<string>();
    public LocalKeywordCollection localKeywordsCollection = new LocalKeywordCollection();
    public List<string> filterShaderNames = new List<string>();

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
}