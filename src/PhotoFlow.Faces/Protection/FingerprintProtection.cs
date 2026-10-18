// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PhotoFlow.Core.Analysis;

namespace PhotoFlow.Faces.Protection;

/// <summary>
/// AES-256-GCM encryption of each fingerprint with the catalog's data key (D12): version (1 byte), key id (8 bytes:
/// first bytes of SHA-256 of the key, so a fingerprint of another key is recognized), nonce (12), tag (16),
/// ciphertext. Only the fingerprints are encrypted; names and assignments stay readable.
/// </summary>
public sealed class FingerprintCipher : IFingerprintProtector
{
    private const byte Version = 1;
    private const int KeyIdLength = 8;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] AssociatedData = "photoflow-fingerprint-v1"u8.ToArray();

    private readonly byte[] _key;
    private readonly byte[] _keyId;

    public FingerprintCipher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("A 256-bit key is required.", nameof(key));
        }

        _key = (byte[])key.Clone();
        _keyId = KeyId(key);
    }

    public bool IsAvailable => true;

    public static byte[] KeyId(byte[] key) => SHA256.HashData(key)[..KeyIdLength];

    public byte[] Protect(ReadOnlySpan<float> fingerprint)
    {
        var plain = MemoryMarshal.AsBytes(fingerprint);
        var blob = new byte[1 + KeyIdLength + NonceLength + TagLength + plain.Length];
        blob[0] = Version;
        _keyId.CopyTo(blob, 1);
        var nonce = blob.AsSpan(1 + KeyIdLength, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagLength);
        aes.Encrypt(nonce, plain, blob.AsSpan(1 + KeyIdLength + NonceLength + TagLength), blob.AsSpan(1 + KeyIdLength + NonceLength, TagLength), AssociatedData);
        return blob;
    }

    public float[] Unprotect(ReadOnlySpan<byte> protectedFingerprint)
    {
        if (protectedFingerprint.Length < 1 + KeyIdLength + NonceLength + TagLength || protectedFingerprint[0] != Version)
        {
            throw new FingerprintKeyUnavailableException("Unknown fingerprint format.");
        }

        if (!protectedFingerprint.Slice(1, KeyIdLength).SequenceEqual(_keyId))
        {
            throw new FingerprintKeyUnavailableException("The fingerprint was encrypted with another key.");
        }

        var cipher = protectedFingerprint[(1 + KeyIdLength + NonceLength + TagLength)..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagLength);
        try
        {
            aes.Decrypt(protectedFingerprint.Slice(1 + KeyIdLength, NonceLength), cipher, protectedFingerprint.Slice(1 + KeyIdLength + NonceLength, TagLength), plain, AssociatedData);
        }
        catch (AuthenticationTagMismatchException e)
        {
            throw new FingerprintKeyUnavailableException($"Fingerprint not readable: {e.Message}");
        }

        return MemoryMarshal.Cast<byte, float>(plain).ToArray();
    }
}

/// <summary>A protector without key: nothing can be encrypted or read (recognition suspended, P13).</summary>
public sealed class UnavailableProtector : IFingerprintProtector
{
    public static UnavailableProtector Instance { get; } = new();

    public bool IsAvailable => false;

    public byte[] Protect(ReadOnlySpan<float> fingerprint) => throw new FingerprintKeyUnavailableException("No fingerprint key.");

    public float[] Unprotect(ReadOnlySpan<byte> protectedFingerprint) => throw new FingerprintKeyUnavailableException("No fingerprint key.");
}

public enum FingerprintKeyState
{
    /// <summary>No key yet: one is created on first use.</summary>
    None,

    /// <summary>The key is open for this account.</summary>
    Unlocked,

    /// <summary>A key exists but this account cannot open it: password or recompute.</summary>
    Locked,
}

/// <summary>
/// The catalog's fingerprint key in a small file of the config folder (F2-3): the 256-bit data key wrapped by DPAPI
/// for the Windows account (default) and, when the user sets one, by a password (PBKDF2-SHA256, 600,000 iterations,
/// AES-GCM). Another account or PC finds the key locked: the password opens it (and wraps it again for that account),
/// otherwise the fingerprints are recomputed with a new key; names and assignments are never lost. Keys never reach
/// the logs.
/// </summary>
public sealed class FingerprintKeyStore(string keyFilePath)
{
    public const int Iterations = 600_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private byte[]? _key;

