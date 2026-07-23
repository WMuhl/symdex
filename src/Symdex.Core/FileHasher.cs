using System.Security.Cryptography;

namespace Symdex.Core;

public static class FileHasher
{
    public static string HashFile(string absolutePath)
    {
        using var stream = File.OpenRead(absolutePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
