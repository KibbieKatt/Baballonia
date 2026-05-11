using Baballonia.Contracts;
using Baballonia.Services;
using Baballonia.Services.Inference;
using Baballonia.Services.Inference.Filters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using OscCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using System.Linq;

namespace Baballonia.ViewModels.SplitViewPane;

public partial class AppSettingsViewModel : ViewModelBase
{
    public IOscTarget OscTarget { get; }
    public GithubService GithubService { get; private set;}
    public ParameterSenderService ParameterSenderService { get; private set;}
    private OpenVRService OpenVrService { get; } = Ioc.Default.GetService<OpenVRService>();
    private ILocalSettingsService SettingsService { get; }

    public string MachineID => _identityService.GetUniqueUserId();

    [ObservableProperty]
    [property: SavedSetting("AppSettings_RecalibrateAddress", "/avatar/parameters/etvr_recalibrate")]
    private string _recalibrateAddress;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_RecenterAddress", "/avatar/parameters/etvr_recenter")]
    private string _recenterAddress;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_OSCPrefix", "")]
    private string _oscPrefix;

    [ObservableProperty]
    private IBrush _oscPrefixBackgroundColor;

    private bool _isOscPrefixValid = true;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_OneEuroEnabled", true)]
    private bool _oneEuroMinEnabled;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_EyeSmoothingFilter", EyeSmoothingModes.SavitzkyGolayFir)]
    private string _eyeSmoothingFilter;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_OneEuroMinFreqCutoff", 0.5f)]
    private float _oneEuroMinFreqCutoff;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_OneEuroSpeedCutoff", 3f)]
    private float _oneEuroSpeedCutoff;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_UseDFR", false)]
    private bool _useDFR;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_UseGPU", true)]
    private bool _useGPU;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_SteamvrAutoStart", true)]
    private bool _steamvrAutoStart;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_CheckForUpdates", false)]
    private bool _checkForUpdates;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_ShareEyeData", false)]
    private bool _shareEyeData;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_LogLevel", "Debug")]
    private string _logLevel;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_AdvancedOptions", false)]
    private bool _advancedOptions;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_StabilizeEyes", true)]
    private bool _stabilizeEyes;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_CorruptionDetector", CorruptionDetectorModes.StockRow)]
    private string _corruptionDetector;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_BigeyeWindowsBackend", "Stable")]
    private string _bigeyeWindowsBackend;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_RecordEyeTrace", false)]
    private bool _recordEyeTrace;

    public List<string> LowestLogLevel { get; } =
    [
        "Debug",
        "Information",
        "Warning",
        "Error"
    ];

    public List<string> CorruptionDetectorOptions { get; } = CorruptionDetectorModes.All.ToList();
    public List<string> BigeyeWindowsBackendOptions { get; } = ["Stable", "Low Latency"];
    public List<string> EyeSmoothingFilterOptions { get; } = EyeSmoothingModes.All.ToList();

    [ObservableProperty] private bool _onboardingEnabled;

    private readonly ILogger<AppSettingsViewModel> _logger;
    private readonly FacePipelineManager _facePipelineManager;
    private readonly EyePipelineManager _eyePipelineManager;
    private readonly IIdentityService _identityService;

    public AppSettingsViewModel(
        FacePipelineManager facePipelineManager,
        EyePipelineManager eyePipelineManager,
        IIdentityService identityService,
        IThemeSelectorService themeSelectorService)
    {
        _facePipelineManager = facePipelineManager;
        _eyePipelineManager = eyePipelineManager;
        _identityService = identityService;

        // General/Calibration Settings
        OscTarget = Ioc.Default.GetService<IOscTarget>()!;
        GithubService = Ioc.Default.GetService<GithubService>()!;
        SettingsService = Ioc.Default.GetService<ILocalSettingsService>()!;
        _logger = Ioc.Default.GetService<ILogger<AppSettingsViewModel>>()!;
        SettingsService.Load(this);
        CorruptionDetector = CorruptionDetectorModes.Normalize(CorruptionDetector);
        BigeyeWindowsBackend = NormalizeBigeyeWindowsBackend(BigeyeWindowsBackend);
        EyeSmoothingFilter = NormalizeEyeSmoothingFilter(EyeSmoothingFilter, OneEuroMinEnabled);

        // Handle edge case where OSC port is used and the system freaks out
        if (OscTarget.OutPort == 0)
        {
            const int port = 8888;
            OscTarget.OutPort = port;
            SettingsService.SaveSetting("OSCOutPort", port);
        }

        // Edge case: Update the OscPrefix Background color if and only if
        // The theme changes and the previous input WAS valid (IE keep red)
        themeSelectorService.ThemeChanged += variant =>
        {
            if (_isOscPrefixValid)
                SetOscPrefixBackgroundColor(variant);
        };

        // Risky Settings
        ParameterSenderService = Ioc.Default.GetService<ParameterSenderService>()!;

        OnboardingEnabled = Utils.IsSupportedDesktopOS;

        PropertyChanged += (_, p) =>
        {
            SettingsService.Save(this);
            _facePipelineManager.LoadFilter();
            _eyePipelineManager.LoadFilter();

            if (p.PropertyName == nameof(StabilizeEyes))
            {
                _eyePipelineManager.LoadEyeStabilization();
            }

            if (p.PropertyName == nameof(CorruptionDetector))
            {
                _eyePipelineManager.LoadCorruptionDetector();
            }
        };
    }

