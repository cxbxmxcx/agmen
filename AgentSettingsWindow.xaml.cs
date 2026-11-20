using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Agmen.Agents;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace Agmen;

/// <summary>
/// Where the user sets how Agmen starts each agent's sessions: model, reasoning effort and
/// permission mode. Each change is saved at once; the workspace offers to restart the running
/// sessions it affects when the window closes.
/// </summary>
public partial class AgentSettingsWindow : Window
{
    private const string OwnSetting = "Its own setting";
    private static readonly Brush Amber = Frozen(0xE5, 0xA5, 0x0A);

    private readonly HashSet<AgentKind> _changed = new();
    private ComboBox? _antigravityModels;

    /// <summary>The agents whose settings changed while the window was open.</summary>
    public IReadOnlyCollection<AgentKind> Changed => _changed;

    public AgentSettingsWindow()
    {
        InitializeComponent();
        foreach (var agent in AgentProfiles.All.Where(AgentProfiles.IsInstalled)) Cards.Children.Add(Card(agent));
        Loaded += async (_, _) =>
        {
            // agy lists its models only by fetching them, which takes a few seconds.
            if (_antigravityModels is not { } box) return;
            var models = await AgentSettings.LoadAntigravityModels();
            var text = box.Text;
            foreach (var model in models) box.Items.Add(model);
            box.Text = text;
        };
    }

    private FrameworkElement Card(AgentKind agent)
    {
        var launch = AgentSettings.For(agent);
        var rows = new Grid();
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        rows.ColumnDefinitions.Add(new ColumnDefinition());

        // Model: a suggestion from the list, or any name the agent accepts.
        var model = new ComboBox { IsEditable = true, IsTextSearchEnabled = false };
        model.Items.Add(OwnSetting);
        foreach (var known in AgentSettings.KnownModels(agent)) model.Items.Add(known);
        model.Text = launch.Model ?? OwnSetting;
        var hint = agent switch
        {
            AgentKind.Claude => "An alias such as opus, or a full model name.",
            AgentKind.Antigravity => "Its models load in a moment; you can also type one.",
            _ => "Pick one, or type any model it accepts.",
        };
        var modelNote = Note(hint);
        void ModelChanged()
        {
            var text = model.Text.Trim();
            if (text.Length == 0 || text == OwnSetting) Save(agent, launch => launch with { Model = null });
            else if (AgentSettings.IsValidModel(text)) Save(agent, launch => launch with { Model = text });
            else
            {
                modelNote.Text = "That isn't a model name: use letters, digits and . _ : / @ + - [ ].";
                modelNote.Foreground = Amber;
                return;
            }
            modelNote.Text = hint;
            modelNote.Foreground = (Brush)FindResource("TextSecondary");
        }
        model.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ModelChanged()));
        model.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ModelChanged);
        if (agent == AgentKind.Antigravity) _antigravityModels = model;

        var effort = new ComboBox();
        effort.Items.Add(OwnSetting);
        foreach (var level in AgentSettings.Efforts(agent)) effort.Items.Add(level);
        effort.SelectedItem = launch.Effort is { } current && effort.Items.Contains(current) ? current : OwnSetting;
        effort.SelectionChanged += (_, _) =>
            Save(agent, launch => launch with { Effort = effort.SelectedItem is string level && level != OwnSetting ? level : null });

        var modes = AgentSettings.PermissionModes(agent);
        var permissions = new ComboBox();
        permissions.Items.Add(OwnSetting);
        foreach (var mode in modes) permissions.Items.Add(mode.Label);
        int index = modes.ToList().FindIndex(m => m.Id == launch.Permissions);
        permissions.SelectedIndex = index + 1;
        permissions.SelectionChanged += (_, _) =>
            Save(agent, launch => launch with { Permissions = permissions.SelectedIndex > 0 ? modes[permissions.SelectedIndex - 1].Id : null });

        AddRow(rows, "Model", model, modelNote);
        AddRow(rows, "Effort", effort, agent == AgentKind.Codex ? Note("ultra suits only some Codex models.") : null);
        AddRow(rows, "Permissions", permissions, null);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        title.Children.Add(new Border
        {
            Background = AgentProfiles.BadgeBrush(agent),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = AgentProfiles.ShortName(agent), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Frozen(0x15, 0x15, 0x1C) },
        });
        title.Children.Add(new TextBlock
        {
            Text = AgentProfiles.DisplayName(agent),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimary"),
        });

        var card = new StackPanel();
        card.Children.Add(title);
        card.Children.Add(rows);
        return new Border
        {
            Background = (Brush)FindResource("CardBg"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = card,
        };
    }

    private void AddRow(Grid rows, string label, System.Windows.Controls.Control input, TextBlock? note)
    {
        int row = rows.RowDefinitions.Count;
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (Brush)FindResource("TextSecondary"),
        };
        Grid.SetRow(text, row);
        rows.Children.Add(text);

        var cell = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        input.FontSize = 12.5;
        cell.Children.Add(input);
        if (note is not null) cell.Children.Add(note);
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, 1);
        rows.Children.Add(cell);
    }

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(2, 3, 0, 0),
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource("TextSecondary"),
    };

    private void Save(AgentKind agent, Func<AgentLaunch, AgentLaunch> change)
    {
        if (!IsLoaded) return;   // the window filling itself in isn't a change
        var before = AgentSettings.For(agent);
        var after = change(before);
        if (after == before) return;
        AgentSettings.Set(agent, after);
        _changed.Add(agent);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
