using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class ShaderVariantCollectorWindow : EditorWindow
{
    [MenuItem("Tools/着色器变种收集器", false, 100)]
    public static void OpenWindow()
    {
        ShaderVariantCollectorWindow window = GetWindow<ShaderVariantCollectorWindow>("着色器变种收集工具", true);
        window.minSize = new Vector2(800, 600);
    }

    private static string _currentPackageName = "Default";

    private Vector2 _scrollPos;
    private bool _showBlacklist = true;
    private bool _showFilterShaders = true;
    private bool _showLocalKeywords = true;
    private bool _showGlobalKeywords = true;

    private string _newBlackScenePath = "";
    private string _newFilterShaderName = "";
    private string _newLocalShaderName = "";
    private string _newLocalKeyword = "";
    private string _newGlobalKeyword = "";

    // 延迟写入：在绘制阶段收集变更，绘制结束后统一应用
    private string _pendingFileName;
    private string _pendingSavePath;
    private string _pendingSearchPath;
    private string _pendingScenePath;
    private int _pendingCapacity = -1;
    private bool? _pendingSplitByName;
    private bool? _pendingCollectScene;
    private bool? _pendingSaveJson;
    private int _pendingMaxVariantsPerFile = -1;
    private int _pendingRemoveBlackIndex = -1;
    private int _pendingRemoveFilterIndex = -1;
    private int _pendingRemoveLocalIndex = -1;
    private int _pendingRemoveGlobalIndex = -1;
    private bool _pendingAddBlack;
    private bool _pendingAddFilter;
    private bool _pendingAddLocal;
    private bool _pendingAddGlobal;

    private void OnGUI()
    {
        // 重置待处理状态
        _pendingFileName = null;
        _pendingSavePath = null;
        _pendingSearchPath = null;
        _pendingScenePath = null;
        _pendingCapacity = -1;
        _pendingSplitByName = null;
        _pendingCollectScene = null;
        _pendingSaveJson = null;
        _pendingMaxVariantsPerFile = -1;
        _pendingRemoveBlackIndex = -1;
        _pendingRemoveFilterIndex = -1;
        _pendingRemoveLocalIndex = -1;
        _pendingRemoveGlobalIndex = -1;
        _pendingAddBlack = false;
        _pendingAddFilter = false;
        _pendingAddLocal = false;
        _pendingAddGlobal = false;

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        // 变体集名字
        string fileName = ShaderVariantCollectorSetting.GetFileName(_currentPackageName);
        string newFileName = EditorGUILayout.DelayedTextField("变体集名字", fileName);
        if (newFileName != fileName)
            _pendingFileName = newFileName;

        // 文件保存路径
        _pendingSavePath = FolderPathField("文件保存路径", ShaderVariantCollectorSetting.GeFileSavePath(_currentPackageName));

        // 材质收集路径
        _pendingSearchPath = FolderPathField("材质收集路径", ShaderVariantCollectorSetting.GeFileSearchPath(_currentPackageName));

        // 场景收集路径
        _pendingScenePath = FolderPathField("场景收集路径", ShaderVariantCollectorSetting.GeSecneSearchPath(_currentPackageName));

        EditorGUILayout.Space(5);

        DrawBlacklist();
        DrawFilterShaderNames();
        DrawLocalKeywords();
        DrawGlobalKeywords();

        EditorGUILayout.Space(5);

        int shaderCount = ShaderVariantCollectionHelper.GetCurrentShaderVariantCollectionShaderCount();
        int variantCount = ShaderVariantCollectionHelper.GetCurrentShaderVariantCollectionVariantCount();
        EditorGUILayout.LabelField($"Current Shader Count : {shaderCount}");
        EditorGUILayout.LabelField($"Current Variant Count : {variantCount}");

        EditorGUILayout.Space(5);

        int capacity = ShaderVariantCollectorSetting.GeProcessCapacity(_currentPackageName);
        int newCapacity = EditorGUILayout.IntSlider("Capacity", capacity, 10, 10000);
        if (newCapacity != capacity)
            _pendingCapacity = newCapacity;

        bool splitByName = ShaderVariantCollectorSetting.GetSplitByShaderName(_currentPackageName);
        bool newSplitByName = EditorGUILayout.Toggle("按Shader名称拆分保存", splitByName);
        if (newSplitByName != splitByName)
            _pendingSplitByName = newSplitByName;

        int maxVariants = ShaderVariantCollectorSetting.GetMaxVariantsPerFile(_currentPackageName);
        int newMaxVariants = EditorGUILayout.IntSlider("每文件最大变种数（0=不拆分）", maxVariants, 0, 100);
        if (newMaxVariants != maxVariants)
            _pendingMaxVariantsPerFile = newMaxVariants;

        bool collectScene = ShaderVariantCollectorSetting.GetCollectSceneVariants(_currentPackageName);
        bool newCollectScene = EditorGUILayout.Toggle("收集场景变体", collectScene);
        if (newCollectScene != collectScene)
            _pendingCollectScene = newCollectScene;

        bool saveJson = ShaderVariantCollectorSetting.GetSaveJsonFile(_currentPackageName);
        bool newSaveJson = EditorGUILayout.Toggle("保存变体JSON文件", saveJson);
        if (newSaveJson != saveJson)
            _pendingSaveJson = newSaveJson;

        EditorGUILayout.Space(10);

        GUI.backgroundColor = new Color(0.24f, 0.65f, 0.25f);
        if (GUILayout.Button("开始搜集", GUILayout.Height(50)))
        {
            // 延迟到当前 GUI 布局结束后执行，避免 Run() 中的场景操作打断布局
            EditorApplication.delayCall += CollectButton_clicked;
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.EndScrollView();

        // 绘制结束后统一应用变更
        ApplyPendingChanges();
    }

    private void ApplyPendingChanges()
    {
        if (_pendingFileName != null)
            ShaderVariantCollectorSetting.SetFileName(_currentPackageName, _pendingFileName);
        if (_pendingSavePath != null)
            ShaderVariantCollectorSetting.SetFileSavePath(_currentPackageName, _pendingSavePath);
        if (_pendingSearchPath != null)
            ShaderVariantCollectorSetting.SetFileSearchPath(_currentPackageName, _pendingSearchPath);
        if (_pendingScenePath != null)
            ShaderVariantCollectorSetting.SetSceneSearchPath(_currentPackageName, _pendingScenePath);
        if (_pendingCapacity >= 0)
            ShaderVariantCollectorSetting.SetProcessCapacity(_currentPackageName, _pendingCapacity);
        if (_pendingSplitByName.HasValue)
            ShaderVariantCollectorSetting.SetSplitByShaderName(_currentPackageName, _pendingSplitByName.Value);
        if (_pendingCollectScene.HasValue)
            ShaderVariantCollectorSetting.SetCollectSceneVariants(_currentPackageName, _pendingCollectScene.Value);
        if (_pendingSaveJson.HasValue)
            ShaderVariantCollectorSetting.SetSaveJsonFile(_currentPackageName, _pendingSaveJson.Value);
        if (_pendingMaxVariantsPerFile >= 0)
            ShaderVariantCollectorSetting.SetMaxVariantsPerFile(_currentPackageName, _pendingMaxVariantsPerFile);

        // 列表删除
        if (_pendingRemoveBlackIndex >= 0)
        {
            var list = ShaderVariantCollectorSetting.GeBlackPath(_currentPackageName);
            if (_pendingRemoveBlackIndex < list.Count)
            {
                list.RemoveAt(_pendingRemoveBlackIndex);
                ShaderVariantCollectorSetting.SetBlackPaths(_currentPackageName, list);
            }
        }
        if (_pendingRemoveFilterIndex >= 0)
        {
            var list = ShaderVariantCollectorSetting.GetFilterShaderNames(_currentPackageName);
            if (_pendingRemoveFilterIndex < list.Count)
            {
                list.RemoveAt(_pendingRemoveFilterIndex);
                ShaderVariantCollectorSetting.SetFilterShaderNames(_currentPackageName, list);
            }
        }
        if (_pendingRemoveLocalIndex >= 0)
        {
            var kw = ShaderVariantCollectorSetting.GetLocalKeywords(_currentPackageName);
            if (_pendingRemoveLocalIndex < kw.LocalKeywords.Count)
            {
                var removed = kw.LocalKeywords[_pendingRemoveLocalIndex];
                kw.RemoveLocalKeyword(removed.ShaderName, removed.Keyword);
                ShaderVariantCollectorSetting.SetLocalKeywords(_currentPackageName, kw);
            }
        }
        if (_pendingRemoveGlobalIndex >= 0)
        {
            var list = ShaderVariantCollectorSetting.GetGlobalKeywords(_currentPackageName);
            if (_pendingRemoveGlobalIndex < list.Count)
            {
                list.RemoveAt(_pendingRemoveGlobalIndex);
                ShaderVariantCollectorSetting.SetGlobalKeywords(_currentPackageName, list);
            }
        }

        // 列表添加
        if (_pendingAddBlack)
        {
            var list = ShaderVariantCollectorSetting.GeBlackPath(_currentPackageName);
            if (!string.IsNullOrEmpty(_newBlackScenePath) && !list.Contains(_newBlackScenePath))
            {
                list.Add(_newBlackScenePath);
                ShaderVariantCollectorSetting.SetBlackPaths(_currentPackageName, list);
                _newBlackScenePath = "";
            }
        }
        if (_pendingAddFilter)
        {
            var list = ShaderVariantCollectorSetting.GetFilterShaderNames(_currentPackageName);
            if (!string.IsNullOrEmpty(_newFilterShaderName) && !list.Contains(_newFilterShaderName))
            {
                list.Add(_newFilterShaderName);
                ShaderVariantCollectorSetting.SetFilterShaderNames(_currentPackageName, list);
                _newFilterShaderName = "";
            }
        }
        if (_pendingAddLocal)
        {
            var kw = ShaderVariantCollectorSetting.GetLocalKeywords(_currentPackageName);
            if (!string.IsNullOrEmpty(_newLocalShaderName) && !string.IsNullOrEmpty(_newLocalKeyword))
            {
                kw.AddLocalKeyword(_newLocalShaderName, _newLocalKeyword);
                ShaderVariantCollectorSetting.SetLocalKeywords(_currentPackageName, kw);
                _newLocalShaderName = "";
                _newLocalKeyword = "";
            }
        }
        if (_pendingAddGlobal)
        {
            var list = ShaderVariantCollectorSetting.GetGlobalKeywords(_currentPackageName);
            if (!string.IsNullOrEmpty(_newGlobalKeyword) && !list.Contains(_newGlobalKeyword))
            {
                list.Add(_newGlobalKeyword);
                ShaderVariantCollectorSetting.SetGlobalKeywords(_currentPackageName, list);
                _newGlobalKeyword = "";
            }
        }
    }

    private string FolderPathField(string label, string currentPath)
    {
        string result = currentPath;
        EditorGUILayout.BeginHorizontal();
        try
        {
            result = EditorGUILayout.DelayedTextField(label, currentPath);
            if (GUILayout.Button("选择", GUILayout.Width(60)))
            {
                string selected = EditorUtility.OpenFolderPanel("选择文件夹", "Assets", "");
                if (!string.IsNullOrEmpty(selected) && selected.Contains("/Assets"))
                {
                    result = EditorTools.AbsolutePathToAssetPath(selected);
                }
            }
        }
        finally
        {
            EditorGUILayout.EndHorizontal();
        }
        return result != currentPath ? result : null;
    }

    private void DrawBlacklist()
    {
        _showBlacklist = EditorGUILayout.Foldout(_showBlacklist, "黑名单路径", true);
        if (!_showBlacklist) return;

        EditorGUI.indentLevel++;
        List<string> blackPaths = ShaderVariantCollectorSetting.GeBlackPath(_currentPackageName);

        for (int i = 0; i < blackPaths.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            try
            {
                EditorGUILayout.LabelField(blackPaths[i]);
                if (GUILayout.Button("X", GUILayout.Width(25)))
                {
                    _pendingRemoveBlackIndex = i;
                }
            }
            finally
            {
                EditorGUILayout.EndHorizontal();
            }
        }

        EditorGUILayout.BeginHorizontal();
        try
        {
            _newBlackScenePath = EditorGUILayout.TextField("新增路径", _newBlackScenePath);
            if (GUILayout.Button("添加", GUILayout.Width(60)))
            {
                _pendingAddBlack = true;
            }
        }
        finally
        {
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;
    }

    private void DrawFilterShaderNames()
    {
        _showFilterShaders = EditorGUILayout.Foldout(_showFilterShaders, "过滤着色器", true);
        if (!_showFilterShaders) return;

        EditorGUI.indentLevel++;
        List<string> filterNames = ShaderVariantCollectorSetting.GetFilterShaderNames(_currentPackageName);

        for (int i = 0; i < filterNames.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            try
            {
                EditorGUILayout.LabelField(filterNames[i]);
                if (GUILayout.Button("X", GUILayout.Width(25)))
                {
                    _pendingRemoveFilterIndex = i;
                }
            }
            finally
            {
                EditorGUILayout.EndHorizontal();
            }
        }

        EditorGUILayout.BeginHorizontal();
        try
        {
            _newFilterShaderName = EditorGUILayout.TextField("着色器名称", _newFilterShaderName);
            if (GUILayout.Button("添加", GUILayout.Width(60)))
            {
                _pendingAddFilter = true;
            }
        }
        finally
        {
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;
    }

    private void DrawLocalKeywords()
    {
        _showLocalKeywords = EditorGUILayout.Foldout(_showLocalKeywords, "局部关键字", true);
        if (!_showLocalKeywords) return;

        EditorGUI.indentLevel++;
        LocalKeywordCollection localKeywords = ShaderVariantCollectorSetting.GetLocalKeywords(_currentPackageName);

        for (int i = 0; i < localKeywords.LocalKeywords.Count; i++)
        {
            var kw = localKeywords.LocalKeywords[i];
            EditorGUILayout.BeginHorizontal();
            try
            {
                EditorGUILayout.LabelField($"{kw.ShaderName}  |  {kw.Keyword}");
                if (GUILayout.Button("X", GUILayout.Width(25)))
                {
                    _pendingRemoveLocalIndex = i;
                }
            }
            finally
            {
                EditorGUILayout.EndHorizontal();
            }
        }

        EditorGUILayout.BeginHorizontal();
        try
        {
            _newLocalShaderName = EditorGUILayout.TextField("着色器名称", _newLocalShaderName);
            _newLocalKeyword = EditorGUILayout.TextField("关键字", _newLocalKeyword);
            if (GUILayout.Button("添加", GUILayout.Width(60)))
            {
                _pendingAddLocal = true;
            }
        }
        finally
        {
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;
    }

    private void DrawGlobalKeywords()
    {
        _showGlobalKeywords = EditorGUILayout.Foldout(_showGlobalKeywords, "排除关键字（不生成变种）", true);
        if (!_showGlobalKeywords) return;

        EditorGUI.indentLevel++;
        List<string> globalKeywords = ShaderVariantCollectorSetting.GetGlobalKeywords(_currentPackageName);

        for (int i = 0; i < globalKeywords.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            try
            {
                EditorGUILayout.LabelField(globalKeywords[i]);
                if (GUILayout.Button("X", GUILayout.Width(25)))
                {
                    _pendingRemoveGlobalIndex = i;
                }
            }
            finally
            {
                EditorGUILayout.EndHorizontal();
            }
        }

        EditorGUILayout.BeginHorizontal();
        try
        {
            _newGlobalKeyword = EditorGUILayout.TextField("关键字", _newGlobalKeyword);
            if (GUILayout.Button("添加", GUILayout.Width(60)))
            {
                _pendingAddGlobal = true;
            }
        }
        finally
        {
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;
    }

    private void CollectButton_clicked()
    {
        string svName = ShaderVariantCollectorSetting.GetFileName(_currentPackageName);
        string savePath = ShaderVariantCollectorSetting.GeFileSavePath(_currentPackageName);
        string searchPath = ShaderVariantCollectorSetting.GeFileSearchPath(_currentPackageName);
        string searchScenePath = ShaderVariantCollectorSetting.GeSecneSearchPath(_currentPackageName);
        List<string> blackPaths = ShaderVariantCollectorSetting.GeBlackPath(_currentPackageName);
        List<string> filterNames = ShaderVariantCollectorSetting.GetFilterShaderNames(_currentPackageName);
        bool splitByShaderName = ShaderVariantCollectorSetting.GetSplitByShaderName(_currentPackageName);
        int processCapacity = ShaderVariantCollectorSetting.GeProcessCapacity(_currentPackageName);
        ShaderVariantCollector.Run($"{savePath}/{svName}", searchPath, searchScenePath, blackPaths, filterNames.ToArray(), processCapacity, splitByShaderName, null, _currentPackageName);
    }
}
