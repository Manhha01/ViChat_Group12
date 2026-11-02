using System.Windows;
using System.Windows.Threading;
using ViChat.Client.ViewModels;

namespace ViChat.Client.Views;

public partial class ChatWindow : Window
{
    private readonly ChatViewModel _vm;

    public ChatWindow()
    {
        InitializeComponent();

        _vm = new ChatViewModel();
        _vm.MessagesChanged += ScrollToBottom;
        _vm.LogoutRequested += Logout;
        DataContext = _vm;

        App.Network.Disconnected += OnDisconnected;

        // Đóng cửa sổ: dọn dẹp để cửa sổ cũ không còn "nghe" gói tin
        Closed += (_, _) =>
        {
            _vm.Cleanup();
            App.Network.Disconnected -= OnDisconnected;
        };

        Loaded += (_, _) => MessageBox.Focus(); // mở ra là gõ được ngay
    }

    // Cuộn sau khi giao diện vẽ xong tin mới (ưu tiên Background = làm sau cùng)
    private void ScrollToBottom() =>
        Dispatcher.InvokeAsync(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void OnDisconnected(string reason)
    {
        System.Windows.MessageBox.Show(reason, "ViChat", MessageBoxButton.OK, MessageBoxImage.Warning);
        BackToLogin();
    }

    private void Logout()
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