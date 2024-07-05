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
    private TextField _collectInputField;
    private Label _currentShaderCountField;
    private Label _currentVariantCountField;
    private SliderInt _processCapacitySlider;
    private PopupField<string> _packageField;

    private List<string> _packageNames;
    private string _currentPackageName;

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



            // 文件输出目录
            _collectOutputField = root.Q<TextField>("CollectOutput");
            _collectOutputField.SetValueWithoutNotify(ShaderVariantCollectorSetting.GeFileSavePath(_currentPackageName));
            _collectOutputField.RegisterValueChangedCallback(evt =>
            {
                ShaderVariantCollectorSetting.SetFileSavePath(_currentPackageName, _collectOutputField.value);
            });
            
            // 文件输入目录
            _collectInputField = root.Q<TextField>("VariantPrefabCollectPath");
            _collectInputField.SetValueWithoutNotify(ShaderVariantCollectorSetting.GeFileSearchPath(_currentPackageName));
            _collectInputField.RegisterValueChangedCallback(evt =>
            {
                ShaderVariantCollectorSetting.SetFileSearchPath(_currentPackageName, _collectInputField.value);
            });

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

            // 变种收集按钮
            _collectButton = root.Q<Button>("CollectButton");
            _collectButton.clicked += CollectButton_clicked;
        }
        catch (Exception e)
        {
            Debug.LogError(e.ToString());
        }
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
        string savePath = ShaderVariantCollectorSetting.GeFileSavePath(_currentPackageName);
        string searchPath = ShaderVariantCollectorSetting.GeFileSearchPath(_currentPackageName);
        int processCapacity = _processCapacitySlider.value;
        ShaderVariantCollector.Run(savePath,searchPath, processCapacity, null);
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