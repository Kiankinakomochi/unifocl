using System.Text.Json;

internal sealed class DaemonRuntime
{
    private readonly string _registryPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public DaemonRuntime(string rootPath)
    {
        _registryPath = Path.Combine(rootPath, "daemons");
        Directory.CreateDirectory(_registryPath);
    }

    public IEnumerable<DaemonInstance> GetAll()
    {
        foreach (var file in Directory.EnumerateFiles(_registryPath, "*.json"))
        {
            DaemonInstance? instance = null;
            try
            {
                var json = File.ReadAllText(file);
                instance = JsonSerializer.Deserialize<DaemonInstance>(json, _jsonOptions);
            }
            catch
            {
                // Ignore malformed files in runtime directory.
            }

            if (instance is not null && ProcessUtil.IsAlive(instance.Pid))
            {
                yield return instance;
            }
        }
    }

    public DaemonInstance? GetByPort(int port)
    {
        var path = GetPath(port);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<DaemonInstance>(json, _jsonOptions);
            return state is not null && ProcessUtil.IsAlive(state.Pid) ? state : null;
        }
        catch
        {
            return null;
        }
    }

    public void Upsert(DaemonInstance instance)
    {
        var path = GetPath(instance.Port);
        var json = JsonSerializer.Serialize(instance, _jsonOptions);
        File.WriteAllText(path, json);
    }

    public void Remove(int port)
    {
        var path = GetPath(port);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void CleanStaleEntries()
    {
        foreach (var file in Directory.EnumerateFiles(_registryPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var instance = JsonSerializer.Deserialize<DaemonInstance>(json, _jsonOptions);
                if (instance is null || !ProcessUtil.IsAlive(instance.Pid))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                File.Delete(file);
            }
        }
    }

    private string GetPath(int port) => Path.Combine(_registryPath, $"{port}.json");
}
