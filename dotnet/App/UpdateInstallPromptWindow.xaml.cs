using System.Windows;

namespace MilestoneDialer;

public partial class UpdateInstallPromptWindow : Window
{
    public bool InstallNow { get; private set; }

    public UpdateInstallPromptWindow()
    {
        InitializeComponent();
    }

    private void InstallNow_Click(object sender, RoutedEventArgs e)
    {
        InstallNow = true;
        DialogResult = true;
    }

    private void InstallLater_Click(object sender, RoutedEventArgs e)
    {
        InstallNow = false;
        DialogResult = true;
    }
}
