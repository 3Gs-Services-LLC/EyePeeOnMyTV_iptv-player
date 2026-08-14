using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;

namespace EyePeeOnMyTV.Dialogs;

public partial class EqualizerWindow : Window
{
    private readonly MediaPlayer _mediaPlayer;
    private Equalizer _equalizer = new();
    private readonly List<Slider> _bandSliders = new();
    private bool _initializing = true;

    public EqualizerWindow(MediaPlayer mediaPlayer)
    {
        InitializeComponent();
        _mediaPlayer = mediaPlayer;

        BuildPresetList();
        BuildBandSliders();

        PreampSlider.Value = _equalizer.Preamp;
        PresetComboBox.SelectedIndex = 0;
        _initializing = false;
    }

    private void BuildPresetList()
    {
        PresetComboBox.Items.Add("(Flat)");
        for (uint i = 0; i < _equalizer.PresetCount; i++)
        {
            PresetComboBox.Items.Add(_equalizer.PresetName(i) ?? $"Preset {i}");
        }
    }

    private void BuildBandSliders()
    {
        BandsPanel.Children.Clear();
        _bandSliders.Clear();

        for (uint band = 0; band < _equalizer.BandCount; band++)
        {
            var frequency = _equalizer.BandFrequency(band);
            var bandIndex = band;

            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -20,
                Maximum = 20,
                Height = 150,
                Value = _equalizer.Amp(band),
                Margin = new Thickness(8, 0, 8, 0),
            };
            slider.ValueChanged += (_, e) =>
            {
                _equalizer.SetAmp((float)e.NewValue, bandIndex);
                ApplyEqualizer();
            };

            var label = new TextBlock
            {
                Text = frequency >= 1000 ? $"{frequency / 1000:0.#}k" : $"{frequency:0}",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = Foreground,
            };

            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            column.Children.Add(slider);
            column.Children.Add(label);

            BandsPanel.Children.Add(column);
            _bandSliders.Add(slider);
        }
    }

    private void ApplyEqualizer()
    {
        if (_initializing || EnableCheckBox.IsChecked != true)
        {
            return;
        }

        _mediaPlayer.SetEqualizer(_equalizer);
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = PresetComboBox.SelectedIndex;
        _equalizer = index <= 0 ? new Equalizer() : new Equalizer((uint)(index - 1));

        PreampSlider.Value = _equalizer.Preamp;
        for (var i = 0; i < _bandSliders.Count; i++)
        {
            _bandSliders[i].Value = _equalizer.Amp((uint)i);
        }

        ApplyEqualizer();
    }

    private void PreampSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PreampValueText.Text = $"{e.NewValue:0.0} dB";
        _equalizer.SetPreamp((float)e.NewValue);
        ApplyEqualizer();
    }

    private void EnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (EnableCheckBox.IsChecked == true)
        {
            ApplyEqualizer();
        }
        else
        {
            _mediaPlayer.UnsetEqualizer();
        }
    }
}
