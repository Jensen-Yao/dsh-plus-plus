using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace DshControl;

public partial class RainbowBrand : UserControl
{
    public RainbowBrand()
    {
        InitializeComponent();
        Loaded += (_, _) => SetMotion(App.Cfg?.AnimateBrand != false);
        Unloaded += (_, _) => Flow.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
    }

    public void SetMotion(bool enabled)
    {
        Flow.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        Flow.Angle = 0;
        if (enabled && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            Flow.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever });
    }
}
