using System.Windows;
using ViChat.Shared;

namespace ViChat.Client.Views;

public partial class ChatWindow : Window
{
    public ChatWindow()
    {
        InitializeComponent();
        HelloText.Text = $"Xin chào, {App.Network.UserName}!";

        App.Network.PacketReceived += OnPacket;
        App.Network.Disconnected += OnDisconnected;

        // Đóng cửa sổ thì HỦY đăng ký sự kiện, nếu không cửa sổ cũ vẫn "nghe" gói tin
        Closed += (_, _) =>
        {
            App.Network.PacketReceived -= OnPacket;
            App.Network.Disconnected -= OnDisconnected;
        };
    }

    private void OnPacket(Packet p)
    {
        string data = string.Join(", ", p.Data.Select(kv => $"{kv.Key}={kv.Value}"));
        LogList.Items.Add($"{p.Type}: {data}");
    }

    private void OnDisconnected(string reason)
    {
        MessageBox.Show(reason, "ViChat", MessageBoxButton.OK, MessageBoxImage.Warning);
        BackToLogin();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        App.Network.Disconnect();
        BackToLogin();
    }

    private void BackToLogin()
    {
        var login = new LoginWindow();
        Application.Current.MainWindow = login;
        login.Show();
        Close();
    }
}