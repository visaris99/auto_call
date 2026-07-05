using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Core;

namespace MilestoneDialer;

public partial class LoginWindow : Window
{
    private readonly AppConfig _config = AppConfig.Load();
    private readonly ApiClient _client;
    private bool _mfaVisible;

    public LoginWindow()
    {
        InitializeComponent();
        _client = new ApiClient(_config.ServerUrl);
        IdBox.Text = _config.LastLoginId;  // 아이디는 한글 허용
        try
        {
            LogoImage.Source = new BitmapImage(new Uri(
                Path.Combine(AppContext.BaseDirectory, "assets", "milestone_logo.png")));
        }
        catch (Exception ex) when (ex is IOException or UriFormatException)
        {
            // 로고 없으면 텍스트 없이 진행 — 치명적이지 않음
        }

        // 비밀번호/MFA는 ASCII만 — PasswordBox는 IME 자체가 차단되지만 붙여넣기까지 방어
        PwBox.PasswordChanged += (_, _) =>
        {
            string filtered = QueueLogic.AsciiOnly(PwBox.Password);
            if (filtered != PwBox.Password)
                PwBox.Password = filtered;
        };
        MfaBox.TextChanged += (_, _) =>
        {
            string filtered = QueueLogic.AsciiOnly(MfaBox.Text);
            if (filtered != MfaBox.Text)
            {
                MfaBox.Text = filtered;
                MfaBox.CaretIndex = filtered.Length;
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Submit_Click(this, new RoutedEventArgs());
        };
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(IdBox.Text)) IdBox.Focus();
            else PwBox.Focus();
        };
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        string loginId = IdBox.Text.Trim();
        string password = PwBox.Password;
        string? code = _mfaVisible && MfaBox.Text.Trim().Length > 0 ? MfaBox.Text.Trim() : null;
        if (loginId.Length == 0 || password.Length == 0)
        {
            ErrorText.Text = "아이디와 비밀번호를 입력하세요.";
            return;
        }
        SubmitBtn.IsEnabled = false;
        SubmitBtn.Content = "확인 중…";
        ErrorText.Text = "";
        try
        {
            var user = await _client.LoginAsync(loginId, password, code);
            if (user.MustChangePassword)
            {
                MessageBox.Show("초기 비밀번호 상태입니다.\n웹 CRM에서 비밀번호를 변경한 뒤 다시 로그인하세요.",
                    "비밀번호 변경 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
                await _client.LogoutAsync();
                return;
            }
            _config.LastLoginId = user.LoginId;
            _config.Save();
            var main = new MainWindow(_client, _config);
            Application.Current.MainWindow = main;
            main.Show();
            Close();
        }
        catch (MfaRequiredException ex)
        {
            ShowMfa(ex);
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            SubmitBtn.IsEnabled = true;
            SubmitBtn.Content = "로그인";
        }
    }

    private void ShowMfa(MfaRequiredException ex)
    {
        if (!_mfaVisible)
        {
            _mfaVisible = true;
            MfaLabel.Visibility = Visibility.Visible;
            MfaBox.Visibility = Visibility.Visible;
            ErrorText.Text = "인증앱의 6자리 코드를 입력하세요.";
        }
        else
        {
            ErrorText.Text = ex.Message;
        }
        MfaBox.Focus();
    }

    private void ServerSettings_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = _client.BaseUrl, Height = 32, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button
        {
            Content = "저장", Height = 32, Margin = new Thickness(0, 12, 0, 0),
            Style = (Style)FindResource("PrimaryBtn"),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "CRM 서버 주소:" });
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var dialog = new Window
        {
            Title = "서버 주소", Width = 420, Height = 170, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Content = panel,
            Background = (System.Windows.Media.Brush)FindResource("B.Background"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("AppFont"),
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() == true && box.Text.Trim().Length > 0)
        {
            _client.BaseUrl = box.Text.Trim().TrimEnd('/');
            _config.ServerUrl = _client.BaseUrl;
            _config.Save();
        }
    }
}
