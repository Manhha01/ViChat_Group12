using System.Windows;
using ViChat.Client.ViewModels;

namespace ViChat.Client.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();

        var vm = new LoginViewModel();
        vm.LoginSucceeded += OpenChat;
        DataContext = vm; // DataContext: nguồn dữ liệu cho mọi {Binding ...} trong XAML
    }

    private void OpenChat()
    {
        var chat = new ChatWindow();
        Application.Current.MainWindow = chat;
        chat.Show();
        Close(); // đóng màn đăng nhập
    }
}