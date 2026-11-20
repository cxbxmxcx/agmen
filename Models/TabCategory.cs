using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Agmen.Models;

/// <summary>What a session is working on. Chosen by the user and shown as a color on its tab.</summary>
public enum TabCategory { None, Bug, Planning, Execution, Ready }

public static class TabCategories
{
    /// <summary>In shortcut order: Ctrl+Shift+1 through Ctrl+Shift+4.</summary>
    public static readonly TabCategory[] All = { TabCategory.Bug, TabCategory.Planning, TabCategory.Execution, TabCategory.Ready };

    private static readonly Brush BugBrush = Frozen("#FFF26D6D");
    private static readonly Brush PlanningBrush = Frozen("#FF5FA2FF");
    private static readonly Brush ExecutionBrush = Frozen("#FFF5CE42");
    private static readonly Brush ReadyBrush = Frozen("#FF3DDC84");
    public static readonly Brush DefaultText = Frozen("#FFECECF1");

    public static Brush? BrushFor(TabCategory c) => c switch
    {
        TabCategory.Bug => BugBrush,
        TabCategory.Planning => PlanningBrush,
        TabCategory.Execution => ExecutionBrush,
        TabCategory.Ready => ReadyBrush,
        _ => null,
    };

    /// <summary>Short label for the header chip, so the meaning never depends on color alone.</summary>
    public static string Label(TabCategory c) => c switch
    {
        TabCategory.Bug => "Bug",
        TabCategory.Planning => "Planning",
        TabCategory.Execution => "Execution",
        TabCategory.Ready => "Ready",
        _ => "",
    };

    public static string MenuText(TabCategory c) => c switch
    {
        TabCategory.Bug => "Bug (red)",
        TabCategory.Planning => "Planning (blue)",
        TabCategory.Execution => "Execution (yellow)",
        TabCategory.Ready => "Ready or awaiting work (green)",
        _ => "No color",
    };

    public static TabCategory Parse(string? text) =>
        Enum.TryParse<TabCategory>(text, ignoreCase: true, out var c) ? c : TabCategory.None;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
