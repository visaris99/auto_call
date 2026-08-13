// 표준 오류 다이얼로그 — 코드 배지 + 원인 + 다음 행동 + 관리자 보고 복사.
// 상담원은 코드를 전화로 읽거나 [관리자 보고 복사]로 전문을 메신저에 붙여넣는다.
using System;
using System.Linq;
using System.Windows;
using Core;

namespace MilestoneDialer;

public partial class ErrorDialog : Window
{
    private readonly ErrorReport _report;
    private readonly string? _account;

    public ErrorDialog(ErrorReport report, string? account)
    {
        InitializeComponent();
        _report = report;
        _account = account;

        CodeText.Text = report.Code;
        TitleText.Text = report.Title;
        CauseText.Text = report.Cause;
        NextActionText.Text = report.NextAction;

        var lines = report.Details
            .Where(d => !string.IsNullOrEmpty(d.Value))
            .Select(d => $"{d.Key}: {d.Value}")
            .ToList();
        if (lines.Count == 0)
            lines.Add("추가 상세 없음");
        DetailItems.ItemsSource = lines;
    }

    /// <summary>표준 오류 다이얼로그 표시. owner가 없으면 화면 중앙.</summary>
    public static void Show(Window? owner, ErrorReport report, string? account = null)
    {
        var dialog = new ErrorDialog(report, account);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        var nowKst = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
        string text = _report.ToReportText(Ui.Version, _account, nowKst);
        try
        {
            Clipboard.SetText(text);
            CopyBtn.Content = "복사됨 — 관리자에게 붙여넣으세요";
        }
        catch (Exception)
        {
            // 클립보드 점유 시에도 보고 자체는 화면에 있으므로 안내만 바꾼다.
            CopyBtn.Content = "복사 실패 — 코드를 직접 전달하세요";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
