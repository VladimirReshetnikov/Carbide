using System.Text.Json;
using System.Text.Json.Nodes;

namespace CarbideShellCore.Vfs;

/// <summary>
/// Serializes a VFS tree to/from JSON. File content is base64-encoded to preserve arbitrary
/// bytes. Snapshots are the persistence-format-in-waiting; a concrete backend (IndexedDB,
/// OPFS, Node JSON file) sits on top of this API.
/// </summary>
public static class VfsSnapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public static string Save(VirtualFileSystem vfs)
    {
        var root = SaveNode(vfs.Root);
        root["currentLocation"] = vfs.CurrentLocation;
        return root.ToJsonString(Options);
    }

    public static void Load(VirtualFileSystem vfs, string json)
    {
        var node = JsonNode.Parse(json) ?? throw new FormatException("Snapshot is null.");
        vfs.Root.Children.Clear();
        LoadDirectory(vfs.Root, node.AsObject());
        if (node["currentLocation"]?.GetValue<string>() is { } loc)
        {
            vfs.CurrentLocation = vfs.IsDirectory(loc) ? loc : VfsPath.RootPath;
        }
    }

    private static JsonObject SaveNode(VfsNode node)
    {
        var obj = new JsonObject
        {
            ["name"] = node.Name,
            ["creationTimeUtc"] = node.CreationTimeUtc.ToString("O"),
            ["lastWriteTimeUtc"] = node.LastWriteTimeUtc.ToString("O"),
        };
        switch (node)
        {
            case VfsFile f:
                obj["type"] = "file";
                obj["encoding"] = f.Encoding;
                obj["content"] = Convert.ToBase64String(f.Content);
                break;
            case VfsDirectory d:
                obj["type"] = "dir";
                var children = new JsonArray();
                foreach (var c in d.Children.Values) children.Add(SaveNode(c));
                obj["children"] = children;
                break;
        }
        return obj;
    }

    private static void LoadDirectory(VfsDirectory dir, JsonObject node)
    {
        var children = node["children"]?.AsArray();
        if (children == null) return;
        foreach (var childNode in children.OfType<JsonObject>())
        {
            var name = childNode["name"]?.GetValue<string>() ?? "";
            var type = childNode["type"]?.GetValue<string>() ?? "file";
            if (type == "dir")
            {
                var sub = new VfsDirectory(name) { Parent = dir };
                ApplyTimestamps(sub, childNode);
                dir.Children[name] = sub;
                LoadDirectory(sub, childNode);
            }
            else
            {
                var b64 = childNode["content"]?.GetValue<string>() ?? "";
                var encoding = childNode["encoding"]?.GetValue<string>() ?? "utf-8";
                var file = new VfsFile(name)
                {
                    Parent = dir,
                    Content = Convert.FromBase64String(b64),
                    Encoding = encoding,
                };
                ApplyTimestamps(file, childNode);
                dir.Children[name] = file;
            }
        }
    }

    private static void ApplyTimestamps(VfsNode node, JsonObject obj)
    {
        if (obj["creationTimeUtc"]?.GetValue<string>() is { } c
            && DateTime.TryParse(c, null, System.Globalization.DateTimeStyles.RoundtripKind, out var cd))
            node.CreationTimeUtc = cd;
        if (obj["lastWriteTimeUtc"]?.GetValue<string>() is { } w
            && DateTime.TryParse(w, null, System.Globalization.DateTimeStyles.RoundtripKind, out var wd))
            node.LastWriteTimeUtc = wd;
    }
}
