using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TurntableStudio.ViewModels
{
    /// <summary>bool → Color，ConverterParameter="TrueColor|FalseColor"</summary>
    public sealed class BoolToColorConverter : IValueConverter
    {
        public static readonly BoolToColorConverter Instance = new();
        public object Convert(object value, Type t, object param, CultureInfo c)
        {
            var parts = (param as string)?.Split('|') ?? Array.Empty<string>();
            string hex = (value is true && parts.Length > 0) ? parts[0]
                       : (parts.Length > 1 ? parts[1] : "#888888");
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>bool → string，ConverterParameter="TrueText|FalseText"</summary>
    public sealed class BoolToTextConverter : IValueConverter
    {
        public static readonly BoolToTextConverter Instance = new();
        public object Convert(object value, Type t, object param, CultureInfo c)
        {
            var parts = (param as string)?.Split('|') ?? Array.Empty<string>();
            return (value is true && parts.Length > 0) ? parts[0]
                 : (parts.Length > 1 ? parts[1] : value?.ToString() ?? string.Empty);
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>bool → !bool</summary>
    public sealed class InvertBoolConverter : IValueConverter
    {
        public static readonly InvertBoolConverter Instance = new();
        public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is not true;
    }
}
