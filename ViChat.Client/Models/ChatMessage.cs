using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViChat.Shared;

namespace ViChat.Client.Models;

// Một tin nhắn hiển thị trong khung chat: tin chữ, thẻ file hoặc ảnh.
// Kế thừa ObservableObject để thanh tiến trình, trạng thái, ảnh tự cập nhật trên giao diện.
public partial class ChatMessage : ObservableObject
{
    // ===== Phần cũ (tin chữ) =====
    public string Room { get; init; } = "";
    public string From { get; init; } = "";
    public string Content { get; init; } = "";   // tin chữ; với file là dòng tóm tắt "📎 tên file"
    public string Time { get; init; } = "";
    public bool IsMine { get; init; }     // tin của mình -> bong bóng tím bên phải
    public bool IsSystem { get; init; }   // "... đã vào phòng" -> chữ xám ở giữa

    public string Initials => AvatarHelper.Initials(From);
    public string AvatarColor => AvatarHelper.ColorFor(From);

    // ===== Phần mới (file / ảnh) =====
    public string Kind { get; init; } = "text";   // "text" | FileKinds.File ("file") | FileKinds.Image ("image")
    public string FileId { get; set; } = "";      // bên gửi chỉ biết sau khi FileSender tạo FILE_START
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
    public string FileSizeText => FormatSize(FileSize);

    [ObservableProperty] private int progress;            // 0..100
    [ObservableProperty] private bool isTransferring;     // true -> hiện thanh tiến trình

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string status = "";                           // "Đang gửi...", "Đã gửi", "Lỗi: ..."
    public bool HasStatus => Status.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private string? savedPath;                            // đường dẫn file trên máy (để mở / xem ảnh)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private ImageSource? thumbnail;                       // ảnh thu nhỏ hiển thị trong bong bóng
    public bool HasThumbnail => Thumbnail != null;

    // Thư viện tự gọi khi SavedPath đổi: nếu là ảnh thì nạp ảnh để hiển thị
    partial void OnSavedPathChanged(string? value)
    {
        if (Kind == FileKinds.Image && !string.IsNullOrEmpty(value))
            Thumbnail = LoadImage(value);
    }

    // Bấm vào bong bóng -> mở file bằng chương trình mặc định của Windows
    private bool CanOpen() => !string.IsNullOrEmpty(SavedPath) && File.Exists(SavedPath);

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SavedPath!) { UseShellExecute = true });
        }
        catch
        {
            Status = "Không mở được file.";
        }
    }

    // ===== Hàm phụ =====
    private static ImageSource? LoadImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // đọc xong đóng file ngay, không khóa file
            bmp.DecodePixelWidth = 560;                   // thu nhỏ khi giải mã cho đỡ tốn RAM
            bmp.UriSource = new Uri(Path.GetFullPath(path));
            bmp.EndInit();
            bmp.Freeze();                                 // cho phép dùng ở mọi luồng
            return bmp;
        }
        catch
        {
            return null;   // ảnh hỏng/không đọc được -> vẫn hiện dạng thẻ file
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB"
    };
}