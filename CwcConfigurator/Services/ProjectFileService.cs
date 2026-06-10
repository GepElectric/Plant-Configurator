using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public sealed class ProjectFileService
{
    private const string EncryptedPrefix = "PLANTCFG1:";
    private static readonly byte[] PrefixBytes = Encoding.ASCII.GetBytes(EncryptedPrefix);
    private static readonly byte[] EncryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes("PlantConfigurator project file v1"));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public CwcProject Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Project file does not exist.", filePath);
        }

        CwcProject? project;
        using (var stream = File.OpenRead(filePath))
        {
            if (IsEncryptedHeader(stream))
            {
                using var base64Stream = new CryptoStream(stream, new FromBase64Transform(), CryptoStreamMode.Read, leaveOpen: true);
                var iv = new byte[16];
                ReadExactly(base64Stream, iv);

                using var aes = Aes.Create();
                aes.Key = EncryptionKey;
                aes.IV = iv;
                using var decryptStream = new CryptoStream(base64Stream, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
                project = JsonSerializer.Deserialize<CwcProject>(decryptStream, JsonOptions);
            }
            else
            {
                project = JsonSerializer.Deserialize<CwcProject>(stream, JsonOptions);
            }
        }

        if (project is null)
        {
            throw new InvalidDataException("Project file is empty or invalid.");
        }

        project.ProjectFilePath = filePath;
        return project;
    }

    public string Serialize(CwcProject project)
    {
        return JsonSerializer.Serialize(project, JsonOptions);
    }

    /// <summary>
    /// Cheap fingerprint of the serialized project, used for unsaved-change
    /// detection without keeping the whole multi-MB JSON string in memory.
    /// </summary>
    public byte[] ComputeSnapshotHash(CwcProject project)
    {
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(project, JsonOptions));
    }

    public void Save(string filePath, CwcProject project)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        project.ProjectFilePath = filePath;

        using var fileStream = File.Create(filePath);
        fileStream.Write(PrefixBytes, 0, PrefixBytes.Length);

        using var aes = Aes.Create();
        aes.Key = EncryptionKey;
        aes.GenerateIV();

        // The on-disk payload is base64(IV || ciphertext). Streaming the IV and the
        // serialized JSON through these chained transforms avoids buffering the whole
        // (multi-MB) project as an intermediate string and byte array.
        using var base64Stream = new CryptoStream(fileStream, new ToBase64Transform(), CryptoStreamMode.Write, leaveOpen: true);
        base64Stream.Write(aes.IV, 0, aes.IV.Length);
        using (var encryptStream = new CryptoStream(base64Stream, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
        {
            JsonSerializer.Serialize(encryptStream, project, JsonOptions);
        }
    }

    private static bool IsEncryptedHeader(Stream stream)
    {
        var header = new byte[PrefixBytes.Length];
        var total = 0;
        while (total < header.Length)
        {
            var read = stream.Read(header, total, header.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var match = total == header.Length && header.AsSpan().SequenceEqual(PrefixBytes);
        if (!match)
        {
            stream.Position = 0;
        }

        return match;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                throw new InvalidDataException("Project file payload is truncated.");
            }

            total += read;
        }
    }
}
