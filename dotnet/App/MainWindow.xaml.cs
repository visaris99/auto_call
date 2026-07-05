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
    private readonly Dictionary<string, ToggleButton> _filterChips = new();
    private string _filter = "ALL";
    private bool _isManualCall;
    private bool _suppressSelection;

    /// <summary>큐 필터 정의: (키, 라벨, 해당 상태들).</summary>
    private static readonly (string Key, string Label, string[] Statuses)[] Filters =
    {
        ("ALL", "전체", Array.Empty<string>()),
        ("INTERESTED", "가망", new[] { "INTERESTED" }),
        ("CALLBACK", "콜백", new[] { "CALLBACK" }),
        ("NEW", "신규", new[] { "NEW", "ASSIGNED" }),
        ("NOANSWER", "부재", new[] { "NOANSWER" }),
        ("CONSULT", "상담중", new[] { "CONSULT" }),
    };

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
        BuildFilterChips();
        UpdateBanner();
        ManualBox.TextChanged += (_, _) =>
        {
            string digits = new(ManualBox.Text.Where(char.IsDigit).ToArray());
            if (digits != ManualBox.Text)
            {
                ManualBox.Text = digits;
                ManualBox.CaretIndex = digits.Length;
            }
        };

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
            var items = await _client.QueueAsync(limit: 100);  // 서버 허용 최대
            SetCrm(true);
            _leads = items;
            RenderQueue();
            var visible = FilteredLeads();
            if (_current == null || visible.All(x => x.Id != _current.Id))
                Select(visible.FirstOrDefault());
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

    // ---------- 필터 ----------

    private void BuildFilterChips()
    {
        foreach (var (key, label, _) in Filters)
        {
            var chip = new ToggleButton
            {
                Style = (Style)FindResource("FilterChip"),
                Content = label,
                IsChecked = key == _filter,
            };
            chip.Click += (_, _) => SelectFilter(key);
            _filterChips[key] = chip;
            FilterPanel.Children.Add(chip);
        }
    }

    private void SelectFilter(string key)
    {
        _filter = key;
        foreach (var (k, chip) in _filterChips)
            chip.IsChecked = k == key;
        RenderQueue();
        var visible = FilteredLeads();
        if (_current == null || visible.All(x => x.Id != _current.Id))
            Select(visible.FirstOrDefault());
    }

    private List<LeadItem> FilteredLeads()
    {
        var statuses = Filters.First(f => f.Key == _filter).Statuses;
        return statuses.Length == 0
            ? _leads
            : _leads.Where(x => statuses.Contains(x.Status)).ToList();
    }

    private void RenderQueue()
    {
        var now = DateTimeOffset.Now;
        var filtered = FilteredLeads();
        var rows = QueueLogic.SortQueue(filtered, now).Select(x => new LeadRow(x, now)).ToList();
        _suppressSelection = true;
        QueueList.ItemsSource = rows;
        if (_current != null)
            QueueList.SelectedItem = rows.FirstOrDefault(r => r.Item.Id == _current.Id);
        _suppressSelection = false;

        QueueCountRun.Text = _filter == "ALL"
            ? $" {_leads.Count}건"
            : $" {filtered.Count}/{_leads.Count}건";
        foreach (var (key, label, statuses) in Filters)
        {
            int count = statuses.Length == 0
                ? _leads.Count
                : _leads.Count(x => statuses.Contains(x.Status));
            _filterChips[key].Content = $"{label} {count}";
        }

        // 콜백 도래 알림은 필터와 무관하게 전체 기준
        foreach (var item in _leads.Where(x => QueueLogic.IsCallbackDue(x, now)
                                               && !_notified.Contains(x.Id)))
        {
            _notified.Add(item.Id);
            var dt = QueueLogic.ParseIso(item.NextCallAt);
            FlashBanner($"재통화 시간: {item.Name} {dt?.ToLocalTime():HH:mm}");
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
        if (_isManualCall)
        {
            // 수동 발신 통화는 CRM 기록 대상이 아님 — 통화시간을 결과 저장에 넘기지 않는다
            _isManualCall = false;
            _talkSeconds = 0;
            TimerText.Text = "00:00";
        }
        HangupBtn.IsEnabled = false;
        DialBtn.IsEnabled = true;
        ManualDialBtn.IsEnabled = true;
    }

    /// <summary>수동 발신 — CRM 리드와 무관, 콜 기록 없음.</summary>
    private async void ManualDial_Click(object sender, RoutedEventArgs e)
    {
        if (_callWatch != null)
            return;
        string phone = ManualBox.Text.Trim();
        if (phone.Length < 8)
        {
            MessageBox.Show("발신할 전화번호를 확인하세요 (숫자만, 8자리 이상).", "수동 발신",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialBtn.IsEnabled = false;
        ManualDialBtn.IsEnabled = false;
        try
        {
            if (!await Task.Run(AdbController.IsConnected))
                throw new InvalidOperationException(
                    "휴대폰이 연결되지 않았습니다.\nUSB 연결과 디버깅 허용을 확인하세요.");
            if (!await Task.Run(() => AdbController.Call(phone)))
                throw new InvalidOperationException("ADB 발신에 실패했습니다.");
            _isManualCall = true;
            _callWatch = Stopwatch.StartNew();
            _todayDials++;
            UpdateToday();
            HangupBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            HandleError(ex);
            DialBtn.IsEnabled = true;
            ManualDialBtn.IsEnabled = true;
        }
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
        if (_isManualCall)
        {
            MessageBox.Show("수동 발신 통화 중에는 결과를 저장할 수 없습니다.\n(수동 발신은 CRM 기록 대상이 아닙니다)",
                "수동 발신", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
            Select(FilteredLeads().FirstOrDefault());
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
