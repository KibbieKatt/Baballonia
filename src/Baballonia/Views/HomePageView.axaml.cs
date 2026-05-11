using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Baballonia.Contracts;
using Baballonia.Helpers;
using Baballonia.ViewModels.SplitViewPane;
using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Baballonia.Views;

public partial class HomePageView : ViewBase
{
    private static readonly FilePickerFileType OnnxAll = new("ONNX Models")
    {
        Patterns = ["*.onnx"],
    };

    private bool _isLayoutUpdating;
    private bool _diagnosticsAttached;

    private readonly IDeviceEnumerator _deviceEnumerator;
    private readonly ILocalSettingsService _localSettings;
    private readonly ILogger<HomePageView> _logger;

    public HomePageView(IDeviceEnumerator deviceEnumerator, ILocalSettingsService localSettings, ILogger<HomePageView> logger)
    {
        _deviceEnumerator = deviceEnumerator;
        _localSettings = localSettings;
        _logger = logger;
        InitializeComponent();

        if (Utils.IsSupportedDesktopOS)
        {
            SizeChanged += (_, _) =>
            {
                if (this.GetVisualRoot() is not Window window) return;

                var camerasGrid = this.FindControl<Grid>("CameraControlsGrid");
                var eyesGrid = this.FindControl<Grid>("EyesGrid");
                var isVertical = window.ClientSize.Width < Utils.MobileWidth;

                // Clear existing row/column definitions
                camerasGrid!.RowDefinitions.Clear();
                camerasGrid.ColumnDefinitions.Clear();

                eyesGrid!.RowDefinitions.Clear();
                eyesGrid.ColumnDefinitions.Clear();

                if (isVertical)
                {
                    // Vertical layout - one column, three rows
                    camerasGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    camerasGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    camerasGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

                    eyesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    eyesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    eyesGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

                    // Set grid positions for all children
                    for (var i = 0; i < camerasGrid.Children.Count; i++)
                    {
                        var child = camerasGrid.Children[i];
                        Grid.SetRow(child, i);
                        Grid.SetColumn(child, 0);
                        child.Margin = new Avalonia.Thickness(0, 0, 0, 16);
                    }
                    for (var i = 0; i < eyesGrid.Children.Count; i++)
                    {
                        var child = eyesGrid.Children[i];
                        Grid.SetRow(child, i);
                        Grid.SetColumn(child, 0);
                        child.Margin = new Avalonia.Thickness(0, 0, 0, 16);
                    }
                }
                else
                {
                    // Horizontal layout - three columns, one row
                    camerasGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Parse("2*")));
                    camerasGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    camerasGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                    eyesGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    eyesGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    eyesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                    // Set grid positions for all children
                    for (var i = 0; i < camerasGrid.Children.Count; i++)
                    {
                        var child = camerasGrid.Children[i];
                        Grid.SetRow(child, 0);
                        Grid.SetColumn(child, i);
                        child.Margin = new Avalonia.Thickness(0, 0, i < 2 ? 12 : 0, 0);
                    }
                    for (var i = 0; i < eyesGrid.Children.Count; i++)
                    {
                        var child = eyesGrid.Children[i];
                        Grid.SetRow(child, 0);
                        Grid.SetColumn(child, i);
                        child.Margin = new Avalonia.Thickness(0, 0, i < 2 ? 12 : 0, 0);
                    }
                }
            };
        }
        else
        {
            Loaded += (_, _) =>
            {
                var camerasGrid = this.FindControl<Grid>("CameraControlsGrid");
                var eyesGrid = this.FindControl<Grid>("EyesGrid");

                // Single column, full-width layout for Android
                camerasGrid!.RowDefinitions.Clear();
                camerasGrid.ColumnDefinitions.Clear();
                camerasGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

                eyesGrid!.RowDefinitions.Clear();
                eyesGrid.ColumnDefinitions.Clear();
                eyesGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

                for (var i = 0; i < camerasGrid.Children.Count; i++)
                {
                    camerasGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var child = camerasGrid.Children[i];
                    Grid.SetRow(child, i);
                    Grid.SetColumn(child, 0);
                    child.Margin = new Avalonia.Thickness(0, 0, 0, 16);
                }

                for (var i = 0; i < eyesGrid.Children.Count; i++)
                {
                    eyesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var child = eyesGrid.Children[i];
                    Grid.SetRow(child, i);
                    Grid.SetColumn(child, 0);
                    child.Margin = new Avalonia.Thickness(0, 0, 0, 16);
                }
            };
        }
        Loaded += (_, _) =>
        {
            if (DataContext is not HomePageViewModel vm) return;

            SetupCropEvents(vm.LeftCamera, LeftMouthWindow);
            SetupCropEvents(vm.RightCamera, RightMouthWindow);
            SetupCropEvents(vm.FaceCamera, FaceWindow);
            AttachDiagnostics(vm);

            vm.SelectedCalibrationTextBlock = this.Find<TextBlock>("SelectedCalibrationTextBlockColor")!;
            vm.SelectedCalibrationTextBlock.Text = Assets.Resources.Home_Eye_Calibration;
        };
    }

    private void SetupCropEvents(HomePageViewModel.CameraControllerModel model, Image image)
    {
        if (DataContext is not HomePageViewModel vm) return;

        // in theory should be cleaned up by the GC so no need to manually unsubscribe
        image.PointerPressed += (sender, e) =>
        {
            if (model.CamViewMode != CamViewMode.Cropping) return;
            var pos = e.GetPosition(image);
            model.CropManager.StartCrop(pos);
            model.OverlayRectangle = model.CropManager.CropZone.GetRect();
        };
        image.PointerMoved += (sender, e) =>
        {
            if (model.CamViewMode != CamViewMode.Cropping) return;

            var pos = e.GetPosition(image);
            model.CropManager.UpdateCrop(pos);
            model.OverlayRectangle = model.CropManager.CropZone.GetRect();
        };
        image.PointerReleased += (sender, e) =>
        {
            if (model.CamViewMode != CamViewMode.Cropping) return;

            model.CropManager.EndCrop();
            model.OnCropUpdated();
            vm.OnCropUpdated(model);
        };
    }

    private void OnCalibrationMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || DataContext is not HomePageViewModel vm) return;

        vm.SelectedCalibrationTextBlock.Text = menuItem.Header?.ToString()!;
        vm.RequestedVRCalibration = CalibrationRoutine.Map[menuItem.Name!];
    }

    private void AttachDiagnostics(HomePageViewModel vm)
    {
        if (Environment.GetEnvironmentVariable("BABALLONIA_DIAGNOSTICS") != "1")
            return;

        if (_diagnosticsAttached)
            return;

        _diagnosticsAttached = true;

        AttachCameraDiagnostics("Left", vm.LeftCamera, LeftViewBox, LeftMouthWindow);
        AttachCameraDiagnostics("Right", vm.RightCamera, RightViewBox, RightMouthWindow);
        AttachCameraDiagnostics("Face", vm.FaceCamera, FaceViewBox, FaceWindow);
    }

    private void AttachCameraDiagnostics(string name, HomePageViewModel.CameraControllerModel model, Viewbox viewbox, Image image)
    {
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not nameof(HomePageViewModel.CameraControllerModel.IsCameraRunning) and
                not nameof(HomePageViewModel.CameraControllerModel.Bitmap))
                return;

            var bitmapSize = model.Bitmap == null ? "null" : $"{model.Bitmap.PixelSize.Width}x{model.Bitmap.PixelSize.Height}";
            _logger.LogInformation(
                "View {CameraName} model {PropertyName}: running={Running} bitmap={BitmapSize} viewVisible={ViewVisible} imageVisible={ImageVisible} sourceNull={SourceNull}",
                name,
                args.PropertyName,
                model.IsCameraRunning,
                bitmapSize,
                viewbox.IsVisible,
                image.IsVisible,
                image.Source == null);
        };

        viewbox.PropertyChanged += (_, args) =>
        {
            if (args.Property?.Name != "IsVisible")
                return;

            _logger.LogInformation(
                "View {CameraName} viewbox visibility: visible={Visible} running={Running} sourceNull={SourceNull}",
                name,
                viewbox.IsVisible,
                model.IsCameraRunning,
                image.Source == null);
        };

        image.PropertyChanged += (_, args) =>
        {
            if (args.Property != Image.SourceProperty)
                return;

            _logger.LogInformation(
                "View {CameraName} image source: sourceNull={SourceNull} running={Running} viewVisible={ViewVisible} imageBounds={Bounds}",
                name,
                image.Source == null,
                model.IsCameraRunning,
                viewbox.IsVisible,
                image.Bounds);
        };
    }

    private void OnExpanderCollapsed(object? sender, RoutedEventArgs e)
    {
        if (_isLayoutUpdating) return;
        _isLayoutUpdating = true;

        // Force layout update
        InvalidateArrange();
        InvalidateMeasure();

        _isLayoutUpdating = false;
    }

    private void OnExpanderExpanded(object? sender, RoutedEventArgs e)
    {
        if (_isLayoutUpdating) return;
        _isLayoutUpdating = true;

        // Force layout update
        InvalidateArrange();
        InvalidateMeasure();

        _isLayoutUpdating = false;
    }

    private void RefreshLeftEyeConnectedDevices(object? sender, CancelEventArgs e)
    {
        //if (DataContext is not HomePageViewModel vm) return;
        //vm.LeftCamera.UpdateCameraDropDown();
    }

    private void RefreshRightEyeDevices(object? sender, CancelEventArgs e)
    {
        //if (DataContext is not HomePageViewModel vm) return;
        //vm.RightCamera.UpdateCameraDropDown();
    }

    private void RefreshConnectedFaceDevices(object? sender, CancelEventArgs e)
    {
        //if (DataContext is not HomePageViewModel vm) return;
        //vm.FaceCamera.UpdateCameraDropDown();
    }

    private async void RefreshDevices(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomePageViewModel vm) return;
        RefreshDevicesText.IsEnabled = false;

        try
        {
            var cameras = _deviceEnumerator.UpdateCameras();
            var friendlyNames = cameras.Keys.ToArray();

            vm.LeftCamera.UpdateCameraDropDown(friendlyNames);
            vm.RightCamera.UpdateCameraDropDown(friendlyNames);
            vm.FaceCamera.UpdateCameraDropDown(friendlyNames);
        }
        catch (Exception)
        {
        }

        RefreshDevicesText.IsEnabled = true;
    }

    private async void EyeModelLoad(object? sender, RoutedEventArgs e)
    {
        var topLevelStorageProvider = TopLevel.GetTopLevel(this)!.StorageProvider;
        var suggestedStartLocation =
            await topLevelStorageProvider.TryGetFolderFromPathAsync(Utils.ModelsDirectory)!;
        var file = await topLevelStorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select ONNX Model",
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation, // Falls back to desktop if Models folder hasn't been created yet
            FileTypeFilter = [OnnxAll]
        })!;

        if (file.Count == 0) return;
        if (DataContext is not HomePageViewModel vm) return;

        _localSettings.SaveSetting("EyeHome_EyeModel", file[0].Path.AbsolutePath);

        await vm.ReloadEyeInference();

        LoadEyeModelText.Text = file[0].Name;
        LoadEyeModelText.Foreground = new SolidColorBrush(Colors.Green);
        await Task.Delay(3000);
        LoadEyeModelText.Text = Baballonia.Assets.Resources.Home_Eye_Load_Model;
        LoadEyeModelText.Foreground = new SolidColorBrush(vm.GetBaseHighColor());
    }
}
