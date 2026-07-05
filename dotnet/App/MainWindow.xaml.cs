// 콜 워크스페이스 — 파이썬 ui/workspace.py와 동일 의미론.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Core;

namespace MilestoneDialer;

public partial class MainWindow : Window
{
    private readonly ApiClient _client;
    private readonly AppConfig _config;
    private readonly PendingCallQueue _pending = new();

    private List<LeadItem> _leads = new();
    private LeadItem? _current;
    private Stopwatch? _callWatch;
    private int _talkSeconds;
    private int _todayDials;
    private int _todayWon;
    private string? _selectedResult;
    private readonly HashSet<string> _notified = new();
    private readonly Dictionary<string, ToggleButton> _resultButtons = new();
    private bool _suppressSelection;

    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _queueTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _adbTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow(ApiClient client, AppConfig config)
    {
        InitializeComponent();
        _client = client;
        _config = config;
        UserText.Text = $"{client.User?.OrgName} · {client.User?.Name}";
        BuildResultButtons();
        UpdateBanner();

        _tickTimer.Tick += (_, _) =>
        {
            if (_callWatch != null)
                TimerText.Text = QueueLogic.FormatSeconds((int)_callWatch.Elapsed.TotalSeconds);
        };
        _queueTimer.Tick += async (_, _) => await RefreshQueueAsync();
        _adbTimer.Tick += async (_, _) => SetAdb(await Task.Run(AdbController.IsConnected));
        _flushTimer.Tick += async (_, _) => await FlushPendingAsync();
        _tickTimer.Start();
        _queueTimer.Start();
        _adbTimer.Start();
        _flushTimer.Start();
        Closed += (_, _) =>
        {
            _tickTimer.Stop(); _queueTimer.Stop(); _adbTimer.Stop(); _flushTimer.Stop();
        };

        Loaded += async (_, _) =>
        {
            await RefreshQueueAsync();
            SetAdb(await Task.Run(AdbController.IsConnected));
            await CheckVersionAsync();
        };
    }

    // ---------- 상단 표시 ----------

    private void UpdateToday() =>
        TodayText.Text = $"오늘: 발신 {_todayDials} · 가입 {_todayWon}";

    private void UpdateBanner()
    {
        int n = _pending.Items.Count;
        BannerText.Text = n > 0 ? $"전송 대기 {n}건" : "";
    }

    private void SetCrm(bool ok) =>
        CrmDot.Foreground = Ui.Brush(ok ? "#1A7F4B" : "#B3372C");

    private void SetAdb(bool ok) =>
        AdbDot.Foreground = Ui.Brush(ok ? "#1A7F4B" : "#B3372C");

    private async void FlashBanner(string message)
    {
        BannerText.Text = message;
        await Task.Delay(5000);
        UpdateBanner();
    }

    // ---------- 큐 ----------

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshQueueAsync();

    private async Task RefreshQueueAsync()
    {
        try
        {
            var items = await _client.QueueAsync();
            SetCrm(true);
            _leads = items;
            RenderQueue();
            if (_current == null || _leads.All(x => x.Id != _current.Id))
                Select(_leads.FirstOrDefault());
        }
        catch (AuthException)
        {
            OnAuthLost();
        }
        catch (ApiException)
        {
            SetCrm(false);
        }
    }