    async partial void OnBigeyeWindowsBackendChanged(string value)
    {
        var normalized = NormalizeBigeyeWindowsBackend(value);
        if (normalized != value)
        {
            BigeyeWindowsBackend = normalized;
            return;
        }

        SettingsService.SaveSetting("AppSettings_BigeyeWindowsBackend", normalized);
        await _eyePipelineManager.ReloadActiveCamerasAsync();
    }

    async partial void OnRecordEyeTraceChanged(bool value)
    {
        SettingsService.SaveSetting("AppSettings_RecordEyeTrace", value);
        if (value)
            await _eyePipelineManager.ReloadActiveCamerasAsync();
    }

    partial void OnEyeSmoothingFilterChanged(string value)
    {
        var normalized = NormalizeEyeSmoothingFilter(value, true);
        if (normalized != value)
        {
            EyeSmoothingFilter = normalized;
            return;
        }

        OneEuroMinEnabled = normalized != EyeSmoothingModes.Off;
        SettingsService.SaveSetting("AppSettings_EyeSmoothingFilter", normalized);
    }

    private static string NormalizeBigeyeWindowsBackend(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "low latency" => "Low Latency",
            "dshow" => "Low Latency",
            _ => "Stable"
        };
    }

    private static string NormalizeEyeSmoothingFilter(string? value, bool fallbackEnabled)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallbackEnabled ? EyeSmoothingModes.SavitzkyGolayFir : EyeSmoothingModes.Off;

        return EyeSmoothingModes.Normalize(value);
    }

    partial void OnOscPrefixChanged(string value)
    {
        // 1) A valid OSC prefix is also a valid message itself
        // IE: /foo/bar + /cheekPuffLeft
        // 2) Empty strings are also valid, IE no prefix
        _isOscPrefixValid = OscMessage.TryParse(value, out _) || string.IsNullOrEmpty(value);

        if (_isOscPrefixValid)
        {
            SettingsService.SaveSetting("AppSettings_OSCPrefix", value);
            SetOscPrefixBackgroundColor(Application.Current!.ActualThemeVariant);
            return;
        }

        OscPrefixBackgroundColor = new SolidColorBrush(Colors.PaleVioletRed);
    }

    private void SetOscPrefixBackgroundColor(ThemeVariant theme)
    {
        // Workaround to get proper SystemChromeMediumColor color
        OscPrefixBackgroundColor = theme.ToString() switch
        {
            "Light" => new SolidColorBrush(Colors.White),
            "Dark" => SolidColorBrush.Parse("#ff202020"),
            _ => OscPrefixBackgroundColor
        };
    }

    partial void OnSteamvrAutoStartChanged(bool value)
    {
        var readValue = SettingsService.ReadSetting("AppSettings_SteamvrAutoStart", value);
        if (readValue == value || OpenVrService == null)
            return;

        try
        {
           OpenVrService.SteamvrAutoStart = value;
           SettingsService.SaveSetting("AppSettings_SteamvrAutoStart", value);
        }
        catch (Exception e)
        {
            _logger.LogError("DLL not found!", e);
        }
    }

    async partial void OnUseGPUChanged(bool value)
    {
        var prev = SettingsService.ReadSetting("AppSettings_UseGPU", value);
        if (prev == value)
            return;

        try
        {
            SettingsService.SaveSetting("AppSettings_UseGPU", value);
            var loadEye = _eyePipelineManager.LoadInferenceAsync();
            var loadFace = _facePipelineManager.HasLoadedInference
                ? _facePipelineManager.LoadInferenceAsync()
                : Task.CompletedTask;

            await loadEye;
            await loadFace;
        }
        catch (Exception e)
        {
            _logger.LogError("", e);
        }
    }
}
