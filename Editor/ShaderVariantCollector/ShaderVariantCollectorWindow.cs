// #if UNITY_2019_4_OR_NEWER
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

public class ShaderVariantCollectorWindow : EditorWindow
{
    [MenuItem("Tools/着色器变种收集器", false, 100)]
    public static void OpenWindow()
    {
        ShaderVariantCollectorWindow window = GetWindow<ShaderVariantCollectorWindow>("着色器变种收集工具", true);
        window.minSize = new Vector2(800, 600);
    }

    private Button _collectButton;
    private TextField _collectOutputField;
    private Label _currentShaderCountField;
    private Label _currentVariantCountField;
    private SliderInt _processCapacitySlider;
    private PopupField<string> _packageField;
    private Toggle _splitByShaderNameToggle;
    private Toggle _collectSceneVariantsToggle;
    private Toggle _saveJsonFileToggle;

    private List<string> _packageNames;
    private static string _currentPackageName = "Default";
    private List<string> _blackSceneNames;
    private List<string> _globalKeywords;
    private List<string> _filterShaderNames;
    private LocalKeywordCollection _localKeywords;
    
    public VisualElement outputContainer;   // 存放列表项的容器  
    public VisualElement prefabCollectContainer;   // 存放列表项的容器  
    public VisualElement sceneCollectContainer;   // 存放列表项的容器  
    public VisualElement blacklistContainer;   // 存放列表项的容器  
    public VisualElement globalKeywordsContainer;   // 存放全局关键字的容器  
    public VisualElement localKeywordsContainer;   // 存放局部关键字的容器  
    public VisualElement filterShaderNamesContainer;   // 存放过滤着色器名称的容器  

