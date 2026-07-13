// 콜 워크스페이스 — 파이썬 ui/workspace.py와 동일 의미론.
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
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
    private readonly CallSessionCoordinator _callSession = new();
    private readonly HashSet<string> _completedLeadIds;

    private List<LeadItem> _leads = new();
    private LeadItem? _current;
    private Stopwatch? _callWatch;
    private int _todayDials;
    private int _todayWon;
    private string? _selectedResult;
    private readonly HashSet<string> _notified = new();
    private readonly Dictionary<string, ToggleButton> _resultButtons = new();
    private readonly Dictionary<string, ToggleButton> _filterChips = new();
    private string _filter = "ALL";
    private bool _suppressSelection;
    private bool _suppressDeviceSelection;
    private bool _sawOffhook;            // 통화 종료 자동 감지: 통화중(2) 관측 후 0이면 종료
    private bool _pollingCallState;
    private bool _serverStats;           // /me/today 사용 가능 여부
    private int _historyToken;
    private int _contactToken;
    private int _clipboardToken;
    private string? _revealedLeadId;
    private string? _revealedPhone;
    private string? _downloadUrl;
    private bool _adbConnected;
    private bool _sendingHeartbeat;
    private bool _refreshingDevices;
    private bool _refreshingQueue;
    private bool _resolvingManualCall;
    private bool _flushingPending;
    private bool _authLost;
    private bool _waitingForExpiredSessionResult;
    private bool _closing;
    private bool _allowClose;
    private string? _adbSerial;
    private string? _lastError;
    private System.Windows.Forms.NotifyIcon? _tray;

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
    private const int QueueFetchLimit = 500;

    private static bool IsSecondaryResult(string code) =>
        code is "APPOINTMENT" or "HANDOFF" or "RISK";

    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _queueTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _adbTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _heartbeatTimer = new() { Interval = TimeSpan.FromSeconds(60) };

    public MainWindow(ApiClient client, AppConfig config)
    {
        InitializeComponent();
        _client = client;
        _config = config;
        _adbSerial = string.IsNullOrWhiteSpace(config.AdbSerial) ? null : config.AdbSerial;
        _completedLeadIds = new HashSet<string>(
            _pending.Items.Select(item => item.LeadId), StringComparer.Ordinal);
        UserText.Text = $"{client.User?.OrgName} · {client.User?.Name}";
        BuildResultButtons();
        BuildFilterChips();
        UpdateBanner();
        // 연속 발신은 별도 운영 승인 전까지 중단한다.
        if (config.AutoDial)
        {
            config.AutoDial = false;
            TrySaveConfig();
        }
        AutoDialCheck.IsChecked = false;
        UpdateCallControls();
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico")),
                Visible = true,
                Text = "Milestone Dialer",
            };
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException)
        {
            // 트레이 아이콘 실패는 무시 — 알림만 못 쓸 뿐
        }

        _tickTimer.Tick += async (_, _) =>
        {
            if (_callWatch != null)
                TimerText.Text = QueueLogic.FormatSeconds((int)_callWatch.Elapsed.TotalSeconds);
            await PollCallStateAsync();
        };
        _queueTimer.Tick += async (_, _) => await RefreshQueueAsync();
        _adbTimer.Tick += async (_, _) => await RefreshAdbDevicesAsync();
        _flushTimer.Tick += async (_, _) => await FlushPendingAsync();
        _heartbeatTimer.Tick += async (_, _) => await SendHeartbeatAsync();
        _tickTimer.Start();
        _queueTimer.Start();
        _adbTimer.Start();
        _flushTimer.Start();
        _heartbeatTimer.Start();
        Closed += (_, _) =>
        {
            _tickTimer.Stop(); _queueTimer.Stop(); _adbTimer.Stop(); _flushTimer.Stop();
            _heartbeatTimer.Stop();
            _tray?.Dispose();
        };

        Loaded += async (_, _) =>
        {
            await RefreshAdbDevicesAsync();
            await SendHeartbeatAsync();
            await RefreshQueueAsync();
            await RefreshTodayAsync();
            await CheckVersionAsync();
        };
    }

    // ---------- 통화 종료 자동 감지 ----------

    private async Task PollCallStateAsync()
    {
        CallSessionSnapshot? session = _callSession.Current;
        if (session?.State is not (CallSessionState.Dialing or CallSessionState.Active)
            || _pollingCallState)
            return;
        _pollingCallState = true;
        try
        {
            int? state = await AdbController.GetCallStateAsync(session.DeviceSerial);
            if (_callSession.Current?.OperationId != session.OperationId || state == null)
                return;
            if (state >= 1)
            {
                _sawOffhook = true;
                _callSession.MarkActive();
                UpdateCallControls();
            }
            else if (_sawOffhook)
            {
                MarkCallEnded();
                FlashBanner("통화 종료 감지 — 결과를 선택하세요");
            }
        }
        finally
        {
            _pollingCallState = false;
        }
    }

    // ---------- 연속 발신 ----------

    private void AutoDial_Toggled(object sender, RoutedEventArgs e)
    {
        if (AutoDialCheck.IsChecked == true)
            AutoDialCheck.IsChecked = false;
        if (_config.AutoDial)
        {
            _config.AutoDial = false;
            TrySaveConfig();
        }
    }

    private void CancelAutoDial()
    {
        // 연속 발신은 2.4.0 실단말 안정화와 운영 승인 전까지 비활성화한다.
    }

    // ---------- 알림 ----------

    private void Notify(string title, string message)
    {
        try
        {
            _tray?.ShowBalloonTip(5000, title, message, System.Windows.Forms.ToolTipIcon.Info);
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 알림 실패는 무시
        }
    }

    // ---------- 상단 표시 ----------

    private void UpdateToday()
    {
        if (!_serverStats)
            TodayText.Text = $"오늘: 발신 {_todayDials} · 가입 {_todayWon}";
    }

    /// <summary>서버 집계(/me/today) — 미구현이면 세션 카운터 유지.</summary>
    private async Task RefreshTodayAsync()
    {
        try
        {
            var stats = await _client.TodayAsync();
            if (stats == null)
                return;
            _serverStats = true;
            int won = stats.ByResult?.GetValueOrDefault("WON") ?? 0;
            TodayText.Text = $"오늘: 발신 {stats.Dials} · 가입 {won}"
                             + $" · 통화 {QueueLogic.FormatSeconds(stats.TalkSeconds)}";
        }
        catch (AuthException)
        {
            OnAuthLost();
        }
        catch (ApiException ex)
        {
            _lastError = ex.Message;
            // 일시 오류 — 다음 갱신에서 재시도
        }
    }

    private void UpdateBanner()
    {
        int n = _pending.Items.Count;
        var parts = new List<string>();
        if (n > 0)
            parts.Add($"전송 대기 {n}건");
        if (_pending.RecoveryFilePath != null)
            parts.Add("손상 대기열 원본 별도 보관");
        if (_pending.LoadError != null)
            parts.Add("전송 대기열 확인 필요");
        BannerText.Text = string.Join(" · ", parts);
        BannerText.ToolTip = _pending.LoadError ?? _pending.RecoveryFilePath;
    }

    private void SetCrm(bool ok) =>
        CrmDot.Foreground = Ui.Brush(ok ? "#1A7F4B" : "#B3372C");

    private void SetAdb(bool ok)
    {
        _adbConnected = ok;
        AdbDot.Foreground = Ui.Brush(ok ? "#1A7F4B" : "#B3372C");
    }

    private async Task RefreshAdbDevicesAsync()
    {
        if (_refreshingDevices)
            return;
        _refreshingDevices = true;
        try
        {
            IReadOnlyList<AdbDeviceInfo> devices = await AdbController.ListDevicesAsync();
            List<AdbDeviceInfo> ready = devices.Where(device => device.IsReady).ToList();
            CallSessionSnapshot? session = _callSession.Current;
            AdbDeviceInfo? selected = session == null
                ? AdbController.ResolveReadyDevice(ready, _adbSerial)
                : ready.FirstOrDefault(device => device.Serial == session.DeviceSerial);

            if (session == null)
                _adbSerial = selected?.Serial;

            _suppressDeviceSelection = true;
            DeviceSelector.ItemsSource = ready;
            DeviceSelector.SelectedItem = selected;
            DeviceSelector.Visibility = ready.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _suppressDeviceSelection = false;

            if (selected != null && session == null && _config.AdbSerial != selected.Serial)
            {
                _config.AdbSerial = selected.Serial;
                TrySaveConfig();
            }

            string detail = selected != null
                ? $"ADB 장치: {selected.Serial}"
                : ready.Count > 1
                    ? "ADB 장치를 선택하세요."
                    : devices.Count > 0
                        ? string.Join(", ", devices.Select(device => $"{device.Serial} ({device.State})"))
                        : "연결된 ADB 장치가 없습니다.";
            AdbDot.ToolTip = detail;
            AdbDot.Text = selected == null ? "● ADB 확인" : "● ADB";
            SetAdb(selected != null);
            UpdateCallControls();
        }
        finally
        {
            _refreshingDevices = false;
        }
    }

    private void DeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeviceSelection)
            return;
        if (_callSession.LocksLeadSelection)
        {
            _suppressDeviceSelection = true;
            DeviceSelector.SelectedItem = (DeviceSelector.ItemsSource as IEnumerable<AdbDeviceInfo>)?
                .FirstOrDefault(device => device.Serial == _callSession.Current?.DeviceSerial);
            _suppressDeviceSelection = false;
            return;
        }
        if (DeviceSelector.SelectedItem is not AdbDeviceInfo selected || !selected.IsReady)
            return;

        _adbSerial = selected.Serial;
        _config.AdbSerial = selected.Serial;
        TrySaveConfig();
        SetAdb(true);
        UpdateCallControls();
    }

    private void TrySaveConfig()
    {
        try
        {
            _config.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastError = $"설정 저장 실패: {ex.Message}";
        }
    }

    private async Task SendHeartbeatAsync()
    {
        if (_sendingHeartbeat)
            return;
        _sendingHeartbeat = true;
        string? lastError = _lastError;
        try
        {
            await _client.HeartbeatAsync(_config.DeviceCode, Ui.Version, _adbConnected, lastError);
            _lastError = null;
        }
        catch (AuthException)
        {
            OnAuthLost();
        }
        catch (NetworkException ex)
        {
            _lastError = ex.Message;
            SetCrm(false);
        }
        catch (ApiException ex)
        {
            _lastError = ex.Message;
        }
        finally
        {
            _sendingHeartbeat = false;
        }
    }

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
        if (_refreshingQueue)
            return;
        _refreshingQueue = true;
        try
        {
            var items = await _client.QueueAllAsync(pageSize: QueueFetchLimit);
            var callableStatuses = Filters.SelectMany(filter => filter.Statuses)
                .ToHashSet(StringComparer.Ordinal);
            SetCrm(true);
            _leads = items
                .Where(item => callableStatuses.Contains(item.Status))
                .ToList();

            CallSessionSnapshot? session = _callSession.Current;
            if (session != null)
            {
                LeadItem? updated = _leads.FirstOrDefault(item => item.Id == session.LeadId);
                if (updated != null)
                    _current = updated;
                RenderQueue();
                return;
            }

            RenderQueue();
            var visible = FilteredLeads();
            if (_current == null || visible.All(x => x.Id != _current.Id))
                Select(FirstSelectableLead(visible));
        }
        catch (AuthException)
        {
            OnAuthLost();
        }
        catch (ApiException ex)
        {
            _lastError = ex.Message;
            SetCrm(false);
        }
        finally
        {
            _refreshingQueue = false;
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
        if (_callSession.LocksLeadSelection)
            return;
        var visible = FilteredLeads();
        if (_current == null || visible.All(x => x.Id != _current.Id))
            Select(FirstSelectableLead(visible));
    }

    private LeadItem? FirstSelectableLead(IEnumerable<LeadItem> items) =>
        items.FirstOrDefault(item => !_completedLeadIds.Contains(item.Id))
        ?? items.FirstOrDefault();

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

        // 콜백 도래 알림은 필터와 무관하게 전체 기준 — 배너 + Windows 알림
        foreach (var item in _leads.Where(x => QueueLogic.IsCallbackDue(x, now)
                                               && !_notified.Contains(x.Id)))
        {
            _notified.Add(item.Id);
            var dt = QueueLogic.ParseIso(item.NextCallAt);
            string message = $"재통화 시간: {item.Name} {dt?.ToLocalTime():HH:mm}";
            FlashBanner(message);
            Notify("재통화 알림", message);
        }
    }

    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || QueueList.SelectedItem is not LeadRow row)
            return;
        if (_callSession.LocksLeadSelection)
        {
            // 발신 시작부터 저장 완료까지 리드 전환 금지 → 이전 선택 복원
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
        if (_callSession.LocksLeadSelection && item?.Id != _callSession.Current?.LeadId)
            return;
        bool changed = item?.Id != _current?.Id;
        _current = item;
        _contactToken++;
        _revealedLeadId = null;
        _revealedPhone = null;
        CancelAutoDial();
        if (changed && !_callSession.LocksLeadSelection)
            ResetForm();
        if (item == null)
        {
            NameText.Text = "대기 중인 콜이 없습니다";
            PhoneText.Text = "큐가 비어 있습니다 — 새 배정을 기다리세요";
            LeadMemoText.Text = "";
            StatusBadge.Visibility = Visibility.Collapsed;
            HistoryList.ItemsSource = null;
            CopyPhoneBtn.IsEnabled = false;
            _historyToken++;
        }
        else
        {
            NameText.Text = string.IsNullOrEmpty(item.Name) ? "(이름없음)" : item.Name;
            PhoneText.Text = item.PhoneMasked;
            CopyPhoneBtn.IsEnabled = false;
            var (bg, fg) = Ui.StatusColors(item.Status);
            StatusBadge.Background = bg;
            StatusBadgeText.Foreground = fg;
            StatusBadgeText.Text = Ui.LabelFor(item.Status);
            StatusBadge.Visibility = Visibility.Visible;
            string memo = string.IsNullOrEmpty(item.Memo) ? "" : $"리드 메모: {item.Memo}";
            LeadMemoText.Text = _completedLeadIds.Contains(item.Id)
                ? $"{memo}{(memo.Length > 0 ? " · " : "")}이번 실행에서 처리 완료"
                : memo;
            LoadHistory(item);
            LoadContact(item, _contactToken);
        }
        UpdateSelectionInList(item);
        UpdateCallControls();
    }

    private async void LoadContact(LeadItem item, int token)
    {
        try
        {
            LeadReveal contact = await _client.RevealLeadAsync(item.Id);
            if (token != _contactToken || _current?.Id != item.Id)
                return;
            _revealedLeadId = item.Id;
            _revealedPhone = contact.Phone;
            if (!string.IsNullOrWhiteSpace(contact.Name))
                NameText.Text = contact.Name;
            PhoneText.Text = QueueLogic.FormatPhone(contact.Phone);
            CopyPhoneBtn.IsEnabled = true;
        }
        catch (AuthException)
        {
            OnAuthLost();
        }
        catch (NetworkException ex)
        {
            _lastError = ex.Message;
            SetCrm(false);
        }
        catch (ApiException ex)
        {
            _lastError = ex.Message;
            CopyPhoneBtn.ToolTip = ex.Message;
        }
    }

    private async void CopyLeadPhone_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _revealedLeadId != _current.Id
            || string.IsNullOrWhiteSpace(_revealedPhone))
            return;
        string phone = _revealedPhone;
        int token = ++_clipboardToken;
        try
        {
            Clipboard.SetText(phone);
            FlashBanner("전화번호를 복사했습니다.");
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (token == _clipboardToken && Clipboard.ContainsText()
                && Clipboard.GetText() == phone)
                Clipboard.Clear();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                   or InvalidOperationException)
        {
            MessageBox.Show("클립보드에 전화번호를 복사하지 못했습니다.", "번호 복사",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateSelectionInList(LeadItem? item)
    {
        if (QueueList.ItemsSource is List<LeadRow> rows)
        {
            _suppressSelection = true;
            QueueList.SelectedItem = item == null
                ? null
                : rows.FirstOrDefault(r => r.Item.Id == item.Id);
            _suppressSelection = false;
        }
    }

    /// <summary>선택 리드의 상담 이력 로드 — 서버 미구현(404)이면 조용히 숨김.</summary>
    private async void LoadHistory(LeadItem item)
    {
        int token = ++_historyToken;
        HistoryList.ItemsSource = null;
        try
        {
            var items = await _client.HistoryAsync(item.Id, 5);
            if (token != _historyToken || items == null)
                return;
            HistoryList.ItemsSource = items.Select(h =>
            {
                var dt = QueueLogic.ParseIso(h.CalledAt);
                string when = dt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "";
                string memo = string.IsNullOrEmpty(h.Memo) ? "" : $" · {h.Memo}";
                return $"{when} · {Ui.LabelFor(h.ResultCode)}{memo}";
            }).ToList();
        }
        catch (ApiException)
        {
            // 이력 로드 실패는 무시 — 표시만 생략
        }
    }

    // ---------- 통화 ----------

    private async void Dial_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _completedLeadIds.Contains(_current.Id))
            return;
        if (_adbSerial == null)
        {
            MessageBox.Show("발신할 Android 장치를 먼저 선택하세요.", "ADB 장치",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LeadItem lead = _current;
        string serial = _adbSerial;
        if (!_callSession.TryBegin(lead.Id, serial, out CallSessionSnapshot? session))
            return;

        CancelAutoDial();
        UpdateCallControls();
        bool attemptAuthorized = false;
        try
        {
            IReadOnlyList<AdbDeviceInfo> devices = await AdbController.ListDevicesAsync();
            if (devices.All(device => device.Serial != serial || !device.IsReady))
                throw new InvalidOperationException(
                    $"선택한 장치({serial})가 연결되지 않았습니다.\nUSB 연결과 디버깅 허용을 확인하세요.");
            CallAttemptResponse attempt = await _client.StartCallAttemptAsync(
                session!.LeadId, _config.DeviceCode, session.DeviceSerial, session.OperationId);
            attemptAuthorized = true;
            if (attempt.AttemptId != session.OperationId || attempt.LeadId != session.LeadId)
                throw new InvalidOperationException("CRM 발신 승인 응답이 요청한 통화와 일치하지 않습니다.");
            if (_callSession.Current?.OperationId != session.OperationId)
                throw new InvalidOperationException("발신 승인 중 통화 세션이 변경되었습니다.");
            if (!_callSession.MarkDialing())
                throw new InvalidOperationException("통화 상태를 시작할 수 없습니다.");
            if (!await AdbController.CallAsync(session.DeviceSerial, attempt.Phone))
                throw new InvalidOperationException("ADB 발신에 실패했습니다.");
            _sawOffhook = false;
            _callWatch = Stopwatch.StartNew();
            _todayDials++;
            UpdateToday();
        }
        catch (Exception ex)
        {
            bool authorizationOutcomeUnknown = ex is NetworkException
                || ex is ApiException { HttpStatus: >= 500 };
            if (attemptAuthorized || authorizationOutcomeUnknown)
                await CancelAttemptQuietlyAsync(session!.OperationId);
            _callSession.FailStart();
            HandleError(ex);
        }
        finally
        {
            UpdateCallControls();
        }
    }

    private async Task CancelAttemptQuietlyAsync(string attemptId)
    {
        try
        {
            await _client.CancelCallAttemptAsync(attemptId);
        }
        catch (ApiException ex)
        {
            _lastError = $"발신 승인 취소 실패: {ex.Message}";
        }
    }

    private async void Hangup_Click(object sender, RoutedEventArgs e)
    {
        await EndActiveCallAsync();
    }

    private async Task<bool> EndActiveCallAsync(bool showError = true)
    {
        CallSessionSnapshot? session = _callSession.Current;
        if (session?.State == CallSessionState.Ended)
            return true;
        if (session == null || !_callSession.TryBeginEnding())
            return false;

        UpdateCallControls();
        bool commandSent = await AdbController.HangupAsync(session.DeviceSerial);
        bool idle = await AdbController.WaitForIdleAsync(session.DeviceSerial);
        if (_callSession.Current?.OperationId != session.OperationId)
            return false;
        if (!idle)
        {
            _callSession.CancelEnding();
            UpdateCallControls();
            if (showError)
            {
                string detail = commandSent
                    ? "종료 명령을 보냈지만 단말의 통화 종료 상태를 확인하지 못했습니다."
                    : "단말에 통화 종료 명령을 보내지 못했습니다.";
                MessageBox.Show($"{detail}\n휴대폰에서 통화를 종료한 뒤 다시 시도하세요.", "통화 종료",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        if (_callSession.State != CallSessionState.Ended)
            MarkCallEnded();
        FlashBanner("통화 종료 확인 — 결과를 선택하세요");
        return _callSession.State == CallSessionState.Ended;
    }

    private void MarkCallEnded()
    {
        int seconds = _callWatch == null ? 0 : (int)_callWatch.Elapsed.TotalSeconds;
        if (!_callSession.MarkEnded(seconds))
            return;
        _callWatch?.Stop();
        _callWatch = null;
        _sawOffhook = false;
        TimerText.Text = QueueLogic.FormatSeconds(seconds);
        UpdateCallControls();
    }

    private async void ManualDial_Click(object sender, RoutedEventArgs e)
    {
        if (_resolvingManualCall || _callSession.State != CallSessionState.Idle)
            return;
        if (!_adbConnected || _adbSerial == null)
        {
            MessageBox.Show("발신할 Android 장치를 먼저 선택하세요.", "수동 발신",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string phone = QueueLogic.PhoneDigits(ManualBox.Text);
        if (phone.Length is < 9 or > 11)
        {
            MessageBox.Show("전화번호 형식을 확인하세요.", "수동 발신",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _resolvingManualCall = true;
        UpdateCallControls();
        try
        {
            LeadItem lead = await _client.ResolveAssignedLeadAsync(phone);
            _completedLeadIds.Remove(lead.Id);
            int index = _leads.FindIndex(item => item.Id == lead.Id);
            if (index >= 0)
                _leads[index] = lead;
            else
                _leads.Add(lead);
            ManualBox.Text = "";
            RenderQueue();
            Select(lead);
            Dial_Click(this, new RoutedEventArgs());
        }
        catch (AuthException)
        {
            OnAuthLost();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            _resolvingManualCall = false;
            UpdateCallControls();
        }
    }

    private void ManualPaste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
                ManualBox.Text = QueueLogic.FormatPhone(Clipboard.GetText());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show("클립보드 내용을 읽지 못했습니다.", "붙여넣기",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateCallControls()
    {
        CallSessionState state = _callSession.State;
        bool idle = state == CallSessionState.Idle;
        bool canEnd = state is CallSessionState.Dialing or CallSessionState.Active;
        bool canSave = state is CallSessionState.Dialing or CallSessionState.Active
            or CallSessionState.Ended;

        DialBtn.IsEnabled = idle && _current != null
            && !_completedLeadIds.Contains(_current.Id)
            && _adbConnected && _adbSerial != null;
        HangupBtn.IsEnabled = canEnd;
        SaveBtn.IsEnabled = canSave;
        QueueList.IsEnabled = idle;
        foreach (ToggleButton chip in _filterChips.Values)
            chip.IsEnabled = idle;
        foreach (ToggleButton resultButton in _resultButtons.Values)
            resultButton.IsEnabled = canSave;
        MemoBox.IsEnabled = canSave;
        CallbackBox.IsEnabled = canSave;
        DeviceSelector.IsEnabled = idle && DeviceSelector.Items.Count > 1;
        ManualBox.IsEnabled = idle && !_resolvingManualCall;
        ManualPasteBtn.IsEnabled = idle && !_resolvingManualCall;
        ManualDialBtn.IsEnabled = idle && !_resolvingManualCall
            && _adbConnected && _adbSerial != null;
        DialBtn.Content = state == CallSessionState.Authorizing ? "확인 중…" : "발신 (F1)";
        HangupBtn.Content = state == CallSessionState.Ending ? "종료 확인 중…" : "종료 (F2)";
        SaveBtn.Content = state == CallSessionState.Saving ? "저장 중…" : "저장하고 다음 (F3)";
    }

    private void ResetCallUi()
    {
        if (_callWatch != null)
        {
            _callWatch.Stop();
            _callWatch = null;
        }
        _sawOffhook = false;
        UpdateCallControls();
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
                    Text = string.IsNullOrEmpty(key) ? label : $"{label}\n({key})",
                    TextAlignment = TextAlignment.Center,
                },
            };
            btn.Click += (_, _) => SelectResult(code);
            _resultButtons[code] = btn;
            if (IsSecondaryResult(code))
                SecondaryResultPanel.Children.Add(btn);
            else
                PrimaryResultPanel.Children.Add(btn);
        }
    }

    private void SelectResult(string code)
    {
        if (_callSession.State is not (CallSessionState.Dialing
            or CallSessionState.Active or CallSessionState.Ended))
            return;
        _selectedResult = code;
        foreach (var (c, b) in _resultButtons)
            b.IsChecked = c == code;
        bool needsTime = code is "CALLBACK" or "APPOINTMENT";
        CallbackPanel.Visibility = needsTime ? Visibility.Visible : Visibility.Collapsed;
        CallbackLabel.Text = code == "APPOINTMENT" ? "상담 예약 시간" : "콜백 예약 시간";
        CallbackHint.Text = code == "APPOINTMENT"
            ? "HH:MM 입력 · 지난 시간은 내일 예약"
            : "HH:MM 입력 · 지난 시간은 내일 콜백";
        CallbackBox.ToolTip = code == "APPOINTMENT"
            ? "예약 시간 (예: 14:30)"
            : "콜백 시간 (예: 14:30)";
        if (needsTime)
            CallbackBox.Focus();
    }

    private void ResetForm()
    {
        _selectedResult = null;
        foreach (var b in _resultButtons.Values)
            b.IsChecked = false;
        MemoBox.Text = "";
        CallbackBox.Text = "";
        CallbackPanel.Visibility = Visibility.Collapsed;
        TimerText.Text = "00:00";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CallSessionSnapshot? currentSession = _callSession.Current;
        if (currentSession == null)
        {
            MessageBox.Show("먼저 선택한 고객에게 발신하세요.", "통화 기록",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (currentSession.State is CallSessionState.Authorizing
            or CallSessionState.Ending or CallSessionState.Saving)
            return;
        if (_selectedResult == null)
        {
            MessageBox.Show("상담 결과를 먼저 선택하세요.", "결과 선택",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string? callbackAt = null;
        string? appointmentAt = null;
        if (_selectedResult == "CALLBACK")
        {
            callbackAt = QueueLogic.LocalTimeIso(CallbackBox.Text, DateTimeOffset.Now);
            if (callbackAt == null)
            {
                MessageBox.Show("콜백 시간을 HH:MM 형식으로 입력하세요 (예: 14:30).", "시간 형식",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (_selectedResult == "APPOINTMENT")
        {
            appointmentAt = QueueLogic.LocalTimeIso(CallbackBox.Text, DateTimeOffset.Now);
            if (appointmentAt == null)
            {
                MessageBox.Show("예약 시간을 HH:MM 형식으로 입력하세요 (예: 14:30).", "시간 형식",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (currentSession.State is CallSessionState.Dialing or CallSessionState.Active)
        {
            if (!await EndActiveCallAsync())
                return;
        }
        if (!_callSession.TryBeginSaving(out CallSessionSnapshot? savingSession))
            return;

        string code = _selectedResult;
        var payload = new PendingCall(
            savingSession!.OperationId,
            savingSession.LeadId,
            code,
            savingSession.TalkSeconds,
            string.IsNullOrWhiteSpace(MemoBox.Text) ? null : MemoBox.Text.Trim(), callbackAt,
            appointmentAt,
            savingSession.OperationId);
        UpdateCallControls();
        try
        {
            await _client.LogCallAttemptAsync(payload.AttemptId!, payload.ResultCode,
                payload.TalkSeconds, payload.Memo, payload.CallbackAt, payload.AppointmentAt);
            if (code == "WON")
                _todayWon++;
            UpdateToday();
            CompleteSavedSession(QueueLogic.CanRedialAfterSavedResult(code, persisted: true));
            await RefreshQueueAsync();
            await RefreshTodayAsync();
        }
        catch (NetworkException)
        {
            if (!TryQueuePending(payload))
                return;
            CompleteSavedSession();
            UpdateBanner();
            SetCrm(false);
            FlashBanner("연결 실패 — 기록을 대기열에 보관했습니다");
            ResumeAuthNavigationAfterResult();
        }
        catch (AuthException)
        {
            if (!TryQueuePending(payload))
                return;
            CompleteSavedSession();
            if (_waitingForExpiredSessionResult)
                ResumeAuthNavigationAfterResult();
            else
                OnAuthLost();
        }
        catch (ApiException ex) when (PendingCallQueue.IsRetryable(ex))
        {
            if (!TryQueuePending(payload))
                return;
            CompleteSavedSession();
            UpdateBanner();
            SetCrm(false);
            FlashBanner("서버 일시 오류 — 기록을 대기열에 보관했습니다");
            ResumeAuthNavigationAfterResult();
        }
        catch (Exception ex)
        {
            _callSession.SaveFailed();
            HandleError(ex);
        }
        finally
        {
            UpdateCallControls();
        }
    }

    private bool TryQueuePending(PendingCall payload)
    {
        try
        {
            _pending.Add(payload);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _callSession.SaveFailed();
            HandleError(new IOException("통화 기록을 로컬 대기열에 보관하지 못했습니다.", ex));
            return false;
        }
    }

    private void CompleteSavedSession(bool allowRedial = false)
    {
        CallSessionSnapshot? completed = _callSession.CompleteSaving();
        if (completed == null)
            return;
        if (allowRedial)
            _completedLeadIds.Remove(completed.LeadId);
        else
            _completedLeadIds.Add(completed.LeadId);
        ResetCallUi();
        ResetForm();
        _leads = _leads.Where(item => item.Id != completed.LeadId).ToList();
        _current = null;
        RenderQueue();
        Select(FirstSelectableLead(FilteredLeads()));
    }

    private void ResumeAuthNavigationAfterResult()
    {
        if (!_waitingForExpiredSessionResult)
            return;
        _waitingForExpiredSessionResult = false;
        _authLost = false;
        OnAuthLost(showMessage: false);
    }

    private async Task FlushPendingAsync()
    {
        if (_pending.Items.Count == 0 || _flushingPending)
            return;
        _flushingPending = true;
        try
        {
            await _pending.FlushAsync(_client);
            UpdateBanner();
        }
        catch (AuthException)
        {
            OnAuthLost();
        }
        catch (ApiException ex)
        {
            _lastError = ex.Message;
            // 다음 주기에 재시도
        }
        finally
        {
            _flushingPending = false;
        }
    }

    // ---------- 공통 ----------

    private void HandleError(Exception ex)
    {
        _lastError = ex.Message;
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

    private async void OnAuthLost(bool showMessage = true)
    {
        if (_authLost || _closing)
            return;
        _authLost = true;
        if (showMessage)
        {
            MessageBox.Show("세션이 만료되었습니다. 다시 로그인해주세요.", "세션 만료",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        for (int i = 0; i < 100 && _callSession.State is (
                 CallSessionState.Authorizing or CallSessionState.Ending or CallSessionState.Saving); i++)
        {
            await Task.Delay(250);
        }
        if (_callSession.State is CallSessionState.Authorizing
            or CallSessionState.Ending or CallSessionState.Saving)
        {
            MessageBox.Show("진행 중인 통화 작업을 완료한 뒤 다시 로그인하세요.", "작업 완료 필요",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _authLost = false;
            return;
        }
        if (_callSession.State is CallSessionState.Dialing or CallSessionState.Active)
        {
            if (!await EndActiveCallAsync(showError: false))
            {
                MessageBox.Show(
                    "단말의 통화 종료를 확인하지 못해 로그인 화면으로 이동하지 않았습니다.\n" +
                    "휴대폰에서 통화를 종료한 뒤 F2를 눌러 다시 확인하세요.",
                    "통화 종료 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
                _authLost = false;
                return;
            }
        }
        if (_callSession.State == CallSessionState.Ended)
        {
            _waitingForExpiredSessionResult = true;
            MessageBox.Show(
                "통화 결과를 선택하고 F3을 누르세요. 결과를 로컬 대기열에 보관한 뒤 로그인 화면으로 이동합니다.",
                "결과 저장 필요", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _callSession.Abandon();
        ResetCallUi();
        await _client.LogoutAsync();
        var login = new LoginWindow();
        Application.Current.MainWindow = login;
        login.Show();
        _allowClose = true;
        Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        if (_closing)
            return;

        CallSessionState state = _callSession.State;
        if (state is CallSessionState.Authorizing or CallSessionState.Ending
            or CallSessionState.Saving)
        {
            MessageBox.Show("진행 중인 작업이 끝난 뒤 다시 종료하세요.", "종료 대기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (state is CallSessionState.Dialing or CallSessionState.Active)
        {
            MessageBoxResult answer = MessageBox.Show(
                "통화를 종료하고 결과를 저장하지 않은 채 앱을 종료하시겠습니까?",
                "통화 중 종료", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
            _closing = true;
            if (!await EndActiveCallAsync())
            {
                _closing = false;
                return;
            }
        }
        else if (state == CallSessionState.Ended)
        {
            MessageBoxResult answer = MessageBox.Show(
                "저장되지 않은 통화 결과가 있습니다. 기록하지 않고 종료하시겠습니까?",
                "미저장 통화", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        _closing = true;
        CallSessionSnapshot? abandonedSession = _callSession.Current;
        if (abandonedSession?.State == CallSessionState.Ended)
            await CancelAttemptQuietlyAsync(abandonedSession.OperationId);
        _callSession.Abandon();
        ResetCallUi();
        await _client.LogoutAsync();
        _allowClose = true;
        Close();
    }

    private async Task CheckVersionAsync()
    {
        var info = await _client.CheckVersionAsync();
        if (info == null || !System.Version.TryParse(Ui.Version, out var mine))
            return;
        _downloadUrl = info.DownloadUrl;
        if (System.Version.TryParse(info.MinVersion, out var required) && mine < required)
        {
            MessageBox.Show(
                $"이 버전({Ui.Version})은 더 이상 지원되지 않습니다.\n" +
                $"관리자에게 새 버전을 요청하세요. (최신: {info.LatestVersion})",
                "업데이트 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (System.Version.TryParse(info.LatestVersion, out var latest) && mine < latest)
        {
            UpdateLinkRun.Text = $"새 버전 v{info.LatestVersion} 받기";
            UpdateLink.Visibility = Visibility.Visible;
        }
    }

    private void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_downloadUrl))
        {
            MessageBox.Show("다운로드 주소가 등록되지 않았습니다.\n관리자에게 새 버전을 요청하세요.",
                "업데이트", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_downloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"브라우저를 열 수 없습니다.\n{_downloadUrl}", "업데이트",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CancelAutoDial();
                e.Handled = true;
                return;
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
        // 숫자키 1~9/0 — 입력창에 타이핑 중일 때는 가로채지 않는다
        if (Keyboard.FocusedElement is TextBox or PasswordBox)
            return;
        int index = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
            Key.D0 => 9,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
            Key.NumPad0 => 9,
            _ => -1,
        };
        if (index >= 0 && index < Ui.Results.Length)
        {
            SelectResult(Ui.Results[index].Code);
            e.Handled = true;
        }
    }
}
