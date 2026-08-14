using System.Windows;
using LibVLCSharp.Shared;

namespace EyePeeOnMyTV.Dialogs;

public partial class VideoAdjustmentsWindow : Window
{
    private readonly MediaPlayer _mediaPlayer;
    private bool _initializing = true;

    public VideoAdjustmentsWindow(MediaPlayer mediaPlayer)
    {
        InitializeComponent();
        _mediaPlayer = mediaPlayer;

        EnableCheckBox.IsChecked = _mediaPlayer.AdjustInt(VideoAdjustOption.Enable) != 0;
        ContrastSlider.Value = _mediaPlayer.AdjustFloat(VideoAdjustOption.Contrast);
        BrightnessSlider.Value = _mediaPlayer.AdjustFloat(VideoAdjustOption.Brightness);
        HueSlider.Value = _mediaPlayer.AdjustFloat(VideoAdjustOption.Hue);
        SaturationSlider.Value = _mediaPlayer.AdjustFloat(VideoAdjustOption.Saturation);
        GammaSlider.Value = _mediaPlayer.AdjustFloat(VideoAdjustOption.Gamma);

        UpdateValueLabels();
        _initializing = false;
    }

    private void UpdateValueLabels()
    {
        ContrastValueText.Text = $"{ContrastSlider.Value:0.00}";
        BrightnessValueText.Text = $"{BrightnessSlider.Value:0.00}";
        HueValueText.Text = $"{HueSlider.Value:0}°";
        SaturationValueText.Text = $"{SaturationSlider.Value:0.00}";
        GammaValueText.Text = $"{GammaSlider.Value:0.00}";
    }

    private void EnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.SetAdjustInt(VideoAdjustOption.Enable, EnableCheckBox.IsChecked == true ? 1 : 0);
    }

    private void ContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        ContrastValueText.Text = $"{e.NewValue:0.00}";
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Contrast, (float)e.NewValue);
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        BrightnessValueText.Text = $"{e.NewValue:0.00}";
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Brightness, (float)e.NewValue);
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        HueValueText.Text = $"{e.NewValue:0}°";
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Hue, (float)e.NewValue);
    }

    private void SaturationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        SaturationValueText.Text = $"{e.NewValue:0.00}";
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Saturation, (float)e.NewValue);
    }

    private void GammaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        GammaValueText.Text = $"{e.NewValue:0.00}";
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Gamma, (float)e.NewValue);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ContrastSlider.Value = 1.0;
        BrightnessSlider.Value = 1.0;
        HueSlider.Value = 0.0;
        SaturationSlider.Value = 1.0;
        GammaSlider.Value = 1.0;
    }
}