    public void CreateGUI()
    {
        try
        {
            VisualElement root = this.rootVisualElement;

            // 加载布局文件
            var visualAsset = UxmlLoader.LoadWindowUXML<ShaderVariantCollectorWindow>();
            if (visualAsset == null)
                return;

            visualAsset.CloneTree(root);
         
            _collectOutputField = root.Q<TextField>("ShaderCollectName");
            _collectOutputField.SetValueWithoutNotify(ShaderVariantCollectorSetting.GetFileName(_currentPackageName));
            _collectOutputField.RegisterValueChangedCallback(evt =>
            {
                ShaderVariantCollectorSetting.SetFileName(_currentPackageName, _collectOutputField.value);
            });
            
            outputContainer = root.Q<VisualElement>("OutPutVE");
            prefabCollectContainer = root.Q<VisualElement>("PrefabCollectVE");
            sceneCollectContainer = root.Q<VisualElement>("SceneCollectPath");
            blacklistContainer = root.Q<VisualElement>("blacklistContainer");

            string outputPath = ShaderVariantCollectorSetting.GeFileSavePath(_currentPackageName);
            PathSelector outputSelector = new PathSelector(outputPath, false,"文件保存路径");
            outputSelector.OnSaveEvent += delegate(string newpath)
            {
                ShaderVariantCollectorSetting.SetFileSavePath(_currentPackageName, newpath);
            };
            outputContainer.Add(outputSelector);

            string prefabCollectPath = ShaderVariantCollectorSetting.GeFileSearchPath(_currentPackageName);
            PathSelector prefabCollectSelector = new PathSelector(prefabCollectPath,false,"材质收集路径");
            prefabCollectSelector.OnSaveEvent += delegate(string newpath)
            {
                ShaderVariantCollectorSetting.SetFileSearchPath(_currentPackageName, newpath);
            };
            prefabCollectContainer.Add(prefabCollectSelector);

            string sceneCollectPath = ShaderVariantCollectorSetting.GeSecneSearchPath(_currentPackageName);
            PathSelector sceneCollectSelector = new PathSelector(sceneCollectPath,false,"场景收集路径");
            sceneCollectSelector.OnSaveEvent += delegate(string newpath)
            {
                ShaderVariantCollectorSetting.SetSceneSearchPath(_currentPackageName, newpath);
            };
            sceneCollectContainer.Add(sceneCollectSelector);
            
            Button addBlackButton = root.Q<Button>("addBlackButton");
            addBlackButton.clicked += OnAddSceneItem;

            filterShaderNamesContainer = root.Q<VisualElement>("filterShaderNamesContainer");
            Button addFilterShaderButton = root.Q<Button>("addFilterShaderButton");
            addFilterShaderButton.clicked += OnAddFilterShaderItem;

            localKeywordsContainer = root.Q<VisualElement>("localKeywordsContainer");
            Button addLocalKeywordButton = root.Q<Button>("addLocalKeywordButton");
            addLocalKeywordButton.clicked += OnAddLocalKeyword;

            globalKeywordsContainer = root.Q<VisualElement>("globalKeywordsContainer");
            Button addGlobalKeywordButton = root.Q<Button>("addGlobalKeywordButton");
            addGlobalKeywordButton.clicked += OnAddGlobalKeyword;

            // 收集的包裹
            // var packageContainer = root.Q("PackageContainer");
            // if (_packageNames.Count > 0)
            // {
            //     int defaultIndex = GetDefaultPackageIndex(_currentPackageName);
            //     _packageField = new PopupField<string>(_packageNames, defaultIndex);
            //     _packageField.label = "Package";
            //     _packageField.style.width = 350;
            //     _packageField.RegisterValueChangedCallback(evt =>
            //     {
            //         _currentPackageName = _packageField.value;
            //     });
            //     packageContainer.Add(_packageField);
            // }
            // else
            // {
            //     _packageField = new PopupField<string>();
            //     _packageField.label = "Package";
            //     _packageField.style.width = 350;
            //     packageContainer.Add(_packageField);
            // }

            // 容器值
            _processCapacitySlider = root.Q<SliderInt>("ProcessCapacity");
            _processCapacitySlider.SetValueWithoutNotify(ShaderVariantCollectorSetting.GeProcessCapacity(_currentPackageName));
#if !UNITY_2020_3_OR_NEWER
            _processCapacitySlider.label = $"Capacity ({_processCapacitySlider.value})";
            _processCapacitySlider.RegisterValueChangedCallback(evt =>
            {
                ShaderVariantCollectorSetting.SetProcessCapacity(_currentPackageName, _processCapacitySlider.value);
                _processCapacitySlider.label = $"Capacity ({_processCapacitySlider.value})";
            });
#else
            _processCapacitySlider.RegisterValueChangedCallback(evt =>
            {
                ShaderVariantCollectorSetting.SetProcessCapacity(_currentPackageName, _processCapacitySlider.value);
            });
#endif

            _currentShaderCountField = root.Q<Label>("CurrentShaderCount");
            _currentVariantCountField = root.Q<Label>("CurrentVariantCount");

            _splitByShaderNameToggle = root.Q<Toggle>("SplitByShaderNameToggle");
            _splitByShaderNameToggle.SetValueWithoutNotify(ShaderVariantCollectorSetting.GetSplitByShaderName(_currentPackageName));
            _splitByShaderNameToggle.RegisterValueChangedCallback(evt =>
            {
                ShaderVariantCollectorSetting.SetSplitByShaderName(_currentPackageName, evt.newValue);
            });

            _collectSceneVariantsToggle = root.Q<Toggle>("CollectSceneVariantsToggle");
            _collectSceneVariantsToggle.SetValueWithoutNotify(ShaderVariantCollectorSetting.GetCollectSceneVariants(_currentPackageName));
            _collectSceneVariantsToggle.RegisterValueChangedCallback(evt =>
            {
                ShaderVariantCollectorSetting.SetCollectSceneVariants(_currentPackageName, evt.newValue);
            });
            
            _saveJsonFileToggle = root.Q<Toggle>("SaveJsonFileToggle");
            _saveJsonFileToggle.SetValueWithoutNotify(ShaderVariantCollectorSetting.GetSaveJsonFile(_currentPackageName));
            _saveJsonFileToggle.RegisterValueChangedCallback(evt =>
            {
                ShaderVariantCollectorSetting.SetSaveJsonFile(_currentPackageName, evt.newValue);
            });

            // 变种收集按钮
            _collectButton = root.Q<Button>("CollectButton");
            _collectButton.clicked += CollectButton_clicked;
            InitializeMaterialList();
            InitializeGlobalKeywords();
            InitializeLocalKeywords();
            InitializeFilterShaderNames();
        }
        catch (Exception e)
        {
            Debug.LogError(e.ToString());
        }
    }
    
    private void InitializeMaterialList()
    {
        List<string> paths = ShaderVariantCollectorSetting.GeBlackPath(_currentPackageName);
        _blackSceneNames = new List<string>();
        
        // 遍历初始化数据，添加项目到列表
        foreach (string itemText in paths)
        {
            AddSceneItem(itemText);
            _blackSceneNames.Add(itemText);
        }
    }

    private void InitializeGlobalKeywords()
    {
        List<string> keywords = ShaderVariantCollectorSetting.GetGlobalKeywords(_currentPackageName);
        _globalKeywords = new List<string>();
        
        foreach (string keyword in keywords)
        {
            AddGlobalKeywordItem(keyword);
            _globalKeywords.Add(keyword);
        }
    }

