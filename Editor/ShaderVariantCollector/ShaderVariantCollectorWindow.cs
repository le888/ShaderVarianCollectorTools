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
    private int _currentTab = 0; // 0=收集模式, 1=裁剪配置

    // 收集 tab
    private bool _showBlacklist = true;
    private bool _showFilterShaders = true;
    private bool _showLocalKeywords = true;
    private bool _showGlobalKeywords = true;

    // 裁剪 tab
    private bool _showStripRefFilterShaders = true;
    private bool _showStripRefKeywords = true;
    private bool _showStripAdditionalShaders = true;
    private bool _showStripAdditionalKeywords = true;

    // 后处理扫描 tab
    private PostProcessScanner.ScanResult _ppScanResult;
    private bool _ppShowUsed = true;
    private bool _ppShowUnused = true;

    private string _newBlackScenePath = "";
    private string _newFilterShaderName = "";
    private string _newLocalShaderName = "";
    private string _newLocalKeyword = "";
    private string _newGlobalKeyword = "";
    private string _newStripAdditionalShader = "";
    private string _newStripAdditionalKeyword = "";

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
    private bool? _pendingSaveDebugRawSVC;
    private bool? _pendingAnalyzeMode;
    private int _pendingRemoveBlackIndex = -1;
    private int _pendingRemoveFilterIndex = -1;
    private int _pendingRemoveLocalIndex = -1;
    private int _pendingRemoveGlobalIndex = -1;
    private bool _pendingAddBlack;
    private bool _pendingAddFilter;
    private bool _pendingAddLocal;
    private bool _pendingAddGlobal;

    // 裁剪 tab 延迟写入
    private string _pendingStripSVCPath;
    private int _pendingRemoveStripAdditionalShaderIndex = -1;
    private int _pendingRemoveStripAdditionalKeywordIndex = -1;
    private bool _pendingAddStripAdditionalShader;
    private bool _pendingAddStripAdditionalKeyword;

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
        _pendingSaveDebugRawSVC = null;
        _pendingAnalyzeMode = null;
        _pendingSelectedPassTypes = null;
        _pendingRemoveBlackIndex = -1;
        _pendingRemoveFilterIndex = -1;
        _pendingRemoveLocalIndex = -1;
        _pendingRemoveGlobalIndex = -1;
        _pendingAddBlack = false;
        _pendingAddFilter = false;
        _pendingAddLocal = false;
        _pendingAddGlobal = false;
        _pendingStripSVCPath = null;
        _pendingRemoveStripAdditionalShaderIndex = -1;
        _pendingRemoveStripAdditionalKeywordIndex = -1;
        _pendingAddStripAdditionalShader = false;
        _pendingAddStripAdditionalKeyword = false;

        // Tab 栏
        _currentTab = GUILayout.Toolbar(_currentTab, new[] { "收集模式", "裁剪配置", "后处理扫描" }, GUILayout.Height(28));
        EditorGUILayout.Space(3);

        // 配置文件快速定位
        var settings = ShaderVariantCollectorSetting.GetSettings();
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.ObjectField("当前配置文件", settings, typeof(ShaderVariantCollectorSetting), false);
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.Space(3);

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        if (_currentTab == 1)
        {
            DrawStripConfigTab();
            EditorGUILayout.EndScrollView();
            ApplyPendingChanges();
            return;
        }

        if (_currentTab == 2)
        {
            DrawPostProcessScanTab();
            EditorGUILayout.EndScrollView();
            return;
        }

        // 收集模式下拉框
        bool analyzeMode = ShaderVariantCollectorSetting.GetAnalyzeMode(_currentPackageName);
        int modeIndex = analyzeMode ? 1 : 0;
        int newModeIndex = EditorGUILayout.Popup("收集模式", modeIndex, new[] { "渲染模式", "分析模式（不渲染）" });
        if (newModeIndex != modeIndex)
            _pendingAnalyzeMode = newModeIndex == 1;

        // Pass Type 选择器
        DrawPassTypeSelector();

        EditorGUILayout.Space(5);

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

        bool debugRaw = ShaderVariantCollectorSetting.GetSaveDebugRawSVC(_currentPackageName);
        bool newDebugRaw = EditorGUILayout.Toggle("Debug: 保存原始渲染变体", debugRaw);
        if (newDebugRaw != debugRaw)
            _pendingSaveDebugRawSVC = newDebugRaw;

        EditorGUILayout.Space(10);

        // 收集按钮
        GUI.backgroundColor = ShaderVariantCollector.IsCollecting ? new Color(0.9f, 0.6f, 0.1f) : new Color(0.24f, 0.65f, 0.25f);
        string buttonText = ShaderVariantCollector.IsCollecting ? "取消搜集" : "开始搜集";
        if (GUILayout.Button(buttonText, GUILayout.Height(50)))
        {
            if (ShaderVariantCollector.IsCollecting)
                ShaderVariantCollector.Cancel();
            else
                EditorApplication.delayCall += CollectButton_clicked;
        }
        GUI.backgroundColor = Color.white;

        // 进度条（收集中显示）
        if (ShaderVariantCollector.IsCollecting)
        {
            float progress = ShaderVariantCollector.GetAnalyzeProgress();
            string status = ShaderVariantCollector.GetAnalyzeStatus();

            // 状态文字
            EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

            // 进度条
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(18));
            EditorGUI.DrawRect(rect, new Color(0.2f, 0.2f, 0.2f));
            var fillRect = new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(progress), rect.height);
            EditorGUI.DrawRect(fillRect, new Color(0.9f, 0.6f, 0.1f));
            EditorGUI.LabelField(rect, $"{Mathf.RoundToInt(progress * 100)}%", new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });
        }

        EditorGUILayout.EndScrollView();

        // 绘制结束后统一应用变更
        ApplyPendingChanges();

        // 收集中持续刷新窗口
        if (ShaderVariantCollector.IsCollecting)
            Repaint();
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
        if (_pendingSaveDebugRawSVC.HasValue)
            ShaderVariantCollectorSetting.SetSaveDebugRawSVC(_currentPackageName, _pendingSaveDebugRawSVC.Value);
        if (_pendingAnalyzeMode.HasValue)
            ShaderVariantCollectorSetting.SetAnalyzeMode(_currentPackageName, _pendingAnalyzeMode.Value);
        if (_pendingSelectedPassTypes != null)
            ShaderVariantCollectorSetting.SetSelectedPassTypes(_currentPackageName, _pendingSelectedPassTypes);

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
            if (!string.IsNullOrEmpty(_newBlackScenePath))
            {
                if (!list.Contains(_newBlackScenePath))
                {
                    list.Add(_newBlackScenePath);
                    ShaderVariantCollectorSetting.SetBlackPaths(_currentPackageName, list);
                    _newBlackScenePath = "";
                }
                else { Debug.LogWarning($"黑名单路径已存在: {_newBlackScenePath}"); }
            }
        }
        if (_pendingAddFilter)
        {
            var list = ShaderVariantCollectorSetting.GetFilterShaderNames(_currentPackageName);
            if (!string.IsNullOrEmpty(_newFilterShaderName))
            {
                if (!list.Contains(_newFilterShaderName))
                {
                    list.Add(_newFilterShaderName);
                    ShaderVariantCollectorSetting.SetFilterShaderNames(_currentPackageName, list);
                    _newFilterShaderName = "";
                }
                else { Debug.LogWarning($"过滤着色器已存在: {_newFilterShaderName}"); }
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
            if (!string.IsNullOrEmpty(_newGlobalKeyword))
            {
                if (!list.Contains(_newGlobalKeyword))
                {
                    list.Add(_newGlobalKeyword);
                    ShaderVariantCollectorSetting.SetGlobalKeywords(_currentPackageName, list);
                    _newGlobalKeyword = "";
                }
                else
                {
                    Debug.LogWarning($"排除关键字已存在: {_newGlobalKeyword}");
                }
            }
        }

        // ---- 裁剪配置 ----
        if (_pendingStripSVCPath != null)
            ShaderVariantCollectorSetting.SetStripSVCPath(_currentPackageName, _pendingStripSVCPath);

        if (_pendingRemoveStripAdditionalShaderIndex >= 0)
        {
            var list = ShaderVariantCollectorSetting.GetStripAdditionalShaderNames(_currentPackageName);
            if (_pendingRemoveStripAdditionalShaderIndex < list.Count)
            {
                list.RemoveAt(_pendingRemoveStripAdditionalShaderIndex);
                ShaderVariantCollectorSetting.SetStripAdditionalShaderNames(_currentPackageName, list);
            }
        }
        if (_pendingRemoveStripAdditionalKeywordIndex >= 0)
        {
            var list = ShaderVariantCollectorSetting.GetStripAdditionalKeywords(_currentPackageName);
            if (_pendingRemoveStripAdditionalKeywordIndex < list.Count)
            {
                list.RemoveAt(_pendingRemoveStripAdditionalKeywordIndex);
                ShaderVariantCollectorSetting.SetStripAdditionalKeywords(_currentPackageName, list);
            }
        }
        if (_pendingAddStripAdditionalShader)
        {
            var list = ShaderVariantCollectorSetting.GetStripAdditionalShaderNames(_currentPackageName);
            if (!string.IsNullOrEmpty(_newStripAdditionalShader))
            {
                if (!list.Contains(_newStripAdditionalShader))
                {
                    list.Add(_newStripAdditionalShader);
                    ShaderVariantCollectorSetting.SetStripAdditionalShaderNames(_currentPackageName, list);
                    _newStripAdditionalShader = "";
                }
                else { Debug.LogWarning($"额外裁剪着色器已存在: {_newStripAdditionalShader}"); }
            }
        }
        if (_pendingAddStripAdditionalKeyword)
        {
            var list = ShaderVariantCollectorSetting.GetStripAdditionalKeywords(_currentPackageName);
            if (!string.IsNullOrEmpty(_newStripAdditionalKeyword))
            {
                if (!list.Contains(_newStripAdditionalKeyword))
                {
                    list.Add(_newStripAdditionalKeyword);
                    ShaderVariantCollectorSetting.SetStripAdditionalKeywords(_currentPackageName, list);
                    _newStripAdditionalKeyword = "";
                }
                else { Debug.LogWarning($"额外排除关键字已存在: {_newStripAdditionalKeyword}"); }
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

    // ---- 裁剪配置 Tab ----

    private void DrawStripConfigTab()
    {
        EditorGUILayout.LabelField("裁剪配置", EditorStyles.boldLabel);
        EditorGUILayout.Space(3);

        // SVC 文件夹路径
        _pendingStripSVCPath = FolderPathField("SVC 文件夹路径", ShaderVariantCollectorSetting.GetStripSVCPath(_currentPackageName));

        EditorGUILayout.Space(5);

        // 只读：收集器配置的过滤着色器
        DrawReadOnlyList("收集器 - 过滤着色器（只读）", ref _showStripRefFilterShaders,
            ShaderVariantCollectorSetting.GetFilterShaderNames(_currentPackageName));

        // 只读：收集器配置的排除关键字
        DrawReadOnlyList("收集器 - 排除关键字（只读）", ref _showStripRefKeywords,
            ShaderVariantCollectorSetting.GetGlobalKeywords(_currentPackageName));

        EditorGUILayout.Space(5);

        // 可编辑：额外裁剪着色器
        DrawStripAdditionalShaders();

        // 可编辑：额外排除关键字
        DrawStripAdditionalKeywords();
    }

    private void DrawReadOnlyList(string label, ref bool foldout, List<string> items)
    {
        foldout = EditorGUILayout.Foldout(foldout, $"{label} ({items.Count})", true);
        if (!foldout) return;

        EditorGUI.indentLevel++;
        if (items.Count == 0)
        {
            EditorGUILayout.LabelField("（无）", EditorStyles.miniLabel);
        }
        else
        {
            foreach (var item in items)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField(item);
                EditorGUI.EndDisabledGroup();
            }
        }
        EditorGUI.indentLevel--;
    }

    private void DrawStripAdditionalShaders()
    {
        _showStripAdditionalShaders = EditorGUILayout.Foldout(_showStripAdditionalShaders, "额外裁剪着色器", true);
        if (!_showStripAdditionalShaders) return;

        EditorGUI.indentLevel++;
        List<string> names = ShaderVariantCollectorSetting.GetStripAdditionalShaderNames(_currentPackageName);

        for (int i = 0; i < names.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            try
            {
                EditorGUILayout.LabelField(names[i]);
                if (GUILayout.Button("X", GUILayout.Width(25)))
                {
                    _pendingRemoveStripAdditionalShaderIndex = i;
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
            _newStripAdditionalShader = EditorGUILayout.TextField("着色器名称", _newStripAdditionalShader);
            if (GUILayout.Button("添加", GUILayout.Width(60)))
            {
                _pendingAddStripAdditionalShader = true;
            }
        }
        finally
        {
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;
    }

    private void DrawStripAdditionalKeywords()
    {
        _showStripAdditionalKeywords = EditorGUILayout.Foldout(_showStripAdditionalKeywords, "额外排除关键字", true);
        if (!_showStripAdditionalKeywords) return;

        EditorGUI.indentLevel++;
        List<string> keywords = ShaderVariantCollectorSetting.GetStripAdditionalKeywords(_currentPackageName);

        for (int i = 0; i < keywords.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            try
            {
                EditorGUILayout.LabelField(keywords[i]);
                if (GUILayout.Button("X", GUILayout.Width(25)))
                {
                    _pendingRemoveStripAdditionalKeywordIndex = i;
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
            _newStripAdditionalKeyword = EditorGUILayout.TextField("关键字", _newStripAdditionalKeyword);
            if (GUILayout.Button("添加", GUILayout.Width(60)))
            {
                _pendingAddStripAdditionalKeyword = true;
            }
        }
        finally
        {
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;
    }

    // ---- 后处理扫描 Tab ----

    private Dictionary<string, bool> _ppSelectedKeywords = new Dictionary<string, bool>();
    private Dictionary<string, bool> _ppEffectFoldouts = new Dictionary<string, bool>();

    private void DrawPostProcessScanTab()
    {
        EditorGUILayout.LabelField("后处理效果扫描", EditorStyles.boldLabel);
        EditorGUILayout.Space(3);

        if (GUILayout.Button("扫描项目中的后处理", GUILayout.Height(30)))
        {
            _ppScanResult = PostProcessScanner.Scan();
            _ppSelectedKeywords.Clear();
            _ppEffectFoldouts.Clear();
            Debug.Log($"[后处理扫描] 扫描完成: {_ppScanResult.totalProfiles} 个 Profile, {_ppScanResult.totalComponents} 个效果组件");
        }

        if (_ppScanResult == null)
        {
            EditorGUILayout.HelpBox("点击上方按钮扫描项目中 VolumeProfile 使用的后处理效果", MessageType.Info);
            return;
        }

        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField($"扫描结果: {_ppScanResult.totalProfiles} 个 Profile, {_ppScanResult.totalComponents} 个效果组件");

        // 已使用的效果（可选择关键字）
        _ppShowUsed = EditorGUILayout.Foldout(_ppShowUsed, $"已使用效果 ({_ppScanResult.usedEffects.Count})", true);
        if (_ppShowUsed)
        {
            EditorGUI.indentLevel++;
            foreach (var effect in _ppScanResult.usedEffects)
            {
                if (!_ppEffectFoldouts.ContainsKey(effect.effectName))
                    _ppEffectFoldouts[effect.effectName] = false;

                _ppEffectFoldouts[effect.effectName] = EditorGUILayout.Foldout(
                    _ppEffectFoldouts[effect.effectName],
                    $"{effect.effectName} ({effect.keywords.Count} 个关键字, 用于 {effect.profilePaths.Count} 个 Profile)",
                    true);

                if (_ppEffectFoldouts[effect.effectName])
                {
                    EditorGUI.indentLevel++;
                    foreach (var kw in effect.keywords)
                    {
                        if (!_ppSelectedKeywords.ContainsKey(kw))
                            _ppSelectedKeywords[kw] = false;

                        EditorGUILayout.BeginHorizontal();
                        _ppSelectedKeywords[kw] = EditorGUILayout.ToggleLeft(kw, _ppSelectedKeywords[kw]);
                        // 显示是否已在排除列表中
                        var existing = ShaderVariantCollectorSetting.GetStripAdditionalKeywords(_currentPackageName);
                        if (existing.Contains(kw))
                        {
                            GUILayout.Label("（已在排除列表）", EditorStyles.miniLabel, GUILayout.Width(100));
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUI.indentLevel--;
        }

        // 将已使用效果中选中的关键字添加到排除列表
        var selectedKws = new List<string>();
        foreach (var kvp in _ppSelectedKeywords)
        {
            if (kvp.Value) selectedKws.Add(kvp.Key);
        }
        if (selectedKws.Count > 0)
        {
            GUI.backgroundColor = new Color(0.9f, 0.6f, 0.1f);
            if (GUILayout.Button($"将选中的关键字添加到裁剪排除列表 ({selectedKws.Count} 个)", GUILayout.Height(25)))
            {
                var existing = ShaderVariantCollectorSetting.GetStripAdditionalKeywords(_currentPackageName);
                int added = 0;
                foreach (var kw in selectedKws)
                {
                    if (!existing.Contains(kw))
                    {
                        existing.Add(kw);
                        added++;
                    }
                    _ppSelectedKeywords[kw] = false;
                }
                ShaderVariantCollectorSetting.SetStripAdditionalKeywords(_currentPackageName, existing);
                Debug.Log($"[后处理扫描] 已添加 {added} 个关键字到裁剪排除列表");
            }
            GUI.backgroundColor = Color.white;
        }

        EditorGUILayout.Space(3);

        // 未使用的效果
        _ppShowUnused = EditorGUILayout.Foldout(_ppShowUnused, $"未使用效果 ({_ppScanResult.unusedEffects.Count})", true);
        if (_ppShowUnused)
        {
            EditorGUI.indentLevel++;
            foreach (var effect in _ppScanResult.unusedEffects)
            {
                string kwStr = effect.keywords.Count > 0 ? string.Join(", ", effect.keywords) : "（无映射）";
                EditorGUILayout.LabelField($"  {effect.effectName}  →  {kwStr}", EditorStyles.miniLabel);
            }
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(5);

        // 一键添加所有未使用效果的关键字
        var unusedKeywords = PostProcessScanner.GetUnusedKeywords(_ppScanResult);
        if (unusedKeywords.Count > 0)
        {
            GUI.backgroundColor = new Color(0.9f, 0.6f, 0.1f);
            if (GUILayout.Button($"将未使用效果的关键字添加到裁剪排除列表 ({unusedKeywords.Count} 个)", GUILayout.Height(30)))
            {
                var existing = ShaderVariantCollectorSetting.GetStripAdditionalKeywords(_currentPackageName);
                int added = 0;
                foreach (var kw in unusedKeywords)
                {
                    if (!existing.Contains(kw))
                    {
                        existing.Add(kw);
                        added++;
                    }
                }
                ShaderVariantCollectorSetting.SetStripAdditionalKeywords(_currentPackageName, existing);
                Debug.Log($"[后处理扫描] 已添加 {added} 个关键字到裁剪排除列表");
            }
            GUI.backgroundColor = Color.white;
        }
        else
        {
            EditorGUILayout.HelpBox("所有后处理效果都在使用中，无需排除", MessageType.Info);
        }
    }

    // 已知的 URP pass type 定义
    private static readonly (int value, string name)[] KnownPassTypes = new[]
    {
        (13, "UniversalForward"),
        (8, "ShadowCaster"),
        (15, "DepthOnly"),
        (14, "UniversalGBuffer"),
        (10, "MotionVectors"),
    };

    private bool _showPassTypes = false;

    private void DrawPassTypeSelector()
    {
        var selected = ShaderVariantCollectorSetting.GetSelectedPassTypes(_currentPackageName);
        var selectedSet = new HashSet<int>(selected);

        _showPassTypes = EditorGUILayout.Foldout(_showPassTypes, "收集的 Pass Type", true);
        if (!_showPassTypes) return;

        EditorGUI.indentLevel++;
        bool changed = false;

        foreach (var (value, name) in KnownPassTypes)
        {
            bool wasSelected = selectedSet.Contains(value);
            bool newSelected = EditorGUILayout.Toggle($"  {name} ({value})", wasSelected);
            if (newSelected != wasSelected)
            {
                if (newSelected) selectedSet.Add(value);
                else selectedSet.Remove(value);
                changed = true;
            }
        }

        if (changed)
        {
            _pendingSelectedPassTypes = new List<int>(selectedSet);
        }

        EditorGUI.indentLevel--;
    }

    private List<int> _pendingSelectedPassTypes;

    private void CollectButton_clicked()
    {
        string svName = ShaderVariantCollectorSetting.GetFileName(_currentPackageName);
        string savePath = ShaderVariantCollectorSetting.GeFileSavePath(_currentPackageName);
        string searchPath = ShaderVariantCollectorSetting.GeFileSearchPath(_currentPackageName);
        List<string> blackPaths = ShaderVariantCollectorSetting.GeBlackPath(_currentPackageName);
        List<string> filterNames = ShaderVariantCollectorSetting.GetFilterShaderNames(_currentPackageName);
        bool splitByShaderName = ShaderVariantCollectorSetting.GetSplitByShaderName(_currentPackageName);

        if (ShaderVariantCollectorSetting.GetAnalyzeMode(_currentPackageName))
        {
            // 分析模式：不渲染，直接读材质关键字
            ShaderVariantCollector.RunAnalyze($"{savePath}/{svName}", searchPath, blackPaths, filterNames.ToArray(), splitByShaderName, _currentPackageName);
        }
        else
        {
            // 渲染模式
            string searchScenePath = ShaderVariantCollectorSetting.GeSecneSearchPath(_currentPackageName);
            int processCapacity = ShaderVariantCollectorSetting.GeProcessCapacity(_currentPackageName);
            ShaderVariantCollector.Run($"{savePath}/{svName}", searchPath, searchScenePath, blackPaths, filterNames.ToArray(), processCapacity, splitByShaderName, null, _currentPackageName);
        }
    }
}
