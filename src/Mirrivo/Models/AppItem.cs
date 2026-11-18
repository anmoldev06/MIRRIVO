using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace Mirrivo.Models;

public partial class AppItem : ObservableObject
{
    [ObservableProperty]
    private string _packageName = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _mainActivity = string.Empty;

    [ObservableProperty]
    private string _versionName = string.Empty;

    [ObservableProperty]
    private bool _isSystemApp = false;

    [ObservableProperty]
    private string _category = "App";

    [ObservableProperty]
    private string _iconColor = "#0071E3";

    [ObservableProperty]
    private string _iconGlyph = "📱";

    [ObservableProperty]
    private string? _iconPath = null;

    [ObservableProperty]
    private bool _hasCustomIcon = false;

    partial void OnIconPathChanged(string? value)
    {
        HasCustomIcon = !string.IsNullOrEmpty(value) && File.Exists(value);
    }

    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DisplayName)) return "APP";
            var parts = DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";
            }
            return DisplayName.Length >= 2 
                ? DisplayName.Substring(0, 2).ToUpper() 
                : DisplayName.ToUpper();
        }
    }

    public string AppTypeBadge => IsSystemApp ? "SYSTEM" : "USER APP";
    public string AppTypeColor => IsSystemApp ? "#8E8E93" : "#34C759";
}
