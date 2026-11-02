namespace ViChat.Client.Models;

// Tạo chữ viết tắt và màu avatar từ tên, giống các vòng tròn "MH", "NL" trong bản thiết kế
public static class AvatarHelper
{
    private static readonly string[] Colors =
        { "#F59E0B", "#14B8A6", "#EC4899", "#6366F1", "#22C55E", "#EF4444", "#0EA5E9", "#8B5CF6" };

    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1)
            return (words[0].Length >= 2 ? words[0][..2] : words[0]).ToUpper();
        return $"{words[0][0]}{words[^1][0]}".ToUpper();
    }

    // Cùng một tên thì luôn ra cùng một màu
    public static string ColorFor(string name)
    {
        int sum = 0;
        foreach (char c in name) sum += c;
        return Colors[sum % Colors.Length];
    }
}