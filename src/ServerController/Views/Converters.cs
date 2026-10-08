using System;
using Avalonia.Data.Converters;

namespace ServerController.Views;

public static class Conv
{
    public static readonly IValueConverter IsHot =
        new FuncValueConverter<double, bool>(v => v >= 85);

    public static readonly IValueConverter Percent =
        new FuncValueConverter<double, string>(v => $"%{v:0}");

    public static readonly IValueConverter NotNullOrEmpty =
        new FuncValueConverter<object?, bool>(v => v is string s ? s.Length > 0 : v != null);

    public static readonly IValueConverter IsZero =
        new FuncValueConverter<int, bool>(v => v == 0);

    public static readonly IValueConverter IsNotZero =
        new FuncValueConverter<int, bool>(v => v != 0);

    public static readonly IValueConverter Not =
        new FuncValueConverter<bool, bool>(v => !v);

    public static readonly IValueConverter Time =
        new FuncValueConverter<DateTime, string>(v => v.ToString("dd.MM.yyyy HH:mm:ss"));

    public static readonly IValueConverter ExitText =
        new FuncValueConverter<int?, string>(v => v == null ? "" : v == 0 ? "✓" : $"✗ {v}");
}
