using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Data;
using System.Net.Sockets;
using ViChat.Client.Services;

namespace ViChat.Client.ViewModels;

// "partial" + ObservableObject: thư viện tự sinh code thông báo cho giao diện khi dữ liệu đổi
public partial class LoginViewModel : ObservableObject
{
    // [ObservableProperty] trên field "serverIp" -> thư viện tự sinh property "ServerIp".
    // Giao diện binding vào ServerIp; gõ chữ vào ô là giá trị ở đây đổi theo, và ngược lại.
    [ObservableProperty] private string serverIp = "";
    [ObservableProperty] private string port = "";
    [ObservableProperty] private string displayName = "";
    [ObservableProperty] private bool rememberServer = true;

    // Khi ErrorMessage đổi thì báo luôn cho HasError (để hiện/ẩn hộp lỗi)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string errorMessage = "";

    // Trạng thái kết nối: "Idle" (chưa kết nối) | "Connecting" (đang kết nối) | "Error" (lỗi)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    private string connectionState = "Idle";

    // Các giá trị tính từ dữ liệu trên, giao diện chỉ đọc
    public bool HasError => ErrorMessage.Length > 0;

    public string StatusText => ConnectionState switch
    {
        "Connecting" => "Đang kết nối...",
        "Error" => "Mất kết nối",
        _ => "Chưa kết nối"
    };

    public string ConnectButtonText => ConnectionState == "Connecting" ? "Đang kết nối..." : "Vào chat";

    // Báo cho cửa sổ biết đã vào được, để cửa sổ chuyển sang màn chat
    public event Action? LoginSucceeded;

    public LoginViewModel()
    {
        // Điền sẵn những gì đã nhớ lần trước
        var settings = SettingsService.Load();
        ServerIp = settings.ServerIp;
        Port = settings.Port.ToString();
        DisplayName = settings.UserName;
    }

    // [RelayCommand] trên hàm ConnectAsync -> thư viện tự sinh "ConnectCommand" để gắn vào nút.
    // Trong lúc hàm đang chạy, nút tự bị khóa, bấm liên tục cũng không kết nối 2 lần.
    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorMessage = "";
        string ip = ServerIp.Trim();
        string name = DisplayName.Trim();

        // Kiểm tra dữ liệu nhập trước khi kết nối
        if (ip.Length == 0)
        {
            ErrorMessage = "Nhập địa chỉ IP của server.";
            return;
        }
        if (!int.TryParse(Port.Trim(), out int portNumber) || portNumber < 1 || portNumber > 65535)
        {
            ErrorMessage = "Cổng phải là số từ 1 đến 65535.";
            return;
        }
        if (name.Length == 0)
        {
            ErrorMessage = "Nhập tên hiển thị.";
            return;
        }

        ConnectionState = "Connecting";
        try
        {
            await App.Network.ConnectAsync(ip, portNumber, name);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            ConnectionState = "Error";
            ErrorMessage = $"Không kết nối được tới {ip}:{portNumber}. Kiểm tra server đã chạy chưa, " +
                           $"IP có đúng không, hai máy có cùng mạng và tường lửa máy server đã mở cổng {portNumber} chưa.";
            return;
        }
        catch (Exception ex)
        {
            ConnectionState = "Error";
            ErrorMessage = "Lỗi: " + ex.Message;
            return;
        }

        if (RememberServer)
            SettingsService.Save(new AppSettings { ServerIp = ip, Port = portNumber, UserName = name });

        ConnectionState = "Idle";
        LoginSucceeded?.Invoke();
    }

    // Nút "Dùng 127.0.0.1" trong hộp lỗi: chạy thử trên chính máy này
    [RelayCommand]
    private void UseLocalhost()
    {
        ServerIp = "127.0.0.1";
        ErrorMessage = "";
        ConnectionState = "Idle";
    }
}