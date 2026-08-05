using System.Text.Json;

class ChannelHistoryEntry
{
    public bool FromBot { get; set; }
    public string Text { get; set; } = "";
}

// Долговременное состояние бота, переживающее рестарты: история разговоров по каналам
// и список «липких» каналов, где Мурка отвечает на всё без упоминания. До этого и то,
// и другое жило в RAM и стиралось при каждом деплое.
class BotStateStore
{
    private class State
    {
        public List<ulong> ActiveChannels { get; set; } = new();
        public Dictionary<ulong, List<ChannelHistoryEntry>> Histories { get; set; } = new();
    }

    private readonly string _file;
    private readonly State _state;
    private readonly object _lock = new();

    public BotStateStore(string file)
    {
        _file = file;
        State? loaded = null;
        try
        {
            if (File.Exists(file))
                loaded = JsonSerializer.Deserialize<State>(File.ReadAllText(file));
        }
        catch
        {
            // повреждённый файл состояния не должен ронять бота — начнём с чистого
        }
        _state = loaded ?? new State();
    }

    public bool IsActiveChannel(ulong channelId)
    {
        lock (_lock) return _state.ActiveChannels.Contains(channelId);
    }

    public void AddActiveChannel(ulong channelId)
    {
        lock (_lock)
        {
            if (_state.ActiveChannels.Contains(channelId)) return;
            _state.ActiveChannels.Add(channelId);
            Save();
        }
    }

    public List<ChannelHistoryEntry> GetHistory(ulong channelId)
    {
        lock (_lock)
        {
            return _state.Histories.TryGetValue(channelId, out var list)
                ? list.Select(e => new ChannelHistoryEntry { FromBot = e.FromBot, Text = e.Text }).ToList()
                : new List<ChannelHistoryEntry>();
        }
    }

    public void AppendExchange(ulong channelId, string userText, string botText, int maxMessages)
    {
        lock (_lock)
        {
            var list = GetOrAddHistory(channelId);
            list.Add(new ChannelHistoryEntry { FromBot = false, Text = userText });
            list.Add(new ChannelHistoryEntry { FromBot = true, Text = botText });
            Trim(list, maxMessages);
            Save();
        }
    }

    public void AppendBotMessage(ulong channelId, string text, int maxMessages)
    {
        lock (_lock)
        {
            var list = GetOrAddHistory(channelId);
            list.Add(new ChannelHistoryEntry { FromBot = true, Text = text });
            Trim(list, maxMessages);
            Save();
        }
    }

    public void ResetHistory(ulong channelId)
    {
        lock (_lock)
        {
            if (_state.Histories.Remove(channelId)) Save();
        }
    }

    private List<ChannelHistoryEntry> GetOrAddHistory(ulong channelId)
    {
        if (!_state.Histories.TryGetValue(channelId, out var list))
        {
            list = new List<ChannelHistoryEntry>();
            _state.Histories[channelId] = list;
        }
        return list;
    }

    private static void Trim(List<ChannelHistoryEntry> list, int maxMessages)
    {
        if (list.Count > maxMessages) list.RemoveRange(0, list.Count - maxMessages);
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _file, overwrite: true);
    }
}
