using System.Security.Cryptography;
using System.Text;

namespace TDGameServer.Helpers
{
    public static class PasswordHelper
    {
        // 비밀번호를 SHA256으로 해싱하여 저장
        public static string HashPassword(string password)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
                return BitConverter.ToString(bytes).Replace("-", "").ToLower();
            }
        }
    }
}
