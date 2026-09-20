namespace TidalSqlLib.Security
{
    using System;
    using System.Security.Cryptography;

    /// <summary>
    /// Hashes and verifies login passwords using salted PBKDF2 (SHA-256). Kept in one place so
    /// login creation, default-admin seeding, and authentication all agree on the format.
    /// </summary>
    internal static class PasswordHasher
    {
        private const int Iterations = 100_000;
        private const int SaltSize = 16;
        private const int HashSize = 32;

        /// <summary>Creates a fresh salt and derived hash for the given password.</summary>
        public static (string Hash, string Salt) Create(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Derive(password, salt);
            return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
        }

        /// <summary>Verifies a password against a stored hash/salt pair in constant time.</summary>
        public static bool Verify(string password, string hashBase64, string saltBase64)
        {
            if (string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64))
            {
                return false;
            }

            byte[] expected;
            byte[] salt;
            try
            {
                expected = Convert.FromBase64String(hashBase64);
                salt = Convert.FromBase64String(saltBase64);
            }
            catch (FormatException)
            {
                return false;
            }

            var actual = Derive(password, salt);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        private static byte[] Derive(string password, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
    }
}