    private void RenderQueue()
    {
        var now = DateTimeOffset.Now;
        var rows = QueueLogic.SortQueue(_leads, now).Select(x => new LeadRow(x, now)).ToList();
        _suppressSelection = true;
        QueueList.ItemsSource = rows;
        if (_current != null)
            QueueList.SelectedItem = rows.FirstOrDefault(r => r.Item.Id == _current.Id);
        _suppressSelection = false;

        foreach (var row in rows.Where(r => r.Due && !_notified.Contains(r.Item.Id)))
        {
            _notified.Add(row.Item.Id);
            FlashBanner($"재통화 시간: {row.Name} {row.TimeText}");
        }
    }

    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || QueueList.SelectedItem is not LeadRow row)
            return;
        if (_callWatch != null)
        {
            // 통화 중에는 리드 전환 금지 → 이전 선택 복원
            _suppressSelection = true;
            QueueList.SelectedItem = (QueueList.ItemsSource as List<LeadRow>)?
                .FirstOrDefault(r => r.Item.Id == _current?.Id);
            _suppressSelection = false;
            return;
        }
        if (row.Item.Id != _current?.Id)
            Select(row.Item);
    }

    private void Select(LeadItem? item)
    {
        _current = item;
        if (item == null)
        {
            NameText.Text = "대기 중인 콜이 없습니다";
            PhoneText.Text = "큐가 비어 있습니다 — 새 배정을 기다리세요";
            LeadMemoText.Text = "";
            StatusBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            NameText.Text = string.IsNullOrEmpty(item.Name) ? "(이름없음)" : item.Name;
            PhoneText.Text = item.PhoneMasked;
            var (bg, fg) = Ui.StatusColors(item.Status);
            StatusBadge.Background = bg;
            StatusBadgeText.Foreground = fg;
            StatusBadgeText.Text = Ui.LabelFor(item.Status);
            StatusBadge.Visibility = Visibility.Visible;
            LeadMemoText.Text = string.IsNullOrEmpty(item.Memo) ? "" : $"리드 메모: {item.Memo}";
        }
        if (QueueList.ItemsSource is List<LeadRow> rows)
        {
            _suppressSelection = true;
            QueueList.SelectedItem = item == null
                ? null
                : rows.FirstOrDefault(r => r.Item.Id == item.Id);
            _suppressSelection = false;
        }
    }

    // ---------- 통화 ----------

    private async void Dial_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _callWatch != null)
            return;
        var lead = _current;
        DialBtn.IsEnabled = false;
        DialBtn.Content = "발신 중…";
        try
        {
            // ADB 확인·발신 모두 백그라운드 — UI 스레드가 멈추지 않게
            if (!await Task.Run(AdbController.IsConnected))
                throw new InvalidOperationException(
                    "휴대폰이 연결되지 않았습니다.\nUSB 연결과 디버깅 허용을 확인하세요.");
            string phone = await _client.RevealAsync(lead.Id);  // 평문은 이 지점에서만
            if (!await Task.Run(() => AdbController.Call(phone)))
                throw new InvalidOperationException("ADB 발신에 실패했습니다.");
            _callWatch = Stopwatch.StartNew();
            _talkSeconds = 0;
            _todayDials++;
            UpdateToday();
            HangupBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            DialBtn.IsEnabled = _callWatch == null;
            DialBtn.Content = "발신 (F1)";
        }
    }

    private async void Hangup_Click(object sender, RoutedEventArgs e)
    {
        if (_callWatch == null)
            return;
        await Task.Run(AdbController.Hangup);
        EndCall();
    }

    private void EndCall()
    {
        if (_callWatch != null)
        {
            _talkSeconds = (int)_callWatch.Elapsed.TotalSeconds;
            _callWatch = null;
        }
        HangupBtn.IsEnabled = false;
        DialBtn.IsEnabled = true;
    }

    // ---------- 결과 기록 ----------

    private void BuildResultButtons()
    {
        foreach (var (code, label, key) in Ui.Results)
        {
            var (bg, fg) = Ui.StatusColors(code);
            var btn = new ToggleButton
            {
                Style = (Style)FindResource("ResultToggle"),
                Background = bg,
                Foreground = fg,
                Height = 48,
                FontSize = 12,
                Margin = new Thickness(3, 0, 3, 0),
                Content = new TextBlock
                {
                    Text = $"{label}\n({key})",
                    TextAlignment = TextAlignment.Center,
                },
            };
            btn.Click += (_, _) => SelectResult(code);
            _resultButtons[code] = btn;
            ResultPanel.Children.Add(btn);
        }
    }

    private void SelectResult(string code)
    {
        _selectedResult = code;
        foreach (var (c, b) in _resultButtons)
            b.IsChecked = c == code;
        CallbackBox.Visibility = code == "CALLBACK" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetForm()
    {
        _selectedResult = null;
        foreach (var b in _resultButtons.Values)
            b.IsChecked = false;
        MemoBox.Text = "";
        CallbackBox.Text = "";
        CallbackBox.Visibility = Visibility.Collapsed;
        _talkSeconds = 0;
        TimerText.Text = "00:00";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
            return;
        if (_selectedResult == null)
        {
            MessageBox.Show("상담 결과를 먼저 선택하세요 (1~7).", "결과 선택",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string? callbackAt = null;
        if (_selectedResult == "CALLBACK")
        {
            callbackAt = QueueLogic.CallbackIso(CallbackBox.Text, DateTimeOffset.Now);
            if (callbackAt == null)
            {
                MessageBox.Show("콜백 시간을 HH:MM 형식으로 입력하세요 (예: 14:30).", "시간 형식",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (_callWatch != null)
            EndCall();

        var lead = _current;
        string code = _selectedResult;
        var payload = new PendingCall(Guid.NewGuid().ToString(), lead.Id, code, _talkSeconds,
            string.IsNullOrWhiteSpace(MemoBox.Text) ? null : MemoBox.Text.Trim(), callbackAt);
        SaveBtn.IsEnabled = false;
        try
        {
            await _client.LogCallAsync(payload.LeadId, payload.ResultCode, payload.TalkSeconds,
                payload.Memo, payload.CallbackAt, payload.IdempotencyKey);
            if (code == "WON")
                _todayWon++;
            UpdateToday();
            ResetForm();
            await RefreshQueueAsync();
        }
        catch (NetworkException)
        {
            _pending.Add(payload);
            UpdateBanner();
            SetCrm(false);
            FlashBanner("연결 실패 — 기록을 대기열에 보관했습니다");
            ResetForm();
            _leads = _leads.Where(x => x.Id != lead.Id).ToList();
            RenderQueue();
            Select(_leads.FirstOrDefault());
        }
        catch (AuthException)
        {
            _pending.Add(payload);
            OnAuthLost();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            SaveBtn.IsEnabled = true;
        }
    }

    private async Task FlushPendingAsync()
    {
        if (_pending.Items.Count == 0)
            return;
        try
        {
            await _pending.FlushAsync(_client);
            UpdateBanner();
        }
        catch (AuthException)
        {
            OnAuthLost();
        }
        catch (ApiException)
        {
            // 다음 주기에 재시도
        }
    }

    // ---------- 공통 ----------

    private void HandleError(Exception ex)
    {
        switch (ex)
        {
            case AuthException:
                OnAuthLost();
                break;
            case NetworkException network:
                SetCrm(false);
                MessageBox.Show(network.Message, "연결 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
            case NightBlockedException night:
                MessageBox.Show(night.Message, "야간 제한", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            case ApiException api:
                MessageBox.Show(api.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
            default:
                App.LogError(ex.ToString());
                MessageBox.Show(ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }

    private void OnAuthLost()
    {
        MessageBox.Show("세션이 만료되었습니다. 다시 로그인해주세요.", "세션 만료",
            MessageBoxButton.OK, MessageBoxImage.Information);
        var login = new LoginWindow();
        Application.Current.MainWindow = login;
        login.Show();
        Close();
    }

    private async Task CheckVersionAsync()
    {
        var info = await _client.CheckVersionAsync();
        if (info?.MinVersion is not { Length: > 0 } min)
            return;
        if (System.Version.TryParse(Ui.Version, out var mine)
            && System.Version.TryParse(min, out var required) && mine < required)
        {
            MessageBox.Show(
                $"이 버전({Ui.Version})은 더 이상 지원되지 않습니다.\n" +
                $"관리자에게 새 버전을 요청하세요. (최신: {info.LatestVersion})",
                "업데이트 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F1:
                Dial_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.F2:
                Hangup_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.F3:
                Save_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
        }
        // 숫자키 1~7 — 입력창에 타이핑 중일 때는 가로채지 않는다
        if (Keyboard.FocusedElement is TextBox or PasswordBox)
            return;
        int index = e.Key switch
        {
            >= Key.D1 and <= Key.D7 => e.Key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad7 => e.Key - Key.NumPad1,
            _ => -1,
        };
        if (index >= 0)
        {
            SelectResult(Ui.Results[index].Code);
            e.Handled = true;
        }
    }
}
