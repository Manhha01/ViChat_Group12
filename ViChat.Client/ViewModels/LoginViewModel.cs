using System.Net.Sockets;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViChat.Client.Services;
using ViChat.Shared;

namespace ViChat.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private static readonly Regex UsernameRule = new("^[a-z0-9._]{3,30}$");

    // ===== Dữ liệu trên form =====
    [ObservableProperty] private string serverIp = "";
    [ObservableProperty] private string port = "";
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string displayName = "";   // chỉ dùng khi đăng ký
    [ObservableProperty] private bool rememberServer = true;

    // PasswordBox không binding được (vì lý do bảo mật của WPF) -> cửa sổ tự gán vào đây
    public string Password { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";

    // ===== Chế độ: đăng nhập hay đăng ký =====
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle), nameof(FormSubtitle), nameof(SubmitButtonText),
                              nameof(SwitchModeQuestion), nameof(SwitchModeText))]
    private bool isRegisterMode;

    public string FormTitle => IsRegisterMode ? "Đăng ký" : "Đăng nhập";
    public string FormSubtitle => IsRegisterMode
        ? "Tạo tài khoản ViChat trên server của nhóm."
        : "Nhập địa chỉ server và tài khoản của bạn.";
    public string SwitchModeQuestion => IsRegisterMode ? "Đã có tài khoản?" : "Chưa có tài khoản?";
    public string SwitchModeText => IsRegisterMode ? "Đăng nhập" : "Đăng ký";

    // ===== Thông báo =====
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string errorMessage = "";
    public bool HasError => ErrorMessage.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    private string infoMessage = "";
    public bool HasInfo => InfoMessage.Length > 0;

    // "Idle" | "Connecting" | "Error"
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(SubmitButtonText))]
    private string connectionState = "Idle";

    public string StatusText => ConnectionState switch
    {
        "Connecting" => "Đang kết nối...",
        "Error" => "Mất kết nối",
        _ => "Chưa kết nối"
    };

    public string SubmitButtonText =>
        ConnectionState == "Connecting" ? "Đang xử lý..."
        : IsRegisterMode ? "Tạo tài khoản" : "Đăng nhập";

    public event Action? LoginSucceeded;
    public event Action? ClearPasswordsRequested;   // báo cửa sổ xóa trắng ô mật khẩu

    public LoginViewModel()
    {
        var settings = SettingsService.Load();
        ServerIp = settings.ServerIp;
        Port = settings.Port.ToString();
        Username = settings.UserName;
    }

    // ===== Nút chính: Đăng nhập / Tạo tài khoản =====
    [RelayCommand]
    private async Task SubmitAsync()
    {
        ErrorMessage = "";
        InfoMessage = "";
        if (!Validate(out string ip, out int portNumber, out string user)) return;

        // 1. Kết nối tới server
        ConnectionState = "Connecting";
        try
        {
            await App.Network.ConnectAsync(ip, portNumber, user);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            ConnectionState = "Error";
            ErrorMessage = $"Không kết nối được tới {ip}:{portNumber}. Kiểm tra server đã chạy chưa, " +
                           $"IP có đúng không, hai máy có cùng mạng và tường lửa máy server đã mở cổng {portNumber} chưa.";
            return;
        }

        // 2. Gửi yêu cầu đăng ký hoặc đăng nhập, chờ trả lời
        try
        {
            if (IsRegisterMode) await RegisterAsync(user);
            else await LoginAsync(user, ip, portNumber);
        }
        catch (Exception ex)
        {
            App.Network.Disconnect();
            ConnectionState = "Idle";
            ErrorMessage = ex is TimeoutException
                ? "Server không phản hồi. Server đã được cập nhật bản có đăng nhập chưa?"
                : "Lỗi: " + ex.Message;
        }
    }

    private async Task LoginAsync(string user, string ip, int portNumber)
    {
        var reply = await RequestAsync("LOGIN",
            new() { ["username"] = user, ["password"] = Password }, "LOGIN_RESULT");
        ConnectionState = "Idle";

        if (reply.Data.GetValueOrDefault("success") != "true")
        {
            App.Network.Disconnect();
            ErrorMessage = reply.Data.GetValueOrDefault("message", "Đăng nhập thất bại.");
            return;
        }

        App.Network.SetLoggedInUser(
            reply.Data.GetValueOrDefault("username", user),
            reply.Data.GetValueOrDefault("displayName", user));

        if (RememberServer)
            SettingsService.Save(new AppSettings { ServerIp = ip, Port = portNumber, UserName = user });

        LoginSucceeded?.Invoke();
    }

    private async Task RegisterAsync(string user)
    {
        var reply = await RequestAsync("REGISTER", new()
        {
            ["username"] = user,
            ["password"] = Password,
            ["displayName"] = DisplayName.Trim()
        }, "REGISTER_RESULT");

        App.Network.Disconnect();   // đăng ký xong thì ngắt, người dùng đăng nhập lại như bình thường
        ConnectionState = "Idle";

        if (reply.Data.GetValueOrDefault("success") != "true")
        {
            ErrorMessage = reply.Data.GetValueOrDefault("message", "Đăng ký thất bại.");
            return;
        }

        IsRegisterMode = false;
        ClearPasswords();
        InfoMessage = "Đăng ký thành công. Hãy đăng nhập bằng tài khoản vừa tạo.";
    }

    // Gửi yêu cầu và chờ trả lời.
    // PHẢI bắt đầu chờ TRƯỚC khi gửi: nếu server trả lời quá nhanh, ta vẫn bắt kịp.
    private static async Task<Packet> RequestAsync(string type, Dictionary<string, string> data, string replyType)
    {
        Task<Packet> waiter = App.Network.WaitForPacketAsync(replyType);
        await App.Network.SendAsync(type, data);
        return await waiter;
    }

    // Kiểm tra dữ liệu nhập trước khi gửi đi
    private bool Validate(out string ip, out int portNumber, out string user)
    {
        ip = ServerIp.Trim();
        user = Username.Trim().ToLowerInvariant();
        portNumber = 0;

        if (ip.Length == 0) { ErrorMessage = "Nhập địa chỉ IP của server."; return false; }
        if (!int.TryParse(Port.Trim(), out portNumber) || portNumber < 1 || portNumber > 65535)
        { ErrorMessage = "Cổng phải là số từ 1 đến 65535."; return false; }
        if (!UsernameRule.IsMatch(user))
        { ErrorMessage = "Tên đăng nhập 3-30 ký tự, chỉ gồm chữ thường không dấu, số, dấu . và _"; return false; }
        if (Password.Length < 6) { ErrorMessage = "Mật khẩu tối thiểu 6 ký tự."; return false; }
        if (IsRegisterMode && Password != ConfirmPassword)
        { ErrorMessage = "Mật khẩu nhập lại không khớp."; return false; }

        return true;
    }

    // ===== Chuyển giữa Đăng nhập và Đăng ký =====
    [RelayCommand]
    private void ToggleMode()
    {
        IsRegisterMode = !IsRegisterMode;
        ErrorMessage = "";
        InfoMessage = "";
        ConnectionState = "Idle";
        ClearPasswords();
    }

    [RelayCommand]
    private void UseLocalhost()
    {
        ServerIp = "127.0.0.1";
        ErrorMessage = "";
        ConnectionState = "Idle";
    }

    private void ClearPasswords()
    {
        Password = "";
        ConfirmPassword = "";
        ClearPasswordsRequested?.Invoke();
    }
}