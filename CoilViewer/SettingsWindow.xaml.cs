using System;
using System.Windows;
using System.Windows.Controls;

namespace CoilViewer;

public partial class SettingsWindow : Window
{
    private readonly ViewerConfig _originalConfig;
    private readonly ViewerConfig _workingConfig;
    private readonly string _configPath;

    public SettingsWindow(ViewerConfig config, string configPath)
    {
        InitializeComponent();
        _originalConfig = config;
        _workingConfig = new ViewerConfig
        {
            PreloadImageCount = config.PreloadImageCount,
            BackgroundColor = config.BackgroundColor,
            FitMode = config.FitMode,
            ScalingMode = config.ScalingMode,
            ShowOverlay = config.ShowOverlay,
            LoopAround = config.LoopAround,
            SortField = config.SortField,
            SortDirection = config.SortDirection,
            EnableNsfwDetection = config.EnableNsfwDetection,
            NsfwModelPath = config.NsfwModelPath,
            NsfwThreshold = config.NsfwThreshold,
            NsfwFilterMode = config.NsfwFilterMode,
            EnableObjectDetection = config.EnableObjectDetection,
            ObjectModelPath = config.ObjectModelPath,
            ObjectLabelsPath = config.ObjectLabelsPath,
            ObjectFilterMode = config.ObjectFilterMode,
            ObjectFilterText = config.ObjectFilterText,
            ObjectFilterThreshold = config.ObjectFilterThreshold
        };
        _configPath = configPath;

        LoadSettings();
    }

    private void LoadSettings()
    {
        PreloadImageCountTextBox.Text = _workingConfig.PreloadImageCount.ToString();
        ShowOverlayCheckBox.IsChecked = _workingConfig.ShowOverlay;
        LoopAroundCheckBox.IsChecked = _workingConfig.LoopAround;
    }

    private void SaveSettings()
    {
        if (int.TryParse(PreloadImageCountTextBox.Text, out var preloadImageCount) && preloadImageCount >= 0)
        {
            _workingConfig.PreloadImageCount = preloadImageCount;
        }

        _workingConfig.ShowOverlay = ShowOverlayCheckBox.IsChecked == true;
        _workingConfig.LoopAround = LoopAroundCheckBox.IsChecked == true;

        _workingConfig.Save(_configPath);
    }

    private void OnPreloadImageCountChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(PreloadImageCountTextBox.Text, out var value) && value >= 0)
        {
            PreloadImageCountTextBox.BorderBrush = System.Windows.Media.Brushes.Transparent;
        }
        else
        {
            PreloadImageCountTextBox.BorderBrush = System.Windows.Media.Brushes.Red;
        }
    }

    private void OnShowOverlayChanged(object sender, RoutedEventArgs e)
    {
        // Changes applied on save
    }

    private void OnLoopAroundChanged(object sender, RoutedEventArgs e)
    {
        // Changes applied on save
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PreloadImageCountTextBox.Text, out var preloadCount) || preloadCount < 0)
        {
            System.Windows.MessageBox.Show("Please enter a valid preload image count (0 or greater).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            PreloadImageCountTextBox.Focus();
            return;
        }

        SaveSettings();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
