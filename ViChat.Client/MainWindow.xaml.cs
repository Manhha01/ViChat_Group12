using System.Windows;
using ViChat.Client.Services;

namespace ViChat.Client;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Điền sẵn IP và tên lần trước
        var settings = SettingsService.Load();
        IpBox.Text = settings.ServerIp;
        NameBox.Text = settings.UserName;

        // Nghe sự kiện từ NetworkClient (đã ở luồng giao diện, sửa UI thoải mái)
        App.Network.PacketReceived += packet =>
        {
            string data = string.Join(", ", packet.Data.Select(kv => $"{kv.Key}={kv.Value}"));
            LogList.Items.Add($"{packet.Type}: {data}");
        };
        App.Network.Disconnected += reason =>
        {
            StatusText.Text = reason;
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
        };
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        string ip = IpBox.Text.Trim();
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "Nhập tên hiển thị trước đã.";
            return;
        }

        ConnectButton.IsEnabled = false;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SubTextBrush");
        StatusText.Text = "Đang kết nối...";

        try
        {
            await App.Network.ConnectAsync(ip, 5000, name);
            SettingsService.Save(new AppSettings { ServerIp = ip, UserName = name });
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            StatusText.Text = $"Đã kết nối tới {ip}:5000";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            StatusText.Text = "Không kết nối được: " + ex.Message;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }
}