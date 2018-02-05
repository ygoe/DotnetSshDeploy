using System;
using System.Security.Cryptography;
using System.Text;

namespace SshDeploy
{
	internal static class CryptoHelper
	{
		/// <summary>
		/// Encrypts a string for the currently logged on Windows user.
		/// </summary>
		/// <param name="str">The string to encrypt.</param>
		/// <returns>The encrypted string with Base64 encoding.</returns>
		public static string EncryptWindows(string str)
		{
			if (string.IsNullOrEmpty(str)) return "";

			byte[] clearData = Encoding.UTF8.GetBytes(str);
			byte[] encryptedData = ProtectedData.Protect(clearData, null, DataProtectionScope.CurrentUser);
			return Convert.ToBase64String(encryptedData);
		}

		/// <summary>
		/// Decrypts a string for the currently logged on Windows user.
		/// </summary>
		/// <param name="str">The base64-encoded encrypted string.</param>
		/// <returns>The decrypted string.</returns>
		public static string DecryptWindows(string str)
		{
			if (string.IsNullOrEmpty(str)) return "";

			byte[] encryptedData = Convert.FromBase64String(str);
			byte[] clearData = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
			return Encoding.UTF8.GetString(clearData);
		}
	}
}
