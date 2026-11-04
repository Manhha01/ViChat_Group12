using System.Windows;
using ViChat.Client.ViewModels;

namespace ViChat.Client.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public LoginWindow()
    {
        InitializeComponent();

        _vm = new LoginViewModel();
        _vm.LoginSucceeded += OpenChat;
        _vm.ClearPasswordsRequested += () =>
        {
            PasswordInput.Clear();
            ConfirmPasswordInput.Clear();
        };
        DataContext = _vm;
    }

    // PasswordBox không binding được nên chuyển giá trị vào ViewModel bằng tay
    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e) =>
        _vm.Password = PasswordInput.Password;

    private void ConfirmPasswordInput_PasswordChanged(object sender, RoutedEventArgs e) =>
        _vm.ConfirmPassword = ConfirmPasswordInput.Password;

    private void OpenChat()
    {
        var chat = new ChatWindow();
        Application.Current.MainWindow = chat;
        chat.Show();
        Close();
    }
}