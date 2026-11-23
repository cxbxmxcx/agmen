using System.Windows;
using System.Windows.Controls;
using Agmen.Services;

namespace Agmen;

public partial class NewWorkspaceWindow : Window
{
    public string WorkspaceName { get; private set; } = "";

    public NewWorkspaceWindow()
    {
        InitializeComponent();
        ContentRendered += (_, _) => NameBox.Focus();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (OpenButton is null) return;
        OpenButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        try { WorkspaceName = WorkspaceIdentity.ValidateName(NameBox.Text); }
        catch (ArgumentException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
