using System.Windows;
using System.Windows.Controls;
using Agmen.Agents;
using Agmen.Hosting;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Label = System.Windows.Controls.Label;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;

namespace Agmen;

public partial class WorkspaceWindow
{
    private void AddSiblingSharingMenu(ContextMenu menu)
    {
        var item = new MenuItem { Header = "Share with sibling…" };
        menu.Items.Add(item);
        menu.Opened += (_, _) => item.Visibility = SelectedTab is { IsEnded: false } source
            && Orchestration.Orchestrator.SiblingsOf(Tabs, source).Any(t => !t.IsEnded)
            ? Visibility.Visible : Visibility.Collapsed;
        item.Click += (_, _) => { if (SelectedTab is { } source) ShowSiblingSharing(source); };
    }

    private void ShowSiblingSharing(HostedTab source)
    {
        var siblings = Orchestration.Orchestrator.SiblingsOf(Tabs, source).Where(t => !t.IsEnded).ToList();
        if (source.IsEnded || siblings.Count == 0) { Notice("There are no active siblings to share with."); return; }
        var canShare = siblings.ToDictionary(t => t, t => Orchestration.Orchestrator.WatchSiblingRelationship(Tabs, source, t));
        var dialog = new Window
        {
            Owner = this, Title = $"Share from {source.Title}", Width = 500, Height = 350,
            MinWidth = 340, MinHeight = 290, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        dialog.SetResourceReference(BackgroundProperty, "PanelBg");
        var panel = new DockPanel { Margin = new Thickness(18) };
        dialog.Content = panel;

        var recipients = new ComboBox { MinHeight = 28 };
        foreach (var sibling in siblings)
            recipients.Items.Add(new ComboBoxItem
            {
                Content = $"{sibling.Title} · {AgentProfiles.DisplayName(sibling.Agent)} · {sibling.Id.ToString()[..8]}", Tag = sibling,
            });
        if (siblings.Count == 1) recipients.SelectedIndex = 0;
        var message = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8),
            Text = source.SelectedText ?? "",
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        message.SetResourceReference(BackgroundProperty, "CardBg");
        message.SetResourceReference(ForegroundProperty, "TextPrimary");
        message.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimary");
        var fields = new StackPanel();
        Label Field(string caption, UIElement target)
        {
            var label = new Label { Content = caption, Target = target, Padding = new Thickness(0, 5, 0, 4) };
            label.SetResourceReference(ForegroundProperty, "TextPrimary");
            return label;
        }
        fields.Children.Add(Field("_To", recipients));
        fields.Children.Add(recipients);
        fields.Children.Add(Field("_Message or selected excerpt", message));
        DockPanel.SetDock(fields, Dock.Top);
        panel.Children.Add(fields);

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "AccentBusy");
        footer.Children.Add(error);
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var send = new Button { Content = "Share", Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
        foreach (var button in new[] { cancel, send })
        {
            button.SetResourceReference(StyleProperty, "ToolButton");
            buttons.Children.Add(button);
        }
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        panel.Children.Add(message);

        void CheckInput() => send.IsEnabled = recipients.SelectedItem != null && !string.IsNullOrWhiteSpace(message.Text);
        recipients.SelectionChanged += (_, _) => CheckInput();
        message.TextChanged += (_, _) => CheckInput();
        CheckInput();
        send.Click += (_, _) =>
        {
            if (recipients.SelectedItem is not ComboBoxItem { Tag: HostedTab to }) return;
            try
            {
                if (!canShare[to]())
                    throw new InvalidOperationException("The selected sessions changed. Reopen Share with sibling to select them again.");
                var result = Orchestrator.QueueSiblingMessage(source, to.Id.ToString(), message.Text);
                dialog.DialogResult = true;
                Notice(result);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
            }
        };
        dialog.ContentRendered += (_, _) => { if (siblings.Count == 1) message.Focus(); else recipients.Focus(); };
        dialog.ShowDialog();
    }
}