    private void InitializeLocalKeywords()
    {
        _localKeywords = ShaderVariantCollectorSetting.GetLocalKeywords(_currentPackageName);
        
        foreach (var localKeyword in _localKeywords.LocalKeywords)
        {
            AddLocalKeywordItem(localKeyword.ShaderName, localKeyword.Keyword);
        }
    }

    private void InitializeFilterShaderNames()
    {
        List<string> shaderNames = ShaderVariantCollectorSetting.GetFilterShaderNames(_currentPackageName);
        _filterShaderNames = new List<string>();
        
        foreach (string shaderName in shaderNames)
        {
            AddFilterShaderItem(shaderName);
            _filterShaderNames.Add(shaderName);
        }
    }
    
    private void OnAddSceneItem()
    {
        AddSceneItem("");
    }
    
    private void AddSceneItem(string path)
    {
        PathSelector pathSelector = new PathSelector(path);
        pathSelector.OnSaveEvent += delegate(string newpath)
        {
            SetBlackScenePath(newpath);
        };
        
        pathSelector.OnRemoveEvent += delegate()
        {
            SetBlackScenePath(pathSelector.SelectedPath, false);
            blacklistContainer.Remove(pathSelector);
        };
        
        blacklistContainer.Add(pathSelector);
    }

    private void OnAddGlobalKeyword()
    {
        // 检查是否已经存在空的关键字输入框
        bool hasEmptyKeywordField = false;
        foreach (var child in globalKeywordsContainer.Children())
        {
            var textField = child.Q<TextField>();
            if (textField != null && string.IsNullOrEmpty(textField.value))
            {
                hasEmptyKeywordField = true;
                break;
            }
        }
        
        // 只有在没有空的关键字输入框时才添加新的
        if (!hasEmptyKeywordField)
        {
            AddGlobalKeywordItem("");
        }
    }
    
    private void AddGlobalKeywordItem(string keyword)
    {
        var container = new VisualElement();
        container.style.flexDirection = FlexDirection.Row;
        container.style.marginBottom = 5;

        var textField = new TextField("关键字");
        textField.value = keyword;
        textField.style.flexGrow = 1;
        container.Add(textField);

        var saveButton = new Button(() => 
        {
            if (!string.IsNullOrEmpty(textField.value))
            {
                SetGlobalKeyword(textField.value);
            }
        }) { text = "保存" };
        container.Add(saveButton);

        var removeButton = new Button(() => 
        {
            if (!string.IsNullOrEmpty(textField.value))
            {
                SetGlobalKeyword(textField.value, false);
            }
            globalKeywordsContainer.Remove(container);
        }) { text = "删除" };
        container.Add(removeButton);

        globalKeywordsContainer.Add(container);
    }

    private void OnAddLocalKeyword()
    {
        AddLocalKeywordItem("", "");
    }
    
    private void AddLocalKeywordItem(string shaderName, string keyword)
    {
        var container = new VisualElement();
        container.style.flexDirection = FlexDirection.Row;
        container.style.marginBottom = 5;

        var shaderNameField = new TextField("着色器名称");
        shaderNameField.value = shaderName;
        shaderNameField.style.flexGrow = 1;
        container.Add(shaderNameField);

        var keywordField = new TextField("关键字");
        keywordField.value = keyword;
        keywordField.style.flexGrow = 1;
        container.Add(keywordField);

        var saveButton = new Button(() => 
        {
            if (!string.IsNullOrEmpty(shaderNameField.value) && !string.IsNullOrEmpty(keywordField.value))
            {
                SetLocalKeyword(shaderNameField.value, keywordField.value);
            }
        }) { text = "保存" };
        container.Add(saveButton);

        var removeButton = new Button(() => 
        {
            if (!string.IsNullOrEmpty(shaderNameField.value) && !string.IsNullOrEmpty(keywordField.value))
            {
                SetLocalKeyword(shaderNameField.value, keywordField.value, false);
            }
            localKeywordsContainer.Remove(container);
        }) { text = "删除" };
        container.Add(removeButton);

        localKeywordsContainer.Add(container);
    }

    private void OnAddFilterShaderItem()
    {
        AddFilterShaderItem("");
    }
    
    private void AddFilterShaderItem(string shaderName)
    {
        var container = new VisualElement();
        container.style.flexDirection = FlexDirection.Row;
        container.style.marginBottom = 5;

        var textField = new TextField("着色器名称");
        textField.value = shaderName;
        textField.style.flexGrow = 1;
        container.Add(textField);

        var saveButton = new Button(() => 
        {
            if (!string.IsNullOrEmpty(textField.value))
            {
                SetFilterShaderName(textField.value);
            }
        }) { text = "保存" };
        container.Add(saveButton);

        var removeButton = new Button(() => 
        {
            if (!string.IsNullOrEmpty(textField.value))
            {
                SetFilterShaderName(textField.value, false);
            }
            filterShaderNamesContainer.Remove(container);
        }) { text = "删除" };
        container.Add(removeButton);

        filterShaderNamesContainer.Add(container);
    }

