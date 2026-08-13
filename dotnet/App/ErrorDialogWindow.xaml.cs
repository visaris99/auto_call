using System.Windows;

namespace MilestoneDialer;

/// <summary>
/// 오류 공통 창 — 코드 배지 + 원인 + 다음 행동 + 관리자 보고 복사.
/// PRODUCT.md: "위험·연결 문제는 한국어로 원인과 다음 행동을 함께 알린다."
/// </summary>
public partial class ErrorDialogWindow : Window
{
    private readonly string _reportText;

    public ErrorDialogWindow(
        string title,
        string code,
        string message,
        string nextAction,
        string reportText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        CodeText.Text = code;
        CodeBadge.Visibility = string.IsNullOrWhiteSpace(code)
            ? Visibility.Collapsed
            : Visibility.Visible;
        MessageText.Text = message;
        NextActionText.Text = nextAction;
        _reportText = reportText;
    }

    /// <summary>관리자에게 전달할 보고 텍스트를 조립한다 (전화번호 등 PII 미포함).</summary>
    public static string BuildReport(string code, string title, string message, string? user)
    {
        return "[Milestone Dialer 오류 보고]\n"
               + $"시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n"
               + $"버전: {Ui.Version}\n"
               + $"코드: {(string.IsNullOrWhiteSpace(code) ? "(없음)" : code)}\n"
               + $"구분: {title}\n"
               + $"내용: {message}\n"
               + $"사용자: {user ?? "(미로그인)"}";
    }

    public static void Show(
        Window? owner,
        string title,
        string code,
        string message,
        string nextAction,
        string? user = null)
    {
        var dialog = new ErrorDialogWindow(
            title, code, message, nextAction,
            BuildReport(code, title, message, user));
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_reportText);
            CopyReportBtn.Content = "복사됨 ✓";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                   or InvalidOperationException)
        {
            CopyReportBtn.Content = "복사 실패";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
