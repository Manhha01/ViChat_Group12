using System.IO;
using System.Text.Json;

namespace ViChat.Client.Services;

// Những gì cần nhớ giữa các lần mở ứng dụng
public class AppSettings
{
    public string ServerIp { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5000;
    public string UserName { get; set; } = "";
}

public static class SettingsService
{
    // Lưu ở C:\Users\<tên>\AppData\Roaming\ViChat\settings.json
    // Nằm ngoài thư mục repo nên không bị đẩy lên GitHub, mỗi máy một file riêng
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ViChat", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // File hỏng thì dùng giá trị mặc định
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
    }
}
//// Từ nay không ai phải sửa IP trong code nữa. Ứng dụng tự nhớ IP mà mỗi máy nhập lần trước.