    public FingerprintKeyState State
    {
        get
        {
            if (_key is not null)
            {
                return FingerprintKeyState.Unlocked;
            }

            return File.Exists(keyFilePath) ? FingerprintKeyState.Locked : FingerprintKeyState.None;
        }
    }

    public bool HasPassword => Read() is { Password: not null };

    /// <summary>The protector for the current state (unavailable when locked or without key).</summary>
    public IFingerprintProtector Protector => _key is { } key ? new FingerprintCipher(key) : UnavailableProtector.Instance;

    /// <summary>Opens the key with the Windows account, or creates one when there is none yet.</summary>
    public FingerprintKeyState Open()
    {
        var file = Read();
        if (file is null)
        {
            if (!OperatingSystem.IsWindows())
            {
                return FingerprintKeyState.None; // a password is required outside Windows (F2-3)
            }

            _key = RandomNumberGenerator.GetBytes(32);
            Write(new KeyFile(Account(_key), null));
            return FingerprintKeyState.Unlocked;
        }

        if (file.Account is { } account && OperatingSystem.IsWindows())
        {
            try
            {
                _key = ProtectedData.Unprotect(Convert.FromBase64String(account), Entropy, DataProtectionScope.CurrentUser);
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                _key = null; // another account or PC, or a damaged wrapper
            }
        }

        return State;
    }

    /// <summary>Creates a key protected by <paramref name="password"/> (and by the account on Windows).</summary>
    public void Create(string password)
    {
        _key = RandomNumberGenerator.GetBytes(32);
        Write(new KeyFile(OperatingSystem.IsWindows() ? Account(_key) : null, Wrap(_key, password)));
    }

    /// <summary>Opens a locked key with its password and wraps it for this account too.</summary>
    public bool Unlock(string password)
    {
        if (Read() is not { Password: { } wrapped } file)
        {
            return false;
        }

        try
        {
            _key = Unwrap(wrapped, password);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }

        Write(file with { Account = OperatingSystem.IsWindows() ? Account(_key) : file.Account });
        return true;
    }

    /// <summary>Adds or replaces the password of the open key (to open the catalog on other accounts or PCs).</summary>
    public void SetPassword(string password)
    {
        var key = _key ?? throw new FingerprintKeyUnavailableException("The key is not open.");
        Write((Read() ?? new KeyFile(null, null)) with { Password = Wrap(key, password) });
    }

    /// <summary>A new key replacing the unreadable one: the fingerprints must be recomputed.</summary>
    public void Reset(string? password)
    {
        _key = RandomNumberGenerator.GetBytes(32);
        Write(new KeyFile(OperatingSystem.IsWindows() ? Account(_key) : null, password is null ? null : Wrap(_key, password)));
    }

    private static readonly byte[] Entropy = "PhotoFlow fingerprint key"u8.ToArray();

    private static string Account(byte[] key) =>
        OperatingSystem.IsWindows()
            ? Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser))
            : throw new PlatformNotSupportedException();

    /// <summary>A key encrypted with a password (PBKDF2-SHA256, AES-256-GCM): the local key, or the key of an export.</summary>
    public static PasswordWrap Wrap(byte[] key, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[key.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, key, cipher, tag);
        return new PasswordWrap(Convert.ToBase64String(salt), Iterations, Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
    }

    /// <exception cref="System.Security.Cryptography.AuthenticationTagMismatchException">Wrong password.</exception>
    public static byte[] Unwrap(PasswordWrap wrap, string password)
    {
        var kek = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(wrap.Salt), wrap.Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = Convert.FromBase64String(wrap.Key);
        var key = new byte[cipher.Length];
        using var aes = new AesGcm(kek, 16);
        aes.Decrypt(Convert.FromBase64String(wrap.Nonce), cipher, Convert.FromBase64String(wrap.Tag), key);
        return key;
    }

    private KeyFile? Read()
    {
        if (!File.Exists(keyFilePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(keyFilePath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Write(KeyFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFilePath))!);
        var temp = keyFilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, keyFilePath, overwrite: true);
    }

    private sealed record KeyFile(string? Account, PasswordWrap? Password);

    public sealed record PasswordWrap(string Salt, int Iterations, string Nonce, string Tag, string Key);
}
