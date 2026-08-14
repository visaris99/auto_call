using System.Windows;
using Core;

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

    /// <summary>
    /// 표준 오류 보고(ErrorReport) 표시 — 코드·문구·관리자 보고 텍스트를
    /// Core 카탈로그가 조립한다 (전화번호 등 PII 미포함, 테스트로 형식 잠김).
    /// </summary>
    public static void Show(Window? owner, ErrorReport report, string? user = null)
    {
        var nowKst = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
        var dialog = new ErrorDialogWindow(
            report.Title, report.Code, report.Cause, report.NextAction,
            report.ToReportText(Ui.Version, user, nowKst));
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