    private void SetBlackScenePath(string newpath, bool isAdd = true)
    {
        if (isAdd)
        {
            if (_blackSceneNames.Contains(newpath))
            {
                return;
            }
            _blackSceneNames.Add(newpath);
        }
        else
        {
            if (!_blackSceneNames.Contains(newpath))
            {
                return;
            }
            _blackSceneNames.Remove(newpath);
        }
        
        string pathlists = "";
        for (int i = 0; i < _blackSceneNames.Count; i++)
        {
            string path = _blackSceneNames[i];
            if (i == 0)
            {
                pathlists += path;
            }
            else
            {
                pathlists += "," +  path;
            }
        }
        ShaderVariantCollectorSetting.SetBlackPath(_currentPackageName, pathlists);
    }

    private void SetGlobalKeyword(string newKeyword, bool isAdd = true)
    {
        if (string.IsNullOrEmpty(newKeyword))
            return;

        if (isAdd)
        {
            if (_globalKeywords.Contains(newKeyword))
            {
                return;
            }
            _globalKeywords.Add(newKeyword);
        }
        else
        {
            if (!_globalKeywords.Contains(newKeyword))
            {
                return;
            }
            _globalKeywords.Remove(newKeyword);
        }
        
        // 保存到设置中
        ShaderVariantCollectorSetting.SetGlobalKeywords(_currentPackageName, _globalKeywords);
    }

    private void SetLocalKeyword(string shaderName, string keyword, bool isAdd = true)
    {
        if (string.IsNullOrEmpty(shaderName) || string.IsNullOrEmpty(keyword))
            return;

        if (isAdd)
        {
            _localKeywords.AddLocalKeyword(shaderName, keyword);
        }
        else
        {
            _localKeywords.RemoveLocalKeyword(shaderName, keyword);
        }
        
        // 保存到设置中
        ShaderVariantCollectorSetting.SetLocalKeywords(_currentPackageName, _localKeywords);
    }

    private void SetFilterShaderName(string newShaderName, bool isAdd = true)
    {
        if (string.IsNullOrEmpty(newShaderName))
            return;

        if (isAdd)
        {
            if (_filterShaderNames.Contains(newShaderName))
            {
                return;
            }
            _filterShaderNames.Add(newShaderName);
        }
        else
        {
            if (!_filterShaderNames.Contains(newShaderName))
            {
                return;
            }
            _filterShaderNames.Remove(newShaderName);
        }
        
        // 保存到设置中
        ShaderVariantCollectorSetting.SetFilterShaderNames(_currentPackageName, _filterShaderNames.ToArray());
    }

    private void Update()
    {
        if (_currentShaderCountField != null)
        {
            int currentShaderCount = ShaderVariantCollectionHelper.GetCurrentShaderVariantCollectionShaderCount();
            _currentShaderCountField.text = $"Current Shader Count : {currentShaderCount}";
        }

        if (_currentVariantCountField != null)
        {
            int currentVariantCount = ShaderVariantCollectionHelper.GetCurrentShaderVariantCollectionVariantCount();
            _currentVariantCountField.text = $"Current Variant Count : {currentVariantCount}";
        }
    }

    private void CollectButton_clicked()
    {
        string svName = ShaderVariantCollectorSetting.GetFileName(_currentPackageName);
        string savePath = ShaderVariantCollectorSetting.GeFileSavePath(_currentPackageName);
        string searchPath = ShaderVariantCollectorSetting.GeFileSearchPath(_currentPackageName);
        string searchScenePath = ShaderVariantCollectorSetting.GeSecneSearchPath(_currentPackageName);
        List<string> blackPaths = ShaderVariantCollectorSetting.GeBlackPath(_currentPackageName);
        string[] filterShaderName = _filterShaderNames.ToArray();
        bool splitByShaderName = ShaderVariantCollectorSetting.GetSplitByShaderName(_currentPackageName);
        int processCapacity = _processCapacitySlider.value;
        ShaderVariantCollector.Run($"{savePath}/{svName}", searchPath, searchScenePath, blackPaths, filterShaderName, processCapacity, splitByShaderName, null, _currentPackageName);
    }

    // 构建包裹相关
    private int GetDefaultPackageIndex(string packageName)
    {
        for (int index = 0; index < _packageNames.Count; index++)
        {
            if (_packageNames[index] == packageName)
            {
                return index;
            }
        }
        return 0;
    }
}
// #endif