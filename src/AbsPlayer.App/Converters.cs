using Microsoft.UI.Xaml.Data;

namespace AbsPlayer.App;

/// <summary>Uri → image decoded at tile size.</summary>
public sealed partial class CoverConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language) => Ui.Image(value as Uri);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>Uri → image decoded at list-row size.</summary>
public sealed partial class SmallCoverConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language) => Ui.Image(value as Uri, 112);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
