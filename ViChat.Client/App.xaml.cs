using System.Windows;
using ViChat.Client.Services;

namespace ViChat.Client;

public partial class App : Application
{
    // Một kết nối duy nhất, màn đăng nhập và màn chat cùng dùng
    public static NetworkClient Network { get; } = new();

    protected override void OnExit(ExitEventArgs e)
    {
        Network.Disconnect(); // đóng ứng dụng thì ngắt kết nối gọn gàng
        base.OnExit(e);
    }
}