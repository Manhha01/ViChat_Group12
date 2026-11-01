using System.Collections.Concurrent;
using ViChat.Shared;

public class Room
{
    public string Name { get; }
    public string Creator { get; }

    // Thành viên đang online trong phòng: key = Id của session
    public ConcurrentDictionary<Guid, ClientSession> Members { get; } = new();

    public Room(string name, string creator)
    {
        Name = name;
        Creator = creator;
    }

    // Gửi một gói cho mọi người trong phòng (trừ exceptId nếu có)
    public async Task BroadcastAsync(Packet packet, Guid? exceptId = null)
    {
        var tasks = new List<Task>();
        foreach (var member in Members.Values)
        {
            if (exceptId.HasValue && member.Id == exceptId.Value) continue;
            tasks.Add(SafeSendAsync(member, packet));
        }
        await Task.WhenAll(tasks);
    }

    // Một client lỗi/mất kết nối không được làm hỏng việc gửi cho người khác
    private static async Task SafeSendAsync(ClientSession s, Packet p)
    {
        try { await s.SendAsync(p); }
        catch { /* client đã đứt, HandleClientAsync của nó sẽ tự dọn */ }
    }
}