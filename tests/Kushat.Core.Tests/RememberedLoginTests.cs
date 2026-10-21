using System.Security.Cryptography;
using System.Text.Json;
using Kushat.App;
using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public sealed class RememberedLoginTests
{
    private static ReferenceConnection Connection(string address = "https://reference.test:8010", string user = "labels") =>
        new() { ServerAddress = address, UserName = user };

    [Fact]
    public void EncryptedLoginSurvivesSettingsRoundTripWithoutPlaintextPassword()
    {
        var cipher = new TestProtector();
        var connection = Connection();
        var settings = new AppSettings();
        settings.RememberConnection(connection, "Попова", LoginEnvelope.Create(connection, "private-test-password", cipher));
        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("private-test-password", json);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        restored.Normalize();
        Assert.Equal("private-test-password", restored.ActiveProfile!.SavedLogin!.Open(restored.Connection, cipher));
    }

    [Theory]
    [InlineData("https://another.test:8010", "labels")]
    [InlineData("https://reference.test:8011", "labels")]
    [InlineData("http://reference.test:8010", "labels")]
    [InlineData("https://reference.test:8010", "other")]
    public void SavedPasswordCannotFollowEditedServerPortProtocolOrEmployee(string address, string user)
    {
        var cipher = new TestProtector();
        var saved = LoginEnvelope.Create(Connection(), "secret", cipher);
        Assert.Throws<CryptographicException>(() => saved.Open(Connection(address, user), cipher));
    }

    [Fact]
    public void CanonicalUrlUsesSameLoginAndDoesNotDuplicateProfile()
    {
        var settings = new AppSettings();
        var first = settings.RememberConnection(Connection(), "Первая", null);
        var second = settings.RememberConnection(Connection("https://REFERENCE.test:8010/rk7api/v0/xmlinterface.xml"), "Переименовано", null);
        Assert.Same(first, second);
        Assert.Single(settings.Profiles);
        Assert.Equal("Переименовано", settings.ActiveProfile!.Name);
    }

    [Fact]
    public void DifferentServersAndEmployeesKeepTheirOwnPasswords()
    {
        var cipher = new TestProtector();
        var settings = new AppSettings();
        var a = Connection();
        var first = settings.RememberConnection(a, "А", LoginEnvelope.Create(a, "first password", cipher));
        var b = Connection("https://second.test:9443", "second");
        var second = settings.RememberConnection(b, "Б", LoginEnvelope.Create(b, "second password", cipher));
        Assert.Equal(2, settings.Profiles.Count);
        Assert.Equal(second.Id, settings.ActiveProfileId);
        Assert.Equal("first password", first.SavedLogin!.Open(first.Connection, cipher));
        Assert.Equal("second password", second.SavedLogin!.Open(second.Connection, cipher));
    }

    [Fact]
    public void LoginWithoutRememberingRemovesPreviouslyStoredPasswordForThatAccount()
    {
        var cipher = new TestProtector();
        var settings = new AppSettings();
        var c = Connection();
        settings.RememberConnection(c, "А", LoginEnvelope.Create(c, "old password", cipher));
        settings.RememberConnection(c, "А", null);
        Assert.Single(settings.Profiles);
        Assert.Null(settings.ActiveProfile!.SavedLogin);
    }

    [Fact]
    public void LegacySettingsMigrateWithoutInventingSavedCredentials()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"Connection":{"ServerAddress":"https://legacy.test:9000","UserName":"legacy"},"PrinterName":"Printer"}
            """)!;
        settings.Normalize();
        Assert.Single(settings.Profiles);
        Assert.Equal("https://legacy.test:9000", settings.ActiveProfile!.Connection.ServerAddress);
        Assert.Equal("Printer", settings.PrinterName);
        Assert.Null(settings.ActiveProfile.SavedLogin);
        Assert.Equal("classic", settings.TemplateId);
    }

    [Fact]
    public void ChangingWindowsProtectionKeyCannotDecryptStoredPassword()
    {
        var saved = LoginEnvelope.Create(Connection(), "secret", new TestProtector());
        Assert.Throws<AuthenticationTagMismatchException>(() => saved.Open(Connection(), new TestProtector()));
    }

    [Fact]
    public void TamperedCiphertextIsRejected()
    {
        var cipher = new TestProtector();
        var saved = LoginEnvelope.Create(Connection(), "secret", cipher);
        var bytes = Convert.FromBase64String(saved.Ciphertext);
        bytes[^1] ^= 1;
        var damaged = saved with { Ciphertext = Convert.ToBase64String(bytes) };
        Assert.Throws<AuthenticationTagMismatchException>(() => damaged.Open(Connection(), cipher));
    }

    [Fact]
    public void UnknownTemplateFallsBackAndChosenTemplateSurvivesRestart()
    {
        var settings = new AppSettings { TemplateId = "missing" };
        settings.Normalize();
        Assert.Equal("classic", settings.TemplateId);
        settings.TemplateId = "chalkboard";
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.Normalize();
        Assert.Equal(LabelStyle.Chalkboard, LabelTemplates.Find(restored.TemplateId).Style);
        Assert.Equal(5, LabelTemplates.All.Count);
    }

    // Tests the envelope's persistence and binding using real authenticated encryption.
    // Windows DPAPI itself is exercised separately by the Windows test project.
    private sealed class TestProtector : ILoginProtector
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] plaintext, byte[] context)
        {
            var output = new byte[12 + 16 + plaintext.Length];
            RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(0, 12), plaintext, output.AsSpan(28), output.AsSpan(12, 16), context);
            return output;
        }
        public byte[] Unprotect(byte[] ciphertext, byte[] context)
        {
            var output = new byte[ciphertext.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), output, context);
            return output;
        }
    }
}
