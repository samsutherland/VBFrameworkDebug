using System.Security.Cryptography;

namespace DesktopClr;

internal sealed class SourcePaths
{
    private KeyValuePair<string, string>[] mapping = Array.Empty<KeyValuePair<string, string>>();

    public void Configure(Dictionary<string, string> paths)
    {
        mapping = paths.Select(p =>
        {
            if (!Path.IsPathRooted(p.Key) || !Path.IsPathRooted(p.Value))
                throw new ArgumentException("Source mappings require absolute Windows paths.");
            return new KeyValuePair<string, string>(Path.GetFullPath(p.Key).TrimEnd('\\'), Path.GetFullPath(p.Value).TrimEnd('\\'));
        }).OrderByDescending(p => p.Key.Length).ToArray();
    }

    public string Map(string path)
    {
        // Generated VB methods can reference an empty or virtual PDB document.
        if (!Path.IsPathRooted(path)) return path;
        string normalized = Path.GetFullPath(path);
        foreach (var pair in mapping)
            if (normalized.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(pair.Key + "\\", StringComparison.OrdinalIgnoreCase))
                return pair.Value + normalized.Substring(pair.Key.Length);
        return normalized;
    }

    public string? Problem(SourcePoint point)
    {
        string path = Map(point.Path);
        if (!File.Exists(path)) return "Source file is missing at the mapped path.";
        if (point.Checksum.Length == 0) return null;
        using HashAlgorithm? algorithm = point.HashAlgorithm.ToString() switch
        {
            "8829d00f-11b8-4213-878b-770e8597ac16" => SHA256.Create(),
            "ff1816ec-aa5e-4d10-87f7-6f4963833460" => SHA1.Create(),
            "406ea660-64cf-4c82-b6f0-42d48172a799" => MD5.Create(),
            _ => null
        };
        if (algorithm == null) return "Unsupported source checksum algorithm.";
        using var stream = File.OpenRead(path);
        return algorithm.ComputeHash(stream).SequenceEqual(point.Checksum) ? null : "Source checksum does not match the PDB; rebuild or select the matching source.";
    }
}
