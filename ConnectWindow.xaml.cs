using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Agmen.Agents;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace Agmen;

/// <summary>
/// Where the user connects Kimi Code and Antigravity to Agmen, after seeing exactly what that
/// writes to their global config, and disconnects them again.
/// </summary>
public partial class ConnectWindow : Window
{
    private static readonly Brush Green = Frozen(0x3D, 0xDC, 0x84);
    private static readonly Brush Amber = Frozen(0xE5, 0xA5, 0x0A);
    private static readonly Brush Gray = Frozen(0x8E, 0x8E, 0xA8);

    /// <summary>One connectable agent, as the window shows it.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        public required AgentKind Agent { get; init; }
        public AgentConnect.State State { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name => AgentProfiles.DisplayName(Agent);
        public string StateText => AgentConnect.Describe(Agent, State);
        public Brush StateBrush => State switch
        {
            AgentConnect.State.Connected => Green,
            AgentConnect.State.NotInstalled => Gray,
            _ => Amber,
        };
        public string ConnectText => State == AgentConnect.State.ConnectedElsewhere ? "Connect here" : "Connect";
        public bool CanConnect => State is AgentConnect.State.NotConnected or AgentConnect.State.ConnectedElsewhere;
        public bool CanDisconnect => State is AgentConnect.State.Connected or AgentConnect.State.ConnectedElsewhere;
        public string Preview => AgentConnect.Preview(Agent);

        public void Refresh()
        {
            State = AgentConnect.StateOf(Agent);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    /// <param name="reason">Why the window opened by itself, shown above everything else; null when the user opened it.</param>
    public ConnectWindow(string? reason = null)
    {
        InitializeComponent();
        if (reason is not null)
        {
            Reason.Text = reason;
            Reason.Visibility = Visibility.Visible;
        }
        var rows = AgentConnect.Connectable.Select(a => new Row { Agent = a }).ToList();
        rows.ForEach(r => r.Refresh());
        Rows.ItemsSource = rows;
    }

    private void OnConnect(object sender, RoutedEventArgs e) => Change(sender, AgentConnect.Connect);
    private void OnDisconnect(object sender, RoutedEventArgs e) => Change(sender, AgentConnect.Disconnect);
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Change(object sender, Action<AgentKind> change)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row row) return;
        try { change(row.Agent); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Agmen couldn't change {row.Name}'s config, so it left the file as it was.\n\n{ex.Message}",
                "Connect agents", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        row.Refresh();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
