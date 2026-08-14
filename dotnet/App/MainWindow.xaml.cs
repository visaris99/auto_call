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
    private readonly bool _serverInsightsEnabled;

    private List<LeadItem> _leads = new();
    private LeadItem? _current;
    private Stopwatch? _callWatch;
    private int _todayDials;
    private int _todayWon;
    private string? _selectedResult;
    private readonly CallbackNotificationTracker _callbackNotifications = new();
    private readonly Dictionary<string, ToggleButton> _resultButtons = new();
    private readonly Dictionary<string, ToggleButton> _filterChips = new();
    private string _filter = "ALL";
    private bool _suppressSelection;
    private bool _suppressDeviceSelection;
    private bool _sawOffhook;            // 통화 종료 자동 감지: 통화중(2) 관측 후 0이면 종료
    private bool _pollingCallState;
    private bool _serverStats;           // 기능 게이트가 켜진 /me/today 응답 사용 여부
    private int _historyToken;
    private int _contactToken;
    private int _clipboardToken;
    private string? _revealedLeadId;
    private string? _revealedPhone;
    private bool _adbConnected;
    private bool _sendingHeartbeat;
    private bool _refreshingDevices;
    private bool _refreshingQueue;
    private bool _queueLoaded;
    private bool _revealingContact;
    private bool _resolvingManualCall;
    private bool _flushingPending;
    private bool _authLost;
    private bool _waitingForExpiredSessionResult;
    private bool _closing;
    private bool _allowClose;
    private bool _manualEndConfirmed;
    private string? _adbSerial;
    private string? _lastError;
    private string? _notificationLeadId;
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
        CallResultCatalog.IsSpecial(code);

    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _queueTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _adbTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _heartbeatTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _stripTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _stripFlashing;

    internal bool IsCallSessionIdle => _callSession.State == CallSessionState.Idle;

    public MainWindow(ApiClient client, AppConfig config)
    {
        InitializeComponent();
        // 콜백 패널까지 스크롤 없이 보이도록 기본 높이를 키우되(780),
        // 작은 화면(768p 노트북 등)에서는 작업영역 안에 맞춘다.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 12);
        _client = client;
        _config = config;
        _serverInsightsEnabled = AppConfig.IsServerInsightsEnabled();
        _adbSerial = string.IsNullOrWhiteSpace(config.AdbSerial) ? null : config.AdbSerial;
        _completedLeadIds = new HashSet<string>(
            _pending.Items.Select(item => item.LeadId), StringComparer.Ordinal);
        UserText.Text = $"{client.User?.OrgName} · {client.User?.Name}";
        VersionText.Text = $"v{Ui.Version}";
        ThemeBtn.Content = ThemeManager.IsDark ? "라이트" : "다크";
        BuildResultButtons();
        BuildFilterChips();
        InitCallbackTimeOptions();
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
            _tray.BalloonTipClicked += (_, _) =>
                Dispatcher.BeginInvoke(ActivateNotificationLead);
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException)
        {
            // 트레이 아이콘 실패는 무시 — 알림만 못 쓸 뿐
        }

        _tickTimer.Tick += async (_, _) =>
        {
            if (_callWatch != null)
                TimerText.Text = QueueLogic.FormatSeconds((int)_callWatch.Elapsed.TotalSeconds);
            CheckCallbackDue();
            if (_callSession.State == CallSessionState.Ended
                && CallWorkflowPolicy.NeedsScheduledTime(_selectedResult))
                UpdateCallControls();
            await PollCallStateAsync();
        };
        _queueTimer.Tick += async (_, _) => await RefreshQueueAsync();
        _adbTimer.Tick += async (_, _) => await RefreshAdbDevicesAsync();
        _flushTimer.Tick += async (_, _) => await FlushPendingAsync();
        _heartbeatTimer.Tick += async (_, _) => await SendHeartbeatAsync();
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            UpdateBanner();
        };
        _stripTimer.Tick += (_, _) =>
        {
            _stripTimer.Stop();
            _stripFlashing = false;
            UpdateStatusStrip();
        };
        _tickTimer.Start();
        _queueTimer.Start();
        _adbTimer.Start();
        _flushTimer.Start();
        _heartbeatTimer.Start();
        Closed += (_, _) =>
        {
            _tickTimer.Stop(); _queueTimer.Stop(); _adbTimer.Stop(); _flushTimer.Stop();
            _heartbeatTimer.Stop(); _bannerTimer.Stop(); _stripTimer.Stop();
            _tray?.Dispose();
        };

        Loaded += async (_, _) =>
        {
            await RefreshAdbDevicesAsync();
            await SendHeartbeatAsync();
            await RefreshQueueAsync();
            if (_serverInsightsEnabled)
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

    private void Notify(string title, string message, string leadId)
    {
        try
        {
            _notificationLeadId = leadId;
            _tray?.ShowBalloonTip(5000, title, message, System.Windows.Forms.ToolTipIcon.Info);
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 알림 실패는 무시
        }
    }

    private void ActivateNotificationLead()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        if (_notificationLeadId == null || _callSession.LocksLeadSelection)
            return;
        LeadItem? lead = _leads.FirstOrDefault(item => item.Id == _notificationLeadId);
        if (lead == null)
            return;
        if (!FilteredLeads().Any(item => item.Id == lead.Id))
            SelectFilter("ALL");
        Select(lead);
        if (QueueList.SelectedItem != null)
            QueueList.ScrollIntoView(QueueList.SelectedItem);
    }

    // ---------- 상단 표시 ----------

    private void UpdateToday()
    {
        if (!_serverStats)
            TodayText.Text = $"오늘: 발신 {_todayDials} · 가입 {_todayWon}";
    }

    /// <summary>기능 게이트가 켜진 경우에만 서버 집계(/me/today)를 사용한다.</summary>
    private async Task RefreshTodayAsync()
    {
        if (!_serverInsightsEnabled)
            return;
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
            _lastError = FormatLastError(ex);
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
        BannerText.Foreground = Ui.Token("B.DangerText");
        BannerText.ToolTip = _pending.LoadError ?? _pending.RecoveryFilePath;
    }

    private bool? _crmOk;
    private bool _adbCheckedOnce;

    private void SetCrm(bool ok)
    {
        // 색과 텍스트를 함께 바꾼다 — 상태를 색상에만 의존해 전달하지 않는다(PRODUCT.md).
        _crmOk = ok;
        CrmDot.Foreground = Ui.Token(ok ? "B.SuccessText" : "B.DangerText");
        CrmDot.Text = ok ? "● CRM" : "● CRM 끊김";
        CrmDot.ToolTip = ok
            ? "CRM 연결 정상"
            : "CRM 서버에 연결할 수 없습니다 — 저장 못 한 기록은 대기열에 보관됩니다";
    }

    private void SetAdb(bool ok)
    {
        _adbConnected = ok;
        _adbCheckedOnce = true;
        AdbDot.Foreground = Ui.Token(ok ? "B.SuccessText" : "B.DangerText");
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
            AdbHelpText.Text = selected != null
                ? $"ADB 연결됨 · {selected.Serial}"
                : devices.Any(device => device.State.Equals(
                    "unauthorized", StringComparison.OrdinalIgnoreCase))
                    ? "휴대폰 화면의 USB 디버깅 허용을 눌러주세요"
                    : ready.Count > 1
                        ? "발신에 사용할 ADB 장치를 선택해주세요"
                        : devices.Count == 0
                            ? "USB 연결 후 개발자 옵션>USB 디버깅 켜기"
                            : "USB 연결 상태와 ADB 장치를 확인해주세요";
            AdbHelpText.Foreground = Ui.Token(selected != null ? "B.SuccessText" : "B.DangerText");
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
        AdbHelpText.Text = $"ADB 연결됨 · {selected.Serial}";
        AdbHelpText.Foreground = Ui.Token("B.SuccessText");
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
            _lastError = FormatLastError(ex);
            SetCrm(false);
        }
        catch (ApiException ex)
        {
            _lastError = FormatLastError(ex);
        }
        finally
        {
            _sendingHeartbeat = false;
        }
    }

    /// <summary>상단 배너 일시 표시. 성공은 초록, 문제는 빨강 — 5초 뒤 대기열 상태로 복귀.</summary>
    private void FlashBanner(string message, string colorToken = "B.DangerText")
    {
        BannerText.Text = message;
        BannerText.Foreground = Ui.Token(colorToken);
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    /// <summary>상태 스트립 일시 피드백(저장 결과·저장 불가 안내) — 4초 뒤 통화 상태 표시로 복귀.</summary>
    private void StripFlash(string message, bool success = false)
    {
        _stripFlashing = true;
        StatusStripText.Text = message;
        StatusStrip.Background = Ui.Token(success ? "B.SuccessSoft" : "B.DangerSoft");
        StatusStripText.Foreground = Ui.Token(success ? "B.SuccessText" : "B.DangerText");
        _stripTimer.Stop();
        _stripTimer.Start();
    }

    /// <summary>통화 수명주기를 항상 문장으로 보여주는 상시 스트립.</summary>
    private void UpdateStatusStrip()
    {
        if (_stripFlashing)
            return;
        var (text, bg, fg) = _callSession.State switch
        {
            CallSessionState.Authorizing => ("CRM 발신 승인 확인 중…", "B.BrandSoft", "B.Ink"),
            CallSessionState.Dialing => ("발신 중 — 고객 응답 대기", "B.SuccessSoft", "B.SuccessText"),
            CallSessionState.Active => ("통화 중 — 결과를 미리 선택해두세요", "B.SuccessSoft", "B.SuccessText"),
            CallSessionState.Ending => ("통화 종료 확인 중…", "B.BrandSoft", "B.Ink"),
            CallSessionState.Ended => ("통화 종료 — 결과 선택 후 저장 (F3)", "B.BrandSoft", "B.Ink"),
            CallSessionState.Saving => ("저장 중…", "B.BrandSoft", "B.Ink"),
            _ => ("대기 — 리드 선택 후 발신 (F1)", "B.Surface2", "B.MutedText"),
        };
        StatusStripText.Text = text;
        StatusStrip.Background = Ui.Token(bg);
        StatusStripText.Foreground = Ui.Token(fg);
    }

    internal void ShowDeferredUpdateNotice() =>
        FlashBanner("업데이트는 통화 결과 저장 후 설치됩니다.", "B.Ink");

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
            _queueLoaded = true;

            CallSessionSnapshot? session = _callSession.Current;
            if (session != null)
            {
                LeadItem? updated = _leads.FirstOrDefault(item => item.Id == session.LeadId);
                if (updated != null)
                    _current = updated;
                RenderQueue();
                CheckCallbackDue();
                return;
            }

            RenderQueue();
            CheckCallbackDue();
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
            _lastError = FormatLastError(ex);
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
        QueueLogic.FirstSelectableLead(items, _completedLeadIds);

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

    }

    private void CheckCallbackDue()
    {
        if (!_queueLoaded)
            return;
        DateTimeOffset now = DateTimeOffset.Now;
        IReadOnlyList<CallbackNotification> notifications =
            _callbackNotifications.Check(_leads, now);
        if (notifications.Any(notification =>
                notification.Kind == CallbackNotificationKind.Due))
            RenderQueue();

        foreach (CallbackNotification notification in notifications)
        {
            string title;
            string message;
            if (notification.Kind == CallbackNotificationKind.StartupSummary)
            {
                DateTimeOffset? oldest = QueueLogic.ParseIso(notification.Target.NextCallAt);
                string oldestText = oldest?.ToOffset(now.Offset).ToString("HH:mm") ?? "--:--";
                title = "지난 콜백 알림";
                message = $"지난 콜백 {notification.Count}건 · 가장 오래된 {oldestText}";
            }
            else
            {
                title = notification.Kind == CallbackNotificationKind.Reminder
                    ? "콜백 재알림"
                    : "콜백 알림";
                message = $"{QueueLogic.MaskName(notification.Target.Name)} · "
                          + $"{QueueLogic.FormatCallbackTime(notification.Target.NextCallAt, now)}";
            }
            FlashBanner(message);
            Notify(title, message, notification.Target.Id);
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
        _completedLeadIds.Remove(row.Item.Id);
        if (row.Item.Id != _current?.Id)
            Select(row.Item);
        else
            UpdateCallControls();
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
        NameEditPanel.Visibility = Visibility.Collapsed;
        NameEditBox.Text = "";
        if (item == null)
        {
            NameText.Text = "대기 중인 콜이 없습니다";
            PhoneText.Text = "큐가 비어 있습니다 — 새 배정을 기다리세요";
            LeadMemoText.Text = "";
            StatusBadge.Visibility = Visibility.Collapsed;
            EditNameBtn.Visibility = Visibility.Collapsed;
            HistoryList.ItemsSource = null;
            RevealContactBtn.IsEnabled = false;
            RevealContactBtn.Content = "연락처 보기";
            CopyPhoneBtn.IsEnabled = false;
            _historyToken++;
        }
        else
        {
            NameText.Text = string.IsNullOrEmpty(item.Name) ? "(이름없음)" : item.Name;
            PhoneText.Text = item.PhoneMasked;
            RevealContactBtn.IsEnabled = true;
            RevealContactBtn.Content = "연락처 보기";
            CopyPhoneBtn.IsEnabled = false;
            var (bg, fg) = Ui.StatusColors(item.Status);
            StatusBadge.Background = bg;
            StatusBadgeText.Foreground = fg;
            StatusBadgeText.Text = Ui.LabelFor(item.Status);
            StatusBadge.Visibility = Visibility.Visible;
            EditNameBtn.Visibility = Visibility.Visible;
            string memo = string.IsNullOrEmpty(item.Memo)
                ? "리드 메모 · 고객에게 계속 남음: 없음"
                : $"리드 메모 · 고객에게 계속 남음: {item.Memo}";
            LeadMemoText.Text = _completedLeadIds.Contains(item.Id)
                ? $"{memo} · 이번 실행에서 처리 완료"
                : memo;
            if (_serverInsightsEnabled)
                LoadHistory(item);
            else
                HistoryList.ItemsSource = null;
        }
        UpdateSelectionInList(item);
        UpdateCallControls();
    }

    private async void RevealContact_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _revealingContact)
            return;
        await LoadContactAsync(_current, _contactToken);
    }

    private async Task LoadContactAsync(LeadItem item, int token)
    {
        _revealingContact = true;
        RevealContactBtn.IsEnabled = false;
        RevealContactBtn.Content = "확인 중…";
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
            RevealContactBtn.Content = "연락처 확인됨";
        }
        catch (AuthException)
        {
            OnAuthLost();
        }
        catch (NetworkException ex)
        {
            _lastError = FormatLastError(ex);
            SetCrm(false);
        }
        catch (ApiException ex)
        {
            _lastError = FormatLastError(ex);
            RevealContactBtn.ToolTip = $"{ex.Message} ({ex.Code})";
        }
        finally
        {
            _revealingContact = false;
            if (_current?.Id == item.Id && _revealedLeadId != item.Id)
            {
                RevealContactBtn.Content = "연락처 보기";
                RevealContactBtn.IsEnabled = true;
            }
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
            FlashBanner("전화번호를 복사했습니다.", "B.SuccessText");
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (token == _clipboardToken && Clipboard.ContainsText()
                && Clipboard.GetText() == phone)
                Clipboard.Clear();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                   or InvalidOperationException)
        {
            _lastError = $"[{ErrorCatalog.AppClipboard}] 클립보드 복사 실패: {ex.GetType().Name}";
            FlashBanner("클립보드에 전화번호를 복사하지 못했습니다");
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

    /// <summary>기능 게이트가 켜진 경우에만 선택 리드의 상담 이력을 로드한다.</summary>
    private async void LoadHistory(LeadItem item)
    {
        if (!_serverInsightsEnabled)
        {
            _historyToken++;
            HistoryList.ItemsSource = null;
            return;
        }
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

    // ---------- 이름 수정 ----------

    private void EditNameBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
            return;
        NameEditBox.Text = NameText.Text;
        NameSaveBtn.IsEnabled = !string.IsNullOrWhiteSpace(NameEditBox.Text);
        NameEditPanel.Visibility = Visibility.Visible;
        NameEditBox.Focus();
        NameEditBox.SelectAll();
    }

    private void NameEditBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        NameSaveBtn.IsEnabled = !string.IsNullOrWhiteSpace(NameEditBox.Text);
    }

    private void NameCancel_Click(object sender, RoutedEventArgs e)
    {
        NameEditPanel.Visibility = Visibility.Collapsed;
        NameEditBox.Text = "";
    }

    private async void NameSave_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null)
            return;
        string name = NameEditBox.Text.Trim();
        if (name.Length == 0)
            return;
        var lead = _current;
        NameSaveBtn.IsEnabled = false;
        NameCancelBtn.IsEnabled = false;
        try
        {
            await _client.UpdateLeadNameAsync(lead.Id, name);
            var updated = lead with { Name = name };
            _current = updated;
            _leads = _leads.Select(x => x.Id == lead.Id ? updated : x).ToList();
            NameText.Text = name;
            NameEditPanel.Visibility = Visibility.Collapsed;
            NameEditBox.Text = "";
            RenderQueue();
            FlashBanner("이름 저장됨", "B.SuccessText");
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
            NameCancelBtn.IsEnabled = true;
            NameSaveBtn.IsEnabled = !string.IsNullOrWhiteSpace(NameEditBox.Text);
        }
    }

    // ---------- 통화 ----------

    private async void Dial_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _completedLeadIds.Contains(_current.Id))
            return;
        if (_adbSerial == null)
        {
            StripFlash("발신할 Android 장치를 먼저 연결·선택하세요");
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
        _manualEndConfirmed = false;
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
            int? observedState = await AdbController.GetCallStateAsync(session.DeviceSerial);
            if (_callSession.Current?.OperationId != session.OperationId)
                return false;
            if (observedState == 0)
                idle = true;
            else if (CallWorkflowPolicy.CanOfferManualEndConfirmation(observedState))
            {
                MessageBoxResult answer = MessageBox.Show(
                    "휴대폰에서 통화가 이미 끝났다면 결과 저장을 진행할까요?",
                    "통화 종료 상태 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    MarkCallEnded();
                    _manualEndConfirmed = _callSession.State == CallSessionState.Ended;
                    if (_manualEndConfirmed)
                        return true;
                }
            }
        }
        if (!idle)
        {
            _callSession.CancelEnding();
            UpdateCallControls();
            if (showError)
            {
                string adbCode = commandSent ? ErrorCatalog.AdbStateUnknown : ErrorCatalog.AdbHangupFailed;
                string detail = commandSent
                    ? "종료 명령을 보냈지만 단말의 통화 종료 상태를 확인하지 못했습니다."
                    : "단말에 통화 종료 명령을 보내지 못했습니다. USB 연결이 끊겼을 수 있습니다.";
                _lastError = $"[{adbCode}] {detail}";
                ErrorDialogWindow.Show(this, ErrorCatalog.Adb(adbCode, detail,
                    "휴대폰에서 통화를 종료한 뒤 다시 시도하세요. 반복되면 USB 케이블을 다시 연결하고 관리자에게 전달하세요.",
                    _adbSerial), ReportUser);
            }
            return false;
        }

        if (_callSession.State != CallSessionState.Ended)
            MarkCallEnded();
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
            StripFlash("발신할 Android 장치를 먼저 연결·선택하세요");
            return;
        }
        string phone = QueueLogic.PhoneDigits(ManualBox.Text);
        if (phone.Length is < 9 or > 11)
        {
            StripFlash("전화번호 형식을 확인하세요 (9~11자리 숫자)");
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
            StripFlash("클립보드 내용을 읽지 못했습니다");
        }
    }

    private void UpdateCallControls()
    {
        CallSessionState state = _callSession.State;
        bool idle = state == CallSessionState.Idle;
        bool canEnd = state is CallSessionState.Dialing or CallSessionState.Active;
        // 결과 선택/메모/콜백 시간 입력은 통화 중에도 미리 할 수 있다 (현행 유지).
        bool canPickResult = state is CallSessionState.Dialing or CallSessionState.Active
            or CallSessionState.Ended;
        // 저장 "실행"은 통화가 실제로 종료(Ended)된 뒤, 결과와 필요한 예약 시간이 채워졌을 때만 가능.
        bool scheduleValid = !CallWorkflowPolicy.NeedsScheduledTime(_selectedResult)
            || CurrentScheduledTime(DateTimeOffset.Now).IsValid;
        bool canExecuteSave = CallWorkflowPolicy.CanSave(
            state, _selectedResult, scheduleValid);

        DialBtn.IsEnabled = idle && _current != null
            && !_completedLeadIds.Contains(_current.Id)
            && _adbConnected && _adbSerial != null;
        HangupBtn.IsEnabled = canEnd;
        SaveBtn.IsEnabled = canExecuteSave;
        QueueList.IsEnabled = idle;
        foreach (ToggleButton chip in _filterChips.Values)
            chip.IsEnabled = idle;
        foreach (ToggleButton resultButton in _resultButtons.Values)
            resultButton.IsEnabled = canPickResult;
        MemoBox.IsEnabled = canPickResult;
        CallbackHourBox.IsEnabled = canPickResult;
        CallbackMinuteBox.IsEnabled = canPickResult;
        CallbackDatePicker.IsEnabled = canPickResult;
        DeviceSelector.IsEnabled = idle && DeviceSelector.Items.Count > 1;
        ManualBox.IsEnabled = idle && !_resolvingManualCall;
        ManualPasteBtn.IsEnabled = idle && !_resolvingManualCall;
        ManualDialBtn.IsEnabled = idle && !_resolvingManualCall
            && _adbConnected && _adbSerial != null;
        DialBtn.Content = state == CallSessionState.Authorizing ? "확인 중…" : "발신 (F1)";
        HangupBtn.Content = state == CallSessionState.Ending ? "종료 확인 중…" : "종료 (F2)";
        SaveBtn.Content = state == CallSessionState.Saving ? "저장 중…" : "저장하고 다음 (F3)";
        UpdateStatusStrip();
    }

    private ScheduledTimeResult CurrentScheduledTime(DateTimeOffset now)
    {
        DateOnly? date = CallbackDatePicker.SelectedDate is DateTime selected
            ? DateOnly.FromDateTime(selected)
            : null;
        return QueueLogic.ScheduledLocalTime(date, SelectedCallbackTimeText(), now);
    }

    /// <summary>시·분 드롭다운 선택을 Core 계약(HH:mm)으로 변환. 미선택이면 빈 문자열.</summary>
    private string SelectedCallbackTimeText() =>
        CallbackHourBox.SelectedItem is string hour
        && CallbackMinuteBox.SelectedItem is string minute
            ? $"{hour}:{minute}"
            : "";

    private void InitCallbackTimeOptions()
    {
        CallbackHourBox.ItemsSource =
            Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray();
        CallbackMinuteBox.ItemsSource =
            Enumerable.Range(0, 12).Select(m => (m * 5).ToString("00")).ToArray();
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
            var content = new TextBlock
            {
                Text = string.IsNullOrEmpty(key) ? label : $"{label}\n({key})",
                TextAlignment = TextAlignment.Center,
                Foreground = fg,
            };
            var btn = new ToggleButton
            {
                Style = (Style)FindResource("ResultToggle"),
                Background = bg,
                MinHeight = 48,
                FontSize = 12,
                Margin = new Thickness(3, 0, 3, 0),
                Content = content,
            };
            // Content TextBlock은 버튼의 논리 트리 자식이라 템플릿 트리거의 글자색이
            // 상속되지 않는다. 체크 상태 전환 시 직접 색을 바꿔 잉크 배경 위에서도 읽히게 한다.
            btn.Checked += (_, _) => content.Foreground = Ui.Token("B.OnInk");
            btn.Unchecked += (_, _) => content.Foreground = fg;
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
        bool needsTime = CallWorkflowPolicy.NeedsScheduledTime(code);
        CallbackPanel.Visibility = needsTime ? Visibility.Visible : Visibility.Collapsed;
        CallbackLabel.Text = code == "APPOINTMENT" ? "상담예약" : "콜백 예약";
        if (needsTime)
        {
            // 기본값: 1시간 뒤 정시 (자정을 넘기면 날짜도 다음날로). 이미 고른 값은 유지.
            DateTime defaultTarget = DateTime.Now.AddHours(1);
            CallbackDatePicker.SelectedDate ??= defaultTarget.Date;
            if (CallbackHourBox.SelectedItem == null)
                CallbackHourBox.SelectedItem = defaultTarget.Hour.ToString("00");
            if (CallbackMinuteBox.SelectedItem == null)
                CallbackMinuteBox.SelectedItem = "00";
            CallbackHourBox.Focus();
            // 패널이 접힘선 아래에 걸리지 않게 레이아웃 계산 후 화면 안으로 스크롤.
            Dispatcher.BeginInvoke(
                new Action(() => CallbackPanel.BringIntoView()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        UpdateCallControls();
    }

    private void CallbackTime_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        UpdateCallControls();

    private void CallbackPreset30m_Click(object sender, RoutedEventArgs e) =>
        ApplyCallbackPreset(DateTime.Now.AddMinutes(30));

    private void CallbackPreset2h_Click(object sender, RoutedEventArgs e) =>
        ApplyCallbackPreset(DateTime.Now.AddHours(2));

    private void CallbackPresetTomorrow_Click(object sender, RoutedEventArgs e) =>
        ApplyCallbackPreset(DateTime.Today.AddDays(1).AddHours(10));

    private void ApplyCallbackPreset(DateTime target)
    {
        int rounded = (target.Minute + 4) / 5 * 5;   // 드롭다운 항목에 맞춰 5분 단위 올림
        target = target.AddMinutes(rounded - target.Minute);
        CallbackDatePicker.SelectedDate = target.Date;
        CallbackHourBox.SelectedItem = target.Hour.ToString("00");
        CallbackMinuteBox.SelectedItem = target.Minute.ToString("00");
        UpdateCallControls();
    }

    private void ManualBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        ManualDial_Click(this, new RoutedEventArgs());
    }

    private void HelpOverlay_MouseDown(object sender, MouseButtonEventArgs e) =>
        HelpOverlay.Visibility = Visibility.Collapsed;

    private void CallbackDatePicker_SelectedDateChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        UpdateCallControls();

    private void ResetForm()
    {
        _selectedResult = null;
        foreach (var b in _resultButtons.Values)
            b.IsChecked = false;
        MemoBox.Text = "";
        CallbackHourBox.SelectedItem = null;
        CallbackMinuteBox.SelectedItem = null;
        CallbackDatePicker.SelectedDate = null;
        CallbackPanel.Visibility = Visibility.Collapsed;
        TimerText.Text = "00:00";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CallSessionSnapshot? currentSession = _callSession.Current;
        if (currentSession == null)
        {
            StripFlash("먼저 선택한 고객에게 발신하세요");
            return;
        }
        if (currentSession.State is CallSessionState.Authorizing
            or CallSessionState.Ending or CallSessionState.Saving)
            return;
        if (currentSession.State is CallSessionState.Dialing or CallSessionState.Active)
        {
            // 저장은 통화종료(Ended)에서만 실행 — 통화를 끊지 않고 인라인 안내만 표시한다.
            StripFlash("통화를 먼저 종료(F2)한 뒤 저장하세요");
            return;
        }
        if (currentSession.State != CallSessionState.Ended)
            return;
        if (_selectedResult == null)
        {
            StripFlash("상담 결과를 먼저 선택하세요");
            return;
        }
        string? callbackAt = null;
        string? appointmentAt = null;
        if (CallWorkflowPolicy.NeedsScheduledTime(_selectedResult))
        {
            ScheduledTimeResult scheduled = CurrentScheduledTime(DateTimeOffset.Now);
            if (!scheduled.IsValid)
            {
                string subject = _selectedResult == "APPOINTMENT" ? "상담예약" : "콜백";
                StripFlash(scheduled.Error switch
                {
                    ScheduledTimeError.MissingDate => $"{subject} 날짜를 선택하세요",
                    ScheduledTimeError.NotFuture => $"{subject} 시간이 이미 지났습니다 — 다시 선택하세요",
                    _ => $"{subject} 시와 분을 선택하세요",
                });
                return;
            }
            if (_selectedResult == "CALLBACK")
                callbackAt = scheduled.Iso;
            else
                appointmentAt = scheduled.Iso;
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
            CallResponse saved = await _client.LogCallAttemptAsync(
                payload.AttemptId!, payload.ResultCode,
                payload.TalkSeconds, payload.Memo, payload.CallbackAt, payload.AppointmentAt);
            if (saved.Lead.Id != savingSession.LeadId)
                throw new InvalidOperationException("CRM 저장 응답이 현재 통화 고객과 일치하지 않습니다.");
            if (code == "WON")
                _todayWon++;
            UpdateToday();
            CompleteSavedSession(code, showSavedFeedback: true);
            await RefreshQueueAsync();
            if (_serverInsightsEnabled)
                await RefreshTodayAsync();
        }
        catch (NetworkException)
        {
            if (!TryQueuePending(payload))
                return;
            CompleteSavedSession(code);
            UpdateBanner();
            SetCrm(false);
            StripFlash("연결 실패 — 기록을 대기열에 보관했습니다 (자동 재전송)");
            ResumeAuthNavigationAfterResult();
        }
        catch (AuthException)
        {
            if (!TryQueuePending(payload))
                return;
            CompleteSavedSession(code);
            if (_waitingForExpiredSessionResult)
                ResumeAuthNavigationAfterResult();
            else
                OnAuthLost();
        }
        catch (ApiException ex) when (PendingCallQueue.IsRetryable(ex))
        {
            if (!TryQueuePending(payload))
                return;
            CompleteSavedSession(code);
            UpdateBanner();
            SetCrm(false);
            StripFlash("서버 일시 오류 — 기록을 대기열에 보관했습니다 (자동 재전송)");
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

    private void CompleteSavedSession(string resultCode, bool showSavedFeedback = false)
    {
        string feedback = $"{QueueLogic.MaskName(NameText.Text)} · "
                          + $"{Ui.LabelFor(resultCode)} 저장됨";
        CallSessionSnapshot? completed = _callSession.CompleteSaving();
        if (completed == null)
            return;
        _completedLeadIds.Add(completed.LeadId);
        ResetCallUi();
        ResetForm();
        _leads = _leads.Where(item => item.Id != completed.LeadId).ToList();
        _current = null;
        RenderQueue();
        Select(FirstSelectableLead(FilteredLeads()));
        if (showSavedFeedback)
            StripFlash(feedback, success: true);
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
            _lastError = FormatLastError(ex);
            // 다음 주기에 재시도
        }
        finally
        {
            _flushingPending = false;
        }
    }

    // ---------- 공통 ----------

    /// <summary>하트비트 lastError 정규화 — CRM 예외는 항상 [코드] 접두(운영 집계용).</summary>
    private static string FormatLastError(Exception ex) =>
        ex is ApiException api ? $"[{ErrorCatalog.FromApi(api).Code}] {api.Message}" : ex.Message;

    /// <summary>관리자 보고의 계정 표기 — 미로그인 상태면 생략.</summary>
    private string? ReportUser =>
        _client.User is { } u ? $"{u.OrgName} · {u.Name}" : null;

    private void HandleError(Exception ex)
    {
        switch (ex)
        {
            case AuthException:
                _lastError = FormatLastError(ex);
                OnAuthLost();
                break;
            case NetworkException network:
                SetCrm(false);
                ShowApiError(network,
                    "인터넷 연결과 CRM 서버 상태를 확인한 뒤 다시 시도하세요. "
                    + "저장하지 못한 통화 기록은 대기열에 보관돼 자동 재전송됩니다.");
                break;
            case NightBlockedException night:
                ShowApiError(night,
                    "야간에는 발신할 수 없습니다. 콜백예약으로 다음 영업시간에 다시 연락하세요.");
                break;
            case DncBlockedException dnc:
                // 사용자에게 새로고침을 시키는 대신 앱이 직접 큐를 갱신한다.
                _ = RefreshQueueAsync();
                ShowApiError(dnc, "큐를 자동으로 새로고침했습니다. 다음 리드로 진행하세요.");
                break;
            case ApiException api:
                ShowApiError(api, nextActionOverride: null);
                break;
            default:
                App.LogError(ex.ToString());
                _lastError = $"[{ErrorCatalog.AppUnhandled}] {ex.Message}";
                ErrorDialogWindow.Show(this, ErrorCatalog.App(
                    ErrorCatalog.AppUnhandled,
                    ex.Message,
                    "앱은 계속 사용할 수 있습니다. 같은 문제가 반복되면 "
                    + "'관리자 보고 복사'로 내용을 전달하세요.",
                    ex.GetType().Name), ReportUser);
                break;
        }
    }

    /// <summary>카탈로그 보고 표시 — 원인·코드는 카탈로그 고정, 다음 행동만 화면 맥락으로 덧입힌다.</summary>
    private void ShowApiError(ApiException ex, string? nextActionOverride)
    {
        ErrorReport report = ErrorCatalog.FromApi(ex);
        if (nextActionOverride != null)
            report = report with { NextAction = nextActionOverride };
        _lastError = $"[{report.Code}] {ex.Message}";
        ErrorDialogWindow.Show(this, report, ReportUser);
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        bool dark = !ThemeManager.IsDark;
        ThemeManager.Apply(dark);
        _config.Theme = dark ? "dark" : "light";
        TrySaveConfig();
        ThemeBtn.Content = dark ? "라이트" : "다크";
        RefreshThemedVisuals();
    }

    /// <summary>코드에서 직접 칠한 색(스트립·배너·상태 점·칩 글자·큐 행)을 새 팔레트로 다시 그린다.</summary>
    private void RefreshThemedVisuals()
    {
        UpdateBanner();
        if (_crmOk is bool crmOk)
            SetCrm(crmOk);
        if (_adbCheckedOnce)
            SetAdb(_adbConnected);
        foreach (var (code, btn) in _resultButtons)
        {
            if (btn.Content is TextBlock text)
                text.Foreground = btn.IsChecked == true
                    ? Ui.Token("B.OnInk")
                    : Ui.StatusColors(code).Fg;
        }
        RenderQueue();
        UpdateCallControls();
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (_callSession.State != CallSessionState.Idle)
        {
            StripFlash("통화 작업이 진행 중입니다 — 종료·저장 후 로그아웃하세요");
            return;
        }
        if (MessageBox.Show("로그아웃하고 로그인 화면으로 돌아갈까요?", "로그아웃",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _callSession.Abandon();
        ResetCallUi();
        await _client.LogoutAsync();
        var login = new LoginWindow();
        Application.Current.MainWindow = login;
        login.Show();
        _allowClose = true;
        Close();
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
            if (_manualEndConfirmed)
            {
                _closing = false;
                StripFlash("통화 종료 상태로 전환했습니다 — 결과를 저장한 뒤 종료하세요");
                MessageBox.Show(
                    "통화 종료 상태로 전환했습니다.\n결과를 저장한 뒤 앱을 종료하세요.",
                    "결과 저장 가능",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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

    private async void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app)
        {
            MessageBox.Show("업데이트 확인을 시작할 수 없습니다.", "업데이트",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateLink.IsEnabled = false;
        try
        {
            await app.RunVerifiedUpdateAsync(userInitiated: true);
        }
        finally
        {
            UpdateLink.IsEnabled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F10은 시스템 키(메뉴 활성화)로 들어오므로 SystemKey로 판별한다.
        if (e.Key == Key.System && e.SystemKey == Key.F10)
        {
            HelpOverlay.Visibility = HelpOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                if (HelpOverlay.Visibility == Visibility.Visible)
                {
                    HelpOverlay.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                    return;
                }
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
        // 숫자키 1~9/0 — 입력·선택 컨트롤에 포커스가 있으면 가로채지 않는다
        // (콜백 시·분 ComboBox에서 숫자로 항목을 찾는 입력까지 보호)
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox
            or ComboBox or ComboBoxItem or DatePicker or Calendar
            or CalendarButton or CalendarDayButton)
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
