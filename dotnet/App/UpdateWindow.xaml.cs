using System.Windows;

namespace MilestoneDialer;

public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
    }

    public void SetProgress(double percent, string detail)
    {
        Progress.Value = percent;
        DetailText.Text = detail;
    }

    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }
}
