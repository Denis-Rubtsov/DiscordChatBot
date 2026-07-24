using System.Text.Json;

class MemberRelationship
{
    public ulong UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Note { get; set; } = "";
    public int MessageCount { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

class RelationshipStore
{
    private readonly string _file;
    private readonly Dictionary<ulong, MemberRelationship> _members;
    private readonly object _lock = new();

    public RelationshipStore(string file)
    {
        _file = file;
        _members = File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<ulong, MemberRelationship>>(File.ReadAllText(file)) ?? new()
            : new();
    }

    public MemberRelationship GetOrCreate(ulong userId, string displayName)
    {
        lock (_lock)
        {
            if (!_members.TryGetValue(userId, out var member))
            {
                member = new MemberRelationship { UserId = userId, DisplayName = displayName };
                _members[userId] = member;
            }

            member.DisplayName = displayName;
            member.MessageCount++;
            member.LastSeenUtc = DateTime.UtcNow;
            Save();

            return member;
        }
    }

    public void UpdateNote(ulong userId, string note)
    {
        lock (_lock)
        {
            if (!_members.TryGetValue(userId, out var member)) return;

            member.Note = note;
            Save();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_members, new JsonSerializerOptions { WriteIndented = true });
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _file, overwrite: true);
    }
}
