using System.IO;
using System.Security.Cryptography;

namespace DshControl;

/// <summary>Keeps versioned scripts alive independently of the controller installation and build directories.</summary>
internal static class BundledAssets
{
    static readonly Dictionary<string, string> Resources = new(StringComparer.Ordinal)
    {
        ["scripts/runtime-host.mjs"] = "DshControl.assets.runtime-host.mjs",
        ["scripts/apply-update.ps1"] = "DshControl.assets.apply-update.ps1",
        ["compat-polyfill.js"] = "DshControl.assets.compat-polyfill.js",
    };

    public static string Get(string relative)
    {
        if (!Resources.TryGetValue(relative, out var resource)) throw new ArgumentException("Unknown bundled asset.", nameof(relative));
        using var stream = typeof(BundledAssets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("The application is missing " + resource);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var expected = buffer.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(expected));
        var destination = Path.Combine(AppConfig.ConfigDir, "runtime-assets", digest, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        if (!File.Exists(destination) || !File.ReadAllBytes(destination).AsSpan().SequenceEqual(expected))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, expected);
            File.Move(temporary, destination, true);
        }
        return destination;
    }
}
