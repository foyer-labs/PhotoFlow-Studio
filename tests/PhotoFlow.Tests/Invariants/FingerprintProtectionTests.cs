// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PhotoFlow.Core.Analysis;
using PhotoFlow.Faces.Protection;
using PhotoFlow.Tests.Support;

namespace PhotoFlow.Tests.Invariants;

/// <summary>
/// Fingerprint protection (D12, F2-3): never in clear, readable only with the key that wrote it; the key opens with
/// the Windows account or the password; another account finds it locked.
/// </summary>
[Trait(TestCategory.Name, TestCategory.Unit)]
public sealed class FingerprintProtectionTests : IDisposable
{
    private readonly TempFolder _temp = new("pf-key");

    [Fact]
    public void A_fingerprint_round_trips_and_is_never_stored_in_clear()
    {
        var cipher = new FingerprintCipher(RandomNumberGenerator.GetBytes(32));
        var fingerprint = Enumerable.Range(0, 512).Select(i => MathF.Cos(i * 0.37f)).ToArray();

        var blob = cipher.Protect(fingerprint);

        Assert.Equal(fingerprint, cipher.Unprotect(blob));
        var clear = MemoryMarshal.AsBytes(fingerprint.AsSpan()).ToArray();
        Assert.True(blob.AsSpan().IndexOf(clear.AsSpan(0, 16)) < 0, "Plain fingerprint bytes found in the protected blob.");
        Assert.NotEqual(blob, cipher.Protect(fingerprint)); // random nonce
    }

    [Fact]
    public void Another_key_or_a_tampered_blob_cannot_read_a_fingerprint()
    {
        var blob = new FingerprintCipher(RandomNumberGenerator.GetBytes(32)).Protect([1f, 2f, 3f]);

        Assert.Throws<FingerprintKeyUnavailableException>(() => new FingerprintCipher(RandomNumberGenerator.GetBytes(32)).Unprotect(blob));
        Assert.Throws<FingerprintKeyUnavailableException>(() => UnavailableProtector.Instance.Unprotect(blob));
        Assert.False(UnavailableProtector.Instance.IsAvailable);
    }

    [Fact]
    public void A_password_opens_the_key_on_another_account_and_a_wrong_one_does_not()
    {
        var path = Path.Combine(_temp.Path, "face-key.json");
        var first = new FingerprintKeyStore(path);
        first.Create("correct horse battery staple");
        var blob = first.Protector.Protect([0.5f, -0.25f]);
        Assert.True(first.HasPassword);

        // Another account: the account wrapper is not readable, the password is.
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Account\": \"", "\"Account\": \"x", StringComparison.Ordinal));
        var other = new FingerprintKeyStore(path);
        Assert.Equal(FingerprintKeyState.Locked, other.Open());
        Assert.False(other.Protector.IsAvailable);
        Assert.False(other.Unlock("wrong"));
        Assert.True(other.Unlock("correct horse battery staple"));
        Assert.Equal(FingerprintKeyState.Unlocked, other.State);
        Assert.Equal([0.5f, -0.25f], other.Protector.Unprotect(blob));

        // Reset: a new key, the old fingerprints become unreadable (they are recomputed).
        other.Reset(null);
        Assert.Throws<FingerprintKeyUnavailableException>(() => other.Protector.Unprotect(blob));
    }

    [Fact]
    public void On_windows_the_account_opens_the_key_created_at_first_use()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI is Windows only (F2-3).");
        }

        var path = Path.Combine(_temp.Path, "face-key.json");
        var first = new FingerprintKeyStore(path);
        Assert.Equal(FingerprintKeyState.Unlocked, first.Open());
        var blob = first.Protector.Protect([1f]);

        var again = new FingerprintKeyStore(path);
        Assert.Equal(FingerprintKeyState.Unlocked, again.Open());
        Assert.Equal([1f], again.Protector.Unprotect(blob));
        Assert.DoesNotContain(Convert.ToBase64String(blob), File.ReadAllText(path), StringComparison.Ordinal);
    }

    public void Dispose() => _temp.Dispose();
}
