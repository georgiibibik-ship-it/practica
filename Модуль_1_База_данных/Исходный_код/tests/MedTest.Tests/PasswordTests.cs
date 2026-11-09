using System.Security.Cryptography;
using System.Text;
using MedTest.Data;

namespace MedTest.Tests;

public class PasswordTests
{
    [Theory]
    [InlineData("password", "salt", 1)]
    [InlineData("password", "salt", 4096)]
    [InlineData("Проверка-2026!", "соль", 1000)]
    public void Fallback_pbkdf2_equals_the_system_function(string password, string salt, int iterations)
    {
        var s = Encoding.UTF8.GetBytes(salt);
        Assert.Equal(Rfc2898DeriveBytes.Pbkdf2(password, s, iterations, HashAlgorithmName.SHA256, 32),
            Passwords.Pbkdf2Sha256(Encoding.UTF8.GetBytes(password), s, iterations));
    }

    [Fact]
    public void Rfc_test_vector_sha256()
    {
        // PBKDF2-HMAC-SHA256("password", "salt", 4096, 32), RFC 7914 / common test vector.
        Assert.Equal("c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a",
            Convert.ToHexStringLower(Passwords.Pbkdf2Sha256(Encoding.UTF8.GetBytes("password"), Encoding.UTF8.GetBytes("salt"), 4096)));
    }
}
