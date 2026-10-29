using System.Collections.Concurrent;
using ViChat.Shared;

public class RoomManager
{
    // Tên phòng không phân biệt hoa thường
    private readonly ConcurrentDictionary<string, Room> _rooms =
        new(StringComparer.OrdinalIgnoreCase);

    public RoomManager()
    {
        // Phòng mặc định để chạy được ngay như trước
        _rooms["General"] = new Room("General", "system");
    }

    // Trả null nếu tên đã tồn tại
    public Room? Create(string name, string creator)
    {
        var room = new Room(name, creator);
        return _rooms.TryAdd(name, room) ? room : null;
    }

    public Room? Get(string name) =>
        _rooms.TryGetValue(name, out var r) ? r : null;

    public bool Join(string name, ClientSession s)
    {
        var room = Get(name);
        if (room == null) return false;
        room.Members[s.Id] = s;
        s.Rooms[room.Name] = 0;
        return true;
    }

    public bool Leave(string name, ClientSession s)
    {
        var room = Get(name);
        if (room == null) return false;
        room.Members.TryRemove(s.Id, out _);
        s.Rooms.TryRemove(room.Name, out _);
        return true;
    }

    // Gọi khi client mất kết nối: rời mọi phòng
    public void LeaveAll(ClientSession s)
    {
        foreach (var name in s.Rooms.Keys.ToList())
            Leave(name, s);
    }

    // Chuỗi dạng "General:3,Nhom12:2" vì Data chỉ là Dictionary<string,string>
    public string ListAsString() =>
        string.Join(",", _rooms.Values.Select(r => $"{r.Name}:{r.Members.Count}"));
}