using System.Security.Cryptography;
using System.Text;

namespace TDGameServer.Helpers
{
    public static class PasswordHelper
    {
        /// <summary>
        /// 기존 DB와의 호환을 위해 비밀번호를 SHA-256 16진수 문자열로 변환합니다.
        /// 운영 배포 전에는 사용자별 salt가 있는 PBKDF2 또는 Argon2로 마이그레이션해야 합니다.
        /// </summary>
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
